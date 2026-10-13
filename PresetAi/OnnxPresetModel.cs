using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace UniversalFrameFX.PresetAi;

public enum PresetModelKind { HybridR3, G14 }

public sealed class OnnxPresetModel : IDisposable
{
    readonly InferenceSession _session;
    public PresetModelKind Kind { get; }

    OnnxPresetModel(InferenceSession session, PresetModelKind kind)
    {
        _session = session;
        Kind = kind;
    }

    public static OnnxPresetModel? TryLoad(PresetModelKind kind, string? modelsDir = null)
    {
        modelsDir ??= Path.Combine(AppContext.BaseDirectory, "Models");
        string file = kind switch
        {
            PresetModelKind.G14 => "preset_g14.onnx",
            _ => "preset_r3.onnx",
        };
        var path = Path.Combine(modelsDir, file);
        if (!File.Exists(path)) return null;
        var opts = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        opts.AddSessionConfigEntry("session.load_model_format", "ONNX");
        try
        {
            return new OnnxPresetModel(new InferenceSession(path, opts), kind);
        }
        catch
        {
            return null;
        }
    }

    public string Generate(string prompt, int maxTokens = 160)
    {
        var ids = GptTokenizer.Encode(GptTokenizer.PreparePrompt(prompt) + "\t").ToList();
        for (int step = 0; step < maxTokens; step++)
        {
            int start = Math.Max(0, ids.Count - GptTokenizer.MaxContext);
            var window = ids.GetRange(start, ids.Count - start);
            int next = ArgmaxLastLogit(window);
            if (next == GptTokenizer.NewlineId) break;
            ids.Add(next);
            if (ids.Count >= GptTokenizer.MaxContext) break;
        }
        var text = GptTokenizer.Decode(ids);
        var tab = text.IndexOf('\t');
        return tab >= 0 && tab + 1 < text.Length ? text[(tab + 1)..].Split('\n')[0] : "";
    }

    int ArgmaxLastLogit(IReadOnlyList<int> window)
    {
        var input = new DenseTensor<long>(new[] { 1, window.Count });
        for (int i = 0; i < window.Count; i++) input[0, i] = window[i];
        using var results = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", input),
        });
        var tensor = results.First().AsTensor<float>();
        int vocab = tensor.Dimensions[^1];
        int last = window.Count - 1;
        int best = 0;
        float bestV = float.NegativeInfinity;
        for (int i = 0; i < vocab; i++)
        {
            float v = tensor[0, last, i];
            if (v > bestV) { bestV = v; best = i; }
        }
        return best;
    }

    public void Dispose() => _session.Dispose();
}
