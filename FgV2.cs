using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FgV2Params
{
    public float InW, InH, OutW, OutH;
    public float TileSize, TilesX, TilesY, InterpT;
    public float HistoryValid, Lambda, CensusW, SadW;
    public float OcclThresh, StaticThresh, SceneCutThresh, FrameCount;
    public float JitterX, JitterY, BidirOn, Fallback;
    public float MvGapN, WideSearch, ClassicCand, EdgeSmooth;
    public float AmbiguityGate, TemporalExtrap, History2Valid, BicubicOn;
    public float SharpBlendOn, PadFg2a, PadFg2b, PadFg2c, PadFg2d, PadFg2e, PadFg2f, PadFg2g, PadFg2h, PadFg2i, PadFg2j, PadFg2k;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FgV2Global
{
    public float A00, A01, A02, A10;
    public float A11, A12, AffConf, SnapOn;
    public float HwScale, HwGrid, RansacPx, GmvY;
    public float ConfThresh, ColorGate, VecLenK, SoftOccK;
}

public static class FgV2Logic
{
    public static int PickTile(ReadOnlySpan<float> sad, ReadOnlySpan<float> census, ReadOnlySpan<float> vx, ReadOnlySpan<float> vy, float censusW, float lambda)
    {
        int best = 0;
        float bestC = float.MaxValue;
        for (int i = 0; i < 9; i++)
        {
            float sm = 0;
            for (int j = 0; j < 9; j++)
            {
                if (j == i) continue;
                float dx = vx[i] - vx[j], dy = vy[i] - vy[j];
                sm += MathF.Sqrt(dx * dx + dy * dy);
            }
            sm *= 0.125f;
            float c = sad[i] + censusW * census[i] + lambda * sm;
            if (c < bestC) { bestC = c; best = i; }
        }
        return best;
    }

    public static bool Occluded(float bwdX, float bwdY, float fwdX, float fwdY, float thresh)
    {
        float dx = fwdX + bwdX, dy = fwdY + bwdY;
        return MathF.Sqrt(dx * dx + dy * dy) > thresh;
    }

    public static bool OccludedAt(float[,] bwdX, float[,] bwdY, float[,] fwdX, float[,] fwdY, int tx, int ty, float thresh)
    {
        int h = bwdX.GetLength(0), w = bwdX.GetLength(1);
        float bx = bwdX[ty, tx], by = bwdY[ty, tx];
        int dx = (int)MathF.Round(tx + 0.5f + bx / 8f - 0.5f);
        int dy = (int)MathF.Round(ty + 0.5f + by / 8f - 0.5f);
        dx = Math.Clamp(dx, 0, w - 1);
        dy = Math.Clamp(dy, 0, h - 1);
        return Occluded(bx, by, fwdX[dy, dx], fwdY[dy, dx], thresh);
    }

    public static float SoftOcclusion(float err, float thresh, float vecLen, float rel = 0f)
    {
        float thr = thresh + rel * vecLen;
        return Math.Clamp((err - 0.5f * thr) / Math.Max(0.5f * thr, 1e-6f), 0f, 1f);
    }

    public static float SoftOccWeight(float occ, float dcol, float colorGate, float vecLen, float vecLenK)
    {
        float colW = Math.Clamp((dcol - colorGate) / Math.Max(colorGate, 1e-4f), 0f, 1f);
        return occ * colW * Math.Clamp(vecLen * vecLenK, 0f, 1f);
    }
}

public sealed class FgV2 : IDisposable
{
    readonly Gpu? _g;
    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11SamplerState _lin, _pt;
    readonly bool _ownSamp;
    readonly Dictionary<string, ID3D11ComputeShader> _cs = new();
    readonly ID3D11Buffer _cb, _cbG;
    FgV2IGpu? _igpu;
    GlobalAffineEstimator? _affine;
    IFlowEstimator? _flow;
    Fsr1? _fsr1;
    Tex[] _lumaA = Array.Empty<Tex>(), _lumaB = Array.Empty<Tex>();
    Tex? _tv8, _tv8s, _tv4, _mv2, _mvHist, _mvHist2, _fwd, _fwdHist, _occl, _statCount, _hud, _chgPrev, _chgOut, _clean, _gmv2, _hudLo, _colorA, _colorB, _genLow, _hudSeed, _l8Prev, _l8Curr, _tv16, _mvHalf;
    readonly Tex?[] _gen = new Tex?[7];
    ID3D11Texture2D? _hudStage;
    double _hudLastRead;
    int _inW, _inH, _outW, _outH;
    int _ping;
    bool _hasHistory;
    bool _hist2Valid;
    bool _hudSeeded;
    bool _flowHwDead;
    MotionPreference _flowPref = (MotionPreference)(-1);
    int _flowW, _flowH;
    readonly bool _bidir = Environment.GetEnvironmentVariable("UFX_FG_V2_BIDIR") != "0";
    bool _wideSearch;
    bool _classicCand;
    bool _edgeSmooth;
    bool _ambiguityGate;
    bool _temporalExtrap;
    bool _bicubic;
    bool _sharpBlend;
    Tex? _classicMv;
    FgV2Global _glob;
    float _occlThresh = 1f;
    float _staticThresh = 1.5f / 255f;
    float _fallback;
    readonly MotionPreference _estimatorPref;
    readonly string _estimatorLabel;
    public float ConfThresh { get; private set; } = 0.35f;
    public float StaticThresh => _staticThresh;
    public float ColorGate { get; private set; } = 0.08f;
    public float VecLenK { get; private set; } = 0.04f;
    public float OcclThresh => _occlThresh;
    public float RansacPx { get; private set; } = 1.75f;
    public float Fallback => _fallback;
    public string EstimatorLabel => _estimatorLabel;
    public float WideSearchOn => _wideSearch ? 1f : 0f;
    public float ClassicCandOn => _classicCand ? 1f : 0f;
    public float EdgeSmoothOn => _edgeSmooth ? 1f : 0f;
    public float AmbiguityGateOn => _ambiguityGate ? 1f : 0f;
    public float TemporalExtrapOn => _temporalExtrap ? 1f : 0f;
    public float BicubicOn => _bicubic ? 1f : 0f;
    public float SharpBlendOn => _sharpBlend ? 1f : 0f;
    public float MvGapN { get; set; } = 2;
    public bool ClassicCand => _classicCand;
    public void SetClassicMv(Tex? mv) { _classicMv = mv; }
    public bool LowResGen;
    public MotionPreference Motion { get; set; } = MotionPreference.Auto;
    public IFlowEstimator Flow
    {
        get { EnsureFlow(); return _flow!; }
    }
    public string EstimatorName { get; internal set; } = "pyramid";
    public string? HudMaskB64 { get; private set; }
    public string? HudMaskSeed;

    public static readonly string[] V2Kernels =
    {
        "FG2_Luma", "FG2_Halve", "FG2_Luma8", "FG2_NvofImport", "FG2_P8Search", "FG2_P8Prop",
        "FG2_P4Refine", "FG2_P2Final", "FG2_Fwd", "FG2_Consistency", "FG2_StaticCount", "FG2_HudMask",
        "FG2_SceneCut", "FG2_Interp", "FG2_InterpFast", "FG2_InterpOracle", "FG2_InterpN", "FG2_Cleanup",
        "FG2_GlobalMotion", "FG2_HudDownsample", "FG2_HudUpload", "FG2_Extrap",
        "FG2_P16Search", "FG2_EdgeSmooth",
        "CSRAIFG_PackA", "CSRAIFG_PackB", "CSRAIFG_Compose"
    };

    static string LoadSrc()
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", "FgV2.hlsl");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FgV2.hlsl") ?? throw new InvalidOperationException("FgV2.hlsl is missing.");
        return new StreamReader(s).ReadToEnd();
    }

    public FgV2(Gpu g) : this(g.Device, g.Context, g.LinearSampler, g.PointSampler, g) { }

    public FgV2(ID3D11Device device, ID3D11DeviceContext context, ID3D11SamplerState? linear = null, ID3D11SamplerState? point = null, Gpu? gpu = null)
    {
        _g = gpu;
        _dev = device;
        _ctx = context;
        if (linear != null && point != null) { _lin = linear; _pt = point; _ownSamp = false; }
        else
        {
            _lin = device.CreateSamplerState(SamplerDescription.LinearClamp);
            _pt = device.CreateSamplerState(SamplerDescription.PointClamp);
            _ownSamp = true;
        }
        string src = LoadSrc();
        var blobs = ShaderCache.GetMany(src, V2Kernels.Select(k => (k, "cs_5_0")), "FgV2.hlsl");
        byte[] Fresh(string e) { using var bl = Gpu.CompileSource(src, e, "cs_5_0", "FgV2.hlsl"); return bl.AsSpan().ToArray(); }
        foreach (var k in V2Kernels)
        {
            try { _cs[k] = device.CreateComputeShader(blobs[k]); }
            catch { _cs[k] = device.CreateComputeShader(Fresh(k)); }
        }
        _cb = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 160, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write
        });
        _cbG = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 64, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write
        });
        ReadKnobs(out _estimatorPref, out _estimatorLabel);
        Motion = _estimatorPref;
        _glob = DefaultGlobal();
        _glob.ConfThresh = ConfThresh;
        _glob.ColorGate = ColorGate;
        _glob.VecLenK = VecLenK;
        _glob.RansacPx = RansacPx;
        GlobalAffineEstimator.InlierPx = RansacPx;
        TryOpenIGpu(gpu);
        try { _affine = new GlobalAffineEstimator(device, context); } catch { _affine = null; }
    }

    void ReadKnobs(out MotionPreference pref, out string label)
    {
        if (TryEnv("UFX_FG2_CONF_THRESH", out var ct)) ConfThresh = ct;
        if (TryEnv("UFX_FG2_STATIC_THRESH", out var st)) _staticThresh = st;
        if (TryEnv("UFX_FG2_COLOR_GATE", out var cg)) ColorGate = cg;
        if (TryEnv("UFX_FG2_VECLEN_K", out var vk)) VecLenK = vk;
        if (TryEnv("UFX_FG2_OCCL_THRESH", out var ot)) _occlThresh = ot;
        if (TryEnv("UFX_FG2_RANSAC_PX", out var rp)) RansacPx = rp;
        if (TryEnv("UFX_FG2_FALLBACK", out var fb)) _fallback = fb;
        _wideSearch = EnvOn("UFX_FG2_WIDE_SEARCH");
        _classicCand = EnvOn("UFX_FG2_CLASSIC_CAND");
        _edgeSmooth = EnvOn("UFX_FG2_EDGE_SMOOTH");
        _ambiguityGate = EnvOn("UFX_FG2_AMBIGUITY_GATE");
        _temporalExtrap = EnvOn("UFX_FG2_TEMPORAL_EXTRAP");
        _bicubic = EnvOn("UFX_FG2_BICUBIC");
        _sharpBlend = EnvOn("UFX_FG2_SHARP_BLEND");
        string est = Environment.GetEnvironmentVariable("UFX_FG2_ESTIMATOR")?.Trim().ToLowerInvariant() ?? "auto";
        (pref, label) = est switch
        {
            "pyramid" or "sw" or "software" => (MotionPreference.Software, "pyramid"),
            "nvof" => (MotionPreference.Nvof, "nvof"),
            _ => (MotionPreference.Auto, "auto"),
        };
    }

    static bool TryEnv(string name, out float v)
    {
        v = 0;
        string? s = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static bool EnvOn(string name) => Environment.GetEnvironmentVariable(name) == "1";

    FgV2Global DefaultGlobal() => new()
    {
        A00 = 1, A11 = 1, ConfThresh = ConfThresh, ColorGate = ColorGate, VecLenK = VecLenK, SoftOccK = 0, RansacPx = RansacPx
    };

    int Tvx => Math.Max(1, (_inW + 7) / 8);
    int Tvy => Math.Max(1, (_inH + 7) / 8);

    void SetParams(in FgV2Params p)
    {
        var m = _ctx.Map(_cb, 0, MapMode.WriteDiscard);
        try { unsafe { *(FgV2Params*)m.DataPointer = p; } }
        finally { _ctx.Unmap(_cb, 0); }
    }

    void SetGlobal(in FgV2Global g)
    {
        _glob = g;
        var m = _ctx.Map(_cbG, 0, MapMode.WriteDiscard);
        try { unsafe { *(FgV2Global*)m.DataPointer = g; } }
        finally { _ctx.Unmap(_cbG, 0); }
    }

    FgV2Params Base(float jx, float jy, float t = 0.5f) => new()
    {
        InW = _inW, InH = _inH, OutW = _outW, OutH = _outH,
        TileSize = 8, TilesX = Tvx, TilesY = Tvy, InterpT = t,
        HistoryValid = _hasHistory ? 1 : 0, Lambda = 0.02f, CensusW = 0.3f, SadW = 1f,
        OcclThresh = _occlThresh, StaticThresh = _staticThresh, SceneCutThresh = 0.6f * 4096f, FrameCount = 1,
        JitterX = jx, JitterY = jy, BidirOn = _bidir ? 1 : 0, Fallback = _fallback,
        MvGapN = Math.Max(1f, MvGapN), WideSearch = _wideSearch ? 1 : 0, ClassicCand = _classicCand ? 1 : 0, EdgeSmooth = _edgeSmooth ? 1 : 0,
        AmbiguityGate = _ambiguityGate ? 1 : 0, TemporalExtrap = _temporalExtrap ? 1 : 0, History2Valid = _hist2Valid ? 1 : 0, BicubicOn = _bicubic ? 1 : 0,
        SharpBlendOn = _sharpBlend ? 1 : 0
    };

    void Dispatch(string kernel, int w, int h,
        (int slot, ID3D11ShaderResourceView? srv)[] srvs,
        (int slot, ID3D11UnorderedAccessView? uav)[] uavs)
    {
        var ctx = _ctx;
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetConstantBuffer(1, _cbG);
        ctx.CSSetSampler(0, _lin);
        ctx.CSSetSampler(1, _pt);
        foreach (var (slot, srv) in srvs) ctx.CSSetShaderResource((uint)slot, srv);
        foreach (var (slot, uav) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, uav);
        ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        foreach (var (slot, _) in srvs) ctx.CSSetShaderResource((uint)slot, null);
        foreach (var (slot, _) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, null);
        if (_g != null && _g.Profiling) _g.Mark(kernel);
    }

    void DispatchGroups(string kernel, int gx, int gy,
        (int slot, ID3D11ShaderResourceView? srv)[] srvs,
        (int slot, ID3D11UnorderedAccessView? uav)[] uavs)
    {
        var ctx = _ctx;
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetConstantBuffer(1, _cbG);
        ctx.CSSetSampler(0, _lin);
        ctx.CSSetSampler(1, _pt);
        foreach (var (slot, srv) in srvs) ctx.CSSetShaderResource((uint)slot, srv);
        foreach (var (slot, uav) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, uav);
        ctx.Dispatch((uint)Math.Max(1, gx), (uint)Math.Max(1, gy), 1);
        foreach (var (slot, _) in srvs) ctx.CSSetShaderResource((uint)slot, null);
        foreach (var (slot, _) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, null);
        if (_g != null && _g.Profiling) _g.Mark(kernel);
    }

    static void DisposeAll(IEnumerable<Tex?> ts) { foreach (var t in ts) t?.Dispose(); }

    public void Ensure(int inW, int inH, int outW, int outH)
    {
        if (inW == _inW && inH == _inH && outW == _outW && outH == _outH && _mv2 != null) return;
        DisposeTextures();
        _inW = inW; _inH = inH; _outW = outW; _outH = outH;
        Reset();
        var la = new List<Tex>(); var lb = new List<Tex>();
        int w = inW, h = inH;
        int nLuma = _wideSearch ? 5 : 4;
        for (int i = 0; i < nLuma; i++)
        {
            la.Add(new Tex(_dev, w, h, Format.R16_Float));
            lb.Add(new Tex(_dev, w, h, Format.R16_Float));
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        _lumaA = la.ToArray(); _lumaB = lb.ToArray();
        int tx = Tvx, ty = Tvy;
        _tv8 = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        _tv8s = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        _tv4 = new Tex(_dev, tx, ty, Format.R16G16_Float);
        _mv2 = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        _mvHist = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        if (_temporalExtrap) _mvHist2 = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        if (_wideSearch) _tv16 = new Tex(_dev, _lumaA[4].Width, _lumaA[4].Height, Format.R16G16B16A16_Float);
        if (_edgeSmooth) _mvHalf = new Tex(_dev, Math.Max(1, inW / 2), Math.Max(1, inH / 2), Format.R16G16B16A16_Float);
        _fwd = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        _fwdHist = new Tex(_dev, tx, ty, Format.R16G16B16A16_Float);
        _occl = new Tex(_dev, tx, ty, Format.R8_UNorm);
        _statCount = new Tex(_dev, inW, inH, Format.R32_Float);
        _hud = new Tex(_dev, inW, inH, Format.R16_Float);
        _chgPrev = new Tex(_dev, 64, 64, Format.R32_Float);
        _chgOut = new Tex(_dev, 2, 1, Format.R32_Float);
        _clean = new Tex(_dev, outW, outH, Format.R8G8B8A8_UNorm);
        _gmv2 = new Tex(_dev, 1, 1, Format.R32G32_Float);
        _hudLo = new Tex(_dev, 64, 36, Format.R16_Float);
        _hudSeed = new Tex(_dev, 64, 36, Format.R16_Float);
        _l8Prev = new Tex(_dev, inW, inH, Format.R8_UNorm);
        _l8Curr = new Tex(_dev, inW, inH, Format.R8_UNorm);
        _colorA = new Tex(_dev, inW, inH, Format.B8G8R8A8_UNorm, uav: false);
        _colorB = new Tex(_dev, inW, inH, Format.B8G8R8A8_UNorm, uav: false);
        _hudStage = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = 64, Height = 36, MipLevels = 1, ArraySize = 1, Format = Format.R16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read
        });
        _ctx.ClearUnorderedAccessView(_statCount.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _ctx.ClearUnorderedAccessView(_hud.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _igpu?.Ensure(tx, ty);
        _affine?.Ensure(tx, ty);
        _hudSeeded = false;
        TrySeedHud();
    }

    void DisposeTextures()
    {
        DisposeAll(_lumaA); DisposeAll(_lumaB);
        DisposeAll(new[] { _tv8, _tv8s, _tv4, _mv2, _mvHist, _mvHist2, _fwd, _fwdHist, _occl, _statCount, _hud, _chgPrev, _chgOut, _clean, _gmv2, _hudLo, _hudSeed, _l8Prev, _l8Curr, _colorA, _colorB, _genLow, _tv16, _mvHalf });
        _tv8 = _tv8s = _tv4 = _mv2 = _mvHist = _mvHist2 = _fwd = _fwdHist = _occl = _statCount = _hud = _chgPrev = _chgOut = _clean = _gmv2 = _hudLo = _hudSeed = _l8Prev = _l8Curr = _colorA = _colorB = _genLow = _tv16 = _mvHalf = null;
        _classicMv = null;
        _hudStage?.Dispose(); _hudStage = null;
        DisposeAll(_gen); Array.Clear(_gen);
        _lumaA = Array.Empty<Tex>(); _lumaB = Array.Empty<Tex>();
    }

    public void Reset()
    {
        _hasHistory = false;
        _hist2Valid = false;
        _ping = 0;
        if (_flow is NvofFlowEstimator nv) nv.ResetHistory();
    }

    void EnsureFlow()
    {
        if (_flowHwDead)
        {
            _flow ??= new PyramidFlowEstimator(this);
            return;
        }
        var pref = _estimatorLabel is "auto" ? Motion : _estimatorPref;
        if (_estimatorLabel is "auto" && pref == MotionPreference.Auto && (ForceCompute || (long)_inW * _inH > 2560L * 1440L)) pref = MotionPreference.Software;
        if (_flow != null && _flowPref == pref && _flowW == _inW && _flowH == _inH) return;
        _flow?.Dispose();
        _flow = FlowEstimators.Create(this, _g, _inW, _inH, pref);
        _flowPref = pref;
        _flowW = _inW;
        _flowH = _inH;
    }

    void FallbackPyramid()
    {
        _flowHwDead = true;
        _flow?.Dispose();
        _flow = new PyramidFlowEstimator(this);
        _flowPref = Motion;
        _flowW = _inW;
        _flowH = _inH;
    }

    public Tex Luma8Prev => _l8Prev!;
    public Tex Luma8Curr => _l8Curr!;

    public void FillLuma8(Tex prevR16, Tex currR16)
    {
        Dispatch("FG2_Luma8", prevR16.Width, prevR16.Height, new[] { (1, (ID3D11ShaderResourceView?)prevR16.Srv) }, new[] { (0, _l8Prev!.Uav) });
        Dispatch("FG2_Luma8", currR16.Width, currR16.Height, new[] { (1, (ID3D11ShaderResourceView?)currR16.Srv) }, new[] { (0, _l8Curr!.Uav) });
    }

    public Tex RefineNvof(Tex[] prev, Tex[] curr, Tex hist, ID3D11ShaderResourceView hw, float scale, float grid, Tex dest)
    {
        var p = Base(0, 0);
        var g = _glob;
        g.HwScale = scale;
        g.HwGrid = grid;
        SetParams(p);
        SetGlobal(g);
        int tx = Tvx, ty = Tvy;
        RunP16(prev, curr);
        Dispatch("FG2_NvofImport", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[3].Srv), (3, curr[3].Srv), (15, hist.Srv), (20, hw), (12, _tv16?.Srv), (13, _mvHist2?.Srv) },
            new[] { (1, _tv8!.Uav) });
        Dispatch("FG2_P4Refine", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[2].Srv), (3, curr[2].Srv), (4, _tv8.Srv) },
            new[] { (2, _tv4!.Uav) });
        Dispatch("FG2_P2Final", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[1].Srv), (3, curr[1].Srv), (4, _tv4.Srv) },
            new[] { (3, dest.Uav) });
        return dest;
    }

    void RunFlow(Tex[] prev, Tex[] curr)
    {
        try
        {
            if (_flow is NvofFlowEstimator nv) nv.BeginFrame();
            Flow.EstimateBackward(prev, curr, _mvHist!, null);
            if (_bidir) Flow.EstimateForward(curr, prev, _fwdHist!, null);
        }
        catch when (_flow?.HardwareAccelerated == true)
        {
            FallbackPyramid();
            Flow.EstimateBackward(prev, curr, _mvHist!, null);
            if (_bidir) Flow.EstimateForward(curr, prev, _fwdHist!, null);
        }
        EstimatorName = Flow.Name;
    }

    void BuildLuma(Tex[] curr, ID3D11ShaderResourceView input, in FgV2Params p)
    {
        SetParams(p);
        Dispatch("FG2_Luma", curr[0].Width, curr[0].Height, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (0, curr[0].Uav) });
        for (int i = 0; i < curr.Length - 1; i++)
            Dispatch("FG2_Halve", curr[i + 1].Width, curr[i + 1].Height, new[] { (1, (ID3D11ShaderResourceView?)curr[i].Srv) }, new[] { (0, curr[i + 1].Uav) });
    }

    void RunP16(Tex[] prev, Tex[] curr)
    {
        if (!_wideSearch || _tv16 == null || prev.Length < 5) return;
        Dispatch("FG2_P16Search", _tv16.Width, _tv16.Height,
            new[] { (2, (ID3D11ShaderResourceView?)prev[4].Srv), (3, curr[4].Srv) },
            new[] { (1, _tv16.Uav) });
    }

    public Tex SearchPyramid(Tex[] prev, Tex[] curr, Tex hist, FlowHint? hint, Tex dest)
    {
        var p = Base(0, 0);
        var g = _glob;
        g.HwScale = hint?.Scale ?? 0;
        g.HwGrid = hint?.Grid ?? 0;
        SetParams(p);
        SetGlobal(g);
        int tx = Tvx, ty = Tvy;
        var hw = hint?.Srv;
        RunP16(prev, curr);
        bool igpu = !_wideSearch && !_ambiguityGate && !_temporalExtrap && _igpu != null && _igpu.TryP8(_ctx, prev[3], curr[3], hist, _tv8!);
        if (!igpu)
            Dispatch("FG2_P8Search", tx, ty,
                new[] { (2, (ID3D11ShaderResourceView?)prev[3].Srv), (3, curr[3].Srv), (15, hist.Srv), (17, _gmv2!.Srv), (20, hw), (12, _tv16?.Srv), (13, _mvHist2?.Srv) },
                new[] { (1, _tv8!.Uav) });
        SetParams(p);
        Dispatch("FG2_P8Prop", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[3].Srv), (3, curr[3].Srv), (4, _tv8.Srv), (15, hist.Srv) },
            new[] { (1, _tv8s!.Uav) });
        Dispatch("FG2_P4Refine", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[2].Srv), (3, curr[2].Srv), (4, _tv8s.Srv) },
            new[] { (2, _tv4!.Uav) });
        Dispatch("FG2_P2Final", tx, ty,
            new[] { (2, (ID3D11ShaderResourceView?)prev[1].Srv), (3, curr[1].Srv), (4, _tv4.Srv) },
            new[] { (3, dest.Uav) });
        return dest;
    }

    public Tex Process(ID3D11ShaderResourceView input, int inW, int inH, float jx, float jy)
    {
        Ensure(inW, inH, Math.Max(_outW, 1), Math.Max(_outH, 1));
        var curr = _ping == 0 ? _lumaB : _lumaA;
        var prev = _ping == 0 ? _lumaA : _lumaB;
        var p = Base(jx, jy);
        SetGlobal(_glob);
        BuildLuma(curr, input, p);
        CopyColor(input);
        ApplyAffine(_affine?.Last ?? GlobalAffineEstimator.Identity());
        SetParams(p);
        DispatchGroups("FG2_GlobalMotion", 1, 1,
            new[] { (2, (ID3D11ShaderResourceView?)prev[3].Srv), (3, curr[3].Srv) },
            new[] { (2, _gmv2!.Uav) });
        RunFlow(prev, curr);
        if (!_bidir)
        {
            SetParams(p);
            Dispatch("FG2_Fwd", Tvx, Tvy, new[] { (4, (ID3D11ShaderResourceView?)_mv2!.Srv) }, new[] { (4, _fwd!.Uav) });
        }
        SetParams(p);
        Dispatch("FG2_Consistency", Tvx, Tvy,
            new[] { (4, (ID3D11ShaderResourceView?)_mv2!.Srv), (5, _fwd!.Srv) },
            new[] { (5, _occl!.Uav) });
        Dispatch("FG2_StaticCount", _inW, _inH,
            new[] { (2, (ID3D11ShaderResourceView?)prev[0].Srv), (3, curr[0].Srv) },
            new[] { (6, _statCount!.Uav) });
        Dispatch("FG2_HudMask", _inW, _inH,
            new[] { (6, (ID3D11ShaderResourceView?)_statCount.Srv), (3, curr[0].Srv) },
            new[] { (7, _hud!.Uav) });
        DispatchGroups("FG2_SceneCut", 1, 1,
            new[] { (0, (ID3D11ShaderResourceView?)input) },
            new[] { (0, _chgOut!.Uav), (4, _chgPrev!.Uav) });
        if (_temporalExtrap && _mvHist2 != null && _hasHistory)
        {
            _ctx.CopyResource(_mvHist2.Texture, _mvHist!.Texture);
            _hist2Valid = true;
        }
        _ctx.CopyResource(_mvHist!.Texture, _mv2!.Texture);
        _ctx.CopyResource(_fwdHist!.Texture, _fwd!.Texture);
        _affine?.Push(_mv2);
        TickHudPersist();
        _ping ^= 1;
        _hasHistory = true;
        _frameId++;
        return _mv2!;
    }

    int _frameId, _edgeDone = -1;

    void MaybeEdgeSmooth()
    {
        if (!_edgeSmooth || _mvHalf == null || _mv2 == null) return;
        if (_edgeDone == _frameId) return;
        var prev = _ping == 0 ? _colorB : _colorA;
        var curr = _ping == 0 ? _colorA : _colorB;
        if (prev == null || curr == null) return;
        var p = Base(0, 0);
        SetParams(p);
        Dispatch("FG2_EdgeSmooth", _mvHalf.Width, _mvHalf.Height,
            new[] { (4, (ID3D11ShaderResourceView?)_mv2.Srv), (0, prev.Srv), (9, curr.Srv) },
            new[] { (3, _mvHalf.Uav) });
        _edgeDone = _frameId;
    }

    void CopyColor(ID3D11ShaderResourceView input)
    {
        var dest = _ping == 0 ? _colorB : _colorA;
        if (dest == null) return;
        using var r = input.Resource;
        using var t2 = r.QueryInterfaceOrNull<ID3D11Texture2D>();
        if (t2 == null) return;
        try { _ctx.CopyResource(dest.Texture, t2); } catch { }
    }

    void ApplyAffine(AffineModel m)
    {
        var g = _glob;
        g.A00 = m.A00; g.A01 = m.A01; g.A02 = m.A02;
        g.A10 = m.A10; g.A11 = m.A11; g.A12 = m.A12;
        g.AffConf = m.Conf;
        g.SnapOn = m.SnapOn;
        g.ConfThresh = ConfThresh;
        g.ColorGate = ColorGate;
        g.VecLenK = VecLenK;
        g.RansacPx = RansacPx;
        SetGlobal(g);
    }

    Tex EnsureGen(int i)
    {
        var t = _gen[i];
        if (t == null || t.Width != _outW || t.Height != _outH)
        {
            t?.Dispose();
            t = _gen[i] = new Tex(_dev, _outW, _outH, Format.R8G8B8A8_UNorm);
        }
        return t;
    }

    void Cleanup(Tex src, Tex dst, Tex prev, Tex curr, float t) => CleanupAt(src, dst, prev, curr, t, _outW, _outH);

    void CleanupAt(Tex src, Tex dst, Tex prev, Tex curr, float t, int w, int h)
    {
        var p = Base(0, 0, t);
        p.OutW = w; p.OutH = h;
        SetParams(p);
        Dispatch("FG2_Cleanup", w, h,
            new[] { (8, (ID3D11ShaderResourceView?)src.Srv), (0, prev.Srv), (4, _mv2!.Srv), (5, _occl!.Srv), (9, curr.Srv) },
            new[] { (0, dst.Uav) });
    }

    Tex EnsureLow(int w, int h)
    {
        if (_genLow == null || _genLow.Width != w || _genLow.Height != h)
        {
            _genLow?.Dispose();
            _genLow = new Tex(_dev, w, h, Format.R8G8B8A8_UNorm);
        }
        return _genLow;
    }

    public Tex Extrapolate(Tex latest, float t)
    {
        var (prev, curr, gw, gh) = GenSources(latest, latest);
        var dest = gw == _outW && gh == _outH ? EnsureGen(0) : EnsureLow(gw, gh);
        var p = Base(0, 0, t);
        p.OutW = gw; p.OutH = gh;
        SetParams(p);
        SetGlobal(_glob);
        Dispatch("FG2_Extrap", gw, gh,
            new[] { (0, (ID3D11ShaderResourceView?)curr.Srv), (5, _fwd!.Srv) },
            new[] { (3, dest.Uav) });
        return UpscaleGen(dest);
    }

    (Tex prev, Tex curr, int gw, int gh) GenSources(Tex prevOutput, Tex currOutput)
    {
        if (!LowResGen) return (prevOutput, currOutput, _outW, _outH);
        var prev = _ping == 0 ? _colorB : _colorA;
        var curr = _ping == 0 ? _colorA : _colorB;
        if (prev == null || curr == null) return (prevOutput, currOutput, _outW, _outH);
        return (prev, curr, _inW, _inH);
    }

    Tex UpscaleGen(Tex src)
    {
        if (!LowResGen || (src.Width == _outW && src.Height == _outH)) return src;
        var dest = EnsureGen(0);
        if (_g != null)
        {
            _fsr1 ??= new Fsr1(_g);
            _fsr1.Encode(src.Srv, src.Width, src.Height, dest, 0.2f);
            return dest;
        }
        return src;
    }

    public Tex Generate(Tex prevOutput, Tex currOutput, float t)
    {
        var (prev, curr, gw, gh) = GenSources(prevOutput, currOutput);
        var dest = gw == _outW && gh == _outH ? EnsureGen(0) : EnsureLow(gw, gh);
        if (_clean == null || _clean.Width != gw || _clean.Height != gh)
        { _clean?.Dispose(); _clean = new Tex(_dev, gw, gh, Format.R8G8B8A8_UNorm); }
        var ip = Base(0, 0, t);
        ip.OutW = gw; ip.OutH = gh;
        SetParams(ip);
        SetGlobal(_glob);
        MaybeEdgeSmooth();
        Dispatch("FG2_Interp", gw, gh,
            new[] {
                (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, _mv2!.Srv),
                (5, _occl!.Srv), (7, _hud!.Srv), (16, _chgOut!.Srv),
                (10, _classicMv?.Srv), (11, _mvHalf?.Srv)
            },
            new[] { (3, dest.Uav) });
        CleanupAt(dest, _clean, prev, curr, t, gw, gh);
        _ctx.CopyResource(dest.Texture, _clean.Texture);
        return UpscaleGen(dest);
    }

    public bool ForceCompute;

    public Tex GenerateFast(Tex prevOutput, Tex currOutput, float t)
    {
        var (prev, curr, gw, gh) = GenSources(prevOutput, currOutput);
        var dest = gw == _outW && gh == _outH ? EnsureGen(0) : EnsureLow(gw, gh);
        var ip = Base(0, 0, t);
        ip.OutW = gw; ip.OutH = gh;
        SetParams(ip);
        SetGlobal(_glob);
        Dispatch("FG2_InterpFast", gw, gh,
            new[] {
                (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, _mv2!.Srv),
                (7, _hud!.Srv), (16, _chgOut!.Srv)
            },
            new[] { (3, dest.Uav) });
        return UpscaleGen(dest);
    }

    public Tex GenerateOracle(Tex prevOutput, Tex currOutput, Tex bwd, Tex fwd, float t)
    {
        var dest = EnsureGen(0);
        var ip = Base(0, 0, t);
        ip.OutW = _outW; ip.OutH = _outH;
        SetParams(ip);
        SetGlobal(_glob);
        Dispatch("FG2_InterpOracle", _outW, _outH,
            new[] {
                (0, (ID3D11ShaderResourceView?)prevOutput.Srv), (9, currOutput.Srv), (4, bwd.Srv),
                (5, fwd.Srv), (7, _hud!.Srv)
            },
            new[] { (3, dest.Uav) });
        return dest;
    }

    public Tex Blit(Tex src)
    {
        var dest = EnsureGen(0);
        _ctx.CopyResource(dest.Texture, src.Texture);
        return dest;
    }

    public Tex[] GenerateMulti(Tex prevOutput, Tex currOutput, int n, Tex[] dests)
    {
        n = Math.Clamp(n, 2, 8);
        int g = n - 1;
        var list = new Tex[g];
        for (int i = 0; i < g; i++)
        {
            if (dests != null && i < dests.Length && dests[i] != null) list[i] = dests[i];
            else list[i] = EnsureGen(i);
        }
        var p = Base(0, 0); p.FrameCount = g; p.OutW = _outW; p.OutH = _outH; SetParams(p); SetGlobal(_glob);
        MaybeEdgeSmooth();
        var uavs = new (int, ID3D11UnorderedAccessView?)[g];
        for (int i = 0; i < g; i++) uavs[i] = (i, list[i].Uav);
        Dispatch("FG2_InterpN", _outW, _outH,
            new[] {
                (0, (ID3D11ShaderResourceView?)prevOutput.Srv), (9, currOutput.Srv), (4, _mv2!.Srv),
                (5, _occl!.Srv), (7, _hud!.Srv), (16, _chgOut!.Srv),
                (10, _classicMv?.Srv), (11, _mvHalf?.Srv)
            }, uavs);
        if (_clean == null || _clean.Width != _outW || _clean.Height != _outH)
        { _clean?.Dispose(); _clean = new Tex(_dev, _outW, _outH, Format.R8G8B8A8_UNorm); }
        for (int i = 0; i < g; i++)
        {
            Cleanup(list[i], _clean, prevOutput, currOutput, (i + 1) / (float)n);
            _ctx.CopyResource(list[i].Texture, _clean.Texture);
        }
        return list;
    }

    public Tex MotionTex => _mv2!;
    public Tex FwdTex => _fwd!;
    public Tex OcclTex => _occl!;
    public Tex HudTex => _hud!;
    public Tex ChgTex => _chgOut!;

    (int slot, ID3D11ShaderResourceView? srv)[] InterpSrvs(Tex prev, Tex curr) =>
        new[] {
            (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, _mv2!.Srv),
            (5, _occl!.Srv), (7, _hud!.Srv), (16, _chgOut!.Srv),
            (10, _classicMv?.Srv), (11, _mvHalf?.Srv)
        };

    public void PackAi(Tex prev, Tex curr, ID3D11UnorderedAccessView dst, int qw, int qh)
    {
        MaybeEdgeSmooth();
        var p = Base(0, 0, 0.5f);
        p.OutW = qw; p.OutH = qh;
        SetParams(p);
        SetGlobal(_glob);
        Dispatch("CSRAIFG_PackA", qw, qh, InterpSrvs(prev, curr), new[] { (0, (ID3D11UnorderedAccessView?)dst) });
    }

    public void PackAiB(Tex prev, Tex curr, ID3D11ShaderResourceView pack, ID3D11ShaderResourceView netA, ID3D11UnorderedAccessView dst, int hw, int hh, float t)
    {
        var p = Base(0, 0, t);
        p.OutW = hw; p.OutH = hh;
        SetParams(p);
        SetGlobal(_glob);
        Dispatch("CSRAIFG_PackB", hw, hh,
            new[] {
                (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv),
                (14, pack), (19, netA)
            },
            new[] { (0, (ID3D11UnorderedAccessView?)dst) });
    }

    public void ComposeAi(Tex prev, Tex curr, ID3D11ShaderResourceView pack, ID3D11ShaderResourceView netA, ID3D11ShaderResourceView netB, Tex dest, float t, float maskA, float maskB, bool lpSigmoid = false, int netBOut = 5)
    {
        var p = Base(0, 0, t);
        p.OutW = dest.Width; p.OutH = dest.Height;
        p.PadFg2a = maskA;
        p.PadFg2b = maskB;
        p.PadFg2c = lpSigmoid ? 1f : 0f;
        p.PadFg2d = netBOut;
        SetParams(p);
        SetGlobal(_glob);
        Dispatch("CSRAIFG_Compose", dest.Width, dest.Height,
            new[] {
                (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (7, _hud!.Srv), (16, _chgOut!.Srv),
                (14, pack), (19, netA), (21, netB)
            },
            new[] { (3, dest.Uav) });
    }

    void TrySeedHud()
    {
        if (_hudSeeded || string.IsNullOrEmpty(HudMaskSeed) || _hud == null || _hudSeed == null) return;
        try
        {
            byte[] raw = Convert.FromBase64String(HudMaskSeed);
            UnpackHud(raw, _hudSeed);
            var p = Base(0, 0);
            SetParams(p);
            Dispatch("FG2_HudUpload", _inW, _inH,
                new[] { (18, (ID3D11ShaderResourceView?)_hudSeed.Srv) },
                new[] { (7, _hud.Uav) });
            _hudSeeded = true;
        }
        catch { }
    }

    void TickHudPersist()
    {
        if (_hud == null || _hudLo == null || _hudStage == null) return;
        double now = FgV2Pacing.QpcSeconds();
        if (_hudLastRead > 0 && now - _hudLastRead < 5) return;
        var p = Base(0, 0);
        SetParams(p);
        Dispatch("FG2_HudDownsample", 64, 36,
            new[] { (7, (ID3D11ShaderResourceView?)_hud.Srv) },
            new[] { (0, _hudLo.Uav) });
        _ctx.CopyResource(_hudStage, _hudLo.Texture);
        if (_ctx.Map(_hudStage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var m).Failure) return;
        try
        {
            HudMaskB64 = Convert.ToBase64String(PackHud(m, 64, 36));
            _hudLastRead = now;
        }
        finally { _ctx.Unmap(_hudStage, 0); }
    }

    static byte[] PackHud(MappedSubresource m, int w, int h)
    {
        int n = (w * h + 7) / 8;
        var bits = new byte[n];
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)m.DataPointer + y * (long)m.RowPitch;
                for (int x = 0; x < w; x++)
                {
                    float v = (float)BitConverter.UInt16BitsToHalf(*(ushort*)(row + x * 2));
                    if (v > 0.5f)
                    {
                        int i = y * w + x;
                        bits[i >> 3] |= (byte)(1 << (i & 7));
                    }
                }
            }
        }
        return bits;
    }

    void UnpackHud(byte[] bits, Tex dest)
    {
        var stage = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = 64, Height = 36, MipLevels = 1, ArraySize = 1, Format = Format.R16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Write
        });
        try
        {
            var m = _ctx.Map(stage, 0, MapMode.Write);
            try
            {
                unsafe
                {
                    for (int y = 0; y < 36; y++)
                    {
                        byte* row = (byte*)m.DataPointer + y * (long)m.RowPitch;
                        for (int x = 0; x < 64; x++)
                        {
                            int i = y * 64 + x;
                            float v = (i >> 3) < bits.Length && (bits[i >> 3] & (1 << (i & 7))) != 0 ? 1f : 0f;
                            *(ushort*)(row + x * 2) = unchecked((ushort)BitConverter.HalfToInt16Bits((Half)v));
                        }
                    }
                }
            }
            finally { _ctx.Unmap(stage, 0); }
            _ctx.CopyResource(dest.Texture, stage);
        }
        finally { stage.Dispose(); }
    }

    public void CopyMotionFrom(ID3D11Texture2D mv, ID3D11Texture2D occl, ID3D11Texture2D hud, ID3D11Texture2D chg)
    {
        _ctx.CopyResource(_mv2!.Texture, mv);
        _ctx.CopyResource(_occl!.Texture, occl);
        _ctx.CopyResource(_hud!.Texture, hud);
        _ctx.CopyResource(_chgOut!.Texture, chg);
    }

    void TryOpenIGpu(Gpu? gpu)
    {
        if (gpu == null || Environment.GetEnvironmentVariable("UFX_FG_V2_IGPU") != "1") return;
        if (Gpu.Choice == GpuChoice.Integrated) return;
        if (gpu.KindName != "dedicated") return;
        try { _igpu = new FgV2IGpu(); }
        catch { _igpu = null; }
    }

    public void Dispose()
    {
        DisposeTextures();
        _flow?.Dispose(); _flow = null;
        _igpu?.Dispose(); _igpu = null;
        _affine?.Dispose(); _affine = null;
        _fsr1?.Dispose(); _fsr1 = null;
        foreach (var cs in _cs.Values) cs.Dispose();
        _cs.Clear();
        _cb.Dispose();
        _cbG.Dispose();
        if (_ownSamp) { _lin.Dispose(); _pt.Dispose(); }
    }
}
