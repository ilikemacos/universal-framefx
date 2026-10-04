using Vortice.DXGI;

namespace UniversalFrameFX;

// Settings types shared between the app shell and the closed engine. These are plain option lists shown in the UI;
// the engine code that acts on them is not part of this repository. Enum order is persisted by index: append only.

public enum Backend { Temporal, Spatial, Bilinear, Fsr1, Fsr2, Fsr3, Fsr4, XeSS }
public enum GpuChoice { Auto, Integrated, Dedicated }
public enum MotionPreference { Auto, Nvof, D3D12, Software }
public enum FgKind { FrameFX, Fsr3 }
public enum OutputMode { Overlay, Window, Fullscreen }
/// <summary>Output resolution choice (index order is persisted).</summary>
public enum OutputRes { Auto, P1440, P2160, Source, P1080 }

public static class BackendNames
{
    public static string Long(Backend b) => b switch
    {
        Backend.Temporal => "CSR 1.3",
        Backend.Spatial => "CSR 1.2",
        Backend.Fsr1 => "AMD FSR 1",
        Backend.Fsr2 => "AMD FSR 2",
        Backend.Fsr3 => "AMD FSR 3",
        Backend.Fsr4 => "AMD FSR 4",
        Backend.XeSS => "Intel XeSS",
        _ => "Bilinear",
    };
    public static string Short(Backend b) => Long(b);

    /// <summary>Name -> option. Accepts current and older names so saved settings and shortcuts keep working.</summary>
    public static Backend Parse(string? name)
    {
        var n = (name ?? "").ToLowerInvariant().Replace("-", "").Replace(" ", "").Replace("_", "");
        return n switch
        {
            "spatial" or "framefxspatial" or "csr1.2" or "csr12" => Backend.Spatial,
            "bilinear" => Backend.Bilinear,
            "fsr1" or "amdfsr1" => Backend.Fsr1,
            "fsr2" or "amdfsr2" => Backend.Fsr2,
            "fsr3" or "amdfsr3" => Backend.Fsr3,
            "fsr4" or "amdfsr4" => Backend.Fsr4,
            "xess" or "intelxess" => Backend.XeSS,
            _ => Backend.Temporal,
        };
    }
}

public static class Backends
{
    /// <summary>Runs in a third-party runtime shipped with the app (AMD FidelityFX / Intel XeSS).</summary>
    public static bool IsVendor(Backend b) => b is Backend.Fsr2 or Backend.Fsr3 or Backend.Fsr4 or Backend.XeSS;
}

public static class OutputModeNames
{
    public static string Long(OutputMode m) => m switch
    {
        OutputMode.Overlay => "Overlay on the source window (click-through)",
        OutputMode.Window => "Separate window",
        _ => "Separate fullscreen window",
    };
}

public static class OutputResNames
{
    public static string Long(OutputRes r) => r switch
    {
        OutputRes.P1440 => "1440p (2560 × 1440)",
        OutputRes.P1080 => "1080p (1920 × 1080)",
        OutputRes.P2160 => "4K (3840 × 2160)",
        OutputRes.Source => "Source window size (1:1)",
        _ => "Auto (overlay: window size · separate output: fit monitor)",
    };
    public static (int w, int h) Size(OutputRes r) => r switch { OutputRes.P1080 => (1920, 1080), OutputRes.P1440 => (2560, 1440), OutputRes.P2160 => (3840, 2160), _ => (0, 0) };
}

/// <summary>Frame generation multipliers offered in the UI (output frames per game frame).</summary>
public static class FgMul
{
    public static readonly int[] Allowed = { 2, 3, 4, 8 };
    /// <summary>UFX_FG_ADVANCED=1 lists 8× in the multiplier menu.</summary>
    public static readonly bool Advanced = Environment.GetEnvironmentVariable("UFX_FG_ADVANCED") == "1";
    public static int Clamp(int n) => n is 3 or 4 or 8 ? n : 2;
    public static int IndexOf(int n)
    {
        int i = Array.IndexOf(Allowed, Clamp(n));
        return i < 0 ? 0 : i;
    }
}

/// <summary>Which third-party options can run on this PC. The real check lives in the engine; the public build
/// reports every third-party option as unavailable.</summary>
public static class VendorSupport
{
    public sealed record Info(bool Ok, string Why, string Detail);
    static readonly Info NotIncluded = new(false, "engine not included in this build", "");
    public static Info Get(Backend b) => !Backends.IsVendor(b) && b != Backend.Fsr1 ? new(true, "", "") : NotIncluded;
    public static Info FsrFrameGen => NotIncluded;
    public static bool Probed => true;
    public static void Probe(Gpu g) { }
}

public static class LatencyBudget
{
    /// <summary>Default target for FrameFX's added response time per frame (ms). UFX_LATENCY_BUDGET overrides.</summary>
    public static double DefaultMs = double.TryParse(Environment.GetEnvironmentVariable("UFX_LATENCY_BUDGET"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1.5;
}

public static class Ssgi
{
    public static readonly string[] PresetNames = { "Auto", "GTX 1050 Ti (1080p)", "GTX 980 Ti (1440p)" };
}

public static class MotionEngines
{
    public static string PrefName(MotionPreference p) => p switch
    {
        MotionPreference.Nvof => "NVIDIA Optical Flow",
        MotionPreference.D3D12 => "D3D12 Video Motion Estimation",
        MotionPreference.Software => "Software only",
        _ => "Auto (hardware when available)",
    };
}

/// <summary>The user's settings for a session, read by the engine.</summary>
public sealed class SessionSettings
{
    public volatile FgKind FgKind = FgKind.FrameFX;
    public volatile Backend Backend = Backend.Temporal;
    public volatile MotionPreference Motion = MotionPreference.Auto;
    public volatile float Sharpness = 0.25f;
    public volatile bool FrameGen;
    public volatile int FgMultiplier = 4;
    public volatile bool Ssgi;
    public volatile int SsgiPreset;
    public volatile bool LatencyBudget = true;
    public volatile bool Hud = true;
    public volatile bool Performance = true;
    public volatile bool Competitive;
}

/// <summary>GPU device owned by the engine. The public build can list adapters (read-only DXGI query) but cannot
/// create the engine's device.</summary>
public sealed class Gpu : IDisposable
{
    public static readonly object Lock = new();
    /// <summary>Settings → Advanced → Processing GPU, or --gpu auto|igpu|dgpu.</summary>
    public static GpuChoice Choice = GpuChoice.Auto;

    public Vortice.Direct3D11.ID3D11Device Device => throw new EngineNotIncludedException();
    public string AdapterName { get; } = "";
    public string KindName { get; } = "";
    public GpuChoice ActiveChoice { get; }
    public double LastGpuMs => 0;

    public Gpu() => throw new EngineNotIncludedException();

    /// <summary>Adapter Windows returns for a preference (name, or null), without creating a device.</summary>
    public static string? AdapterNameFor(GpuChoice c)
    {
        try
        {
            using var f6 = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            var gp = c == GpuChoice.Integrated ? GpuPreference.MinimumPower : GpuPreference.HighPerformance;
            if (f6.EnumAdapterByGpuPreference(0, gp, out IDXGIAdapter1? ad).Success && ad != null) using (ad) return ad.Description1.Description;
        }
        catch { }
        return null;
    }

    public static CompiledProgram CompileSource(string src, string entry, string profile, string fileName) => throw new EngineNotIncludedException();

    public void Dispose() { }
}

/// <summary>A compiled GPU program (placeholder type for ShaderCache).</summary>
public sealed class CompiledProgram : IDisposable
{
    public Span<byte> AsSpan() => Span<byte>.Empty;
    public void Dispose() { }
}

/// <summary>Per-app engine state (placeholder).</summary>
public sealed class FramePipeline : IDisposable
{
    public FramePipeline(Gpu gpu) => throw new EngineNotIncludedException();
    public string MotionSource => "not included";
    public void ClearVendorFailures() { }
    public void Reset() { }
    public void Dispose() { }
}
