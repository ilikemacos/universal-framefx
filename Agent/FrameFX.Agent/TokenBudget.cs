namespace UniversalFrameFX.Agent;

/// <summary>
/// Two limits: the model's context window (what fits in one request, set by ContextSizer) and a
/// per-session total budget (default 256k tokens, prompt + completion summed over every request).
/// When the history passes <see cref="SummarizeAt"/> of the window, AgentLoop folds old turns into a summary.
/// </summary>
public sealed class TokenBudget
{
    public int ContextWindow { get; }
    public long SessionLimit { get; }
    public long Used { get; private set; }
    public double SummarizeAt { get; init; } = 0.70;
    /// <summary>Tokens kept free for the reply.</summary>
    public int ReplyReserve { get; init; } = 1024;

    public TokenBudget(int contextWindow, long sessionLimit = 262_144)
    { ContextWindow = contextWindow; SessionLimit = sessionLimit; }

    public void Add(Usage? u, int fallbackPrompt, int fallbackCompletion)
        => Used += u is null ? fallbackPrompt + fallbackCompletion : u.PromptTokens + u.CompletionTokens;

    public long Remaining => Math.Max(0, SessionLimit - Used);
    public bool Exhausted => Used >= SessionLimit;
    public bool NeedsSummary(int historyTokens) => historyTokens + ReplyReserve > ContextWindow * SummarizeAt;

    /// <summary>Rough token count (≈3.6 chars/token for English + JSON; slight overestimate on purpose).</summary>
    public static int Estimate(string? s) => s is null ? 0 : (int)Math.Ceiling(s.Length / 3.6) + 4;
    public static int Estimate(IEnumerable<ChatMessage> msgs) =>
        msgs.Sum(m => Estimate(m.Content) + (m.ToolCalls?.Sum(c => Estimate(c.ArgumentsJson) + 8) ?? 0));
}
