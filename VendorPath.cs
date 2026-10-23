using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace UniversalFrameFX;

public enum FgKind { FrameFX, Fsr3 }

/// <summary>Main frame-generation selector: Off, FrameFX's two engines, or AMD's.</summary>
public enum FgSelect { Off, Csr13, Csr20, Fsr3, CsrAi }

public static class FgSelectUi
{
    public static readonly FgSelect[] MenuOrder = { FgSelect.Off, FgSelect.Csr13, FgSelect.Csr20, FgSelect.CsrAi, FgSelect.Fsr3 };
}

/// <summary>1.3.4: allowed frame-generation multipliers (N real+generated output frames per source frame).</summary>
public static class FgMul
{
    public static readonly int[] Allowed = { 2, 3, 4, 8 };
    public static readonly int[] AllowedV2 = { 2, 3, 4, 5, 6, 8, 10, 20 };
    /// <summary>UFX_FG_ADVANCED=1 lists 8× in the multiplier menu.</summary>
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
    /// <summary>Safeguard step-down: 8→4→3→2→off.</summary>
    public static int Down(int n) => n > 4 ? 4 : n > 3 ? 3 : n > 2 ? 2 : 0;
    /// <summary>Safeguard step-up, capped at <paramref name="cap"/> (refresh clamp / requested).</summary>
    public static int Up(int n, int cap)
    {
        int next = n < 2 ? 2 : n < 3 ? 3 : n < 4 ? 4 : 8;
        return Math.Min(next, Math.Max(0, cap));
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

/// <summary>Which third-party options can run on this PC, and why not.</summary>
public static class VendorSupport
{
    public sealed record Info(bool Ok, string Why, string Detail);
    static readonly Dictionary<Backend, Info> _info = new();
    static Info _fg = new(false, "not probed", "");
    static ulong _fsr2Id, _fsr3Id, _fsr4Id;
    static bool _probed;
    public static string Fsr4Tooltip = "Requires AMD Radeon RX 9000 series";

    public static Info Get(Backend b)
    {
        if (!Backends.IsVendor(b) && b != Backend.Fsr1 && b != Backend.CsrAi) return new(true, "", "");
        lock (_info) return _info.TryGetValue(b, out var i) ? i : new(false, "not probed", "");
    }
    public static Info FsrFrameGen { get { lock (_info) return _fg; } }
    public static ulong VersionId(Backend b) => b switch { Backend.Fsr2 => _fsr2Id, Backend.Fsr3 => _fsr3Id, Backend.Fsr4 => _fsr4Id, _ => 0 };
    public static bool Probed => _probed;

    public static void Probe(Gpu g)
    {
        lock (_info)
        {
            if (_probed) return;
            _probed = true;
            _info[Backend.Fsr1] = new(true, "", "AMD FidelityFX FSR 1.0.2 shaders (EASU + RCAS), run in FrameFX's Direct3D 11 pipeline");
            bool haveW = File.Exists(CsrAiUpscale.WeightsPath);
            bool haveO = File.Exists(CsrAiUpscale.OnnxPath);
            _info[Backend.CsrAi] = haveW || haveO
                ? new(true, "", "CSR AI (experimental): a small neural super-resolution model that runs on the GPU")
                : new(false, "CSR AI model files are not installed", "");
            ID3D12Device? dev = null;
            try { dev = D3D12.D3D12CreateDevice<ID3D12Device>(g.Adapter, Vortice.Direct3D.FeatureLevel.Level_11_0); } catch { }
            if (dev == null)
            {
                foreach (var b in new[] { Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS }) _info[b] = new(false, "Direct3D 12 is not available on this GPU", "");
                _fg = new(false, "Direct3D 12 is not available on this GPU", "");
                return;
            }
            using (dev)
            {
                // FSR 2 / FSR 3 (FidelityFX SDK 1.1.4, MIT).
                var lib = FfxLib.Load("amd_fidelityfx_dx12.dll", out var why);
                if (lib == null)
                {
                    _info[Backend.Fsr2] = _info[Backend.Fsr3] = new(false, why, "");
                    _fg = new(false, why, "");
                }
                else
                {
                    var vs = lib.UpscalerVersions(dev);
                    var v2 = vs.FirstOrDefault(v => FfxLib.Major(v.name) == 2);
                    var v3 = vs.FirstOrDefault(v => FfxLib.Major(v.name) == 3);
                    _fsr2Id = v2.id; _fsr3Id = v3.id;
                    _info[Backend.Fsr2] = v2.name != null ? new(true, "", "AMD FSR " + v2.name) : new(false, "The FidelityFX runtime offers no FSR 2 provider", "");
                    _info[Backend.Fsr3] = v3.name != null ? new(true, "", "AMD FSR " + v3.name) : new(false, "The FidelityFX runtime offers no FSR 3 provider", "");
                    _fg = v3.name != null ? new(true, "", "AMD FSR 3.1 frame generation") : new(false, "FSR 3 is not available", "");
                }
                // FSR 4 (FidelityFX SDK 2.3 loader + upscaler; the runtime lists FSR 4 only on supported AMD GPUs).
                var lib4 = FfxLib.Load("amd_fidelityfx_loader_dx12.dll", out var why4);
                if (lib4 == null) _info[Backend.Fsr4] = new(false, Fsr4Tooltip, why4);
                else
                {
                    var v4 = lib4.UpscalerVersions(dev).FirstOrDefault(v => FfxLib.Major(v.name) == 4);
                    _fsr4Id = v4.id;
                    _info[Backend.Fsr4] = v4.name != null ? new(true, "", "AMD FSR " + v4.name) : new(false, Fsr4Tooltip, "no FSR 4 provider for this GPU");
                }
                // Intel XeSS.
                var x = XessLib.Load(out var whyX);
                if (x == null) _info[Backend.XeSS] = new(false, whyX, "");
                else
                {
                    unsafe
                    {
                        IntPtr ctx;
                        int rc = x.D3D12CreateContext(dev.NativePointer, &ctx);
                        if (rc >= 0) { x.DestroyContext(ctx); _info[Backend.XeSS] = new(true, "", $"Intel XeSS-SR {x.VersionName} from XeSS SDK 3.0.2 (D3D12{(g.VendorId == 0x8086 ? ", XMX" : ", DP4a")})"); }
                        else _info[Backend.XeSS] = new(false, $"XeSS does not support this GPU/driver (code {rc})", "");
                    }
                }
            }
        }
    }
}

/// <summary>Runs the vendor upscalers / FSR 3 frame generation for FramePipeline. Callers hold Gpu.Lock.</summary>
public sealed class VendorPath : IDisposable
{
    readonly Gpu _g;
    VendorBridge? _br;
    SharedTex? _color, _mv, _depth, _out, _fgIn, _fgOut;
    IVendorUpscaler? _up;
    Backend _upKind; int _upIn, _upOut;
    FfxFrameGen? _fg;
    bool _upReset = true, _fgReset = true;
    long _lastUp, _lastFg;
    int _inW, _inH;
    public string ActiveName => _up?.Name ?? "";
    public double LastMs => _br?.LastMs ?? 0;

    public VendorPath(Gpu g) { _g = g; }

    VendorBridge Bridge => _br ??= new VendorBridge(_g);

    static float Dt(ref long last)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = last == 0 ? 16.7f : (float)((now - last) * 1000.0 / Stopwatch.Frequency);
        last = now; return dt;
    }

    void EnsureInputs(int inW, int inH)
    {
        if (_color != null && inW == _inW && inH == _inH) return;
        _color?.Dispose(); _mv?.Dispose(); _depth?.Dispose();
        var br = Bridge;
        _color = br.Shared(inW, inH, Format.R8G8B8A8_UNorm, true);
        _mv = br.Shared(inW, inH, Format.R16G16_Float, true);
        _depth = br.Shared(inW, inH, Format.R32_Float, true);
        // Captured windows have no depth: a flat mid-range depth everywhere.
        _g.Context.ClearUnorderedAccessView(_depth.Uav!, new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 0.5f));
        _inW = inW; _inH = inH;
        _up?.Dispose(); _up = null;
        _fg?.Dispose(); _fg = null;
    }

    /// <summary>Pack FrameFX's motion (pixels, current→previous) into the shared RG16F texture.</summary>
    void PackMotion(Tex motion)
    {
        _g.Dispatch("FX_PackMv", _inW, _inH, new[] { (4, (ID3D11ShaderResourceView?)motion.Srv) }, new[] { (1, _mv!.Uav) });
    }

    IVendorUpscaler Create(Backend b, int inW, int inH, int outW, int outH)
    {
        var dev = Bridge.Device;
        switch (b)
        {
            case Backend.Fsr2:
            case Backend.Fsr3:
            {
                var lib = FfxLib.Load("amd_fidelityfx_dx12.dll", out var why) ?? throw new NotSupportedException(why);
                return new FfxUpscaler(lib, dev, VendorSupport.VersionId(b), VendorSupport.Get(b).Detail, inW, inH, outW, outH);
            }
            case Backend.Fsr4:
            {
                var lib = FfxLib.Load("amd_fidelityfx_loader_dx12.dll", out var why) ?? throw new NotSupportedException(why);
                return new FfxUpscaler(lib, dev, VendorSupport.VersionId(b), VendorSupport.Get(b).Detail, inW, inH, outW, outH);
            }
            case Backend.XeSS:
            {
                var x = XessLib.Load(out var why) ?? throw new NotSupportedException(why);
                return new XessUpscaler(x, dev, inW, inH, outW, outH);
            }
        }
        throw new ArgumentException(b.ToString());
    }

    public void Upscale(Backend b, ID3D11ShaderResourceView input, int inW, int inH, Tex motion, Tex output, float jx, float jy, float sharpness, bool reset)
    {
        var info = VendorSupport.Get(b);
        if (!info.Ok) throw new NotSupportedException(info.Why);
        EnsureInputs(inW, inH);
        int outW = output.Width, outH = output.Height;
        if (_out == null || _out.Width != outW || _out.Height != outH)
        {
            _out?.Dispose(); _out = Bridge.Shared(outW, outH, Format.R16G16B16A16_Float, true);
            _up?.Dispose(); _up = null;
        }
        int inKey = inW * 65536 + inH, outKey = outW * 65536 + outH;
        if (_up == null || _upKind != b || _upIn != inKey || _upOut != outKey)
        {
            _up?.Dispose(); _up = null;
            Bridge.Flush();
            _up = Create(b, inW, inH, outW, outH);
            _upKind = b; _upIn = inKey; _upOut = outKey; _upReset = true;
        }
        _g.Dispatch("FX_CopyIn", inW, inH, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (3, _color!.Uav) });
        PackMotion(motion);
        var br = Bridge;
        br.Begin();
        br.Barrier(_color.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_mv!.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_depth!.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_out.T12, ResourceStates.Common, ResourceStates.UnorderedAccess);
        Exception? err = null;
        try { _up.Record(br, _color, _mv, _depth.T12, _out, inW, inH, jx, jy, sharpness, Dt(ref _lastUp), reset || _upReset); }
        catch (Exception ex) { err = ex; }
        br.Barrier(_color.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_mv.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_depth.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_out.T12, ResourceStates.UnorderedAccess, ResourceStates.Common);
        br.End();
        if (err != null) throw err;
        _upReset = false;
        // RGBA16F vendor output → the pipeline's RGBA8 output (1:1).
        _g.Dispatch("FX_Bilinear", outW, outH, new[] { (0, (ID3D11ShaderResourceView?)_out.Srv) }, new[] { (3, output.Uav) });
    }

    /// <summary>FSR 3 frame generation: writes the frame between the previous and the current output into <paramref name="dest"/>.
    /// Returns false while it has no history yet.</summary>
    public bool FrameGen(Tex current, Tex dest, Tex motion, int inW, int inH, float jx, float jy, bool motionPacked, bool reset)
    {
        var info = VendorSupport.FsrFrameGen;
        if (!info.Ok) throw new NotSupportedException(info.Why);
        EnsureInputs(inW, inH);
        int w = current.Width, h = current.Height;
        if (_fgIn == null || _fgIn.Width != w || _fgIn.Height != h)
        {
            _fgIn?.Dispose(); _fgOut?.Dispose();
            _fgIn = Bridge.Shared(w, h, Format.R8G8B8A8_UNorm, false);
            _fgOut = Bridge.Shared(w, h, Format.R8G8B8A8_UNorm, true);
            _fg?.Dispose(); _fg = null;
        }
        if (_fg == null)
        {
            Bridge.Flush();
            var lib = FfxLib.Load("amd_fidelityfx_dx12.dll", out var why) ?? throw new NotSupportedException(why);
            _fg = new FfxFrameGen(lib, Bridge.Device, inW, inH, w, h);
            _fgReset = true;
        }
        if (!motionPacked) PackMotion(motion);
        _g.Context.CopyResource(_fgIn.T11, current.Texture);
        var br = Bridge;
        br.Begin();
        br.Barrier(_fgIn.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_mv!.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_depth!.T12, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
        br.Barrier(_fgOut!.T12, ResourceStates.Common, ResourceStates.UnorderedAccess);
        bool ok = false; Exception? err = null;
        try { ok = _fg.Record(br, _fgIn, _mv, _depth.T12, _fgOut, jx, jy, Dt(ref _lastFg), reset || _fgReset); }
        catch (Exception ex) { err = ex; }
        br.Barrier(_fgIn.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_mv.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_depth.T12, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        br.Barrier(_fgOut.T12, ResourceStates.UnorderedAccess, ResourceStates.Common);
        br.End();
        if (err != null) throw err;
        _fgReset = false;
        if (ok) _g.Context.CopyResource(dest.Texture, _fgOut.T11);
        return ok;
    }

    public void ResetHistory() { _upReset = true; _fgReset = true; _lastUp = _lastFg = 0; }

    public void Dispose()
    {
        _br?.Flush();
        _up?.Dispose(); _fg?.Dispose();
        foreach (var t in new[] { _color, _mv, _depth, _out, _fgIn, _fgOut }) t?.Dispose();
        _br?.Dispose();
        _up = null; _fg = null; _br = null; _color = _mv = _depth = _out = _fgIn = _fgOut = null;
    }
}
