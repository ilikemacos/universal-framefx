using System.Text;
using System.Text.Json.Nodes;

namespace UniversalFrameFX.Agent;

public abstract record AgentEvent;
public sealed record TextDelta(string Text) : AgentEvent;
public sealed record ToolStarted(ToolCall Call) : AgentEvent;
public sealed record ToolFinished(ToolCall Call, ToolResult Result) : AgentEvent;
public sealed record AgentNotice(string Text) : AgentEvent;
public sealed record TurnDone(string FinalText, int Steps) : AgentEvent;

/// <summary>Seam for tests: the real one is LlamaChatClient.</summary>
public interface IChatBackend
{
    Task<AssistantTurn> CompleteAsync(IEnumerable<ChatMessage> messages, JsonArray? tools, Action<string>? onText,
                                      CancellationToken ct, double temperature = 0.4, int maxTokens = 1024);
}

public sealed class LlamaBackend(LlamaChatClient client) : IChatBackend
{
    public Task<AssistantTurn> CompleteAsync(IEnumerable<ChatMessage> m, JsonArray? t, Action<string>? o, CancellationToken ct,
        double temperature = 0.4, int maxTokens = 1024) => client.CompleteAsync(m, t, o, ct, temperature, maxTokens);
}

/// <summary>
/// The agent loop: send history + tools → stream text → if the model asked for tools, run them (through the
/// registry, which confirms side effects) → append results → repeat, up to <see cref="MaxSteps"/>.
/// </summary>
public sealed class AgentLoop
{
    public const string SystemPrompt =
        "You are WyneAI, the AI built into the FrameFX app (frame generation, upscaling and lighting for PC games). " +
        "You can chat about anything, and you have tools to search the web, read pages, launch allowed apps, build and apply FrameFX presets, " +
        "change FrameFX settings and start or stop FrameFX on a game. Use tools when they help; chain several when a request has several steps. " +
        "Actions that change something ask the user to confirm; if they decline, don't retry. Never claim an action happened unless its tool result says so. " +
        "For current facts, use web_search and cite the page. If asked what model you are, say you are WyneAI made for FrameFX. Be concise.";

    readonly IChatBackend _backend;
    readonly ToolRegistry _tools;
    readonly List<ChatMessage> _history = [];
    public TokenBudget Budget { get; }
    public int MaxSteps { get; init; } = 8;
    public IReadOnlyList<ChatMessage> History => _history;

    public AgentLoop(IChatBackend backend, ToolRegistry tools, TokenBudget budget)
    {
        _backend = backend; _tools = tools; Budget = budget;
        _history.Add(ChatMessage.System(SystemPrompt));
    }

    public void Reset() { _history.RemoveRange(1, _history.Count - 1); }

    public async Task RunTurnAsync(string userText, Action<AgentEvent> emit, CancellationToken ct)
    {
        if (Budget.Exhausted)
        {
            emit(new AgentNotice($"This chat has used its {Budget.SessionLimit / 1024}k-token budget. Start a new chat to keep going."));
            emit(new TurnDone("", 0)); return;
        }
        _history.Add(ChatMessage.User(userText));
        var tools = _tools.ToOpenAiTools();
        string final = "";
        int step = 0;
        for (; step < MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            await MaybeSummarizeAsync(emit, ct);
            int promptEst = TokenBudget.Estimate(_history) + TokenBudget.Estimate(tools.ToJsonString());
            int maxReply = Math.Max(256, Math.Min(2048, Budget.ContextWindow - promptEst - 64));
            var sb = new StringBuilder();
            var turn = await _backend.CompleteAsync(_history, step == MaxSteps - 1 ? null : tools,
                d => { sb.Append(d); emit(new TextDelta(d)); }, ct, maxTokens: maxReply);
            Budget.Add(turn.Usage, promptEst, TokenBudget.Estimate(turn.Content));

            _history.Add(ChatMessage.Assistant(string.IsNullOrEmpty(turn.Content) ? null : turn.Content,
                                               turn.ToolCalls.Count > 0 ? turn.ToolCalls : null));
            if (turn.ToolCalls.Count == 0) { final = turn.Content; break; }

            foreach (var call in turn.ToolCalls)
            {
                emit(new ToolStarted(call));
                var r = await _tools.InvokeAsync(call, ct);
                emit(new ToolFinished(call, r));
                _history.Add(ChatMessage.Tool(call.Id, call.Name, r.Text));
            }
            if (Budget.Exhausted) { emit(new AgentNotice("Session token budget reached.")); break; }
        }
        if (step >= MaxSteps) emit(new AgentNotice($"Stopped after {MaxSteps} steps."));
        emit(new TurnDone(final, step + 1));
    }

    /// <summary>Fold the oldest turns into one summary message once the history gets near the window.
    /// Keeps the system prompt and the last few messages verbatim. Never splits a tool call from its result.</summary>
    internal async Task MaybeSummarizeAsync(Action<AgentEvent> emit, CancellationToken ct)
    {
        if (!Budget.NeedsSummary(TokenBudget.Estimate(_history))) return;
        int keepFrom = Math.Max(1, _history.Count - 6);
        while (keepFrom > 1 && _history[keepFrom].Role == "tool") keepFrom--; // don't orphan tool results
        if (keepFrom <= 2) return;
        var old = _history.GetRange(1, keepFrom - 1);
        var transcript = new StringBuilder();
        foreach (var m in old)
        {
            string c = m.Content ?? "";
            if (m.Role == "tool" && c.Length > 1500) c = c[..1500] + "…";
            if (m.ToolCalls != null) c += " [called: " + string.Join(", ", m.ToolCalls.Select(t => t.Name + t.ArgumentsJson)) + "]";
            transcript.Append(m.Role).Append(": ").AppendLine(c);
        }
        string src = transcript.ToString();
        int maxSrc = (int)(Budget.ContextWindow * 3.6 * 0.6);
        if (src.Length > maxSrc) src = src[^maxSrc..];
        var req = new[]
        {
            ChatMessage.System("Summarize this conversation for your own memory in under 250 words: user's goals, PC/GPU/games, presets and settings changed, facts found, open questions."),
            ChatMessage.User(src),
        };
        var s = await _backend.CompleteAsync(req, null, null, ct, temperature: 0.2, maxTokens: 400);
        Budget.Add(s.Usage, TokenBudget.Estimate(src), TokenBudget.Estimate(s.Content));
        _history.RemoveRange(1, keepFrom - 1);
        _history.Insert(1, ChatMessage.System("Summary of the earlier conversation: " + s.Content));
        emit(new AgentNotice("Older messages were summarized to stay within the context window."));
    }
}
