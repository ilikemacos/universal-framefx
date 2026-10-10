using Vortice.DXGI;

namespace UniversalFrameFX;

public enum Backend { Temporal, Spatial, Bilinear, Fsr1, Fsr2, Fsr3, Fsr4, XeSS, Csr20 }
public enum GpuChoice { Auto, Integrated, Dedicated }
public enum MotionPreference { Auto, Nvof, D3D12, Software }
public enum FgKind { FrameFX, Fsr3 }
public enum FgSelect { Off, Csr13, Csr20, Fsr3 }
public enum OutputMode { Overlay, Window, Fullscreen }
public enum OutputRes { Auto, P1440, P2160, Source, P1080 }

public static class BackendNames
{
    public static string Long(Backend b) => b switch
    {
        Backend.Temporal => "CSR 1.3",
        Backend.Csr20 => "CSR 2.0",
        Backend.Spatial => "CSR 1.2",
        Backend.Fsr1 => "AMD FSR 1",
        Backend.Fsr2 => "AMD FSR 2",
        Backend.Fsr3 => "AMD FSR 3",
        Backend.Fsr4 => "AMD FSR 4",
        Backend.XeSS => "Intel XeSS",
        _ => "Bilinear",
    };
    public static string Short(Backend b) => Long(b);

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
            "csr2.0" or "csr20" => Backend.Csr20,
            _ => Backend.Temporal,
        };
    }
}

public static class Backends
{
    public static bool IsVendor(Backend b) => b is Backend.Fsr2 or Backend.Fsr3 or Backend.Fsr4 or Backend.XeSS;
    public static readonly Backend[] MenuOrder = { Backend.Temporal, Backend.Csr20, Backend.Spatial, Backend.Fsr1, Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS, Backend.Bilinear };
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

public static class FgMul
{
    public static readonly int[] Allowed = { 2, 3, 4, 8 };
    public static readonly int[] AllowedV2 = { 2, 3, 4, 5, 6, 8, 10, 20 };
    public static readonly bool Advanced = Environment.GetEnvironmentVariable("UFX_FG_ADVANCED") == "1";
    public static int Clamp(int n) => n is 0 or 3 or 4 or 8 ? n : 2;
    public static int ClampV2(int n) => n == 0 || Array.IndexOf(AllowedV2, n) >= 0 ? n : 2;
    public static int IndexOf(int n)
    {
        int i = Array.IndexOf(Allowed, n is 3 or 4 or 8 ? n : 2);
        return i < 0 ? 0 : i;
    }
    public static int MenuIndex(int n) => n == 0 ? 0 : IndexOf(n) + 1;
    public static int FromMenu(int i) => i <= 0 ? 0 : Allowed[Math.Clamp(i - 1, 0, Allowed.Length - 1)];
    public static bool ShowAdvanced8(int current) => Advanced || current == 8 || Clamp(current) == 8;
    public static int[] MenuValues(bool csr20, int current)
    {
        if (csr20) return new[] { 0, 2, 3, 4, 5, 6, 8, 10, 20 };
        return ShowAdvanced8(current) ? new[] { 0, 2, 3, 4, 8 } : new[] { 0, 2, 3, 4 };
    }
    public static int MenuIndexFor(int n, bool csr20, int current)
    {
        var v = MenuValues(csr20, current);
        int i = Array.IndexOf(v, n);
        if (i >= 0) return i;
        int fall = n == 0 ? 0 : csr20 ? ClampV2(n) : Clamp(n);
        i = Array.IndexOf(v, fall);
        return i < 0 ? 0 : i;
    }
    public static int FromMenuFor(int i, bool csr20, int current)
    {
        var v = MenuValues(csr20, current);
        return v[Math.Clamp(i, 0, v.Length - 1)];
    }
    public static int DownV2(int n)
    {
        if (n > 10) return 10;
        if (n > 8) return 8;
        if (n > 6) return 6;
        if (n > 5) return 5;
        if (n > 4) return 4;
        if (n > 3) return 3;
        if (n > 2) return 2;
        return 0;
    }
    public static int UpV2(int n, int cap)
    {
        int next = n < 2 ? 2 : n < 3 ? 3 : n < 4 ? 4 : n < 5 ? 5 : n < 6 ? 6 : n < 8 ? 8 : n < 10 ? 10 : 20;
        return Math.Min(next, Math.Max(0, cap));
    }
}

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
    public static double DefaultMs = double.TryParse(Environment.GetEnvironmentVariable("UFX_LATENCY_BUDGET"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1.5;
}

public static class Ssgi
{
    public static readonly string[] PresetNames = { "Auto", "GTX 1050 Ti (1080p)", "GTX 980 Ti (1440p)" };
    public const bool TemporalDefault = false;

    public static bool TemporalEnabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSGI_TEMPORAL");
        if (e == "0") return false;
        if (e == "1") return true;
        return setting;
    }
}

public static class Ssrt
{
    public const bool TemporalDefault = false;
    public static readonly string[] PresetNames = { "Auto", "GTX 1050 Ti", "GTX 980 Ti" };

    public static bool Enabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSRT");
        if (e == "0") return false;
        if (e == "1") return true;
        return setting;
    }

    public static bool TemporalEnabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSRT_TEMPORAL");
        if (e == "0") return false;
        if (e == "1") return true;
        _ = setting;
        return TemporalDefault;
    }

    public static int Resolve(int preset, int outW, int outH, string? adapter)
    {
        if (preset is 1 or 2) return preset;
        bool le1080 = outW <= 1920 && outH <= 1080;
        if (le1080 || !Above1050Ti(adapter)) return 1;
        return 2;
    }

    public static bool Above1050Ti(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.ToUpperInvariant();
        string[] low = { "1050", "1030", "960", "950", "750", "740", "730", "710", " MX", "GT 1", "UHD", "IRIS", "VEGA", "HD GRAPHICS" };
        foreach (var s in low)
            if (n.Contains(s, StringComparison.Ordinal)) return false;
        string[] high = { "RTX", "TITAN", "1080", "1070", "1060", "1660", "1650", "980", "970",
            "2060", "2070", "2080", "3050", "3060", "3070", "3080", "3090", "4050", "4060", "4070", "4080", "4090",
            "RX 57", "RX 58", "RX 59", "RX 6", "RX 7", "RX 9", "ARC" };
        foreach (var s in high)
            if (n.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string PresetLabel(int id) => id == 2 ? "GTX 980 Ti" : "GTX 1050 Ti";
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
    public volatile bool SsgiTemporal = global::UniversalFrameFX.Ssgi.TemporalDefault;
    public volatile bool Ssrt;
    public volatile int SsrtPreset;
    public volatile bool SsrtTemporal = global::UniversalFrameFX.Ssrt.TemporalDefault;
    public volatile bool CompareOff;
    public volatile bool LatencyBudget = true;
    public volatile bool Hud = true;
    public volatile bool Performance = true;
    public volatile bool Competitive;
    public volatile bool FgV2 = Environment.GetEnvironmentVariable("UFX_FG_V2") == "1";
    public volatile bool FgV2Async = Environment.GetEnvironmentVariable("UFX_FG_V2_ASYNC") == "1";
    public volatile bool FgV2Vsync = Environment.GetEnvironmentVariable("UFX_FG_V2_VSYNC") == "1";
    public volatile bool FgV2LowResGen = Environment.GetEnvironmentVariable("UFX_FG_V2_LOWRES") == "1";
    public volatile bool FgV2Extrap;
    public volatile bool FgV2Cursor;
    public volatile int FpsCap;
    public volatile bool FixedPacing = Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
}

public sealed class Gpu : IDisposable
{
    public static readonly object Lock = new();
    public static GpuChoice Choice = GpuChoice.Auto;

    public Vortice.Direct3D11.ID3D11Device Device => throw new EngineNotIncludedException();
    public string AdapterName { get; } = "";
    public string KindName { get; } = "";
    public GpuChoice ActiveChoice { get; }
    public double LastGpuMs => 0;

    public Gpu() => throw new EngineNotIncludedException();

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

public sealed class CompiledProgram : IDisposable
{
    public Span<byte> AsSpan() => Span<byte>.Empty;
    public void Dispose() { }
}

public sealed class FramePipeline : IDisposable
{
    public FramePipeline(Gpu gpu) => throw new EngineNotIncludedException();
    public string MotionSource => "not included";
    public bool FgV2Active => false;
    public string Csr20Label => "not included";
    public void ClearVendorFailures() { }
    public void Reset() { }
    public void Dispose() { }
}
