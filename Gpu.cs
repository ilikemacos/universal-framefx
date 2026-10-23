using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

/// <summary>Constant buffer shared by every compute pass. Must match cbuffer Params in FrameFX.hlsl (176 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FxParams
{
    public float InW, InH, OutW, OutH;
    public float JitterX, JitterY, PrevJitterX, PrevJitterY;
    public float JitterDeltaX, JitterDeltaY, PanX, PanY;
    public float HistoryValid, Sharpness, MaxAccum, JitterActive;
    public int SearchRadius, ApplyJitterCancel;
    public float InterpT, Sensitivity;
    public float SceneTime, LevelScale, Pad0, Pad1;
    public float TestHud, HwScale, HwGrid, StaticThresh;
    public float TileSize, StaticOn, Pad2, Pad3;
    public float FastSharp, FastForce, FastLevelScale, Pad5;
    public float FastConfK, FastMinW, FastStatMargin, FastAccumMax;
    public float FastFgK, FastFgMvW, FastFgGate, Pad6;
}

/// <summary>A 2D texture with the views we need.</summary>
public sealed class Tex : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public ID3D11UnorderedAccessView? Uav { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    public Tex(ID3D11Device dev, int w, int h, Format fmt, bool uav = true, bool renderTarget = false)
    {
        Width = Math.Max(1, w); Height = Math.Max(1, h); Format = fmt;
        var bind = BindFlags.ShaderResource;
        if (uav) bind |= BindFlags.UnorderedAccess;
        if (renderTarget) bind |= BindFlags.RenderTarget;
        Texture = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = fmt,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = bind,
        });
        Srv = dev.CreateShaderResourceView(Texture);
        Uav = uav ? dev.CreateUnorderedAccessView(Texture) : null;
    }

    public void Dispose() { Uav?.Dispose(); Srv.Dispose(); Texture.Dispose(); }
}

/// <summary>Which GPU runs FrameFX's processing (persisted index order: append only).</summary>
public enum GpuChoice { Auto, Integrated, Dedicated }

/// <summary>Device, shaders and the dispatch helper. All context use must hold <see cref="Lock"/>.</summary>
public sealed class Gpu : IDisposable
{
    public static readonly object Lock = new();

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }
    public uint VendorId { get; }
    public IDXGIAdapter Adapter { get; }
    public List<string> Report { get; } = new();

    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _linear, _point;
    private readonly Dictionary<string, ID3D11ComputeShader> _cs = new();
    public ID3D11VertexShader PresentVS { get; }
    public ID3D11PixelShader PresentPS { get; }
    public ID3D11VertexShader PresentCursorVS { get; }
    public ID3D11PixelShader PresentCursorPS { get; }
    public ID3D11BlendState AlphaBlend { get; }
    public ID3D11Buffer PresentCB { get; }

    private readonly ID3D11Query[] _disjointQ = new ID3D11Query[3], _t0Q = new ID3D11Query[3], _t1Q = new ID3D11Query[3];
    private int _qi, _qIssued;
    public double LastGpuMs { get; private set; }

    public static readonly string[] Kernels =
    {
        "FX_LumaExtract", "FX_LumaHalve", "FX_MotionInit", "FX_MotionRefine", "FX_MotionSmooth2",
        "FX_MotionSubpixel", "FX_MotionSmooth4", "FX_Dilate", "FX_Disocclusion", "FX_Reactive",
        "FX_LockDetect", "FX_LockUpdate", "FX_Accumulate", "FX_EASU", "FX_RCAS", "FX_Bilinear",
        "FX_Interpolate", "FX_TestScene", "FX_TestSceneSS",
        "FX_HwImport", "FX_FwdFromBwd", "FX_Luma8", "FX_StaticCount", "FX_HudMask", "FX_StaticTiles", "FX_Interpolate2", "FX_MvClean", "FX_LumaHalveAA", "FX_FastLumaAA", "FX_MvBorder", "FX_MvHud", "FX_MotionFail", "FX_GlobalMotion", "FX_Interp1",
        "FX_CopyIn", "FX_PackMv",
        "FX_FastLuma", "FX_FastSearch", "FX_FastClean", "FX_FastAA", "FX_FastRefine", "FX_FastFinal", "FX_FastTiles", "FX_FastAccum", "FX_FastInterp",
        "FX_ChangeDetect", "FX_FastInterpN", "FX_MvMedian", "FX_FgPrep",
    };

    /// <summary>The GPU switch (Settings → Advanced → Processing GPU, or --gpu auto|igpu|dgpu). Read once at device creation.</summary>
    public static GpuChoice Choice = GpuChoice.Auto;
    public GpuChoice ActiveChoice { get; }
    /// <summary>"integrated" / "dedicated" / "Windows default" for the HUD.</summary>
    public string KindName { get; } = "";

    public static IDXGIAdapter1? OpenAdapterFor(GpuChoice c)
    {
        try
        {
            using var f6 = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            var gp = c == GpuChoice.Integrated ? GpuPreference.MinimumPower : GpuPreference.HighPerformance;
            if (f6.EnumAdapterByGpuPreference(0, gp, out IDXGIAdapter1? ad).Success && ad != null) return ad;
        }
        catch { }
        return null;
    }

    public static ID3D11Device? CreateDeviceOn(IDXGIAdapter adapter)
    {
        var fl = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        return D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, fl, out ID3D11Device? dev).Success ? dev : null;
    }

    /// <summary>Adapter Windows returns for a preference (name, or null), without creating a device.</summary>
    public static string? AdapterNameFor(GpuChoice c)
    {
        try
        {
            using var ad = OpenAdapterFor(c);
            return ad?.Description1.Description;
        }
        catch { }
        return null;
    }

    public Gpu()
    {
        // 1.3.4 GPU switch: Auto / Dedicated = DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE (the discrete GPU on a hybrid laptop),
        // Integrated = DXGI_GPU_PREFERENCE_MINIMUM_POWER. The D3D12 vendor bridge, the vendor probes and the capture all use
        // this adapter. When it is not the GPU that drives the game's display, Windows copies across adapters: the capture
        // (DWM -> our device) and the swap chain (hybrid presentation -> display GPU). The HUD warns about that.
        // UFX_GPU=power|high|default still overrides (default = let Windows choose).
        var fl = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        ID3D11Device? dev = null;
        var pref = (Environment.GetEnvironmentVariable("UFX_GPU") ?? "").Trim().ToLowerInvariant();
        ActiveChoice = pref switch { "power" => GpuChoice.Integrated, "high" => GpuChoice.Dedicated, _ => Choice };
        if (pref != "default")
        {
            try
            {
                using var ad = OpenAdapterFor(ActiveChoice);
                if (ad != null)
                    if (D3D11.D3D11CreateDevice(ad, DriverType.Unknown, DeviceCreationFlags.BgraSupport, fl, out dev).Failure) dev = null;
            }
            catch { dev = null; }
        }
        var hr = dev != null ? SharpGen.Runtime.Result.Ok
            : D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, fl, out dev);
        if (hr.Failure || dev is null) throw new InvalidOperationException("Could not create a Direct3D 11 hardware device (feature level 11_0 required).");
        Device = dev;
        Context = dev.ImmediateContext;
        using (var dxgi = dev.QueryInterface<IDXGIDevice>())
        {
            Adapter = dxgi.GetAdapter();
            AdapterName = Adapter.Description.Description;
            VendorId = Adapter.Description.VendorId;
        }
        if (pref == "default") KindName = "Windows default";
        else
        {
            // Integrated if it is the minimum-power adapter and differs from the high-performance one.
            var hi = AdapterNameFor(GpuChoice.Dedicated); var lo = AdapterNameFor(GpuChoice.Integrated);
            KindName = hi == lo ? "only GPU" : AdapterName == lo ? "integrated" : "dedicated";
        }
        Diag.Mark($"gpu: choice {ActiveChoice} -> {AdapterName} ({KindName})");

        // 1.3.4: the overlay shares the GPU with the game it captures. A slightly lower GPU scheduling priority lets the
        // game's frames win when both are queued (UFX_GPU_PRIO, -7..0; 0 = normal, the old behaviour).
        GpuPriority = int.TryParse(Environment.GetEnvironmentVariable("UFX_GPU_PRIO"), out var gpr) ? Math.Clamp(gpr, -7, 0) : -2;
        if (GpuPriority != 0)
            try { using var dxp = dev.QueryInterface<IDXGIDevice>(); dxp.SetGPUThreadPriority(GpuPriority); } catch { GpuPriority = 0; }
        Diag.Mark($"gpu: adapter {AdapterName}, GPU priority {GpuPriority}");

        _cb = dev.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<FxParams>(), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write,
        });
        PresentCB = dev.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write,
        });
        _linear = dev.CreateSamplerState(SamplerDescription.LinearClamp);
        _point = dev.CreateSamplerState(SamplerDescription.PointClamp);

        string src = LoadShaderSource();
        Diag.Mark("gpu: device created (" + AdapterName + ")");
        // 1.3.3: compile in parallel and cache the blobs on disk (was ~39 sequential compiles on the UI thread).
        var blobs = ShaderCache.GetMany(src, Kernels.Select(k => (k, "cs_5_0")).Append(("PresentVS", "vs_5_0")).Append(("PresentPS", "ps_5_0")).Append(("PresentCursorVS", "vs_5_0")).Append(("PresentCursorPS", "ps_5_0")), "FrameFX.hlsl");
        // Beta2: blobs may come from the build-time baked cache; if one is ever rejected, compile it fresh instead of failing.
        byte[] Fresh(string e, string prof) { using var bl = CompileSource(src, e, prof, "FrameFX.hlsl"); return bl.AsSpan().ToArray(); }
        foreach (var k in Kernels)
        {
            try { _cs[k] = dev.CreateComputeShader(blobs[k]); }
            catch { _cs[k] = dev.CreateComputeShader(Fresh(k, "cs_5_0")); }
            Report.Add($"{k}: OK");
        }
        try { PresentVS = dev.CreateVertexShader(blobs["PresentVS"]); } catch { PresentVS = dev.CreateVertexShader(Fresh("PresentVS", "vs_5_0")); }
        try { PresentPS = dev.CreatePixelShader(blobs["PresentPS"]); } catch { PresentPS = dev.CreatePixelShader(Fresh("PresentPS", "ps_5_0")); }
        try { PresentCursorVS = dev.CreateVertexShader(blobs["PresentCursorVS"]); } catch { PresentCursorVS = dev.CreateVertexShader(Fresh("PresentCursorVS", "vs_5_0")); }
        try { PresentCursorPS = dev.CreatePixelShader(blobs["PresentCursorPS"]); } catch { PresentCursorPS = dev.CreatePixelShader(Fresh("PresentCursorPS", "ps_5_0")); }
        AlphaBlend = dev.CreateBlendState(BlendDescription.NonPremultiplied);
        Report.Add("PresentVS/PresentPS: OK");
        Diag.Mark($"gpu: shaders ready (cache hits {ShaderCache.Hits}, compiled {ShaderCache.Misses})");

        for (int i = 0; i < 3; i++)
        {
            _disjointQ[i] = dev.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _t0Q[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _t1Q[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _stQ[i] = Enumerable.Range(0, MaxStages).Select(_ => dev.CreateQuery(new QueryDescription(QueryType.Timestamp))).ToArray();
            _stName[i] = new string[MaxStages];
            _pDj[i] = dev.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _p0[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _p1[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _gDj[i] = dev.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _g0[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _g1[i] = dev.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
        // 1.3.4 low latency: at most one frame queued ahead of the GPU (default 3 adds up to 2 frames of lag).
        try { using var d1 = dev.QueryInterface<IDXGIDevice1>(); d1.MaximumFrameLatency = 1; } catch { }
    }

    public int GpuPriority { get; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>Which adapter drives the monitor the source window is on. On a hybrid laptop the internal panel is often
    /// wired to the iGPU: then every captured frame and every presented frame crosses between the two GPUs.</summary>
    public static string DisplayAdapterNote(Gpu g, IntPtr hwnd)
    {
        try
        {
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; f.EnumAdapters1(i, out IDXGIAdapter1? a).Success && a != null; i++)
            {
                using (a)
                {
                    for (uint j = 0; a.EnumOutputs(j, out IDXGIOutput? o).Success && o != null; j++)
                    {
                        using (o)
                        {
                            if (o.Description.Monitor != mon) continue;
                            string name = a.Description1.Description;
                            bool same = a.Description1.Luid.LowPart == g.Adapter.Description.Luid.LowPart && a.Description1.Luid.HighPart == g.Adapter.Description.Luid.HighPart;
                            return same ? $"Processing GPU: {g.AdapterName} ({g.KindName}), also drives this display"
                                        : $"Processing GPU: {g.AdapterName} ({g.KindName}); display on {name}: every frame is copied between GPUs (capture in + present out, costs bandwidth and ~1 frame of latency)";
                        }
                    }
                }
            }
        }
        catch { }
        return $"Processing GPU: {g.AdapterName} ({g.KindName})";
    }

    private static string LoadShaderSource()
    {
        var exeDir = AppContext.BaseDirectory;
        var loose = Path.Combine(exeDir, "Shaders", "FrameFX.hlsl");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FrameFX.hlsl")
            ?? throw new InvalidOperationException("FrameFX.hlsl is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static Vortice.Direct3D.Blob Compile(string src, string entry, string profile) => CompileSource(src, entry, profile, "FrameFX.hlsl");

    public static Vortice.Direct3D.Blob CompileSource(string src, string entry, string profile, string fileName)
    {
        var res = Vortice.D3DCompiler.Compiler.Compile(src, entry, fileName, profile, out var blob, out var err);
        if (res.Failure || blob is null)
            throw new InvalidOperationException($"{entry} failed to compile:\n{err?.AsString() ?? res.ToString()}");
        err?.Dispose();
        return blob;
    }

    public void SetParams(in FxParams p)
    {
        var m = Context.Map(_cb, 0, MapMode.WriteDiscard);
        try { unsafe { *(FxParams*)m.DataPointer = p; } }
        finally { Context.Unmap(_cb, 0); }
    }

    private static readonly ID3D11ShaderResourceView?[] NullSrv = new ID3D11ShaderResourceView?[10];
    private static readonly ID3D11UnorderedAccessView?[] NullUav = new ID3D11UnorderedAccessView?[6];

    /// <summary>Bind, dispatch 8x8 groups over w×h, unbind.</summary>
    public void Dispatch(string kernel, int w, int h,
                         (int slot, ID3D11ShaderResourceView? srv)[] srvs,
                         (int slot, ID3D11UnorderedAccessView? uav)[] uavs)
    {
        var ctx = Context;
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _linear);
        ctx.CSSetSampler(1, _point);
        foreach (var (slot, srv) in srvs) ctx.CSSetShaderResource((uint)slot, srv);
        foreach (var (slot, uav) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, uav);
        ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        foreach (var (slot, _) in srvs) ctx.CSSetShaderResource((uint)slot, null);
        foreach (var (slot, _) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, null);
        if (Profiling) Mark(kernel);
    }

    /// <summary>Dispatch an explicit number of thread groups (tile-per-group kernels).</summary>
    public void DispatchGroups(string kernel, int gx, int gy,
                               (int slot, ID3D11ShaderResourceView? srv)[] srvs,
                               (int slot, ID3D11UnorderedAccessView? uav)[] uavs)
    {
        var ctx = Context;
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _linear);
        ctx.CSSetSampler(1, _point);
        foreach (var (slot, srv) in srvs) ctx.CSSetShaderResource((uint)slot, srv);
        foreach (var (slot, uav) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, uav);
        ctx.Dispatch((uint)Math.Max(1, gx), (uint)Math.Max(1, gy), 1);
        foreach (var (slot, _) in srvs) ctx.CSSetShaderResource((uint)slot, null);
        foreach (var (slot, _) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, null);
        if (Profiling) Mark(kernel);
    }

    // ── Optional per-pass GPU profiler (self-test only): a timestamp after every dispatch.
    public bool Profiling;
    readonly List<ID3D11Query> _profPool = new();
    readonly List<(string label, int q)> _prof = new();
    ID3D11Query? _profDisjoint;
    public void ProfileBegin()
    {
        _prof.Clear();
        _profDisjoint ??= Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        Context.Begin(_profDisjoint);
        Profiling = true;
        Mark("start");
    }
    public void Mark(string label)
    {
        if (!Profiling) return;
        if (_prof.Count >= _profPool.Count) _profPool.Add(Device.CreateQuery(new QueryDescription(QueryType.Timestamp)));
        Context.End(_profPool[_prof.Count]);
        _prof.Add((label, _prof.Count));
    }
    /// <summary>Ends a profiled frame (blocking) and returns ms per label (summed per label).</summary>
    public Dictionary<string, double> ProfileEnd()
    {
        Mark("end");
        Profiling = false;
        Context.End(_profDisjoint!);
        Sync();
        var res = new Dictionary<string, double>();
        QueryDataTimestampDisjoint dj;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Context.GetData(_profDisjoint!, out dj)) { if (sw.ElapsedMilliseconds > 2000) return res; Thread.SpinWait(100); }
        if (dj.Disjoint || dj.Frequency == 0) return res;
        ulong prev = 0;
        for (int i = 0; i < _prof.Count; i++)
        {
            ulong t = 0;
            while (!Context.GetData(_profPool[_prof[i].q], out t)) { if (sw.ElapsedMilliseconds > 2000) return res; Thread.SpinWait(100); }
            if (i > 0 && _prof[i].label != "end")
                res[_prof[i].label] = res.GetValueOrDefault(_prof[i].label) + (t - prev) * 1000.0 / dj.Frequency;
            if (_prof[i].label == "end") res["total"] = (t - FirstStamp) * 1000.0 / dj.Frequency;
            if (i == 0) FirstStamp = t;
            prev = t;
        }
        return res;
    }
    ulong FirstStamp;

    public ID3D11SamplerState LinearSampler => _linear;
    public ID3D11SamplerState PointSampler => _point;

    ID3D11Query? _syncQ;
    /// <summary>Block until the GPU has finished everything queued so far (benchmarks only).</summary>
    public void Sync()
    {
        _syncQ ??= Device.CreateQuery(new QueryDescription(QueryType.Event));
        Context.End(_syncQ);
        Context.Flush();
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (!Context.GetData(_syncQ, out int done) || done == 0) { if (t.ElapsedMilliseconds > 5000) break; Thread.SpinWait(200); }
    }

    /// <summary>GPU frame timing (timestamp queries); the app enables it only while the HUD is shown.</summary>
    public bool TimingEnabled = true;
    bool _timingOpen;
    public void BeginTiming() { _timingOpen = TimingEnabled && !Profiling; if (!_timingOpen) return; _stCount[_qi] = 0; Context.Begin(_disjointQ[_qi]); Context.End(_t0Q[_qi]); }

    // 1.3.4 per-stage GPU timing (non-blocking, read two frames late like LastGpuMs).
    const int MaxStages = 10;
    readonly ID3D11Query[][] _stQ = new ID3D11Query[3][];
    readonly string[][] _stName = new string[3][];
    readonly int[] _stCount = new int[3];
    /// <summary>Per-stage GPU ms of the last timed frame (stage = work since the previous mark), in order.</summary>
    public IReadOnlyList<(string stage, double ms)> LastStages { get; private set; } = Array.Empty<(string, double)>();
    /// <summary>Mark the end of a pipeline stage inside BeginTiming/EndTiming (no-op when timing is off).</summary>
    public void Stage(string name)
    {
        if (!_timingOpen) return;
        int i = _stCount[_qi];
        if (i >= MaxStages) return;
        Context.End(_stQ[_qi][i]); _stName[_qi][i] = name; _stCount[_qi] = i + 1;
    }

    readonly ID3D11Query[] _pDj = new ID3D11Query[3], _p0 = new ID3D11Query[3], _p1 = new ID3D11Query[3];
    int _pi, _pIssued; bool _pOpen;
    /// <summary>GPU ms of the last timed present draw (fullscreen copy to the swap chain).</summary>
    public double LastPresentMs { get; private set; }
    public void BeginPresentTiming() { _pOpen = TimingEnabled && !Profiling; if (!_pOpen) return; Context.Begin(_pDj[_pi]); Context.End(_p0[_pi]); }
    public void EndPresentTiming()
    {
        if (!_pOpen) return;
        _pOpen = false;
        Context.End(_p1[_pi]); Context.End(_pDj[_pi]);
        _pi = (_pi + 1) % 3; _pIssued++;
        if (_pIssued < 3) return;
        int r = _pi;
        if (!Context.GetData(_pDj[r], out QueryDataTimestampDisjoint dj) || dj.Disjoint || dj.Frequency == 0) return;
        if (!Context.GetData(_p0[r], out ulong a) || !Context.GetData(_p1[r], out ulong b) || b < a) return;
        LastPresentMs = (b - a) * 1000.0 / dj.Frequency;
    }

    readonly ID3D11Query[] _gDj = new ID3D11Query[3], _g0 = new ID3D11Query[3], _g1 = new ID3D11Query[3];
    int _gi, _gIssued; bool _gOpen;
    public double LastGenMs { get; private set; }
    public void BeginGenTiming() { _gOpen = TimingEnabled && !Profiling; if (!_gOpen) return; Context.Begin(_gDj[_gi]); Context.End(_g0[_gi]); }
    public void EndGenTiming()
    {
        if (!_gOpen) return;
        _gOpen = false;
        Context.End(_g1[_gi]); Context.End(_gDj[_gi]);
        _gi = (_gi + 1) % 3; _gIssued++;
        if (_gIssued < 3) return;
        int r = _gi;
        if (!Context.GetData(_gDj[r], out QueryDataTimestampDisjoint dj) || dj.Disjoint || dj.Frequency == 0) return;
        if (!Context.GetData(_g0[r], out ulong a) || !Context.GetData(_g1[r], out ulong b) || b < a) return;
        LastGenMs = (b - a) * 1000.0 / dj.Frequency;
    }

    public Tex LoadPng(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var tex = new Tex(Device, w, h, Format.R8G8B8A8_UNorm);
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write, BindFlags = BindFlags.None,
        });
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var m = Context.Map(staging, 0, MapMode.Write);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* src = (byte*)bd.Scan0 + y * (long)bd.Stride;
                    byte* dst = (byte*)m.DataPointer + y * (long)m.RowPitch;
                    for (int x = 0; x < w; x++)
                    {
                        dst[x * 4] = src[x * 4 + 2];
                        dst[x * 4 + 1] = src[x * 4 + 1];
                        dst[x * 4 + 2] = src[x * 4];
                        dst[x * 4 + 3] = src[x * 4 + 3];
                    }
                }
            }
        }
        finally
        {
            Context.Unmap(staging, 0);
            bmp.UnlockBits(bd);
        }
        Context.CopyResource(tex.Texture, staging);
        return tex;
    }

    public Tex UploadRg16(int w, int h, byte[] data)
    {
        int need = w * h * 4;
        if (data.Length < need) throw new InvalidOperationException($"RG16 field is {data.Length} bytes, need {need}");
        var tex = new Tex(Device, w, h, Format.R16G16_Float);
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write, BindFlags = BindFlags.None,
        });
        var m = Context.Map(staging, 0, MapMode.Write);
        try
        {
            unsafe
            {
                fixed (byte* src0 = data)
                {
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(src0 + y * (long)w * 4, (byte*)m.DataPointer + y * (long)m.RowPitch, m.RowPitch, w * 4L);
                    }
                }
            }
        }
        finally { Context.Unmap(staging, 0); }
        Context.CopyResource(tex.Texture, staging);
        return tex;
    }

    public byte[] ReadRgb8(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new byte[t.Width * t.Height * 3];
        bool bgra = t.Format == Format.B8G8R8A8_UNorm;
        try
        {
            unsafe
            {
                for (int y = 0; y < t.Height; y++)
                {
                    byte* row = (byte*)m.DataPointer + y * (long)m.RowPitch;
                    for (int x = 0; x < t.Width; x++)
                    {
                        int o = (y * t.Width + x) * 3;
                        byte c0 = row[x * 4], c1 = row[x * 4 + 1], c2 = row[x * 4 + 2];
                        outp[o] = bgra ? c2 : c0;
                        outp[o + 1] = c1;
                        outp[o + 2] = bgra ? c0 : c2;
                    }
                }
            }
        }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    public void EndTiming()
    {
        if (!_timingOpen) return;
        _timingOpen = false;
        Context.End(_t1Q[_qi]);
        Context.End(_disjointQ[_qi]);
        _qi = (_qi + 1) % 3; _qIssued++;
        if (_qIssued < 3) return;
        // Read the oldest set (issued two frames ago), non-blocking.
        int r = _qi;
        if (!Context.GetData(_disjointQ[r], out QueryDataTimestampDisjoint dj) || dj.Disjoint || dj.Frequency == 0) return;
        if (!Context.GetData(_t0Q[r], out ulong a) || !Context.GetData(_t1Q[r], out ulong b) || b < a) return;
        LastGpuMs = (b - a) * 1000.0 / dj.Frequency;
        var st = new List<(string, double)>();
        ulong prev = a;
        for (int j = 0; j < _stCount[r]; j++)
        {
            if (!Context.GetData(_stQ[r][j], out ulong t) || t < prev) { st = null; break; }
            string nm = _stName[r][j];
            int at = st.FindIndex(x => x.Item1 == nm);
            double ms = (t - prev) * 1000.0 / dj.Frequency;
            if (at >= 0) st[at] = (nm, st[at].Item2 + ms); else st.Add((nm, ms));
            prev = t;
        }
        if (st != null) { if (b > prev && (b - prev) * 1000.0 / dj.Frequency > 0.005) st.Add(("other", (b - prev) * 1000.0 / dj.Frequency)); LastStages = st; }
    }

    /// <summary>Diagnostics: read an RGBA16F texture.</summary>
    public float[] ReadHalf4(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new float[t.Width * t.Height * 4];
        try
        {
            unsafe
            {
                for (int y = 0; y < t.Height; y++)
                {
                    Half* row = (Half*)((byte*)m.DataPointer + y * (long)m.RowPitch);
                    for (int i = 0; i < t.Width * 4; i++) outp[y * t.Width * 4 + i] = (float)row[i];
                }
            }
        }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    /// <summary>Diagnostics: read a 16-bit float texture with n channels.</summary>
    public float[] ReadHalfN(Tex t, int n)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new float[t.Width * t.Height * n];
        try { unsafe { for (int y = 0; y < t.Height; y++) { Half* row = (Half*)((byte*)m.DataPointer + y * (long)m.RowPitch); for (int i = 0; i < t.Width * n; i++) outp[y * t.Width * n + i] = (float)row[i]; } } }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    public byte[] ReadR16ArrayCOrder(ID3D11Texture2D tex, int w, int h, int slices)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = Math.Max((uint)slices, tex.Description.ArraySize), Format = Format.R16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, tex);
        var dst = new byte[slices * h * w * 2];
        for (int c = 0; c < slices; c++)
        {
            var m = Context.Map(staging, (uint)c, MapMode.Read);
            try
            {
                unsafe
                {
                    int baseOff = c * h * w * 2;
                    for (int y = 0; y < h; y++)
                        Marshal.Copy((nint)((byte*)m.DataPointer + y * (long)m.RowPitch), dst, baseOff + y * w * 2, w * 2);
                }
            }
            finally { Context.Unmap(staging, (uint)c); }
        }
        return dst;
    }

    public float[] ReadR32(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new float[t.Width * t.Height];
        try
        {
            unsafe
            {
                for (int y = 0; y < t.Height; y++)
                {
                    float* row = (float*)((byte*)m.DataPointer + y * (long)m.RowPitch);
                    for (int x = 0; x < t.Width; x++) outp[y * t.Width + x] = row[x];
                }
            }
        }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    /// <summary>Diagnostics: read an R16_FLOAT texture.</summary>
    public float[] ReadHalf4Any(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new float[t.Width * t.Height];
        try { unsafe { for (int y = 0; y < t.Height; y++) { Half* row = (Half*)((byte*)m.DataPointer + y * (long)m.RowPitch); for (int i = 0; i < t.Width; i++) outp[y * t.Width + i] = (float)row[i]; } } }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    /// <summary>Diagnostics: read an R16G16_SINT texture.</summary>
    public short[] ReadShort2(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new short[t.Width * t.Height * 2];
        try { unsafe { for (int y = 0; y < t.Height; y++) { short* row = (short*)((byte*)m.DataPointer + y * (long)m.RowPitch); for (int i = 0; i < t.Width * 2; i++) outp[y * t.Width * 2 + i] = row[i]; } } }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    /// <summary>Diagnostics: save an 8-bit texture as PNG.</summary>
    public void SavePng(Tex t, string path)
    {
        var rgb = ReadRgb(t);
        using var bmp = new System.Drawing.Bitmap(t.Width, t.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, t.Width, t.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        unsafe
        {
            for (int y = 0; y < t.Height; y++)
            {
                byte* row = (byte*)bd.Scan0 + y * (long)bd.Stride;
                for (int x = 0; x < t.Width; x++)
                {
                    int o = (y * t.Width + x) * 3;
                    row[x * 3] = (byte)Math.Round(rgb[o + 2] * 255); row[x * 3 + 1] = (byte)Math.Round(rgb[o + 1] * 255); row[x * 3 + 2] = (byte)Math.Round(rgb[o] * 255);
                }
            }
        }
        bmp.UnlockBits(bd);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Blocking readback of an 8-bit RGBA/BGRA texture as float RGB.</summary>
    public float[] ReadRgb(Tex t)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = t.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        Context.CopyResource(staging, t.Texture);
        var m = Context.Map(staging, 0, MapMode.Read);
        var outp = new float[t.Width * t.Height * 3];
        bool bgra = t.Format == Format.B8G8R8A8_UNorm;
        try
        {
            unsafe
            {
                for (int y = 0; y < t.Height; y++)
                {
                    byte* row = (byte*)m.DataPointer + y * (long)m.RowPitch;
                    for (int x = 0; x < t.Width; x++)
                    {
                        int o = (y * t.Width + x) * 3;
                        byte c0 = row[x * 4], c1 = row[x * 4 + 1], c2 = row[x * 4 + 2];
                        outp[o] = (bgra ? c2 : c0) / 255f; outp[o + 1] = c1 / 255f; outp[o + 2] = (bgra ? c0 : c2) / 255f;
                    }
                }
            }
        }
        finally { Context.Unmap(staging, 0); }
        return outp;
    }

    public void Dispose()
    {
        foreach (var c in _cs.Values) c.Dispose();
        PresentVS.Dispose(); PresentPS.Dispose(); PresentCursorVS.Dispose(); PresentCursorPS.Dispose(); AlphaBlend.Dispose(); PresentCB.Dispose();
        foreach (var q in _disjointQ.Concat(_t0Q).Concat(_t1Q).Concat(_pDj).Concat(_p0).Concat(_p1).Concat(_gDj).Concat(_g0).Concat(_g1)) q?.Dispose();
        foreach (var row in _stQ) if (row != null) foreach (var q in row) q?.Dispose();
        foreach (var q in _profPool) q?.Dispose();
        _profDisjoint?.Dispose();
        _syncQ?.Dispose();
        _linear.Dispose(); _point.Dispose(); _cb.Dispose();
        Context.Dispose(); Adapter.Dispose(); Device.Dispose();
    }
}
