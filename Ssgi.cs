using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

/// <summary>SSGI r5: experimental screen-space bounce + AO (windows/ssgi, native ssgi.dll + precompiled SM5.0 .cso),
/// applied to the captured frame BEFORE upscaling / frame generation, so real and generated frames match.
/// OFF by default; when off nothing here runs and the capture SRV goes to the pipeline exactly as before.
/// Runs on the app's own D3D11 device + immediate context (present thread, Gpu.Lock held); no read-backs on the
/// render thread (the native side's 64 KB grid copy is read with DO_NOT_WAIT on its worker ring).</summary>
public sealed class Ssgi : IDisposable
{
    public enum Preset { Auto = 0, Gtx1050Ti = 1, Gtx980Ti = 2 }
    public static readonly string[] PresetNames = { "Auto", "GTX 1050 Ti (1080p)", "GTX 980 Ti (1440p)" };
    /// <summary>Steadier lighting while moving. The CPU pan test decides this; UFX_SSGI_TEMPORAL=0/1 overrides a saved choice.</summary>
    public const bool TemporalDefault = false;

    const string Dll = "ssgi.dll";
    [StructLayout(LayoutKind.Sequential)] struct Bytecode { public IntPtr Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] struct Shaders { public Bytecode Down, March, Filter, Shift, Vs, Ps, Pan; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr ufx_ssgi_create(IntPtr dev, ref Shaders sh, uint w, uint h, uint lightDivisor);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssgi_resize(IntPtr p, uint w, uint h, uint lightDivisor);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssgi_execute_preset(IntPtr p, IntPtr ctx, IntPtr game, IntPtr rtv, uint preset);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ufx_ssgi_execute_preset2(IntPtr p, IntPtr ctx, IntPtr game, IntPtr rtv, uint preset, IntPtr pan, int temporal);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ufx_ssgi_destroy(IntPtr p);

    /// <summary>Saved choice, unless UFX_SSGI_TEMPORAL is 0 or 1.</summary>
    public static bool TemporalEnabled(bool setting)
    {
        string? e = Environment.GetEnvironmentVariable("UFX_SSGI_TEMPORAL");
        if (e == "0") return false;
        if (e == "1") return true;
        return setting;
    }

    readonly Gpu _gpu;
    IntPtr _h;
    ID3D11Texture2D? _out; ID3D11RenderTargetView? _rtv; ID3D11ShaderResourceView? _srv;
    int _w, _h2; Format _fmt; uint _div;
    public string Status { get; private set; } = "off";
    bool _failed, _noPreset2;

    public Ssgi(Gpu gpu) { _gpu = gpu; }

    /// <summary>Resolved preset id for the native side (1 = GTX1050Ti, 2 = GTX980Ti). Auto: 980 Ti-class settings for
    /// sources of 1440p and up on a desktop-class GPU name match, else the cheaper 1050 Ti set.</summary>
    public uint Resolve(Preset p, int srcH)
    {
        if (p != Preset.Auto) return (uint)p;
        string n = _gpu.AdapterName ?? "";
        bool big = n.Contains("980") || n.Contains("1070") || n.Contains("1080") || n.Contains("RTX") || n.Contains("RX 6") || n.Contains("RX 7");
        return big && srcH >= 1300 ? 2u : 1u;
    }

    /// <summary>Runs SSGI on <paramref name="src"/> (w x h texture) and returns the SRV to feed the pipeline instead,
    /// or null (caller keeps the original SRV) if SSGI is unavailable / failed.
    /// <paramref name="pan"/> is the previous frame's camera-pan estimate (1x1), or null when there isn't one.
    /// <paramref name="temporal"/> asks for steadier lighting; a null pan leaves that frame looking like SSGI without it.</summary>
    public ID3D11ShaderResourceView? Apply(ID3D11ShaderResourceView src, ID3D11Texture2D srcTex, Preset preset,
                                            ID3D11ShaderResourceView? pan = null, bool temporal = false)
    {
        if (_failed) return null;
        try
        {
            var d = srcTex.Description;
            uint id = Resolve(preset, (int)d.Height);
            uint div = id == 2 ? 3u : 4u;
            if (_h == IntPtr.Zero) Create((int)d.Width, (int)d.Height, div);
            if (_h == IntPtr.Zero) return null;
            if (_w != (int)d.Width || _h2 != (int)d.Height || _fmt != d.Format || _div != div) Resize((int)d.Width, (int)d.Height, d.Format, div);
            if (_out == null) MakeOut((int)d.Width, (int)d.Height, d.Format);
            int ran = Execute(id, src.NativePointer, temporal ? (pan?.NativePointer ?? IntPtr.Zero) : IntPtr.Zero, temporal);
            // Leave nothing bound that the pipeline reads next: the output texture must not stay an RTV while it is used
            // as an SRV (D3D11 would null the SRV binding), and the composite's PS constant buffer slot 0 is cleared.
            _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            _gpu.Context.PSSetConstantBuffer(0, null!);
            string steady = temporal && !_noPreset2 ? ", steadier lighting" : "";
            Status = ran == 1 ? $"on ({(id == 2 ? "GTX 980 Ti" : "GTX 1050 Ti")} preset){steady}" : "warming up";
            return ran == 1 ? _srv : null;
        }
        catch (Exception ex) { Fail("SSGI unavailable: " + ex.Message); return null; }
    }

    void Create(int w, int h, uint div)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ssgi");
        string[] names = { "CSDownsample", "CSMarch", "CSFilter", "CSShift", "VSFullscreen", "PSComposite", "CSPanGate" };
        var blobs = new byte[names.Length][];
        for (int i = 0; i < names.Length; i++)
        {
            string p = Path.Combine(dir, names[i] + ".cso");
            if (!File.Exists(p)) { Fail("SSGI shaders missing (" + p + ")"); return; }
            blobs[i] = File.ReadAllBytes(p);
        }
        var pins = new GCHandle[names.Length];
        try
        {
            for (int i = 0; i < names.Length; i++) pins[i] = GCHandle.Alloc(blobs[i], GCHandleType.Pinned);
            Bytecode B(int i) => new() { Data = pins[i].AddrOfPinnedObject(), Size = (nuint)blobs[i].Length };
            var sh = new Shaders { Down = B(0), March = B(1), Filter = B(2), Shift = B(3), Vs = B(4), Ps = B(5), Pan = B(6) };
            _h = ufx_ssgi_create(_gpu.Device.NativePointer, ref sh, (uint)w, (uint)h, div);   // shaders are copied by D3D
        }
        finally { foreach (var g in pins) if (g.IsAllocated) g.Free(); }
        if (_h == IntPtr.Zero) { Fail("SSGI init failed"); return; }
        _w = w; _h2 = h; _div = div;
    }

    int Execute(uint preset, IntPtr src, IntPtr pan, bool temporal)
    {
        if (!_noPreset2)
        {
            try { return ufx_ssgi_execute_preset2(_h, _gpu.Context.NativePointer, src, _rtv!.NativePointer, preset, pan, temporal ? 1 : 0); }
            catch (EntryPointNotFoundException) { _noPreset2 = true; }
        }
        return ufx_ssgi_execute_preset(_h, _gpu.Context.NativePointer, src, _rtv!.NativePointer, preset);
    }

    void Resize(int w, int h, Format f, uint div)
    {
        if (_w != w || _h2 != h || _div != div)
            if (ufx_ssgi_resize(_h, (uint)w, (uint)h, div) < 0) { Fail("SSGI resize failed"); return; }
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
    void Fail(string why) { _failed = true; Status = why; }

    public void Dispose()
    {
        DropOut();
        if (_h != IntPtr.Zero) { try { ufx_ssgi_destroy(_h); } catch { } _h = IntPtr.Zero; }
    }
}
