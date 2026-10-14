using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UniversalFrameFX.Agent;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One OpenAI-style chat message. Content may be null on assistant tool-call turns.</summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; set; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }

    public static ChatMessage System(string s) => new() { Role = "system", Content = s };
    public static ChatMessage User(string s) => new() { Role = "user", Content = s };
    public static ChatMessage Assistant(string? s, List<ToolCall>? calls = null) => new() { Role = "assistant", Content = s, ToolCalls = calls };
    public static ChatMessage Tool(string id, string name, string s) => new() { Role = "tool", ToolCallId = id, Name = name, Content = s };

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["role"] = Role, ["content"] = Content };
        if (ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        if (ToolCallId != null) o["tool_call_id"] = ToolCallId;
        if (Name != null && Role == "tool") o["name"] = Name;
        return o;
    }
}

public sealed record Usage(int PromptTokens, int CompletionTokens);

/// <summary>Finished assistant turn (after streaming).</summary>
public sealed record AssistantTurn(string Content, List<ToolCall> ToolCalls, string? FinishReason, Usage? Usage);

/// <summary>
/// Accumulates OpenAI streaming chunks ("data: {...}" lines). Tool-call arguments arrive in fragments keyed
/// by index. If the server leaves Qwen-style &lt;tool_call&gt;{json}&lt;/tool_call&gt; text in content
/// (older llama.cpp, or --jinja missing), those are parsed out as a fallback.
/// </summary>
public sealed partial class StreamAccumulator
{
    readonly StringBuilder _content = new();
    readonly SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)> _calls = new();
    string? _finish;
    Usage? _usage;

    /// <summary>Feed one SSE line. Returns the visible text delta (may be empty). Returns null on [DONE].</summary>
    public string? FeedLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return "";
        string data = line[5..].Trim();
        if (data == "[DONE]") return null;
        if (data.Length == 0) return "";
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            _usage = new Usage(GetInt(u, "prompt_tokens"), GetInt(u, "completion_tokens"));
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return "";
        var ch = choices[0];
        if (ch.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) _finish = fr.GetString();
        if (!ch.TryGetProperty("delta", out var delta)) return "";
        string text = "";
        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            text = c.GetString() ?? "";
            _content.Append(text);
        }
        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            foreach (var tc in tcs.EnumerateArray())
            {
                int idx = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : _calls.Count;
                if (!_calls.TryGetValue(idx, out var cur)) cur = (null, null, new StringBuilder());
                if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) cur.Id = id.GetString();
                if (tc.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) cur.Name = (cur.Name ?? "") + n.GetString();
                    if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) cur.Args.Append(a.GetString());
                }
                _calls[idx] = cur;
            }
        return text;
    }

    public AssistantTurn Finish()
    {
        var calls = new List<ToolCall>();
        int k = 0;
        foreach (var (_, c) in _calls)
            if (!string.IsNullOrEmpty(c.Name))
                calls.Add(new ToolCall(c.Id ?? $"call_{k++}", c.Name!, NormalizeArgs(c.Args.ToString())));
        string content = _content.ToString();
        if (calls.Count == 0)
        {
            var (clean, parsed) = ToolCallParser.ExtractInline(content);
            if (parsed.Count > 0) { content = clean; calls = parsed; }
        }
        return new AssistantTurn(content.Trim(), calls, _finish, _usage);
    }

    static int GetInt(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.TryGetInt32(out int i) ? i : 0;

    internal static string NormalizeArgs(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "{}";
        try { using var _ = JsonDocument.Parse(s); return s; }
        catch (JsonException) { return JsonSerializer.Serialize(new { _raw = s }); } // tool gets a clear "bad arguments" error
    }
}

/// <summary>Fallback parser for &lt;tool_call&gt; blocks written into the text.</summary>
public static partial class ToolCallParser
{
    [GeneratedRegex(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex Block();

    public static (string Clean, List<ToolCall> Calls) ExtractInline(string content)
    {
        var calls = new List<ToolCall>();
        int k = 0;
        string clean = Block().Replace(content, m =>
        {
            try
            {
                var node = JsonNode.Parse(m.Groups[1].Value) as JsonObject;
                string? name = node?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) return m.Value;
                var args = node!["arguments"];
                string argJson = args switch
                {
                    null => "{}",
                    JsonValue v when v.TryGetValue(out string? s) => StreamAccumulator.NormalizeArgs(s ?? ""),
                    _ => args.ToJsonString(),
                };
                calls.Add(new ToolCall($"inline_{k++}", name, argJson));
                return "";
            }
            catch (JsonException) { return m.Value; }
        });
        return (clean.Trim(), calls);
    }
}

/// <summary>Minimal streaming client for llama-server's /v1/chat/completions.</summary>
public sealed class LlamaChatClient
{
    readonly HttpClient _http;
    public LlamaChatClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.BaseAddress = baseUri;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<AssistantTurn> CompleteAsync(IEnumerable<ChatMessage> messages, JsonArray? tools,
        Action<string>? onText, CancellationToken ct, double temperature = 0.4, int maxTokens = 1024)
    {
        var body = new JsonObject
        {
            ["model"] = "framefx-assistant",
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.ToJson()).ToArray()),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        if (tools is { Count: > 0 }) { body["tools"] = tools.DeepClone(); body["tool_choice"] = "auto"; }

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"assistant server error {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var acc = new StreamAccumulator();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            string? delta = acc.FeedLine(line);
            if (delta == null) break;
            if (delta.Length > 0) onText?.Invoke(delta);
        }
        return acc.Finish();
    }
}
