using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalFrameFX.Agent;

/// <summary>What the Confirm dialog shows. Lines are exact: the program path, the setting and its old → new value, etc.</summary>
public sealed record ConfirmRequest(string Title, IReadOnlyList<string> Lines, string ConfirmLabel = "Confirm");

/// <summary>Implemented by the UI (ConfirmDialog). Must return false on close/timeout. Never auto-approves.</summary>
public interface IConfirmer
{
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
}

/// <summary>Lets a tool ask the user to pick a file (launch_app path picker). Returns null if cancelled.</summary>
public interface IUserPicker
{
    Task<string?> PickExecutableAsync(string reason, CancellationToken ct);
}

public sealed record ToolResult(bool Ok, string Text)
{
    public static ToolResult Success(string t) => new(true, t);
    public static ToolResult Fail(string t) => new(false, "ERROR: " + t);
}

/// <summary>A tool the model can call. Side-effecting tools return a ConfirmRequest from <see cref="Describe"/>;
/// the registry shows it and only then calls <see cref="ExecuteAsync"/>. Tools can't skip that step.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON Schema for the arguments object.</summary>
    JsonObject Parameters { get; }
    /// <summary>Validate args and return what confirmation to ask for, or null if the tool is read-only.
    /// Throw ArgumentException for bad args.</summary>
    Task<ConfirmRequest?> DescribeAsync(JsonElement args, CancellationToken ct);
    Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct);
}

public sealed class ToolRegistry
{
    readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);
    readonly IConfirmer _confirmer;
    public int MaxResultChars { get; init; } = 12_000;

    public ToolRegistry(IConfirmer confirmer) => _confirmer = confirmer;

    public ToolRegistry Add(IAgentTool t) { _tools[t.Name] = t; return this; }
    public IReadOnlyCollection<IAgentTool> Tools => _tools.Values;

    public JsonArray ToOpenAiTools() => new(_tools.Values.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Parameters.DeepClone(),
        },
    }).ToArray());

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ToolResult.Fail($"unknown tool '{call.Name}'. Available: {string.Join(", ", _tools.Keys)}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
        catch (JsonException e) { return ToolResult.Fail("arguments are not valid JSON: " + e.Message); }
        using (doc)
        {
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return ToolResult.Fail("arguments must be a JSON object");
            if (args.TryGetProperty("_raw", out _)) return ToolResult.Fail("arguments could not be parsed; send valid JSON");
            try
            {
                var confirm = await tool.DescribeAsync(args, ct);
                if (confirm != null && !await _confirmer.ConfirmAsync(confirm, ct))
                    return ToolResult.Fail("the user declined. Do not retry this action unless they ask again.");
                var r = await tool.ExecuteAsync(args, ct);
                return r.Text.Length > MaxResultChars ? r with { Text = r.Text[..MaxResultChars] + "\n…[truncated]" } : r;
            }
            catch (ArgumentException e) { return ToolResult.Fail(e.Message); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return ToolResult.Fail($"{call.Name} failed: {e.Message}"); }
        }
    }
}

/// <summary>Small helpers for reading tool args.</summary>
public static class Args
{
    public static string Str(JsonElement a, string name, bool required = true)
    {
        if (a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            return v.GetString()!.Trim();
        if (required) throw new ArgumentException($"missing string argument '{name}'");
        return "";
    }
    public static int? Int(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    public static JsonObject Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var p = new JsonObject();
        foreach (var x in props) p[x.Name] = new JsonObject { ["type"] = x.Type, ["description"] = x.Desc };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = new JsonArray(props.Where(x => x.Required).Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray()),
        };
    }
}
