using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalFrameFX.Agent.Cloud;

/// <summary>LarkquillAI plans. Hobby is free; Pro and Max are one-time purchases on the LarkquillAI site.</summary>
public enum LarkquillPlan { Hobby = 0, Pro = 1, Max = 2 }

public sealed record LarkquillModel(string Id, string Name, LarkquillPlan Plan, int ContextTokens, params string[] Aliases)
{
    public string PlanLabel => Plan switch { LarkquillPlan.Pro => "Pro", LarkquillPlan.Max => "Max", _ => "Hobby (free)" };
}

/// <summary>Public endpoints and the model list (mirrors GET /v1/models on larkquillai.vercel.app).</summary>
public static class Larkquill
{
    public const string ApiBase = "https://larkquillai.vercel.app/v1/";
    /// <summary>Where users sign in and create an API key (free on Hobby).</summary>
    public const string GetKeyUrl = "https://larkquillai.vercel.app/api";
    /// <summary>Fallback when a 403 doesn't carry its own upgrade_url.</summary>
    public const string UpgradeUrl = "https://ilikemacos.github.io/larkquillai/";
    public const string PricingSummary = "Hobby (free): Quill Wren and Quill Scribe. Pro ($7.99 one-time): adds Quill Sage. Max ($12.99 one-time): every model, including Quill Sovereign.";

    public static readonly IReadOnlyList<LarkquillModel> Models =
    [
        new("quill-wren", "Quill Wren", LarkquillPlan.Hobby, 16_000, "larkquill-chirp-1", "larkquill-chirp-1.1", "larkquill-chirp", "wrenby-chirp"),
        new("quill-scribe", "Quill Scribe", LarkquillPlan.Hobby, 64_000, "larkquill-trill-1", "larkquill-trill", "wrenby-trill"),
        new("quill-sage", "Quill Sage", LarkquillPlan.Pro, 128_000, "larkquill-aria-1.5", "larkquill-aria", "wrenby-aria"),
        new("quill-sovereign", "Quill Sovereign", LarkquillPlan.Max, 200_000, "larkquill-chorus-0.7", "larkquill-chorus", "wrenby-chorus"),
    ];

    /// <summary>Quill Scribe, the same default the LarkquillAI site uses when signed out.</summary>
    public static LarkquillModel Default => Models[1];

    /// <summary>Finds a model by id or old id (saved settings from earlier betas keep working).</summary>
    public static LarkquillModel ById(string? id)
    {
        string k = id?.Trim() ?? "";
        return Models.FirstOrDefault(m => string.Equals(m.Id, k, StringComparison.OrdinalIgnoreCase)
            || m.Aliases.Any(a => string.Equals(a, k, StringComparison.OrdinalIgnoreCase))) ?? Default;
    }

    public static bool Unlocked(LarkquillModel m, LarkquillPlan plan) => (int)plan >= (int)m.Plan;

    public static LarkquillPlan ParsePlan(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "max" => LarkquillPlan.Max,
        "pro" => LarkquillPlan.Pro,
        _ => LarkquillPlan.Hobby,
    };

    /// <summary>System prompt for the FrameFX panel. Never names the underlying model.</summary>
    public const string SystemPrompt =
        "You are LarkquillAI, built into the FrameFX app (real-time upscaling, frame generation and lighting for PC games on Windows). " +
        "Answer questions about FrameFX: frame generation modes (CSR 1.3, CSR 2.0, FSR 3, CSR AI) and multipliers, upscalers (FSR 1-4, XeSS, CSR), " +
        "presets (Performance, Quality, Competitive), SSGI and ray-traced lighting, borderless windowed mode, latency and anti-cheat. " +
        "You can read FrameFX's state, build a preset with make_preset and save it with apply_preset, change one setting with set_setting, " +
        "and start or stop FrameFX. FrameFX checks every change and asks the user to confirm it; if they decline, don't retry. " +
        "Never say a change happened unless the tool result says so. Frame generation stays off for anti-cheat games. " +
        "If asked what model you are, say you are LarkquillAI. Be concise and friendly.";
}

/// <summary>A LarkquillAI API error, parsed from the OpenAI-style error body.</summary>
public sealed class LarkquillApiException : Exception
{
    public int Status { get; }
    public string? Code { get; }
    public string? UpgradeUrl { get; }
    public LarkquillPlan? Plan { get; }
    public LarkquillPlan? PlanNeeded { get; }

    public LarkquillApiException(int status, string message, string? code = null, string? upgradeUrl = null,
                                 LarkquillPlan? plan = null, LarkquillPlan? planNeeded = null) : base(message)
    { Status = status; Code = code; UpgradeUrl = upgradeUrl; Plan = plan; PlanNeeded = planNeeded; }

    /// <summary>The model needs a higher plan (HTTP 403, code plan_required).</summary>
    public bool IsPlanRequired => Status == 403 || Code == "plan_required";
    /// <summary>Missing, invalid or revoked key (HTTP 401).</summary>
    public bool IsKeyProblem => Status == 401;
    public bool IsRateLimit => Status == 429;
    /// <summary>Worth falling back to the offline preset model (server down or busy).</summary>
    public bool IsTransient => Status >= 500 || Code is "upstream_busy";

    public static LarkquillApiException FromResponse(int status, string body)
    {
        string msg = $"LarkquillAI error {status}.";
        string? code = null, upgrade = null; LarkquillPlan? plan = null, need = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString() ?? msg;
                if (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (e.TryGetProperty("upgrade_url", out var u) && u.ValueKind == JsonValueKind.String) upgrade = u.GetString();
                if (e.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String) plan = Larkquill.ParsePlan(p.GetString());
                if (e.TryGetProperty("plan_needed", out var n) && n.ValueKind == JsonValueKind.String) need = Larkquill.ParsePlan(n.GetString());
            }
        }
        catch (JsonException) { if (body.Length > 0) msg += " " + (body.Length > 200 ? body[..200] : body); }
        if (status == 403 || code == "plan_required")
        {
            // Only accept an https upgrade link on our own domains; anything else falls back to the LarkquillAI site.
            if (!SafeUpgradeUrl(upgrade)) upgrade = Larkquill.UpgradeUrl;
        }
        else upgrade = null;
        return new LarkquillApiException(status, msg, code, upgrade, plan, need);
    }

    internal static bool SafeUpgradeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "ilikemacos.github.io" || u.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase) && u.Host.StartsWith("larkquillai", StringComparison.OrdinalIgnoreCase)
         || u.Host.EndsWith("chopstickshq.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Streaming client for LarkquillAI's OpenAI-compatible API (POST /v1/chat/completions with Bearer key).
/// Implements <see cref="IChatBackend"/> so the existing AgentLoop and tools work unchanged.
/// </summary>
public sealed class LarkquillClient : IChatBackend
{
    readonly HttpClient _http;
    readonly string _key;
    public LarkquillModel Model { get; set; }

    public LarkquillClient(string apiKey, LarkquillModel model, HttpMessageHandler? handler = null, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A LarkquillAI API key is required.", nameof(apiKey));
        _key = apiKey.Trim();
        Model = model;
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.BaseAddress = baseUri ?? new Uri(Larkquill.ApiBase);
        _http.Timeout = Timeout.InfiniteTimeSpan; // streaming; cancellation comes from the caller's token
    }

    internal JsonObject BuildBody(IEnumerable<ChatMessage> messages, JsonArray? tools, double temperature, int maxTokens)
    {
        var body = new JsonObject
        {
            ["model"] = Model.Id,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.ToJson()).ToArray()),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        if (tools is { Count: > 0 }) { body["tools"] = tools.DeepClone(); body["tool_choice"] = "auto"; }
        return body;
    }

    public async Task<AssistantTurn> CompleteAsync(IEnumerable<ChatMessage> messages, JsonArray? tools, Action<string>? onText,
                                                   CancellationToken ct, double temperature = 0.4, int maxTokens = 1024)
    {
        var body = BuildBody(messages, tools, temperature, maxTokens);
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Headers.UserAgent.ParseAdd("Universal-FrameFX/1.4.1-beta.11");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw LarkquillApiException.FromResponse((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var acc = new StreamAccumulator();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal) && line.Contains("\"error\"", StringComparison.Ordinal) && !line.Contains("\"choices\"", StringComparison.Ordinal))
                throw LarkquillApiException.FromResponse(502, line[5..].Trim());
            string? delta = acc.FeedLine(line);
            if (delta == null) break;
            if (delta.Length > 0) onText?.Invoke(delta);
        }
        return acc.Finish();
    }
}

/// <summary>Stores the user's API key. The Windows UI uses DPAPI (current user); tests use memory.</summary>
public interface ILarkquillKeyStore
{
    string? Load();
    void Save(string key);
    void Clear();
}

public sealed class MemoryKeyStore : ILarkquillKeyStore
{
    string? _k;
    public string? Load() => _k;
    public void Save(string key) => _k = key;
    public void Clear() => _k = null;
}
