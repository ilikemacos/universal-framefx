using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalFrameFX.PresetAi;
using Xunit;

namespace UniversalFrameFX.PresetAi.Tests;

public class OnnxParityTests
{
    public static IEnumerable<object[]> GoldenRows()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "onnx_golden.json");
        if (!File.Exists(path)) yield break;
        var rows = JsonSerializer.Deserialize<List<GoldenRow>>(File.ReadAllText(path), JsonOpts) ?? [];
        foreach (var r in rows)
            yield return new object[] { r.Model, r.Prompt, r.Line };
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    sealed record GoldenRow(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("line")] string Line);

    [Theory]
    [MemberData(nameof(GoldenRows))]
    public void Greedy_decode_matches_python(string modelTag, string prompt, string expected)
    {
        var kind = modelTag == "g14" ? PresetModelKind.G14 : PresetModelKind.HybridR3;
        using var m = OnnxPresetModel.TryLoad(kind);
        if (m == null) return;
        var line = m.Generate(prompt);
        Assert.Equal(expected, line);
    }
}
