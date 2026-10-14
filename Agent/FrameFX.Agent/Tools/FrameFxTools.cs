using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalFrameFX.Agent.Tools;

/// <summary>Plain snapshot of a game profile (mirrors UniversalFrameFX.GameProfile; the app adapter maps it).</summary>
public sealed record ProfileSnapshot(string Exe, int Preset, bool FrameGen, int FgSelect, int FgMultiplier,
                                     bool Ssgi, bool Ssrt, int Backend, int Res);

public sealed record FrameFxState(bool Running, string? ActiveGameExe, IReadOnlyList<ProfileSnapshot> Profiles, string GpuName);

/// <summary>Result of the Beta7 preset pipeline (ChatSession, model-first).</summary>
public sealed record PresetProposal(string Reply, ProfileSnapshot? Profile, string? AntiCheatNote);

/// <summary>Wraps PresetAi.ChatSession + PresetAssistantMapper (see HANDOFF.md). Keeps conversation state across calls.</summary>
public interface IPresetEngine
{
    PresetProposal Turn(string request);
    void Reset();
}

/// <summary>Implemented by the app (MainForm adapter). All calls are marshalled to the UI thread by the adapter.</summary>
public interface IFrameFxHost
{
    FrameFxState GetState();
    ProfileSnapshot? GetProfile(string exe);
    /// <summary>Saves the profile (the app runs GameProfiles.Sanitize + anti-cheat checks on it).</summary>
    ProfileSnapshot SaveProfile(ProfileSnapshot p);
    bool IsAntiCheatGame(string exe);
    /// <summary>Start FrameFX on the running game's window; false + reason if the game isn't running.</summary>
    (bool Ok, string Message) Start(string exe);
    (bool Ok, string Message) Stop();
}

public static class Labels
{
    public static readonly string[] Preset = ["Performance", "Quality", "Competitive"];
    public static readonly string[] Fg = ["Off", "CSR 1.3", "CSR 2.0", "FSR 3", "CSR AI"];
    public static string Describe(ProfileSnapshot p) =>
        $"{p.Exe}: preset {Preset.ElementAtOrDefault(p.Preset) ?? p.Preset.ToString()}, frame gen " +
        (p.FrameGen ? $"{Fg.ElementAtOrDefault(Math.Max(0, p.FgSelect)) ?? "on"} {p.FgMultiplier}x" : "off") +
        $", SSGI {(p.Ssgi ? "on" : "off")}, ray-traced lighting {(p.Ssrt ? "on" : "off")}, backend {p.Backend}, output res {p.Res}";
    public static string Json(object o) => JsonSerializer.Serialize(o);
}

/// <summary>make_preset: read-only. Runs the Beta7 model-first pipeline and returns a proposal (nothing is saved).</summary>
public sealed class MakePresetTool(IPresetEngine engine) : IAgentTool
{
    public string Name => "make_preset";
    public string Description => "Build or change a FrameFX preset from a plain-language request (game, GPU, fps now/target, " +
        "preferences like quieter or sharper). Follow-ups refine the last preset. Returns the proposal only; use apply_preset to save it.";
    public JsonObject Parameters { get; } = Args.Schema(("request", "string", "the user's request in their own words", true),
                                                       ("reset", "boolean", "start a new preset instead of refining", false));
    internal PresetProposal? Last { get; private set; }
    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct) { Args.Str(a, "request"); return Task.FromResult<ConfirmRequest?>(null); }
    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        if (a.TryGetProperty("reset", out var r) && r.ValueKind == JsonValueKind.True) engine.Reset();
        Last = engine.Turn(Args.Str(a, "request"));
        string s = Last.Reply;
        if (Last.Profile != null) s += "\nPROPOSAL: " + Labels.Describe(Last.Profile);
        if (Last.AntiCheatNote != null) s += "\nNOTE: " + Last.AntiCheatNote;
        return Task.FromResult(ToolResult.Success(s));
    }
}

/// <summary>apply_preset: saves the last make_preset proposal to that game's profile, after Confirm (old → new).</summary>
public sealed class ApplyPresetTool(MakePresetTool maker, IFrameFxHost host) : IAgentTool
{
    public string Name => "apply_preset";
    public string Description => "Save the most recent make_preset proposal to the game's FrameFX profile. Asks the user first.";
    public JsonObject Parameters { get; } = Args.Schema(("exe", "string", "optional game exe, defaults to the proposal's game", false));
    ProfileSnapshot? _pending;

    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct)
    {
        var p = maker.Last?.Profile ?? throw new ArgumentException("no preset proposal yet; call make_preset first");
        string exe = Args.Str(a, "exe", required: false);
        if (exe.Length > 0) p = p with { Exe = exe };
        if (string.IsNullOrEmpty(p.Exe)) throw new ArgumentException("the proposal has no game; ask which game");
        if (host.IsAntiCheatGame(p.Exe)) p = p with { FrameGen = false, FgSelect = 0 };
        _pending = p;
        var old = host.GetProfile(p.Exe);
        var lines = new List<string> { "Game: " + p.Exe, "Before: " + (old == null ? "(no saved profile)" : Labels.Describe(old)), "After:  " + Labels.Describe(p) };
        if (host.IsAntiCheatGame(p.Exe)) lines.Add("Frame generation stays off: this game uses anti-cheat.");
        return Task.FromResult<ConfirmRequest?>(new ConfirmRequest("Apply preset?", lines, "Apply"));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var saved = host.SaveProfile(_pending ?? throw new InvalidOperationException("not confirmed"));
        _pending = null;
        return Task.FromResult(ToolResult.Success("Saved. " + Labels.Describe(saved)));
    }
}

/// <summary>set_setting: one whitelisted field of a game profile.</summary>
public sealed class SetSettingTool(IFrameFxHost host) : IAgentTool
{
    public static readonly string[] Keys = ["frame_gen", "fg_mode", "fg_multiplier", "preset", "ssgi", "ssrt", "backend", "res"];
    public string Name => "set_setting";
    public string Description => "Change one FrameFX setting for a game. key: frame_gen (on/off), fg_mode (off|csr13|csr20|fsr3|csrai), " +
        "fg_multiplier (2,3,4,8), preset (performance|quality|competitive), ssgi (on/off), ssrt (on/off), backend (number), res (number). Asks the user first.";
    public JsonObject Parameters { get; } = Args.Schema(("exe", "string", "game exe, e.g. eldenring.exe", true),
        ("key", "string", "setting name", true), ("value", "string", "new value", true));
    ProfileSnapshot? _pending;

    internal static ProfileSnapshot Apply(ProfileSnapshot p, string key, string value)
    {
        string v = value.Trim().ToLowerInvariant();
        bool Bool() => v switch { "on" or "true" or "1" or "yes" => true, "off" or "false" or "0" or "no" => false,
                                  _ => throw new ArgumentException($"'{value}' isn't on/off") };
        int Num(int lo, int hi) => int.TryParse(v, out int n) && n >= lo && n <= hi ? n : throw new ArgumentException($"'{value}' must be {lo}-{hi}");
        return key switch
        {
            "frame_gen" => p with { FrameGen = Bool() },
            "fg_mode" => v switch
            {
                "off" => p with { FrameGen = false, FgSelect = 0 },
                "csr13" or "csr 1.3" => p with { FrameGen = true, FgSelect = 1 },
                "csr20" or "csr 2.0" => p with { FrameGen = true, FgSelect = 2 },
                "fsr3" or "fsr 3" => p with { FrameGen = true, FgSelect = 3 },
                "csrai" or "csr ai" => p with { FrameGen = true, FgSelect = 4 },
                _ => throw new ArgumentException($"unknown fg_mode '{value}'"),
            },
            "fg_multiplier" => v is "2" or "3" or "4" or "8" ? p with { FgMultiplier = int.Parse(v) } : throw new ArgumentException("fg_multiplier must be 2, 3, 4 or 8"),
            "preset" => v switch { "performance" => p with { Preset = 0 }, "quality" => p with { Preset = 1 }, "competitive" => p with { Preset = 2 },
                                   _ => throw new ArgumentException($"unknown preset '{value}'") },
            "ssgi" => p with { Ssgi = Bool() },
            "ssrt" => p with { Ssrt = Bool() },
            "backend" => p with { Backend = Num(0, 9) },
            "res" => p with { Res = Num(0, 9) },
            _ => throw new ArgumentException($"unknown key '{key}'. Allowed: {string.Join(", ", Keys)}"),
        };
    }

    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct)
    {
        string exe = Args.Str(a, "exe"), key = Args.Str(a, "key").ToLowerInvariant(), val = Args.Str(a, "value");
        var old = host.GetProfile(exe) ?? new ProfileSnapshot(exe, 0, false, 0, 4, false, false, 0, 0);
        var neu = Apply(old, key, val);
        if (neu.FrameGen && host.IsAntiCheatGame(exe))
            throw new ArgumentException($"{exe} uses anti-cheat, so FrameFX keeps frame generation off for it");
        _pending = neu;
        return Task.FromResult<ConfirmRequest?>(new ConfirmRequest("Change a FrameFX setting?",
            [$"Game: {exe}", $"Setting: {key} → {val}", "Before: " + Labels.Describe(old), "After:  " + Labels.Describe(neu)], "Change"));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var saved = host.SaveProfile(_pending ?? throw new InvalidOperationException("not confirmed"));
        _pending = null;
        return Task.FromResult(ToolResult.Success("Saved. " + Labels.Describe(saved)));
    }
}

public sealed class StartFrameFxTool(IFrameFxHost host) : IAgentTool
{
    public string Name => "start_framefx";
    public string Description => "Start FrameFX on a running game using its saved profile. Asks the user first.";
    public JsonObject Parameters { get; } = Args.Schema(("exe", "string", "game exe", true));
    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct)
    {
        string exe = Args.Str(a, "exe");
        var p = host.GetProfile(exe);
        return Task.FromResult<ConfirmRequest?>(new ConfirmRequest("Start FrameFX?",
            [$"Game: {exe}", "Settings: " + (p == null ? "current global settings" : Labels.Describe(p))], "Start"));
    }
    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var (ok, msg) = host.Start(Args.Str(a, "exe"));
        return Task.FromResult(ok ? ToolResult.Success(msg) : ToolResult.Fail(msg));
    }
}

public sealed class StopFrameFxTool(IFrameFxHost host) : IAgentTool
{
    public string Name => "stop_framefx";
    public string Description => "Stop FrameFX output. Asks the user first.";
    public JsonObject Parameters { get; } = Args.Schema();
    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct) =>
        Task.FromResult<ConfirmRequest?>(new ConfirmRequest("Stop FrameFX?", ["FrameFX output will stop. The game keeps running."], "Stop"));
    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var (ok, msg) = host.Stop();
        return Task.FromResult(ok ? ToolResult.Success(msg) : ToolResult.Fail(msg));
    }
}

public sealed class GetStateTool(IFrameFxHost host) : IAgentTool
{
    public string Name => "get_framefx_state";
    public string Description => "Read FrameFX's current state: running or not, active game, GPU, and saved game profiles.";
    public JsonObject Parameters { get; } = Args.Schema();
    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct) => Task.FromResult<ConfirmRequest?>(null);
    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var s = host.GetState();
        var lines = new List<string> { $"Running: {s.Running}", $"Active game: {s.ActiveGameExe ?? "none"}", $"GPU: {s.GpuName}", "Profiles:" };
        lines.AddRange(s.Profiles.Select(p => " - " + Labels.Describe(p)));
        return Task.FromResult(ToolResult.Success(string.Join("\n", lines)));
    }
}

public static class DefaultTools
{
    public static ToolRegistry Build(IConfirmer confirmer, IUserPicker picker, IFrameFxHost host, IPresetEngine presets,
                                     ISearchProvider search, HttpClient http, LaunchAllowlist allowlist)
    {
        var maker = new MakePresetTool(presets);
        return new ToolRegistry(confirmer)
            .Add(new WebSearchTool(search)).Add(new FetchPageTool(http))
            .Add(new LaunchAppTool(allowlist, picker))
            .Add(maker).Add(new ApplyPresetTool(maker, host)).Add(new SetSettingTool(host))
            .Add(new StartFrameFxTool(host)).Add(new StopFrameFxTool(host)).Add(new GetStateTool(host));
    }

    /// <summary>Settings: provider = duckduckgo (default) | searxng (needs url) | brave | tavily (need key).</summary>
    public static ISearchProvider Search(HttpClient http, string? provider, string? url, string? key) => (provider ?? "duckduckgo").ToLowerInvariant() switch
    {
        "searxng" when !string.IsNullOrWhiteSpace(url) => new SearxngProvider(http, url!),
        "brave" when !string.IsNullOrWhiteSpace(key) => new BraveProvider(http, key!),
        "tavily" when !string.IsNullOrWhiteSpace(key) => new TavilyProvider(http, key!),
        _ => new DuckDuckGoHtmlProvider(http),
    };
}
