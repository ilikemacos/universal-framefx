using System.Net.Http;
using System.Text.Json;
using UniversalFrameFX.Agent.Tools;

namespace UniversalFrameFX.Agent.Cloud;

/// <summary>Raised to the panel so it can show the right link or state.</summary>
public abstract record PanelSignal : AgentEvent;
/// <summary>The chosen model needs a higher plan: show an Upgrade link.</summary>
public sealed record UpgradeNeeded(string Message, string UpgradeUrl, LarkquillPlan? Plan, LarkquillPlan? PlanNeeded) : PanelSignal;
/// <summary>No key, or the key was rejected: show the key box and "Get a free key".</summary>
public sealed record KeyNeeded(string Message) : PanelSignal;
/// <summary>This turn was answered by the offline preset model.</summary>
public sealed record UsedOffline(string Reason) : PanelSignal;

/// <summary>
/// The LarkquillAI panel's brain. With a key, each turn goes to LarkquillAI through the normal AgentLoop and the FrameFX
/// tools (make_preset, apply_preset, set_setting, start/stop, state); every change goes through the app's validator and a Confirm.
/// Without a key, or when the cloud can't be reached, the turn falls back to the local preset model (offline, no account),
/// and "apply" saves its last proposal through the same apply_preset tool.
/// </summary>
public sealed class LarkquillSession
{
    readonly ToolRegistry _tools;
    readonly MakePresetTool _maker;
    readonly Func<string?> _key;
    readonly Func<HttpMessageHandler?> _handler;
    AgentLoop? _loop;
    LarkquillClient? _client;
    string? _loopKey;
    bool _planKnown;

    public LarkquillModel Model { get; set; } = Larkquill.Default;
    /// <summary>Best known plan for this key. Starts at Hobby; a 403 tells us the real plan, and a successful call to a model raises it.</summary>
    public LarkquillPlan KnownPlan { get; private set; } = LarkquillPlan.Hobby;
    public TokenBudget Budget { get; } = new(16_000, 262_144);
    public bool HasKey => !string.IsNullOrWhiteSpace(_key());

    public LarkquillSession(IConfirmer confirmer, IFrameFxHost host, IPresetEngine presets, Func<string?> key,
                            Func<HttpMessageHandler?>? handler = null)
    {
        _key = key; _handler = handler ?? (() => null);
        _maker = new MakePresetTool(presets);
        _tools = BuildTools(confirmer, host, _maker);
    }

    /// <summary>FrameFX-only tools. No web, no app launching, no shell.</summary>
    public static ToolRegistry BuildTools(IConfirmer confirmer, IFrameFxHost host, MakePresetTool maker) =>
        new ToolRegistry(confirmer)
            .Add(maker).Add(new ApplyPresetTool(maker, host)).Add(new SetSettingTool(host))
            .Add(new StartFrameFxTool(host)).Add(new StopFrameFxTool(host)).Add(new GetStateTool(host));

    public IReadOnlyCollection<string> ToolNames => _tools.Tools.Select(t => t.Name).ToArray();

    public void Reset() { _loop?.Reset(); }

    /// <summary>Call after the key changes so the next turn uses it.</summary>
    public void KeyChanged() { _loop = null; _client = null; _loopKey = null; KnownPlan = LarkquillPlan.Hobby; _planKnown = false; }

    AgentLoop Loop(string key)
    {
        if (_loop == null || _loopKey != key)
        {
            _client = new LarkquillClient(key, Model, _handler());
            _loop = new AgentLoop(_client, _tools, Budget, Larkquill.SystemPrompt) { MaxSteps = 6 };
            _loopKey = key;
        }
        _client!.Model = Model;
        return _loop;
    }

    public async Task RunTurnAsync(string text, Action<AgentEvent> emit, CancellationToken ct)
    {
        string? key = _key();
        if (string.IsNullOrWhiteSpace(key))
        {
            emit(new KeyNeeded("Add a free LarkquillAI key to chat. Until then, I'll use the offline preset model."));
            await OfflineTurnAsync(text, "no key", emit, ct);
            return;
        }
        if (_planKnown && !Larkquill.Unlocked(Model, KnownPlan))
        {
            // We already know this key's plan doesn't include the model: don't spend a request.
            emit(new UpgradeNeeded($"{Model.Name} needs LarkquillAI {Model.PlanLabel}.", Larkquill.UpgradeUrl, KnownPlan, Model.Plan));
            emit(new TurnDone("", 0));
            return;
        }
        var loop = Loop(key);
        bool anyText = false;
        try
        {
            await loop.RunTurnAsync(text, e => { if (e is TextDelta) anyText = true; emit(e); }, ct);
            if ((int)Model.Plan > (int)KnownPlan) KnownPlan = Model.Plan; // the call worked, so the plan includes this model
        }
        catch (LarkquillApiException ex) when (ex.IsPlanRequired)
        {
            loop.UndoUnansweredUserTurn();
            if (ex.Plan is { } p) { KnownPlan = p; _planKnown = true; }
            emit(new UpgradeNeeded(ex.Message, ex.UpgradeUrl ?? Larkquill.UpgradeUrl, ex.Plan, ex.PlanNeeded ?? Model.Plan));
            emit(new TurnDone("", 0));
        }
        catch (LarkquillApiException ex) when (ex.IsKeyProblem)
        {
            loop.UndoUnansweredUserTurn();
            emit(new KeyNeeded(ex.Message));
            await OfflineTurnAsync(text, "key rejected", emit, ct);
        }
        catch (LarkquillApiException ex) when (ex.IsRateLimit && !ex.IsTransient)
        {
            loop.UndoUnansweredUserTurn();
            emit(new AgentNotice(ex.Message));
            await OfflineTurnAsync(text, "limit reached", emit, ct);
        }
        catch (Exception ex) when (!anyText && ex is LarkquillApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            loop.UndoUnansweredUserTurn();
            await OfflineTurnAsync(text, "LarkquillAI is unreachable", emit, ct);
        }
    }

    static readonly string[] ApplyWords = ["apply", "apply it", "yes", "yes apply", "save", "save it", "do it", "ok apply", "use it"];

    /// <summary>Offline: the local preset model makes or refines a preset; "apply" saves the last one (with Confirm).</summary>
    internal async Task OfflineTurnAsync(string text, string reason, Action<AgentEvent> emit, CancellationToken ct)
    {
        emit(new UsedOffline(reason));
        string t = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        if (_maker.Last?.Profile != null && ApplyWords.Contains(t))
        {
            var call = new ToolCall("offline_apply", "apply_preset", "{}");
            emit(new ToolStarted(call));
            var r = await _tools.InvokeAsync(call, ct);
            emit(new ToolFinished(call, r));
            string msg = r.Ok ? r.Text : r.Text.Replace("ERROR: ", "");
            emit(new TextDelta(msg));
            emit(new TurnDone(msg, 1));
            return;
        }
        var mk = new ToolCall("offline_make", "make_preset", JsonSerializer.Serialize(new { request = text }));
        var res = await _tools.InvokeAsync(mk, ct);
        string reply;
        if (!res.Ok) reply = "The offline preset model couldn't help with that. Add a LarkquillAI key to ask anything about FrameFX.";
        else
        {
            var p = _maker.Last;
            reply = p?.Reply ?? "";
            if (p?.Profile != null) reply += $"\n\nProposed: {Labels.Describe(p.Profile)}" + (p.AntiCheatNote != null ? $"\n{p.AntiCheatNote}" : "") + "\nSay \"apply\" to save it.";
        }
        emit(new TextDelta(reply.Trim()));
        emit(new TurnDone(reply.Trim(), 1));
    }
}
