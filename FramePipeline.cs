using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

// Order is persisted by index: append only.
public enum Backend { Temporal, Spatial, Bilinear, Fsr1, Fsr2, Fsr3, Fsr4, XeSS, Csr20, CsrAi }

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
        Backend.Csr20 => "CSR 2.0",
        Backend.CsrAi => "CSR AI (experimental)",
        _ => "Bilinear",
    };
    public static string Short(Backend b) => Long(b);

    /// <summary>Name -> backend. Accepts the current names ("CSR 1.3", "csr1.2", "fsr3"...) and every older
    /// name ("temporal", "spatial", "FrameFX Temporal", "FrameFX Spatial"), so saved configs and shortcuts
    /// from 1.3 and earlier keep loading. (Settings store the enum, whose members keep their old names.)</summary>
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
            "csrai" => Backend.CsrAi,
            _ => Backend.Temporal,   // "temporal", "framefxtemporal", "csr1.3", "csr13", unknown
        };
    }
}

public static class Backends
{
    /// <summary>Runs in a vendor runtime (D3D12): FSR 2/3/4, XeSS.</summary>
    public static bool IsVendor(Backend b) => b is Backend.Fsr2 or Backend.Fsr3 or Backend.Fsr4 or Backend.XeSS;
    /// <summary>Temporal upscalers: want jittered input and motion vectors.</summary>
    public static bool IsTemporal(Backend b) => b == Backend.Temporal || b == Backend.Csr20 || IsVendor(b);
    public static readonly Backend[] MenuOrder = { Backend.Temporal, Backend.Csr20, Backend.CsrAi, Backend.Spatial, Backend.Fsr1, Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS, Backend.Bilinear };
    public static Backend ApplyLatencyStepDown(Backend b, int level, bool competitive, bool oneToOne)
    {
        if ((competitive || level >= 3) && (IsVendor(b) || b == Backend.Csr20 || b == Backend.CsrAi)) b = Backend.Temporal;
        if (level >= 5 && (b == Backend.Temporal || IsVendor(b))) b = oneToOne ? Backend.Bilinear : Backend.Spatial;
        return b;
    }
}

/// <summary>Halton(2,3) jitter; phase count as ffxFsr2GetJitterPhaseCount. Offsets in input pixels.</summary>
public static class Jitter
{
    public static int PhaseCount(int inW, int outW)
    {
        double r = (double)outW / Math.Max(1, inW);
        return Math.Max(1, (int)Math.Ceiling(8.0 * r * r));
    }
    static float Halton(int i, int b) { float f = 1, r = 0; while (i > 0) { f /= b; r += f * (i % b); i /= b; } return r; }
    public static (float x, float y) Offset(int index, int phases)
    {
        int i = (index % Math.Max(1, phases)) + 1;
        return (Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }
}

/// <summary>Every GPU stage. Callers must hold Gpu.Lock.</summary>
public sealed class FramePipeline : IDisposable
{
    const int Levels = 5;
    readonly Gpu _g;
    ID3D11Device Dev => _g.Device;

    int _inW, _inH, _outW, _outH;
    Tex? _gmv;   // 1.3.4: global pan estimate (1x1, UV). FX_GlobalMotion backward vector.
    bool _globValid;   // _gmv holds an estimate from the last Process that ran the pass
    bool _panUpdated;  // this Process refreshed _globValid (otherwise the next SSGI frame must not reuse it)
    /// <summary>When set, the fast and quality motion passes still run FX_GlobalMotion if frame generation is off.
    /// One group on luma that is already being built. The vector is not fed into the motion search unless frame generation is on.</summary>
    public bool WantGlobalPan { get; set; }
    /// <summary>When set, a Process that did not already estimate motion still runs the motion pass, so the next
    /// frame can read it. The upscale output is unchanged.</summary>
    public bool WantMotionField { get; set; }
    bool _motionValid;
    bool _motionWrote;
    /// <summary>Previous Process's FX_GlobalMotion result (1x1 R32G32_Float, backward UV), or null if that pass did not run.</summary>
    public ID3D11ShaderResourceView? GlobalPanSrv => _globValid && _gmv != null ? _gmv.Srv : null;
    /// <summary>Motion field written by the previous Process (xy in source pixels), or null if that pass did not run.</summary>
    public ID3D11ShaderResourceView? PrevMotionSrv => _motionValid && LastMotion != null ? LastMotion.Srv : null;
    public bool GlobalPanValid => _globValid && _gmv != null;
    Tex? _aaA, _aaB; bool _aaPrevValid;   // 1.3.4: anti-aliased half-res luma for frame-gen motion (ping-pong)
    Tex[] _lumaA = Array.Empty<Tex>(), _lumaB = Array.Empty<Tex>(), _mvL = Array.Empty<Tex>(), _mvS = Array.Empty<Tex>();
    Tex? _mvPx, _mvFinal, _dilated, _disoccl, _reactive, _newLocks, _lockA, _lockB, _histA, _histB, _color, _spatialMid;
    Tex? _hwSeed, _fwd, _count, _hud, _tiles, _l8A, _l8B;
    Tex? _mvLo, _tileCnt, _tileRef;                       // Performance preset
    int _fRing, _forceFrames, _fgForce;
    bool _lastFast, _lastFgOn;
    /// <summary>Performance preset (1.3.1 default): low-res motion search, one fused output pass,
    /// unchanged tiles skipped. False = Quality preset (the 1.3 pipeline).</summary>
    public bool Fast { get; set; } = true;
    /// <summary>Final motion level for the Performance preset (1 = half res, 2 = quarter res).</summary>
    public static int FastLevel = int.TryParse(Environment.GetEnvironmentVariable("UFX_FAST_LEVEL"), out var fl) && fl is 1 or 2 ? fl : 1;
    /// <summary>Luma sharpening amount of the fused pass at 0 stops.</summary>
    public static float FastSharpBase = float.TryParse(Environment.GetEnvironmentVariable("UFX_FAST_SHARP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fs) ? fs : 0.15f;
    static float EnvF(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static float FastConfK = EnvF("UFX_FAST_CONF", 0.5f), FastMinW = EnvF("UFX_FAST_MINW", 0.15f),
        FastStatMargin = EnvF("UFX_FAST_STAT", 0.0005f), FastAccumMax = EnvF("UFX_FAST_ACC", 16f),
        FastFgK = EnvF("UFX_FAST_FGK", 1f), FastFgMvW = EnvF("UFX_FAST_FGMV", 0f), FastFgGate = EnvF("UFX_FAST_FGGATE", 0.1f);
    public static float[]? FastOracle = Environment.GetEnvironmentVariable("UFX_FAST_ORACLE") is { } fo ? fo.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
    public static bool FastNoSkip = Environment.GetEnvironmentVariable("UFX_FAST_NOSKIP") == "1";
    /// <summary>1.3.4 latency budget: final Performance motion at 1/4 input res instead of 1/2 (FastLevel 2).</summary>
    public bool LowResMotion { get; set; }
    /// <summary>1.3.4 latency budget: all generated frames in one fused pass (FX_FastInterpN, Performance preset).</summary>
    public bool FusedFg { get; set; }
    /// <summary>1.3.4 latency budget: use FrameFX generation even when AMD FSR 3 FG is selected (saves the ~4 ms
    /// D3D12 round trip on an RTX 3060).</summary>
    public bool ForceFrameFxFg { get; set; }
    /// <summary>1.3.4 quality: edge-aware motion-vector pick in the generation passes (~10 extra taps per pixel, once
    /// per real frame in the fused pass). The latency budget turns it off from level 4. UFX_FG_PICK=0 disables it.</summary>
    public bool PickVectors { get; set; } = true;
    /// <summary>1.3.4 Competitive preset: skip the optional border/HUD motion passes (UFX_FG_COMPFIX=1 keeps them).</summary>
    public bool Lean { get; set; }
    /// <summary>1.3.4 round 4 Competitive: 3-candidate final pick (start, own texel, camera pan) + 2nd median, no 1/4 propagation.</summary>
    public bool PickLite { get; set; }
    public bool SkipClean { get; set; }
    /// <summary>Round 4: per-step vector in the fused pass (0 = shared t = 0.5 field; 1 = one fixed-point motion tap at t for
    /// steps t &lt;= 0.5; 2 = that tap for every step; 3 = the per-frame vector, two taps per step; 4 = mode 1 during fast
    /// camera pans only). UFX_FG_FUSEDV=0..4 overrides.</summary>
    public int FusedVecMode { get; set; }
    public static readonly int FusedVecEnv = int.TryParse(Environment.GetEnvironmentVariable("UFX_FG_FUSEDV"), out var fv) ? fv : -1;
    /// <summary>Round 4: UFX_FG_PERFRAME=1 restores the Performance preset's one-pass-per-generated-frame path.</summary>
    public static readonly bool PerFrameEnv = Environment.GetEnvironmentVariable("UFX_FG_PERFRAME") == "1";
    public static readonly bool Lk2Env = Environment.GetEnvironmentVariable("UFX_FG_LK2") == "1";
    /// <summary>Self-test ablation: two LK iterations at the final level.</summary>
    public bool Lk2Override { get; set; }
    public static readonly bool CompLeanEnv = Environment.GetEnvironmentVariable("UFX_FG_COMPLEAN") == "1";
    static readonly bool PickEnv = Environment.GetEnvironmentVariable("UFX_FG_PICK") != "0";
    /// <summary>Per-output-pixel vector pick in the generation pass: off by default since the border/HUD motion passes
    /// (harness: neutral, ~0.25 ms at 1440p); UFX_FG_PIXELPICK=1 turns it on.</summary>
    static readonly bool PixelPickEnv = Environment.GetEnvironmentVariable("UFX_FG_PIXELPICK") == "1";
    int _lvl = FastLevel;
    int WantLevel => LowResMotion ? 2 : FastLevel;
    public const string FastMotionName = "CSR low-res motion search (1/8 → 1/2 res)";
    /// <summary>Motion texture of the last frame (either preset).</summary>
    internal Tex[] DebugMvLevels => _mvL;
    internal Tex[] DebugLuma(bool curr) => (_ping == 0) == curr ? _lumaB : _lumaA;
    public Tex? MotionTex => Fast ? _mvLo : _mvFinal;
    IMotionEngine? _engine;
    bool _engineTried;
    MotionPreference _enginePref = (MotionPreference)(-1);
    public List<string> MotionLog { get; } = new();
    Tex[] _outputs = Array.Empty<Tex>(), _mids = Array.Empty<Tex>();
    int _ring, _ping, _histPing, _lockPing;
    bool _hasHistory, _hasPrevLuma;
    float _pjx, _pjy;
    Backend? _lastBackend;
    Tex? _prevOutput;

    public Tex? LastMotion { get; private set; }
    public float Sharpness { get; set; } = 0.25f;
    /// <summary>Diagnostics only: treat the scene as static (zero motion, full confidence).</summary>
    public bool OracleStatic { get; set; }
    /// <summary>Lanczos footprint scale for un-jittered (captured) input; 0.8 measured best in the self-test.</summary>
    public static float NoJitterKernel = 0.8f;

    /// <summary>Which motion source to use (Auto = NVIDIA Optical Flow / D3D12 ME / software).</summary>
    public MotionPreference Motion { get; set; } = MotionPreference.Auto;
    /// <summary>Motion source that produced the last vectors.</summary>
    public string MotionSource { get; private set; } = MotionEngines.SoftwareName;
    /// <summary>Static tiles take the spatial path (1.2). False = 1.1 behaviour.</summary>
    public bool StaticTiles { get; set; } = true;
    /// <summary>1.2 frame generation (occlusion-aware, HUD-protected). False = 1.1 kernel.</summary>
    public bool NewFrameGen { get; set; } = true;
    /// <summary>Test scene only: draw a static HUD.</summary>
    public bool TestHud { get; set; }
    public const int TileSize = 16;
    public static bool FgDebug = Environment.GetEnvironmentVariable("UFX_FGDEBUG") == "1";
    public Tex? Forward => _fwd;
    public IMotionEngine? Engine => _engine;
    public Tex? HudMask => _hud;
    public Tex? Tiles => _tiles;

    public FramePipeline(Gpu gpu) { _g = gpu; }

    VendorPath? _vendor;
    Fsr1? _fsr1;
    Csr2Upscale? _csr2;
    CsrAiUpscale? _csrAi;
    readonly Dictionary<Backend, string> _vendorFailed = new();
    string? _fgFailed;
    public bool UseV2 { get; set; } = Environment.GetEnvironmentVariable("UFX_FG_V2") == "1";
    public bool UseCsrAiFg { get; set; }
    CsrAiFg? _csrAiFg;
    bool _csrAiFgFailed;
    public bool CsrAiFgFailed => _csrAiFgFailed;
    public string? CsrAiFgFailReason { get; private set; }
    public CsrAiFg? CsrAiFg => _csrAiFg;

    void FailCsrAiFg(string where, Exception ex)
    {
        _csrAiFgFailed = true;
        CsrAiFgFailReason = where + ": " + ex.Message;
        MotionLog.Add("CSR Flux failed (" + where + "), falling back to CSR 2.0: " + ex.Message);
    }
    public bool UseV2Async { get; set; } = Environment.GetEnvironmentVariable("UFX_FG_V2_ASYNC") == "1";
    public bool FgV2VsyncGen { get; set; }
    public bool FgV2ExtrapOn { get; set; }
    public bool LowResGen { get; set; }
    public int MotionGapN { get; set; }
    bool _classicLumaReady;
    public string? FgV2HudSeed { get; set; }
    FgV2? _v2;
    FgV2Async? _v2Async;
    bool _v2Failed, _v2AsyncFailed;
    public bool FgV2Active { get; private set; }
    public string FlowEstimatorName => _v2?.EstimatorName ?? "";
    /// <summary>Frame generation implementation (FrameFX's own, or AMD FSR 3 frame generation).</summary>
    public FgKind FrameGenKind { get; set; } = FgKind.FrameFX;
    /// <summary>Skip FSR 2/3/4 / XeSS when input and output are the same size (UFX_VENDOR_1TO1=1 forces them to run).</summary>
    public static bool VendorPassthrough = Environment.GetEnvironmentVariable("UFX_VENDOR_1TO1") != "1";
    /// <summary>What actually produced the last frame (e.g. "AMD FSR 3.1.4"), and any fallback note.</summary>
    public string ActiveUpscaler { get; private set; } = "";
    public string ActiveFrameGen { get; private set; } = "";
    public string? VendorNote { get; private set; }
    /// <summary>1.3.4 multi-frame generation: the generated frames of the last Process() call, in display order
    /// (t = 1/N … (N-1)/N between the previous and the current output). Empty when nothing was generated.</summary>
    public IReadOnlyList<Tex> Generated => _generated;
    Tex[] _generated = Array.Empty<Tex>();
    /// <summary>Generated-frame textures per ring slot and step ([slot][k-1]); two slots, so FX_FastInterp's skipped
    /// tiles still hold the same-t content from two frames ago. Allocated lazily for the steps in use.</summary>
    readonly Tex?[][] _gens = { new Tex?[7], new Tex?[7] };
    Tex? _fgPrepA, _fgPrepB;   // round 4: FX_FgPrep copies of A / B with the static mask in alpha
    /// <summary>Round 4 (experimental, off by default): static-mask pre-pass for the fused generation pass, UFX_FG_PREP=1.
    /// Fewer taps per generated frame (tap-count estimate: Competitive 720p->1080p x8 -0.10 ms) but two extra full-frame
    /// copies (a bandwidth estimate says +0.1 ms); the self-test 'Preset timing' ablation rows measure it.</summary>
    public static readonly bool FgPrepEnv = Environment.GetEnvironmentVariable("UFX_FG_PREP") == "1";
    public bool FgPrep { get; set; } = FgPrepEnv;
    int _genRing, _lastMul = 2;
    /// <summary>Skip frame generation while the captured picture is unchanged (UFX_FG_SKIPSTATIC=0 turns it off).</summary>
    public bool SkipUnchanged { get; set; } = Environment.GetEnvironmentVariable("UFX_FG_SKIPSTATIC") != "0";
    /// <summary>The last Process() call skipped frame generation because the picture had not changed.</summary>
    public bool LastSkippedUnchanged { get; private set; }
    /// <summary>×N (N even) with AMD FSR 3: use AMD FSR 3 FG for the t = 0.5 frame (UFX_FG_FSRMID=1). Off by default:
    /// mixing AMD's midpoint with FrameFX's other steps makes the look alternate frame to frame.</summary>
    /// <summary>Pre-tuning FX_FastInterp (UFX_FG_LEGACY=1) for A/B comparison; the fused pass always uses the tuned logic.</summary>
    /// <summary>3x3 vector median of the motion field before generation (UFX_FG_MVMEDIAN=0 turns it off).</summary>
    public static bool FgMvMedian = Environment.GetEnvironmentVariable("UFX_FG_MVMEDIAN") != "0";
    Tex? _mvMed, _mvCln;
    /// <summary>UFX_FG_MVCLEAN=0 turns off the final-field neighbour propagation.</summary>
    /// <summary>UFX_FG_LUMAAA=0: plain box 1/4-res luma even with frame gen on.</summary>
    public static bool LumaAaEnv = Environment.GetEnvironmentVariable("UFX_FG_LUMAAA") != "0";
    /// <summary>UFX_FG_MVBORDER=0 turns off the frame-border vector re-test.</summary>
    public static bool MvBorderEnv = Environment.GetEnvironmentVariable("UFX_FG_MVBORDER") != "0";
    /// <summary>UFX_FG_MVHUD=0 turns off the vector re-test next to static overlay pixels.</summary>
    public static bool MvHudEnv = Environment.GetEnvironmentVariable("UFX_FG_MVHUD") != "0";
    public static bool HudPanEnv = Environment.GetEnvironmentVariable("UFX_FG_HUDPAN") != "0";
    /// <summary>FX_MvClean is off by default since the border/HUD passes (harness +0.05 dB for ~0.25 ms at 1440p); UFX_FG_MVCLEAN=1 turns it on.</summary>
    /// <summary>Out-of-range-motion fallback (repeat the nearest real frame during flicks beyond the search range).
    /// Opt-in (UFX_FG_MOTIONFAIL=1): the harness scores the garbage warp slightly above the repeat (PSNR/SSIM can't see
    /// tearing vs judder), so it is not on by default.</summary>
    public static bool MotionFailEnv = Environment.GetEnvironmentVariable("UFX_FG_MOTIONFAIL") == "1";
    /// <summary>UFX_FG_GLOBAL=0 turns off the global-pan motion candidate.</summary>
    public static bool GlobalEnv = Environment.GetEnvironmentVariable("UFX_FG_GLOBAL") != "0";
    public static bool GlobalBorderEnv = Environment.GetEnvironmentVariable("UFX_FG_GLOBALBORDER") != "0";
    /// <summary>UFX_FG_Q2=1: the Quality preset keeps the 1.2 bidirectional generator (FX_Interpolate2).</summary>
    public static bool QualityOldFg = Environment.GetEnvironmentVariable("UFX_FG_Q2") == "1";
    public static bool MvCleanEnv = Environment.GetEnvironmentVariable("UFX_FG_MVCLEAN") == "1";
    /// <summary>Motion for the generation passes: the field, vector-median filtered once per real frame (reused by every step).</summary>
    Tex FgMotion(Tex motion, Tex? prev, Tex curr)
    {
        if (!FgMvMedian || FgLegacy || motion.Format != Format.R16G16B16A16_Float) return motion;
        if (_mvMed == null || _mvMed.Width != motion.Width || _mvMed.Height != motion.Height)
        { _mvMed?.Dispose(); _mvMed = new Tex(Dev, motion.Width, motion.Height, Format.R16G16B16A16_Float); }
        // 1.3.4: neighbour-vector propagation on the final field against the two real frames (budget level < 4).
        bool rich = PickVectors && PickEnv;
        bool med2 = rich || (PickLite && PickEnv);
        if ((med2 || ((MvBorderEnv || MvHudEnv) && prev != null)) && (_mvCln == null || _mvCln.Width != motion.Width || _mvCln.Height != motion.Height))
        { _mvCln?.Dispose(); _mvCln = new Tex(Dev, motion.Width, motion.Height, Format.R16G16B16A16_Float); }
        if (prev != null && rich && MvCleanEnv)
        {
            _g.SetParams(Base(0, 0));
            _g.Dispatch("FX_MvClean", _mvCln!.Width, _mvCln.Height, new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, motion.Srv) }, new[] { (3, _mvCln.Uav) });
            motion = _mvCln;
        }
        _g.Dispatch("FX_MvMedian", _mvMed.Width, _mvMed.Height, new[] { (4, (ID3D11ShaderResourceView?)motion.Srv) }, new[] { (3, _mvMed.Uav) });
        Tex outM = _mvMed, spare = _mvCln!;
        if (med2)
        {
            // Second median pass (harness +0.15 dB): _mvMed -> _mvCln (the clean output has been consumed by now).
            _g.Dispatch("FX_MvMedian", _mvCln!.Width, _mvCln.Height, new[] { (4, (ID3D11ShaderResourceView?)_mvMed.Srv) }, new[] { (3, _mvCln.Uav) });
            outM = _mvCln; spare = _mvMed;
        }
        if (MvBorderEnv && !Lean && prev != null)
        {
            // Frame borders: off-screen sources -> re-test vectors from further inside (only the 24-texel band works).
            _g.SetParams(Base(0, 0));
            // 1.3.4 round 3: + global pan (t17) for edge texels whose source is off-screen, when the field inside agrees.
            var gB = _globValid && MotionSource == FastMotionName && GlobalBorderEnv ? _gmv?.Srv : null;
            _g.Dispatch("FX_MvBorder", spare.Width, spare.Height, new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, outM.Srv), (17, gB) }, new[] { (3, spare.Uav) });
            (outM, spare) = (spare, outM);
        }
        if (MotionFailEnv && prev != null && _chgOut != null)
        {
            // Out-of-range motion (fast flick): vote a scene cut so generation repeats the nearest real frame.
            _g.SetParams(Base(0, 0));
            _g.DispatchGroups("FX_MotionFail", 1, 1, new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, outM.Srv) }, new[] { (0, _chgOut.Uav) });
        }
        if (MvHudEnv && !Lean && prev != null)
        {
            // Next to static overlay pixels: re-test nearby vectors with a moving-pixels-only cost.
            _g.SetParams(Base(0, 0));
            // Round 4: + global pan (t17): the pass is skipped during fast pans.
            var gH = _globValid && MotionSource == FastMotionName && HudPanEnv ? _gmv?.Srv : null;
            _g.Dispatch("FX_MvHud", spare.Width, spare.Height, new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, outM.Srv), (17, gH) }, new[] { (3, spare.Uav) });
            outM = spare;
        }
        return outM;
    }
    public static bool FgLegacy = Environment.GetEnvironmentVariable("UFX_FG_LEGACY") == "1";
    public static bool FsrMid = Environment.GetEnvironmentVariable("UFX_FG_FSRMID") == "1";
    Tex? _chgPrev, _chgOut;
    readonly ID3D11Texture2D?[] _chgStage = new ID3D11Texture2D?[3];
    int _chgFrame, _unchangedRun;
    bool _chgHist;
    /// <summary>D3D12 time of the vendor runtime's last submission (ms), 0 if none.</summary>
    public double VendorMs { get; private set; }

    public void Reset() { _forceFrames = 2; _fgForce = 2; _chgHist = false; _chgFrame = 0; _unchangedRun = 0; _generated = Array.Empty<Tex>(); _hasHistory = false; _hasPrevLuma = false; _globValid = false; _motionValid = false; _classicLumaReady = false; _pjx = _pjy = 0; _prevOutput = null; _lastBackend = null; _engine?.ResetHistory(); _vendor?.ResetHistory(); _csr2?.ResetHistory(); _v2?.Reset(); }

    /// <summary>Forget earlier vendor failures (e.g. after the user picks the option again).</summary>
    public void ClearVendorFailures() { _vendorFailed.Clear(); _fgFailed = null; VendorNote = null; _v2Failed = false; _v2AsyncFailed = false; _csrAiFgFailed = false; CsrAiFgFailReason = null; }

    static void DisposeAll(IEnumerable<Tex?> ts) { foreach (var t in ts) t?.Dispose(); }

    public void Ensure(int inW, int inH, int outW, int outH)
    {
        if (inW == _inW && inH == _inH && outW == _outW && outH == _outH && _histA != null) return;
        DisposeTextures();
        _inW = inW; _inH = inH; _outW = outW; _outH = outH;
        Reset();
        var la = new List<Tex>(); var lb = new List<Tex>(); var ml = new List<Tex>(); var ms = new List<Tex>();
        int w = inW, h = inH;
        for (int i = 0; i < Levels; i++)
        {
            la.Add(new Tex(Dev, w, h, Format.R16_Float)); lb.Add(new Tex(Dev, w, h, Format.R16_Float));
            ml.Add(new Tex(Dev, w, h, Format.R16G16_Float)); ms.Add(new Tex(Dev, w, h, Format.R16G16_Float));
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        _lumaA = la.ToArray(); _lumaB = lb.ToArray(); _mvL = ml.ToArray(); _mvS = ms.ToArray();
        _aaA = new Tex(Dev, _lumaA[1].Width, _lumaA[1].Height, Format.R16_Float); _aaB = new Tex(Dev, _lumaA[1].Width, _lumaA[1].Height, Format.R16_Float); _aaPrevValid = false;
        _gmv = new Tex(Dev, 1, 1, Format.R32G32_Float);
        _mvPx = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _mvFinal = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _dilated = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _disoccl = new Tex(Dev, inW, inH, Format.R16_Float);
        _reactive = new Tex(Dev, inW, inH, Format.R16_Float);
        _newLocks = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _lockA = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _lockB = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _histA = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _histB = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _color = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _spatialMid = new Tex(Dev, outW, outH, Format.R16G16B16A16_Float);
        _outputs = Enumerable.Range(0, 4).Select(_ => new Tex(Dev, outW, outH, Format.R8G8B8A8_UNorm)).ToArray();
        _mids = Enumerable.Range(0, 4).Select(_ => new Tex(Dev, outW, outH, Format.R8G8B8A8_UNorm)).ToArray();
        _hwSeed = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _fwd = new Tex(Dev, inW, inH, Format.R16G16B16A16_Float);
        _count = new Tex(Dev, inW, inH, Format.R32_Float);
        _hud = new Tex(Dev, inW, inH, Format.R16_Float);
        _tiles = new Tex(Dev, (inW + TileSize - 1) / TileSize, (inH + TileSize - 1) / TileSize, Format.R16_Float);
        _l8A = new Tex(Dev, inW, inH, Format.R8_UNorm);
        _l8B = new Tex(Dev, inW, inH, Format.R8_UNorm);
        _lvl = WantLevel;
        int ls = 1 << _lvl;
        _mvLo = new Tex(Dev, Math.Max(1, inW / ls), Math.Max(1, inH / ls), Format.R16G16B16A16_Float);
        _tileCnt = new Tex(Dev, _tiles.Width, _tiles.Height, Format.R32_Float);
        _tileRef = new Tex(Dev, Math.Max(1, inW / 2), Math.Max(1, inH / 2), Format.R32_Float);
        _chgPrev = new Tex(Dev, 64, 64, Format.R32_Float);
        _chgOut = new Tex(Dev, 2, 1, Format.R32_Float);   // [0] changed samples, [1] scene-cut votes
        for (int i = 0; i < _chgStage.Length; i++)
            _chgStage[i] = Dev.CreateTexture2D(new Texture2DDescription
            {
                Width = 2, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R32_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
            });
        _g.Context.ClearUnorderedAccessView(_tileCnt.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _g.Context.ClearUnorderedAccessView(_count.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _g.Context.ClearUnorderedAccessView(_hud.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _g.Context.ClearUnorderedAccessView(_tiles.Uav!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
    }

    void EnsureEngine()
    {
        if (_engineTried && _enginePref == Motion) return;
        _engine?.Dispose(); _engine = null;
        _engineTried = true; _enginePref = Motion;
        MotionLog.Clear();
        try { _engine = MotionEngines.Create(_g, _inW, _inH, Motion, MotionLog); }
        catch (Exception ex) { MotionLog.Add("hardware motion: " + ex.Message); _engine = null; }
    }

    void DisposeTextures()
    {
        DisposeAll(_lumaA); DisposeAll(_lumaB); DisposeAll(_mvL); DisposeAll(_mvS); DisposeAll(new[] { _aaA, _aaB, _gmv }); _aaA = _aaB = _gmv = null;
        DisposeAll(new[] { _mvPx, _mvFinal, _dilated, _disoccl, _reactive, _newLocks, _lockA, _lockB, _histA, _histB, _color, _spatialMid });
        DisposeAll(_outputs); DisposeAll(_mids);
        foreach (var g in _gens) { DisposeAll(g); Array.Clear(g); }
        DisposeAll(new[] { _fgPrepA, _fgPrepB }); _fgPrepA = _fgPrepB = null;
        DisposeAll(new[] { _chgPrev, _chgOut, _mvMed, _mvCln }); _chgPrev = _chgOut = _mvMed = _mvCln = null;
        for (int i = 0; i < _chgStage.Length; i++) { _chgStage[i]?.Dispose(); _chgStage[i] = null; }
        _generated = Array.Empty<Tex>();
        DisposeAll(new[] { _hwSeed, _fwd, _count, _hud, _tiles, _l8A, _l8B, _mvLo, _tileCnt, _tileRef });
        _engine?.Dispose(); _engine = null; _engineTried = false;
        _histA = null; _prevOutput = null;
    }

    FxParams Base(float jx, float jy) => new()
    {
        InW = _inW, InH = _inH, OutW = _outW, OutH = _outH,
        JitterX = jx, JitterY = jy, PrevJitterX = _pjx, PrevJitterY = _pjy,
        JitterDeltaX = jx - _pjx, JitterDeltaY = jy - _pjy,
        HistoryValid = _hasHistory ? 1 : 0, Sharpness = Sharpness, MaxAccum = 16,
        JitterActive = (jx != 0 || jy != 0 || _pjx != 0 || _pjy != 0) ? 1 : 0,
        InterpT = 0.5f, Sensitivity = 0.45f, LevelScale = 2, Pad0 = OracleStatic ? 1 : 0, Pad1 = NoJitterKernel,
        Pad3 = FgDebug ? 1 : 0, TestHud = TestHud ? 1 : 0, StaticThresh = 1.5f / 255f, TileSize = TileSize, StaticOn = StaticTiles ? 1 : 0,
        FastSharp = FastSharpBase * MathF.Pow(2f, -Sharpness), FastForce = 1, FastLevelScale = 1 << _lvl,
        FastConfK = FastConfK, FastMinW = FastMinW, FastStatMargin = FastStatMargin, FastAccumMax = FastAccumMax,
        FastFgK = FastFgK, FastFgMvW = FastFgMvW, FastFgGate = FastFgGate,
    };

    int TilesX => (_inW + TileSize - 1) / TileSize;
    int TilesY => (_inH + TileSize - 1) / TileSize;

    /// <summary>Performance preset motion: half-res luma pyramid, groupshared full search at 1/8 res,
    /// integer refine at 1/4, sub-pixel fit + static prior + jitter cancel at 1/2 (or 1/4) res, and
    /// per-tile change counters. No hardware engine, no CPU waits.</summary>
    public void EncodeMotionFast(ID3D11ShaderResourceView input, float jx, float jy)
    {
        var curr = _ping == 0 ? _lumaA : _lumaB;
        var prev = _ping == 0 ? _lumaB : _lumaA;
        var p = Base(jx, jy); p.HistoryValid = _hasPrevLuma ? 1 : 0;
        _g.SetParams(p);
        _g.Dispatch("FX_FastLuma", curr[1].Width, curr[1].Height, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (0, curr[1].Uav) });
        bool aa = _lastFgOn && PickVectors && PickEnv && LumaAaEnv && _aaA != null && _aaB != null;
        var aaC = _ping == 0 ? _aaA! : _aaB!; var aaP = _ping == 0 ? _aaB! : _aaA!;
        if (aa)
        {
            _g.Dispatch("FX_FastLumaAA", aaC.Width, aaC.Height, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (0, aaC.Uav) });
            _g.Dispatch("FX_LumaHalveAA", curr[2].Width, curr[2].Height, new[] { (1, (ID3D11ShaderResourceView?)aaC.Srv) }, new[] { (0, curr[2].Uav) });
        }
        else
            _g.Dispatch("FX_LumaHalve", curr[2].Width, curr[2].Height, new[] { (1, (ID3D11ShaderResourceView?)curr[1].Srv) }, new[] { (0, curr[2].Uav) });
        _g.Dispatch("FX_FastAA", curr[3].Width, curr[3].Height, new[] { (1, (ID3D11ShaderResourceView?)curr[2].Srv) }, new[] { (0, curr[3].Uav) });
        // Tile change counters first: the motion passes skip tiles that have not changed.
        _g.Dispatch("FX_FastTiles", _tileCnt!.Width, _tileCnt.Height, new[] { (3, (ID3D11ShaderResourceView?)curr[1].Srv) }, new[] { (7, _tileCnt.Uav), (4, _tileRef!.Uav) });
        p.FastForce = _forceFrames > 0 || FastNoSkip ? 1 : 0;
        _g.SetParams(p);
        var pv = _hasPrevLuma ? prev : curr;
        // 1.3.4: global pan estimate -> extra candidate for the 1/8 search and the 1/4 refine when frame generation is on.
        // SSGI steadier-lighting reads the same 1x1 (previous frame) even when frame generation is off; the search
        // itself keeps a null candidate in that case, so upscaling does not change.
        bool glob = (_lastFgOn || WantGlobalPan) && GlobalEnv && _hasPrevLuma && _gmv != null;
        if (glob)
            _g.DispatchGroups("FX_GlobalMotion", 1, 1, new[] { (2, (ID3D11ShaderResourceView?)pv[3].Srv), (3, curr[3].Srv) }, new[] { (1, _gmv!.Uav) });
        var gSrv = _lastFgOn && glob ? _gmv!.Srv : null;
        _globValid = glob;
        _panUpdated = true;
        _g.DispatchGroups("FX_FastSearch", (_mvL[3].Width + 7) / 8, (_mvL[3].Height + 7) / 8,
            new[] { (2, (ID3D11ShaderResourceView?)pv[3].Srv), (3, curr[3].Srv), (17, gSrv) }, new[] { (1, _mvL[3].Uav) });
        _g.Dispatch("FX_FastRefine", _mvL[2].Width, _mvL[2].Height,
            new[] { (2, (ID3D11ShaderResourceView?)pv[2].Srv), (3, curr[2].Srv), (4, _mvL[3].Srv), (15, _mvLo!.Srv), (10, _tileCnt.Srv), (17, gSrv) }, new[] { (1, _mvL[2].Uav) });
        bool clean = !(SkipClean && _lastFgOn);   // Competitive: no 1/4 propagation (harness -0.14 dB, est. -0.05 ms)
        if (clean)
            _g.Dispatch("FX_FastClean", _mvL[2].Width, _mvL[2].Height,
                new[] { (2, (ID3D11ShaderResourceView?)pv[2].Srv), (3, curr[2].Srv), (4, _mvL[2].Srv), (10, _tileCnt.Srv), (17, gSrv) }, new[] { (1, _mvS[2].Uav) });
        p.ApplyJitterCancel = 1;
        p.Pad2 = _lastFgOn && (PickVectors || PickLite) && PickEnv ? 1 : 0;   // 1.3.4: final-level neighbour vector pick (frame gen only)
        p.Pad6 = (Lk2Env || Lk2Override ? 2 : 1) + (_lastFgOn && PickLite && !PickVectors ? 4 : 0);   // FastFinal: LK iterations, 3-candidate pick
        if (FastOracle is { Length: 2 } fo2) { p.Pad5 = 1; p.PanX = fo2[0]; p.PanY = fo2[1]; }
        _g.SetParams(p);
        int L = _lvl;
        bool aaF = aa && L == 1;
        var fPrev = aaF ? (_hasPrevLuma && _aaPrevValid ? aaP : aaC) : pv[L];
        var fCurr = aaF ? aaC : curr[L];
        _g.Dispatch("FX_FastFinal", _mvLo!.Width, _mvLo.Height,
            new[] { (2, (ID3D11ShaderResourceView?)fPrev.Srv), (3, fCurr.Srv), (4, (clean ? _mvS[2] : _mvL[2]).Srv), (10, _tileCnt.Srv), (17, gSrv) }, new[] { (2, _mvLo.Uav) });
        _aaPrevValid = aa;
        MotionSource = FastMotionName;
        LastMotion = _mvLo;
        _motionValid = true;
        _motionWrote = true;
        _g.Stage("motion");
        _ping = 1 - _ping;
        _pjx = jx; _pjy = jy;
        _hasPrevLuma = true;
    }

    void RunClassicCandKernels()
    {
        var curr = _ping == 0 ? _lumaB : _lumaA;
        var prev = _ping == 0 ? _lumaA : _lumaB;
        var p = Base(0, 0);
        p.HistoryValid = _classicLumaReady ? 1 : 0;
        p.FastForce = 1;
        p.ApplyJitterCancel = 1;
        p.Pad2 = _lastFgOn && (PickVectors || PickLite) && PickEnv ? 1 : 0;
        p.Pad6 = (Lk2Env || Lk2Override ? 2 : 1) + (_lastFgOn && PickLite && !PickVectors ? 4 : 0);
        _g.SetParams(p);
        var pv = _classicLumaReady ? prev : curr;
        var gSrv = _globValid && _gmv != null ? _gmv.Srv : null;
        _g.DispatchGroups("FX_FastSearch", (_mvL[3].Width + 7) / 8, (_mvL[3].Height + 7) / 8,
            new[] { (2, (ID3D11ShaderResourceView?)pv[3].Srv), (3, curr[3].Srv), (17, gSrv) }, new[] { (1, _mvL[3].Uav) });
        _g.Dispatch("FX_FastRefine", _mvL[2].Width, _mvL[2].Height,
            new[] { (2, (ID3D11ShaderResourceView?)pv[2].Srv), (3, curr[2].Srv), (4, _mvL[3].Srv), (15, _mvLo!.Srv), (10, _tileCnt!.Srv), (17, gSrv) }, new[] { (1, _mvL[2].Uav) });
        _g.Dispatch("FX_FastFinal", _mvLo!.Width, _mvLo.Height,
            new[] { (2, (ID3D11ShaderResourceView?)pv[1].Srv), (3, curr[1].Srv), (4, _mvL[2].Srv), (10, _tileCnt.Srv), (17, gSrv) }, new[] { (2, _mvLo.Uav) });
        _classicLumaReady = true;
    }

    /// <summary>Performance preset CSR 1.3: one fused pass per changed tile.</summary>
    public void EncodeTemporalFast(ID3D11ShaderResourceView input, float jx, float jy, Tex output)
    {
        var p = Base(jx, jy);
        EncodeMotionFast(input, jx, jy);
        p.FastForce = _forceFrames > 0 || FastNoSkip ? 1 : 0;
        _g.SetParams(p);
        var hIn = _histPing == 0 ? _histA! : _histB!;
        var hOut = _histPing == 0 ? _histB! : _histA!;
        _g.DispatchGroups("FX_FastAccum", TilesX, TilesY,
            new[] { (0, (ID3D11ShaderResourceView?)input), (4, _mvLo!.Srv), (9, hIn.Srv), (10, _tileCnt!.Srv) },
            new[] { (3, hOut.Uav), (5, output.Uav) });
        _histPing = 1 - _histPing;
        _hasHistory = true;
        if (_forceFrames > 0) _forceFrames--;
    }

    /// <summary>Luma pyramid, motion (hardware engine or hierarchical block matching) in both
    /// directions, and static-area detection (current vs previous frame).</summary>
    public void EncodeMotion(ID3D11ShaderResourceView input, float jx, float jy)
    {
        var curr = _ping == 0 ? _lumaA : _lumaB;
        var prev = _ping == 0 ? _lumaB : _lumaA;
        var c8 = _ping == 0 ? _l8A! : _l8B!;
        var p8 = _ping == 0 ? _l8B! : _l8A!;
        var p = Base(jx, jy);
        _g.SetParams(p);
        _g.Dispatch("FX_LumaExtract", curr[0].Width, curr[0].Height, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (0, curr[0].Uav) });
        for (int i = 1; i < Levels; i++)
            _g.Dispatch("FX_LumaHalve", curr[i].Width, curr[i].Height, new[] { (1, (ID3D11ShaderResourceView?)curr[i - 1].Srv) }, new[] { (0, curr[i].Uav) });

        var pv = _hasPrevLuma ? prev : curr;
        if (WantGlobalPan && GlobalEnv && _hasPrevLuma && _gmv != null && curr.Length > 3)
        {
            _g.DispatchGroups("FX_GlobalMotion", 1, 1, new[] { (2, (ID3D11ShaderResourceView?)pv[3].Srv), (3, curr[3].Srv) }, new[] { (1, _gmv!.Uav) });
            _globValid = true;
        }
        else _globValid = false;
        _panUpdated = true;
        bool hw = false;
        if (Motion != MotionPreference.Software && !OracleStatic)
        {
            EnsureEngine();
            if (_engine != null)
            {
                _g.Dispatch("FX_Luma8", _inW, _inH, new[] { (1, (ID3D11ShaderResourceView?)curr[0].Srv) }, new[] { (0, c8.Uav) });
                if (_hasPrevLuma)
                {
                    try
                    {
                        _engine.Estimate(p8, c8);
                        // Cheap coarse software prior (levels 4..2) guards against aliasing.
                        int coarseLevel = SoftwarePyramid(pv, curr, p, 2);
                        var ph = p; ph.HwScale = _engine.Scale; ph.HwGrid = _engine.Grid; ph.Pad2 = 1; _g.SetParams(ph);
                        // Backward (curr -> prev) seed with consistency, then forward (prev -> curr).
                        _g.Dispatch("FX_HwImport", _inW, _inH,
                            new[] { (20, (ID3D11ShaderResourceView?)_engine.Bwd.Srv), (21, _engine.Fwd.Srv), (2, pv[0].Srv), (3, curr[0].Srv), (4, _mvS[coarseLevel].Srv) }, new[] { (6, _hwSeed!.Uav) });
                        ph.Pad2 = -1; _g.SetParams(ph);
                        _g.Dispatch("FX_HwImport", _inW, _inH,
                            new[] { (20, (ID3D11ShaderResourceView?)_engine.Fwd.Srv), (21, _engine.Bwd.Srv), (2, curr[0].Srv), (3, pv[0].Srv), (4, _mvS[coarseLevel].Srv) }, new[] { (6, _fwd!.Uav) });
                        hw = true;
                        MotionSource = _engine.Name;
                    }
                    catch (Exception ex)
                    {
                        MotionLog.Add($"{_engine.Name} failed, using software: {ex.Message}");
                        _engine.Dispose(); _engine = null;
                    }
                }
                else MotionSource = _engine.Name;
            }
        }
        _g.SetParams(p);
        if (!hw)
        {
            SoftwarePyramid(pv, curr, p, 0);
            MotionSource = (_engine != null && !_hasPrevLuma) ? _engine.Name : MotionEngines.SoftwareName;
        }
        p.SearchRadius = 1; p.ApplyJitterCancel = 1; _g.SetParams(p);
        var seed = hw ? _hwSeed!.Srv : _mvS[0].Srv;
        _g.Dispatch("FX_MotionSubpixel", _inW, _inH,
            new[] { (2, (ID3D11ShaderResourceView?)pv[0].Srv), (3, curr[0].Srv), (4, seed) }, new[] { (2, _mvPx!.Uav) });
        _g.Dispatch("FX_MotionSmooth4", _inW, _inH, new[] { (4, (ID3D11ShaderResourceView?)_mvPx.Srv) }, new[] { (2, _mvFinal!.Uav) });
        if (!hw)
            _g.Dispatch("FX_FwdFromBwd", _inW, _inH, new[] { (4, (ID3D11ShaderResourceView?)_mvFinal.Srv) }, new[] { (6, _fwd!.Uav) });

        // Static areas: per-pixel unchanged counter + HUD mask, then per-tile static flags.
        var ps = p; ps.HistoryValid = _hasPrevLuma ? 1 : 0; _g.SetParams(ps);
        _g.Dispatch("FX_StaticCount", _inW, _inH, new[] { (2, (ID3D11ShaderResourceView?)pv[0].Srv), (3, curr[0].Srv) },
            new[] { (7, _count!.Uav) });
        _g.Dispatch("FX_HudMask", _inW, _inH, new[] { (10, (ID3D11ShaderResourceView?)_count.Srv), (3, curr[0].Srv) }, new[] { (4, _hud!.Uav) });
        _g.Dispatch("FX_StaticTiles", _tiles!.Width, _tiles.Height, new[] { (10, (ID3D11ShaderResourceView?)_count.Srv) }, new[] { (4, _tiles.Uav) });

        LastMotion = _mvFinal;
        _motionValid = true;
        _motionWrote = true;
        _ping = 1 - _ping;
        _pjx = jx; _pjy = jy;
        _hasPrevLuma = true;
    }

    /// <summary>Hierarchical block matching from the coarsest level down to <paramref name="toLevel"/>.</summary>
    int SoftwarePyramid(Tex[] pv, Tex[] curr, FxParams p, int toLevel)
    {
        int last = Levels - 1;
        p.SearchRadius = 7; _g.SetParams(p);
        _g.Dispatch("FX_MotionInit", _mvL[last].Width, _mvL[last].Height,
            new[] { (2, (ID3D11ShaderResourceView?)pv[last].Srv), (3, curr[last].Srv) }, new[] { (1, _mvL[last].Uav) });
        _g.Dispatch("FX_MotionSmooth2", _mvS[last].Width, _mvS[last].Height, new[] { (4, (ID3D11ShaderResourceView?)_mvL[last].Srv) }, new[] { (1, _mvS[last].Uav) });
        p.SearchRadius = 1; _g.SetParams(p);
        for (int l = last - 1; l >= toLevel; l--)
        {
            _g.Dispatch("FX_MotionRefine", _mvL[l].Width, _mvL[l].Height,
                new[] { (2, (ID3D11ShaderResourceView?)pv[l].Srv), (3, curr[l].Srv), (4, _mvS[l + 1].Srv) }, new[] { (1, _mvL[l].Uav) });
            _g.Dispatch("FX_MotionSmooth2", _mvS[l].Width, _mvS[l].Height, new[] { (4, (ID3D11ShaderResourceView?)_mvL[l].Srv) }, new[] { (1, _mvS[l].Uav) });
        }
        return toLevel;
    }

    public float AverageConfidence { get; private set; } = float.NaN;

    /// <summary>FSR 2-style temporal upscale into <paramref name="output"/>.</summary>
    public void EncodeTemporal(ID3D11ShaderResourceView input, float jx, float jy, Tex output)
    {
        var currL = _ping == 0 ? _lumaA : _lumaB;
        var prevL = _ping == 0 ? _lumaB : _lumaA;
        bool prevValid = _hasPrevLuma;
        var p = Base(jx, jy);          // captures prev jitter before EncodeMotion updates it
        EncodeMotion(input, jx, jy);
        _g.SetParams(p);
        var pl = prevValid ? prevL[0] : currL[0];

        _g.Dispatch("FX_Dilate", _inW, _inH, new[] { (4, (ID3D11ShaderResourceView?)_mvFinal!.Srv) }, new[] { (3, _dilated!.Uav) });
        _g.Dispatch("FX_Disocclusion", _inW, _inH,
            new[] { (2, (ID3D11ShaderResourceView?)pl.Srv), (3, currL[0].Srv), (4, _dilated.Srv) }, new[] { (4, _disoccl!.Uav) });
        _g.Dispatch("FX_Reactive", _inW, _inH,
            new[] { (2, (ID3D11ShaderResourceView?)pl.Srv), (3, currL[0].Srv), (4, _dilated.Srv) }, new[] { (4, _reactive!.Uav) });
        _g.Dispatch("FX_LockDetect", _inW, _inH, new[] { (3, (ID3D11ShaderResourceView?)currL[0].Srv) }, new[] { (3, _newLocks!.Uav) });

        var lockIn = _lockPing == 0 ? _lockA! : _lockB!;
        var lockOut = _lockPing == 0 ? _lockB! : _lockA!;
        _g.Dispatch("FX_LockUpdate", _outW, _outH,
            new[] { (4, (ID3D11ShaderResourceView?)_dilated.Srv), (5, _disoccl.Srv), (6, _reactive.Srv), (7, lockIn.Srv), (8, _newLocks.Srv) },
            new[] { (3, lockOut.Uav) });

        var hIn = _histPing == 0 ? _histA! : _histB!;
        var hOut = _histPing == 0 ? _histB! : _histA!;
        if (StaticTiles)
            _g.Dispatch("FX_EASU", _outW, _outH, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (3, _spatialMid!.Uav) });
        _g.Dispatch("FX_Accumulate", _outW, _outH,
            new[] { (0, (ID3D11ShaderResourceView?)input), (4, _dilated.Srv), (5, _disoccl.Srv), (6, _reactive.Srv), (7, lockOut.Srv), (9, hIn.Srv),
                    (11, _tiles!.Srv), (12, _spatialMid!.Srv) },
            new[] { (3, hOut.Uav), (5, _color!.Uav) });
        _g.Dispatch("FX_RCAS", _outW, _outH, new[] { (0, (ID3D11ShaderResourceView?)_color.Srv) }, new[] { (3, output.Uav) });

        LastMotion = _dilated;
        _histPing = 1 - _histPing;
        _lockPing = 1 - _lockPing;
        _hasHistory = true;
    }

    public void EncodeSpatial(ID3D11ShaderResourceView input, Tex output)
    {
        _g.SetParams(Base(0, 0));
        _g.Dispatch("FX_EASU", _outW, _outH, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (3, _spatialMid!.Uav) });
        _g.Dispatch("FX_RCAS", _outW, _outH, new[] { (0, (ID3D11ShaderResourceView?)_spatialMid.Srv) }, new[] { (3, output.Uav) });
    }

    public void EncodeBilinear(ID3D11ShaderResourceView input, Tex output)
    {
        _g.SetParams(Base(0, 0));
        _g.Dispatch("FX_Bilinear", output.Width, output.Height, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (3, output.Uav) });
    }

    bool _fastTileFg;
    public void EncodeInterpolate(Tex prev, Tex curr, Tex motion, Tex dest, float t = 0.5f, bool lastOfFrame = true)
    {
        var p = Base(0, 0); p.InterpT = t;
        if (Fast)
        {
            p.FastForce = _fastTileFg && _fgForce <= 0 ? 0 : 1; p.Pad6 = FgLegacy ? -1 : 0; p.Pad2 = PickVectors && PickEnv && PixelPickEnv ? 1 : 0; _g.SetParams(p);
            _g.DispatchGroups("FX_FastInterp", TilesX, TilesY,
                new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, motion.Srv), (10, _tileCnt!.Srv), (16, _chgOut?.Srv) }, new[] { (3, dest.Uav) });
            if (lastOfFrame && _fgForce > 0) _fgForce--;
            return;
        }
        p.Pad2 = PickVectors && PickEnv && PixelPickEnv && !QualityOldFg ? 1 : 0;
        _g.SetParams(p);
        if (NewFrameGen && !QualityOldFg && !FgLegacy)
            _g.Dispatch("FX_Interp1", dest.Width, dest.Height,
                new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, motion.Srv), (16, _chgOut?.Srv) }, new[] { (3, dest.Uav) });
        else if (NewFrameGen)
            _g.Dispatch("FX_Interpolate2", dest.Width, dest.Height,
                new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, motion.Srv), (13, _fwd!.Srv), (14, _hud!.Srv), (16, _chgOut?.Srv) }, new[] { (3, dest.Uav) });
        else
            _g.Dispatch("FX_Interpolate", dest.Width, dest.Height,
                new[] { (0, (ID3D11ShaderResourceView?)prev.Srv), (9, curr.Srv), (4, motion.Srv) }, new[] { (3, dest.Uav) });
    }

    public void EncodeTestSceneTruth(Tex dest, float time, float panX = 0, float panY = 0)
    {
        var p = new FxParams { SceneTime = time, PanX = panX, PanY = panY, InW = dest.Width, InH = dest.Height, OutW = dest.Width, OutH = dest.Height, TestHud = TestHud ? 1 : 0 };
        _g.SetParams(p);
        _g.Dispatch("FX_TestSceneSS", dest.Width, dest.Height, Array.Empty<(int, ID3D11ShaderResourceView?)>(), new[] { (3, dest.Uav) });
    }

    public void EncodeTestScene(Tex dest, float time, float jx, float jy, float panX = 0, float panY = 0)
    {
        var p = new FxParams { JitterX = jx, JitterY = jy, SceneTime = time, PanX = panX, PanY = panY, InW = dest.Width, InH = dest.Height, OutW = dest.Width, OutH = dest.Height, TestHud = TestHud ? 1 : 0 };
        _g.SetParams(p);
        _g.Dispatch("FX_TestScene", dest.Width, dest.Height, Array.Empty<(int, ID3D11ShaderResourceView?)>(), new[] { (3, dest.Uav) });
    }

    /// <summary>One full frame: upscale (+ optional interpolated frame). Returns (output, interpolated).</summary>
    public (Tex output, Tex? interp) Process(ID3D11ShaderResourceView input, int inW, int inH, int outW, int outH,
                                             Backend backend, float jx, float jy, bool frameGen, int multiplier = 2)
    {
        if (WantLevel != _lvl && _mvLo != null)
        {
            _lvl = WantLevel; _mvLo.Dispose(); int ls = 1 << _lvl;
            _mvLo = new Tex(Dev, Math.Max(1, inW / ls), Math.Max(1, inH / ls), Format.R16G16B16A16_Float);
            _forceFrames = 2; _fgForce = 2; _hasHistory = false; _motionValid = false;
        }
        int n = Math.Clamp(multiplier, 2, 8);
        if (n != _lastMul) { _fgForce = 2; _lastMul = n; }   // 1.3.4: per-step textures must be fully written twice
        int gslot = _genRing; _genRing ^= 1;
        _generated = Array.Empty<Tex>();
        LastSkippedUnchanged = false;
        Ensure(inW, inH, outW, outH);
        _panUpdated = false;
        _motionWrote = false;
        if (_vendorFailed.ContainsKey(backend)) backend = Backend.Temporal;   // fell back earlier
        if (backend != _lastBackend || Fast != _lastFast)
        {
            _hasHistory = false; _hasPrevLuma = false; _prevOutput = null; _lastBackend = backend; _lastFast = Fast; _vendor?.ResetHistory(); _csr2?.ResetHistory();
            _forceFrames = 2; _fgForce = 2;
        }
        if (frameGen != _lastFgOn) { _fgForce = 2; _lastFgOn = frameGen; }
        // Performance preset + CSR 1.3: two ping-pong outputs, so a skipped (unchanged) tile still holds
        // its result from two frames ago, which is the same content.
        bool fastT = Fast && backend == Backend.Temporal;
        _fastTileFg = fastT;
        Tex outT, mid;
        if (fastT) { outT = _outputs[_fRing]; mid = _mids[_fRing]; _fRing ^= 1; }
        else { outT = _outputs[_ring]; mid = _mids[_ring]; _ring = (_ring + 1) % _outputs.Length; }
        _g.BeginTiming();
        Tex? motion = null;
        bool mvPacked = false;
        bool csr2Pending = false;
        VendorMs = 0;
        switch (backend)
        {
            case Backend.Temporal:
                if (Fast) EncodeTemporalFast(input, jx, jy, outT); else EncodeTemporal(input, jx, jy, outT);
                motion = LastMotion; ActiveUpscaler = BackendNames.Long(backend) + (Fast ? " (Performance)" : " (Quality)"); break;
            case Backend.Csr20:
            {
                _csr2 ??= new Csr2Upscale(_g);
                _csr2.Fast = Fast;
                _csr2.JitterX = jx;
                _csr2.JitterY = jy;
                bool reuseV2 = frameGen && UseV2 && !_v2Failed;
                if (!reuseV2) { if (Fast) EncodeMotionFast(input, jx, jy); else EncodeMotion(input, jx, jy); }
                motion = reuseV2 ? null : LastMotion;
                ActiveUpscaler = BackendNames.Long(backend) + (Fast ? " (Performance)" : " (Quality)");
                csr2Pending = true;
                break;
            }
            case Backend.CsrAi:
                try
                {
                    _csrAi ??= new CsrAiUpscale(_g);
                    _csrAi.Encode(input, inW, inH, outT, Sharpness);
                    string ms = _csrAi.LastMs.ToString("F1");
                    ActiveUpscaler = BackendNames.Long(backend) + " — " + ms + " ms" + (_csrAi.Engine == "dml" ? " (CPU round-trip)" : "");
                }
                catch (Exception ex) { Fail(backend, ex); if (Fast) EncodeTemporalFast(input, jx, jy, outT); else EncodeTemporal(input, jx, jy, outT); motion = LastMotion; }
                break;
            case Backend.Spatial: EncodeSpatial(input, outT); ActiveUpscaler = BackendNames.Long(backend); break;
            case Backend.Fsr1:
                try
                {
                    _fsr1 ??= new Fsr1(_g);
                    _fsr1.Encode(input, inW, inH, outT, Sharpness);
                    ActiveUpscaler = "AMD FSR 1 (EASU + RCAS)";
                }
                catch (Exception ex) { Fail(backend, ex); if (Fast) EncodeTemporalFast(input, jx, jy, outT); else EncodeTemporal(input, jx, jy, outT); motion = LastMotion; }
                break;
            case Backend.Fsr2 or Backend.Fsr3 or Backend.Fsr4 or Backend.XeSS when VendorPassthrough && inW == outW && inH == outH:
                // 1.3.4: a 1:1 "upscale" (overlay on the window = capture size) gives a temporal vendor upscaler nothing
                // to do but cost: it re-renders every native frame (copy-in, MV pack, D3D12 dispatch, RGBA16F->RGBA8 copy)
                // while the game is fighting for the same GPU. Show the capture as-is instead.
                EncodeBilinear(input, outT);
                ActiveUpscaler = BackendNames.Long(backend) + " skipped (1:1: source is already the output size)";
                break;
            case Backend.Fsr2: case Backend.Fsr3: case Backend.Fsr4: case Backend.XeSS:
                if (Fast) EncodeMotionFast(input, jx, jy); else EncodeMotion(input, jx, jy);
                motion = MotionTex;
                try
                {
                    _vendor ??= new VendorPath(_g);
                    _vendor.Upscale(backend, input, inW, inH, motion!, outT, jx, jy, Sharpness, !_hasHistory);
                    _hasHistory = true; mvPacked = true;
                    ActiveUpscaler = _vendor.ActiveName;
                    VendorMs = _vendor.LastMs;
                }
                catch (Exception ex)
                {
                    Fail(backend, ex);
                    _hasPrevLuma = false;
                    if (Fast) EncodeTemporalFast(input, jx, jy, outT); else EncodeTemporal(input, jx, jy, outT);
                    motion = LastMotion;
                }
                break;
            default: EncodeBilinear(input, outT); ActiveUpscaler = BackendNames.Long(backend); break;
        }
        if (!csr2Pending) _g.Stage("upscale");
        FgV2Active = false;
        Tex? v2Motion = null;
        bool v2 = frameGen && UseV2 && !_v2Failed;
        if (frameGen && motion == null && !v2) { if (Fast) EncodeMotionFast(input, jx, jy); else EncodeMotion(input, jx, jy); motion = LastMotion; }
        if (WantMotionField && !_motionWrote) { if (Fast) EncodeMotionFast(input, jx, jy); else EncodeMotion(input, jx, jy); motion = LastMotion; }
        if (v2)
        {
            try
            {
                _v2 ??= new FgV2(_g);
                _v2.HudMaskSeed = FgV2HudSeed;
                _v2.LowResGen = LowResGen;
                _v2.Motion = Motion;
                _v2.MvGapN = MotionGapN > 0 ? MotionGapN : n;
                _v2.Ensure(inW, inH, outW, outH);
                if (_v2.ClassicCand)
                {
                    if (!(Fast && _motionWrote && _mvLo != null))
                        RunClassicCandKernels();
                    _v2.SetClassicMv(_mvLo);
                }
                if (UseV2Async && !_v2AsyncFailed)
                {
                    try
                    {
                        _v2Async ??= new FgV2Async(_g, _v2);
                        v2Motion = _v2Async.Process(input, inW, inH, outW, outH, jx, jy);
                    }
                    catch (Exception ex)
                    {
                        _v2AsyncFailed = true;
                        MotionLog.Add("CSR 2.0 async failed, using synchronous CSR 2.0: " + ex.Message);
                        _v2Async?.Dispose(); _v2Async = null;
                        v2Motion = _v2.Process(input, inW, inH, jx, jy);
                    }
                }
                else v2Motion = _v2.Process(input, inW, inH, jx, jy);
                FgV2Active = v2Motion != null;
                if (FgV2Active) MotionSource = _v2.EstimatorName;
                if (FgV2Active && UseCsrAiFg && !_csrAiFgFailed)
                {
                    try { _csrAiFg ??= new CsrAiFg(_g); _csrAiFg.BeginPair(); }
                    catch (Exception ex) { FailCsrAiFg("process-begin", ex); }
                }
            }
            catch (Exception ex)
            {
                _v2Failed = true;
                MotionLog.Add("CSR 2.0 failed, falling back to CSR 1.3: " + ex.Message);
                if (motion == null)
                {
                    if (Fast) EncodeMotionFast(input, jx, jy); else EncodeMotion(input, jx, jy);
                    motion = LastMotion;
                }
            }
        }
        if (csr2Pending && _csr2 != null)
        {
            try
            {
                var mv = v2Motion ?? LastMotion;
                var conf = v2Motion != null && _v2 != null ? _v2.OcclTex.Srv : null;
                _csr2.Encode(input, inW, inH, outT, Sharpness, mv, conf, _hasHistory);
                _hasHistory = true;
            }
            catch (Exception ex)
            {
                Fail(backend, ex);
                _csr2.ResetHistory();
                if (Fast) EncodeTemporalFast(input, jx, jy, outT); else EncodeTemporal(input, jx, jy, outT);
                motion = LastMotion;
            }
            _g.Stage("upscale");
        }
        Tex? interp = null;
        // 1.3.4: the change detector also votes on scene cuts, read on the GPU by the generation passes (no readback).
        if (frameGen) { DetectChange(input); LastSkippedUnchanged = SkipUnchanged && _unchangedRun >= 2; _g.Stage("change"); }
        var fgKind = ForceFrameFxFg ? FgKind.FrameFX : FrameGenKind;
        // FrameFX generation (not the AMD FG input, which keeps the field it was tuned with) uses the filtered field.
        Tex? fgMotion = frameGen && UseV2 && !_v2Failed && v2Motion != null ? v2Motion
            : frameGen && !LastSkippedUnchanged && motion != null && (fgKind == FgKind.FrameFX || n > 2) ? FgMotion(motion, _prevOutput, outT) : motion;
        if (frameGen && LastSkippedUnchanged) frameGen = false;   // 1.3.4: nothing moved: the real frame is all there is to show
        _g.BeginGenTiming();
        if (frameGen && UseV2 && !_v2Failed && _v2 != null && _prevOutput != null && v2Motion != null && !FgV2VsyncGen)
        {
            bool need = true;
            var slot = _gens[gslot];
            Tex[]? dests = null;
            if (n > 2 || (FusedFg && Fast && fgKind == FgKind.FrameFX))
            {
                dests = new Tex[n - 1];
                for (int k = 1; k < n; k++)
                {
                    var dest = slot[k - 1];
                    if (dest == null || dest.Width != _outW || dest.Height != _outH)
                    {
                        dest?.Dispose();
                        dest = slot[k - 1] = new Tex(Dev, _outW, _outH, Format.R8G8B8A8_UNorm);
                    }
                    dests[k - 1] = dest;
                }
            }
            if (UseCsrAiFg && !_csrAiFgFailed)
            {
                try
                {
                    double vms = VendorMs;
                    _csrAiFg ??= new CsrAiFg(_g);
                    if (dests != null)
                    {
                        var list = _csrAiFg.GenerateMulti(_v2, _prevOutput, outT, n, dests);
                        _generated = list;
                        interp = list[0];
                        ActiveFrameGen = "CSR Flux BETA ×" + n;
                    }
                    else
                    {
                        interp = _csrAiFg.Generate(_v2, _prevOutput, outT, 0.5f);
                        ActiveFrameGen = "CSR Flux BETA";
                    }
                    if (VendorMs != vms) throw new InvalidOperationException("generated frames entered FSR2/3/4/XeSS");
                    frameGen = false;
                    need = false;
                }
                catch (Exception ex) { FailCsrAiFg("process-generate", ex); }
            }
            if (need)
            {
                try
                {
                    double vms = VendorMs;
                    if (dests != null)
                    {
                        var list = _v2.GenerateMulti(_prevOutput, outT, n, dests);
                        _generated = list;
                        interp = list[0];
                        ActiveFrameGen = "CSR 2.0 frame generation ×" + n;
                        frameGen = false;
                    }
                    else
                    {
                        interp = _v2.Generate(_prevOutput, outT, 0.5f);
                        ActiveFrameGen = Csr20Label;
                        frameGen = false;
                    }
                    if (VendorMs != vms) throw new InvalidOperationException("generated frames entered FSR2/3/4/XeSS");
                }
                catch (Exception ex)
                {
                    _v2Failed = true;
                    MotionLog.Add("CSR 2.0 failed, falling back to CSR 1.3: " + ex.Message);
                }
            }
        }
        if (FgV2VsyncGen && UseV2 && !_v2Failed && v2Motion != null)
        {
            ActiveFrameGen = FgV2ExtrapOn ? "CSR 2.0 · lowest-latency mode" : "CSR 2.0 · smooth pacing";
            frameGen = false;
            _generated = Array.Empty<Tex>();
        }
        if (frameGen && (n > 2 || (FusedFg && Fast && fgKind == FgKind.FrameFX)))
        {
            interp = GenerateMulti(outT, fgMotion, n, gslot, inW, inH, jx, jy, mvPacked);
            frameGen = false;   // handled
        }
        if (frameGen && fgKind == FgKind.Fsr3 && _fgFailed == null)
        {
            try
            {
                _vendor ??= new VendorPath(_g);
                if (_vendor.FrameGen(outT, mid, motion!, inW, inH, jx, jy, mvPacked, _prevOutput == null)) interp = mid;
                ActiveFrameGen = VendorSupport.FsrFrameGen.Detail;
                VendorMs = _vendor.LastMs;
            }
            catch (Exception ex)
            {
                _fgFailed = ex.Message.Split('\n')[0];
                VendorNote = "AMD FSR 3 frame generation failed (" + _fgFailed + "); using CSR 1.3 frame generation (classic).";
            }
        }
        if (frameGen && interp == null && (fgKind == FgKind.FrameFX || _fgFailed != null) && _prevOutput != null && motion != null)
        {
            EncodeInterpolate(_prevOutput, outT, fgMotion ?? motion, mid);
            interp = mid;
            ActiveFrameGen = "CSR 1.3 frame generation (classic)";
        }
        if (interp != null && _generated.Length == 0) _generated = new[] { interp };
        if (interp != null) _g.Stage("framegen");
        _g.EndGenTiming();
        _prevOutput = outT;
        if (!_panUpdated) _globValid = false;
        if (!_motionWrote) _motionValid = false;
        _g.EndTiming();
        return (outT, interp);
    }

    /// <summary>1.3.4 ×N frame generation (N = 3..8): N-1 frames at t = k/N between the previous and the current output,
    /// all warped along the motion field that fed the upscaler (with AMD FSR 3: the same CSR vectors AMD FSR 3 gets;
    /// AMD FSR 3.1 FG itself can only make the midpoint). All steps are encoded back to back in one batch.</summary>
    Tex? GenerateMulti(Tex outT, Tex? motion, int n, int gslot, int inW, int inH, float jx, float jy, bool mvPacked)
    {
        if (_prevOutput == null || motion == null) return null;
        var slot = _gens[gslot];
        var list = new Tex[n - 1];
        bool fsr = FrameGenKind == FgKind.Fsr3 && !ForceFrameFxFg && _fgFailed == null;
        bool fused = FusedFg && Fast && !(fsr && FsrMid);
        for (int k = 1; k < n; k++)
        {
            var dest = slot[k - 1];
            if (dest == null || dest.Width != _outW || dest.Height != _outH)
            {
                dest?.Dispose();
                dest = slot[k - 1] = new Tex(Dev, _outW, _outH, Format.R8G8B8A8_UNorm);
                _fgForce = 2;
            }
            list[k - 1] = dest;
            if (fused) continue;
            bool done = false;
            if (fsr && FsrMid && 2 * k == n)
            {
                try
                {
                    _vendor ??= new VendorPath(_g);
                    done = _vendor.FrameGen(outT, dest, motion, inW, inH, jx, jy, mvPacked, false);
                    VendorMs = _vendor.LastMs;
                }
                catch (Exception ex)
                {
                    _fgFailed = ex.Message.Split('\n')[0];
                    VendorNote = "AMD FSR 3 frame generation failed (" + _fgFailed + "); using CSR 1.3 frame generation (classic).";
                }
            }
            if (!done) EncodeInterpolate(_prevOutput, outT, motion, dest, k / (float)n, lastOfFrame: false);
        }
        if (fused)
        {
            // One pass for every step: motion resolved once per pixel and reused, shared blend taps.
            var p = Base(0, 0); p.Pad6 = n - 1; p.Pad2 = PickVectors && PickEnv && PixelPickEnv ? 1 : 0;
            p.Pad3 = FusedVecEnv >= 0 ? Math.Min(FusedVecEnv, 4) : FusedVecMode;
            p.FastForce = _fastTileFg && _fgForce <= 0 ? 0 : 1;
            _g.SetParams(p);
            ID3D11ShaderResourceView srcA = _prevOutput.Srv, srcB = outT.Srv;
            if (FgPrep)
            {
                if (_fgPrepA == null || _fgPrepA.Width != _outW || _fgPrepA.Height != _outH)
                {
                    DisposeAll(new[] { _fgPrepA, _fgPrepB });
                    _fgPrepA = new Tex(Dev, _outW, _outH, Format.R8G8B8A8_UNorm);
                    _fgPrepB = new Tex(Dev, _outW, _outH, Format.R8G8B8A8_UNorm);
                }
                _g.Dispatch("FX_FgPrep", _outW, _outH, new[] { (0, (ID3D11ShaderResourceView?)_prevOutput.Srv), (9, outT.Srv) },
                    new[] { (0, (ID3D11UnorderedAccessView?)_fgPrepA.Uav), (1, _fgPrepB.Uav) });
                srcA = _fgPrepA.Srv; srcB = _fgPrepB.Srv;
                p.Pad3 += 8; _g.SetParams(p);
            }
            var uavs = new (int, ID3D11UnorderedAccessView?)[n - 1];
            for (int k = 0; k < n - 1; k++) uavs[k] = (k, list[k].Uav);
            _g.DispatchGroups("FX_FastInterpN", TilesX, TilesY,
                new[] { (0, (ID3D11ShaderResourceView?)srcA), (9, srcB), (4, motion.Srv), (10, _tileCnt!.Srv), (16, _chgOut?.Srv),
                        (17, _globValid && MotionSource == FastMotionName ? _gmv?.Srv : null) }, uavs);
        }
        if (_fgForce > 0) _fgForce--;
        _generated = list;
        ActiveFrameGen = fsr ? $"AMD FSR 3 + CSR 1.3 ×{n}" : $"CSR 1.3 frame generation (classic) ×{n}";
        return list[0];
    }

    /// <summary>1.3.4: count changed samples of the captured frame on the GPU; read the result two frames later
    /// (DoNotWait map of a staging ring, never a stall). Two unchanged readings in a row skip frame generation.</summary>
    void DetectChange(ID3D11ShaderResourceView input)
    {
        if (_chgPrev == null || _chgOut == null) return;
        var p = Base(0, 0); p.HistoryValid = _chgHist ? 1 : 0;
        _g.SetParams(p);
        _g.DispatchGroups("FX_ChangeDetect", 1, 1, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (4, _chgPrev.Uav), (0, _chgOut.Uav) });
        _chgHist = true;
        var ctx = _g.Context;
        ctx.CopyResource(_chgStage[_chgFrame % 3]!, _chgOut.Texture);
        _chgFrame++;
        if (_chgFrame < 3) return;
        var st = _chgStage[_chgFrame % 3]!;   // written two frames ago
        if (ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var m).Success)
        {
            float changed; unsafe { changed = *(float*)m.DataPointer; }
            ctx.Unmap(st, 0);
            _unchangedRun = changed < 0.5f ? _unchangedRun + 1 : 0;
        }
    }

    void Fail(Backend b, Exception ex)
    {
        var msg = ex.Message.Split('\n')[0].Trim();
        _vendorFailed[b] = msg;
        VendorNote = $"{BackendNames.Long(b)} could not run ({msg}); using CSR 1.3.";
        ActiveUpscaler = "CSR 1.3 (fallback)";
    }

    public string Csr20Label
    {
        get
        {
            string n = FlowEstimatorName;
            if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("Optical Flow", StringComparison.OrdinalIgnoreCase) || n.Contains("D3D12", StringComparison.OrdinalIgnoreCase))
                return "CSR 2.0 · hardware motion";
            return "CSR 2.0 frame generation";
        }
    }

    public FgV2? V2 => _v2;

    public Tex? CsrAiFgGenerateAt(Tex prev, Tex curr, float t)
    {
        if (_v2 == null) return null;
        if (UseCsrAiFg && !_csrAiFgFailed)
        {
            try { _csrAiFg ??= new CsrAiFg(_g); return _csrAiFg.Generate(_v2, prev, curr, t); }
            catch (Exception ex) { FailCsrAiFg("generate-at", ex); }
        }
        return V2GenerateAt(prev, curr, t);
    }

    public void CsrAiFgBeginPair()
    {
        if (UseCsrAiFg && !_csrAiFgFailed)
        {
            try { _csrAiFg ??= new CsrAiFg(_g); _csrAiFg.BeginPair(); }
            catch (Exception ex) { FailCsrAiFg("begin-pair", ex); }
        }
    }

    public Tex? V2GenerateAt(Tex prev, Tex curr, float t)
    {
        if (_v2 == null) return null;
        double vms = VendorMs;
        _g.BeginGenTiming();
        var r = _v2.Generate(prev, curr, t);
        _g.EndGenTiming();
        if (VendorMs != vms) throw new InvalidOperationException("generated frames entered FSR2/3/4/XeSS");
        return r;
    }

    public Tex? V2GenerateFastAt(Tex prev, Tex curr, float t)
    {
        if (_v2 == null) return null;
        double vms = VendorMs;
        _g.BeginGenTiming();
        var r = _v2.GenerateFast(prev, curr, t);
        _g.EndGenTiming();
        if (VendorMs != vms) throw new InvalidOperationException("generated frames entered FSR2/3/4/XeSS");
        return r;
    }

    public Tex? V2GenerateOracle(Tex prev, Tex curr, Tex bwd, Tex fwd, float t)
    {
        if (_v2 == null) return null;
        _g.BeginGenTiming();
        var r = _v2.GenerateOracle(prev, curr, bwd, fwd, t);
        _g.EndGenTiming();
        return r;
    }

    public Tex? V2Blit(Tex src)
    {
        if (_v2 == null) return null;
        return _v2.Blit(src);
    }

    public Tex? V2Extrapolate(Tex latest, float t)
    {
        if (_v2 == null) return null;
        double vms = VendorMs;
        _g.BeginGenTiming();
        var r = _v2.Extrapolate(latest, t);
        _g.EndGenTiming();
        if (VendorMs != vms) throw new InvalidOperationException("generated frames entered FSR2/3/4/XeSS");
        return r;
    }

    public string? FgV2HudMask => _v2?.HudMaskB64;

    public void Dispose() { DisposeTextures(); _vendor?.Dispose(); _vendor = null; _fsr1?.Dispose(); _fsr1 = null; _csr2?.Dispose(); _csr2 = null; _csrAi?.Dispose(); _csrAi = null; _csrAiFg?.Dispose(); _csrAiFg = null; _v2Async?.Dispose(); _v2Async = null; _v2?.Dispose(); _v2 = null; }
}
