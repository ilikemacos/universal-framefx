using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

/// <summary>Experimental lighting on the captured frame. Off unless the user turns it on.
/// When it is on it takes the place of SSGI. The native library is the same ssgi.dll; if that build
/// has no entry point, or init fails, this stays off and reports a short status.</summary>
public sealed class Ssrt : IDisposable
{
    /// <summary>Off by default: one of 32 seeds exceeded the ghosting limit.
    /// UFX_SSRT_TEMPORAL=0/1 overrides; a saved true does not turn it on.</summary>
    public const bool TemporalDefault = false;
    public static readonly string[] PresetNames = { "Auto", "GTX 1050 Ti", "GTX 980 Ti" };
    /// <summary>All lighting features. A zero flag word is the previous lighting path.</summary>
    const uint AllFeatures = 15;

    const string Dll = "ssgi.dll";
    [StructLayout(LayoutKind.Sequential)] struct Bytecode { public IntPtr Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] struct SsgiShaders { public Bytecode Down, March, Filter, Shift, Vs, Ps, Pan; }
    [StructLayout(LayoutKind.Sequential)] struct SsrtShaders
    {
        public Bytecode Depth, DepthBlur, March, LightDir, Reflect, Contact, Pack, Ps, Cut, Accum;
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr ufx_ssgi_create(IntPtr dev, ref SsgiShaders sh, uint w, uint h, uint lightDivisor);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssgi_resize(IntPtr p, uint w, uint h, uint lightDivisor);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssrt_init(IntPtr p, ref SsrtShaders sh);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssrt_execute(IntPtr p, IntPtr ctx, IntPtr game, IntPtr rtv, uint preset, uint flags, IntPtr motion, IntPtr pan, int temporal);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ufx_ssgi_destroy(IntPtr p);

    /// <summary>Saved toggle, unless UFX_SSRT is 0 or 1.</summary>
    public static bool Enabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSRT");
        if (e == "0") return false;
        if (e == "1") return true;
        return setting;
    }

    /// <summary>UFX_SSRT_TEMPORAL if it is 0 or 1; otherwise TemporalDefault. A saved true does not turn it on.</summary>
    public static bool TemporalEnabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSRT_TEMPORAL");
        if (e == "0") return false;
        if (e == "1") return true;
        _ = setting;
        return TemporalDefault;
    }

    /// <summary>1 = GTX 1050 Ti settings, 2 = GTX 980 Ti settings. Auto uses the 1050 Ti settings at 1080p and below,
    /// and on GPUs in that class or slower. A manual 1 or 2 is kept.</summary>
    public static int Resolve(int preset, int outW, int outH, string? adapter)
    {
        if (preset is 1 or 2) return preset;
        bool le1080 = outW <= 1920 && outH <= 1080;
        if (le1080 || !Above1050Ti(adapter)) return 1;
        return 2;
    }

    /// <summary>True when the adapter is clearly faster than a GTX 1050 Ti. Unknown names are not.</summary>
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

    readonly Gpu _gpu;
    IntPtr _h;
    ID3D11Texture2D? _out; ID3D11RenderTargetView? _rtv; ID3D11ShaderResourceView? _srv;
    int _w, _h2; Format _fmt; uint _div;
    public string Status { get; private set; } = "off";
    bool _failed;

    public Ssrt(Gpu gpu) { _gpu = gpu; }

    /// <summary>Runs on <paramref name="src"/> and returns the SRV to feed onward, or null to keep the original.
    /// <paramref name="outW"/> / <paramref name="outH"/> pick the automatic preset. Motion and pan may be null.</summary>
    public ID3D11ShaderResourceView? Apply(ID3D11ShaderResourceView src, ID3D11Texture2D srcTex, int preset,
                                            int outW, int outH, ID3D11ShaderResourceView? motion, ID3D11ShaderResourceView? pan, bool temporal)
    {
        if (_failed) return null;
        try
        {
            var d = srcTex.Description;
            int id = Resolve(preset, outW, outH, _gpu.AdapterName);
            uint div = id == 2 ? 3u : 5u;
            if (_h == IntPtr.Zero) Create((int)d.Width, (int)d.Height, div);
            if (_failed || _h == IntPtr.Zero) return null;
            if (_w != (int)d.Width || _h2 != (int)d.Height || _fmt != d.Format || _div != div)
                Resize((int)d.Width, (int)d.Height, d.Format, div);
            if (_failed || _out == null) return null;
            int ran = ufx_ssrt_execute(_h, _gpu.Context.NativePointer, src.NativePointer, _rtv!.NativePointer,
                (uint)id, AllFeatures, temporal ? (motion?.NativePointer ?? IntPtr.Zero) : IntPtr.Zero,
                temporal ? (pan?.NativePointer ?? IntPtr.Zero) : IntPtr.Zero, temporal ? 1 : 0);
            _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            _gpu.Context.PSSetConstantBuffer(0, null!);
            Status = ran == 1 ? PresetLabel(id) : "unavailable";
            return ran == 1 ? _srv : null;
        }
        catch (EntryPointNotFoundException) { Fail("unavailable"); return null; }
        catch (Exception) { Fail("unavailable"); return null; }
    }

    void Create(int w, int h, uint div)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ssgi");
        string[] ssgi = { "CSDownsample", "CSMarch", "CSFilter", "CSShift", "VSFullscreen", "PSComposite", "CSPanGate" };
        string[] ssrt = { "CSDepth", "CSDepthBlur", "CSMarchDepth", "CSLightDir", "CSReflect", "CSContactShadow", "CSPack", "PSCompositeSsrt", "CSSsrtCut", "CSSsrtAccum" };
        if (!Load(dir, ssgi, out var a, out var ap)) return;
        if (!Load(dir, ssrt, out var b, out var bp)) { Free(ap); return; }
        try
        {
            Bytecode B(byte[][] blobs, GCHandle[] pins, int i) => new() { Data = pins[i].AddrOfPinnedObject(), Size = (nuint)blobs[i].Length };
            var sh = new SsgiShaders { Down = B(a, ap, 0), March = B(a, ap, 1), Filter = B(a, ap, 2), Shift = B(a, ap, 3), Vs = B(a, ap, 4), Ps = B(a, ap, 5), Pan = B(a, ap, 6) };
            _h = ufx_ssgi_create(_gpu.Device.NativePointer, ref sh, (uint)w, (uint)h, div);
            if (_h == IntPtr.Zero) { Fail("unavailable"); return; }
            var rh = new SsrtShaders
            {
                Depth = B(b, bp, 0), DepthBlur = B(b, bp, 1), March = B(b, bp, 2), LightDir = B(b, bp, 3),
                Reflect = B(b, bp, 4), Contact = B(b, bp, 5), Pack = B(b, bp, 6), Ps = B(b, bp, 7), Cut = B(b, bp, 8), Accum = B(b, bp, 9),
            };
            if (ufx_ssrt_init(_h, ref rh) < 0) { Fail("unavailable"); return; }
        }
        catch (EntryPointNotFoundException) { Fail("unavailable"); return; }
        finally { Free(ap); Free(bp); }
        _w = w; _h2 = h; _div = div;
    }

    bool Load(string dir, string[] names, out byte[][] blobs, out GCHandle[] pins)
    {
        blobs = new byte[names.Length][];
        pins = new GCHandle[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string p = Path.Combine(dir, names[i] + ".cso");
            if (!File.Exists(p)) { Fail("unavailable"); Free(pins); return false; }
            blobs[i] = File.ReadAllBytes(p);
            pins[i] = GCHandle.Alloc(blobs[i], GCHandleType.Pinned);
        }
        return true;
    }

    static void Free(GCHandle[] pins) { foreach (var g in pins) if (g.IsAllocated) g.Free(); }

    void Resize(int w, int h, Format f, uint div)
    {
        if (_w != w || _h2 != h || _div != div)
            if (ufx_ssgi_resize(_h, (uint)w, (uint)h, div) < 0) { Fail("unavailable"); return; }
        _w = w; _h2 = h; _div = div;
        DropOut(); MakeOut(w, h, f);
    }

    void MakeOut(int w, int h, Format f)
    {
        _out = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rtv = _gpu.Device.CreateRenderTargetView(_out);
        _srv = _gpu.Device.CreateShaderResourceView(_out);
        _fmt = f;
    }

    void DropOut() { _srv?.Dispose(); _rtv?.Dispose(); _out?.Dispose(); _srv = null; _rtv = null; _out = null; }
    void Fail(string why) { _failed = true; Status = why; if (_h != IntPtr.Zero) { try { ufx_ssgi_destroy(_h); } catch { } _h = IntPtr.Zero; } }

    public void Dispose()
    {
        DropOut();
        if (_h != IntPtr.Zero) { try { ufx_ssgi_destroy(_h); } catch { } _h = IntPtr.Zero; }
    }
}
