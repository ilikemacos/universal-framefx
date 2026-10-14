namespace UniversalFrameFX.Agent;

/// <summary>KV-cache element type passed to llama-server (--cache-type-k / --cache-type-v).</summary>
public enum KvCacheType { F16, Q8_0, Q4_0 }

/// <summary>Result of sizing: everything llama-server needs to know about context and offload.</summary>
public sealed record ContextPlan(
    int ContextTokens,
    KvCacheType KvType,
    int GpuLayers,           // -ngl; ModelSpec.Layers (+1 for output) = full offload
    bool UseYarn,            // only when ContextTokens > native 32k
    double YarnFactor,       // ContextTokens / 32768, rounded up to 2 or 4
    long EstimatedVramBytes,
    string Reason)
{
    public bool FullOffload(ModelSpec m) => GpuLayers >= m.Layers + 1;

    /// <summary>Command-line arguments for these settings (model path / port are added by LlamaServerHost).</summary>
    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>
        {
            "-c", ContextTokens.ToString(),
            "-ngl", GpuLayers.ToString(),
            "--cache-type-k", KvName(KvType),
            "--cache-type-v", KvName(KvType),
        };
        // A quantized V cache needs flash attention in llama.cpp.
        if (KvType != KvCacheType.F16) { a.Add("-fa"); a.Add("on"); }
        if (UseYarn)
        {
            a.AddRange(["--rope-scaling", "yarn", "--rope-scale", YarnFactor.ToString("0"),
                        "--yarn-orig-ctx", "32768"]);
        }
        return a;
    }

    public static string KvName(KvCacheType t) => t switch
    {
        KvCacheType.F16 => "f16",
        KvCacheType.Q8_0 => "q8_0",
        _ => "q4_0",
    };
}

/// <summary>Architecture numbers for the bundled model (3B-class, GQA: 36 layers, 2 KV heads x 128).
/// Defaults = the shipped Q4_K_M GGUF (~1.93 GB). Native context 32k; YaRN extends it up to 128k.</summary>
public sealed record ModelSpec(
    int Layers = 36,
    int KvHeads = 2,
    int HeadDim = 128,
    long WeightBytes = 1_929_903_264,   // Q4_K_M GGUF file size, ~1.93 GB
    int NativeContext = 32_768,
    int MaxContext = 131_072,
    double EmbedFraction = 0.10)        // tied embedding/output share of the weights
{
    /// <summary>Bytes per element for each cache type (ggml block sizes: q8_0 = 34 B / 32, q4_0 = 18 B / 32).</summary>
    public static double BytesPerElem(KvCacheType t) => t switch
    {
        KvCacheType.F16 => 2.0,
        KvCacheType.Q8_0 => 34.0 / 32.0,
        _ => 18.0 / 32.0,
    };

    /// <summary>K+V bytes per token for one layer.</summary>
    public double KvBytesPerTokenPerLayer(KvCacheType t) => 2.0 * KvHeads * HeadDim * BytesPerElem(t);

    public double WeightBytesPerLayer => WeightBytes * (1 - EmbedFraction) / Layers;
}

/// <summary>
/// Picks the largest context the GPU's VRAM allows (≤128k) using a fixed ladder.
/// Pure math, so it is unit-tested. LlamaServerHost walks <see cref="Ladder"/> downward if a start fails (OOM).
/// </summary>
public static class ContextSizer
{
    public static readonly int[] ContextSteps =
        [131_072, 98_304, 65_536, 49_152, 32_768, 24_576, 16_384, 12_288, 8_192, 4_096];

    /// <summary>Kept free for the desktop, the game, and FrameFX itself.</summary>
    public const long DefaultReserveBytes = 900L * 1024 * 1024;
    /// <summary>Use this reserve when a game + FrameFX are already running on the same GPU.</summary>
    public const long GamingReserveBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>llama.cpp compute buffer: fixed part + a part that grows with context (flash attention on).</summary>
    public static long ComputeBufferBytes(int ctx) => 350L * 1024 * 1024 + (long)ctx * 3_000;

    public static long EstimateVram(ModelSpec m, int ctx, KvCacheType kv, int gpuLayers)
    {
        int repeating = Math.Min(gpuLayers, m.Layers);
        bool output = gpuLayers > m.Layers;
        double weights = repeating * m.WeightBytesPerLayer + (output ? m.WeightBytes * m.EmbedFraction : 0);
        double kvBytes = (double)ctx * repeating * m.KvBytesPerTokenPerLayer(kv);
        return (long)(weights + kvBytes) + ComputeBufferBytes(ctx);
    }

    /// <summary>Best plan for the VRAM budget. Preference: full offload with the biggest context
    /// (q8 cache first, then q4), and only below 16k does it trade GPU layers for context.</summary>
    public static ContextPlan Pick(long vramBytes, ModelSpec? spec = null, long reserveBytes = DefaultReserveBytes,
                                   int maxContext = 131_072)
    {
        var m = spec ?? new ModelSpec();
        long budget = vramBytes - reserveBytes;
        int full = m.Layers + 1;
        maxContext = Math.Min(maxContext, m.MaxContext);

        // 1) Full offload, largest context, q8 then q4.
        foreach (int ctx in ContextSteps.Where(c => c <= maxContext))
            foreach (var kv in new[] { KvCacheType.Q8_0, KvCacheType.Q4_0 })
            {
                if (ctx < 16_384) goto partial;
                long need = EstimateVram(m, ctx, kv, full);
                if (need <= budget) return Make(m, ctx, kv, full, need, "full GPU offload");
            }

        partial:
        // 2) Partial offload: keep ≥16k (else 8k, 4k) and put as many layers on the GPU as fit.
        foreach (int ctx in new[] { 16_384, 12_288, 8_192, 4_096 }.Where(c => c <= maxContext))
            foreach (var kv in new[] { KvCacheType.Q8_0, KvCacheType.Q4_0 })
            {
                for (int ngl = full; ngl >= 8; ngl--)
                {
                    long need = EstimateVram(m, ctx, kv, ngl);
                    if (need <= budget)
                        return Make(m, ctx, kv, ngl, need,
                            ngl == full ? "full GPU offload" : $"partial offload ({ngl}/{full} layers on GPU, rest on CPU)");
                }
            }

        // 3) Tiny GPU / none: CPU only, 4k context.
        return Make(m, 4_096, KvCacheType.Q8_0, 0, 0, "CPU only (not enough VRAM)");
    }

    /// <summary>Plans to try in order after <paramref name="first"/> fails to start (OOM): smaller context, then fewer layers.</summary>
    public static IEnumerable<ContextPlan> Ladder(ContextPlan first, ModelSpec? spec = null)
    {
        var m = spec ?? new ModelSpec();
        yield return first;
        foreach (int ctx in ContextSteps.Where(c => c < first.ContextTokens))
            yield return Make(m, ctx, KvCacheType.Q4_0, first.GpuLayers, EstimateVram(m, ctx, KvCacheType.Q4_0, first.GpuLayers), "fallback: smaller context");
        for (int ngl = first.GpuLayers - 4; ngl >= 0; ngl -= 4)
            yield return Make(m, 4_096, KvCacheType.Q4_0, ngl, EstimateVram(m, 4_096, KvCacheType.Q4_0, ngl), "fallback: fewer GPU layers");
        // Last resort for drivers/builds without flash attention on Vulkan: f16 cache (no -fa), small context.
        foreach (int ctx in new[] { 8_192, 4_096 })
            yield return Make(m, ctx, KvCacheType.F16, first.GpuLayers, EstimateVram(m, ctx, KvCacheType.F16, first.GpuLayers), "fallback: f16 cache, no flash attention");
    }

    static ContextPlan Make(ModelSpec m, int ctx, KvCacheType kv, int ngl, long need, string why)
    {
        bool yarn = ctx > m.NativeContext;
        double factor = yarn ? (ctx <= 2 * m.NativeContext ? 2 : 4) : 1;
        return new ContextPlan(ctx, kv, ngl, yarn, factor, need, why);
    }
}
