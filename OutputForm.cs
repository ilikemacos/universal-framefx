using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;

namespace UniversalFrameFX;

public enum OutputMode { Overlay, Window, Fullscreen }

public static class OutputModeNames
{
    public static string Long(OutputMode m) => m switch
    {
        OutputMode.Overlay => "Overlay on the source window (click-through)",
        OutputMode.Window => "Separate window",
        _ => "Separate fullscreen window",
    };
}

/// <summary>Output resolution choice (index order is persisted).</summary>
public enum OutputRes { Auto, P1440, P2160, Source, P1080 }

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

public sealed class SessionSettings
{
    public volatile FgKind FgKind = FgKind.FrameFX;
    public volatile Backend Backend = Backend.Temporal;
    public volatile MotionPreference Motion = MotionPreference.Auto;
    public volatile float Sharpness = 0.25f;
    public volatile bool FrameGen;
    /// <summary>1.3.4 frame-generation multiplier requested in the UI: 2, 3, 4 or 8 output frames per source frame.</summary>
    public volatile int FgMultiplier = 4;
    /// <summary>SSGI r5 (experimental, OFF by default): screen-space bounce + AO on the captured frame.</summary>
    public volatile bool Ssgi;
    public volatile int SsgiPreset;   // Ssgi.Preset: 0 Auto, 1 GTX 1050 Ti, 2 GTX 980 Ti
    /// <summary>Steadier SSGI lighting while the view moves. UFX_SSGI_TEMPORAL overrides this at the call site.</summary>
    public volatile bool SsgiTemporal = global::UniversalFrameFX.Ssgi.TemporalDefault;
    /// <summary>Experimental lighting. Off by default. UFX_SSRT=0/1 overrides the saved toggle.</summary>
    public volatile bool Ssrt;
    public volatile int SsrtPreset;
    /// <summary>Steadier picture while this lighting is on. UFX_SSRT_TEMPORAL overrides the saved choice.</summary>
    public volatile bool SsrtTemporal = global::UniversalFrameFX.Ssrt.TemporalDefault;
    /// <summary>1.3.4 latency budget (default on): auto-pick faster paths to keep added GPU time per frame under
    /// LatencyBudget.DefaultMs (1.5 ms).</summary>
    public volatile bool LatencyBudget = true;
    public volatile bool Hud = true;
    /// <summary>Performance preset (default since 1.3.1) vs Quality (the 1.3 pipeline).</summary>
    public volatile bool Performance = true;
    /// <summary>1.3.4 Competitive preset: 1080p output, 8× FrameFX generation (refresh cap and safeguards still apply),
    /// latency budget on, Performance path with the fused generation pass and no optional quality passes.</summary>
    public volatile bool Competitive;
    public volatile bool FgV2 = Environment.GetEnvironmentVariable("UFX_FG_V2") == "1";
    public volatile bool FgCsrAi;
    public volatile bool FgV2Async = Environment.GetEnvironmentVariable("UFX_FG_V2_ASYNC") == "1";
    public volatile bool FgV2Vsync = Environment.GetEnvironmentVariable("UFX_FG_V2_VSYNC") == "1";
    public volatile bool FgV2LowResGen = Environment.GetEnvironmentVariable("UFX_FG_V2_LOWRES") == "1";
    public volatile bool FgV2Extrap;
    public volatile bool FgV2Cursor;
    public volatile int FpsCap;
    public volatile bool FixedPacing = Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
}

/// <summary>Frame hand-off with frame-generation scheduling. 1.3.4: ×N — the N-1 generated frames are shown first,
/// then the real one, evenly spaced over one source interval (×2: generated now, real half an interval later).</summary>
public sealed class FrameSlot
{
    readonly object _l = new();
    Tex? _current;
    readonly Queue<(Tex tex, double due, bool generated, float t, double capS)> _queue = new();
    double _lastArrival, _interval = 1.0 / 60;
    public bool LastGenerated { get; private set; }
    public float LastT { get; private set; } = 1;
    public double LastContentTime { get; private set; }
    public long Arrived, Shown;
    /// <summary>Signalled when a new frame is pushed: the present thread sleeps on it instead of re-presenting
    /// an unchanged frame every vsync.</summary>
    public readonly AutoResetEvent Wake = new(false);
    /// <summary>Frames are queued behind the current one (frame generation pacing).</summary>
    public bool HasPending { get { lock (_l) return _queue.Count > 0; } }
    /// <summary>Seconds until the next queued frame is due (0 if overdue), or -1 with nothing queued.</summary>
    public double NextDueIn { get { lock (_l) return _queue.Count > 0 ? Math.Max(0, _queue.Peek().due - Now) : -1; } }
    /// <summary>Smoothed interval between source frames (s).</summary>
    public double SourceInterval { get { lock (_l) return _interval; } }
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    /// <summary>Display refresh interval. 1.3.4: read from the monitor (OutputForm.QueryRefresh). It used to be
    /// measured from vsync-paced presents only, which reads LOW when the GPU is saturated (presents slip), so frame
    /// generation never paused on a high-refresh monitor and kept adding load.</summary>
    public double RefreshInterval = 1.0 / 60;
    public bool RefreshFromMonitor;
    public int FpsCap;
    public int AutoCap = 4;
    public bool FixedPacing;
    Tex? _prevReal, _currReal;
    double _prevCapS, _currCapS;
    bool _haveTwoReal;
    static readonly bool FixedPacingEnv = Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
    /// <summary>Source already runs at ≥ ~75% of the refresh rate: interpolating would only delay real frames.</summary>
    public bool FgBypass => _lastArrival > 0 && _interval < RefreshInterval * 1.33;

    /// <summary>1.3.4 refresh clamp: the largest multiplier (≤ <paramref name="requested"/>) whose output still fits the
    /// refresh rate (base fps × N ≤ refresh, 15% slack). ×2 keeps the old rule (allowed below 75% of the refresh).
    /// Below 2 means frame generation is bypassed.</summary>
    public int EffectiveMultiplier(int requested)
    {
        if (requested != 0 && requested < 2) return 1;
        if (_lastArrival <= 0) return requested == 0 ? Math.Min(AutoCap, 4) : requested;
        double ratio = SourceInterval / Math.Max(RefreshInterval, 1e-4);
        int fit = (int)Math.Floor(ratio + 0.15);
        if (fit < 2 && !FgBypass) fit = 2;
        int cap = requested == 0 ? AutoCap : requested;
        int n = Math.Clamp(fit, 1, cap);
        if (requested == 0 && n >= 20 && ratio < 20 * 1.2) n = Math.Min(10, Math.Max(1, fit < 20 ? fit : 10));
        if (FpsCap > 0)
        {
            double baseFps = 1.0 / Math.Max(SourceInterval, 1e-6);
            while (n >= 2 && baseFps * n > FpsCap) n--;
        }
        return n;
    }

    public void Push(Tex real, Tex? interp) => Push(real, interp == null ? null : new[] { interp });

    public void Push(Tex real, IReadOnlyList<Tex>? generated, double captureTimeS = double.NaN)
    {
        double now = Now;
        lock (_l)
        {
            if (_lastArrival > 0) { double dt = now - _lastArrival; if (dt > 0.001 && dt < 0.5) _interval = _interval * 0.9 + dt * 0.1; }
            _lastArrival = now; Arrived++;
            _queue.Clear();
            int g = generated?.Count ?? 0;
            if (g > 0)
            {
                double step = _interval / (g + 1);
                SetCurrent(generated![0], true, 1f / (g + 1), captureTimeS);
                if (FixedPacing || FixedPacingEnv)
                {
                    double vs = Math.Max(RefreshInterval, 1e-6);
                    for (int k = 1; k < g; k++)
                        _queue.Enqueue((generated[k], now + Math.Max(1, Math.Round(k * step / vs)) * vs, true, (k + 1f) / (g + 1), captureTimeS));
                    _queue.Enqueue((real, now + Math.Max(1, Math.Round(g * step / vs)) * vs, false, 1f, captureTimeS));
                }
                else
                {
                    for (int k = 1; k < g; k++) _queue.Enqueue((generated[k], now + step * k, true, (k + 1f) / (g + 1), captureTimeS));
                    _queue.Enqueue((real, now + step * g, false, 1f, captureTimeS));
                }
            }
            else SetCurrent(real, false, 1f, captureTimeS);
        }
        Wake.Set();
    }

    void SetCurrent(Tex? tex, bool generated, float t, double capS)
    {
        _current = tex;
        LastGenerated = generated;
        LastT = t;
        LastContentTime = capS;
    }

    public void PushReal(Tex real, double captureTimeS)
    {
        lock (_l)
        {
            if (_currReal != null)
            {
                double dt = captureTimeS - _currCapS;
                if (dt > 0.001 && dt < 0.5) _interval = _interval * 0.9 + dt * 0.1;
            }
            else if (_lastArrival > 0)
            {
                double dt = Now - _lastArrival;
                if (dt > 0.001 && dt < 0.5) _interval = _interval * 0.9 + dt * 0.1;
            }
            _lastArrival = Now; Arrived++;
            _prevReal = _currReal; _prevCapS = _currCapS;
            _currReal = real; _currCapS = captureTimeS;
            _haveTwoReal = _prevReal != null;
            SetCurrent(real, false, 1f, captureTimeS);
            _queue.Clear();
        }
        Wake.Set();
    }

    public (Tex? prev, Tex? curr, double prevCapS, double currCapS, bool haveTwo) SnapshotReal()
    {
        lock (_l) return (_prevReal, _currReal, _prevCapS, _currCapS, _haveTwoReal);
    }

    public Tex? Take()
    {
        lock (_l)
        {
            double now = Now;
            while (_queue.Count > 0 && now >= _queue.Peek().due)
            {
                var n = _queue.Dequeue();
                SetCurrent(n.tex, n.generated, n.t, n.capS);
            }
            return _current;
        }
    }

    public void Clear() { lock (_l) { SetCurrent(null, false, 1f, double.NaN); _queue.Clear(); _lastArrival = 0; _prevReal = _currReal = null; _haveTwoReal = false; } }
}

/// <summary>Fullscreen/windowed output with a flip-model swapchain, a present thread and a HUD.</summary>
public sealed class OutputForm : Form
{
    readonly Gpu _gpu;
    readonly FramePipeline _pipe;
    Ssgi? _ssgi;                         // SSGI r5: created on first use (present thread), never when the toggle is off
    Ssrt? _ssrt;
    public string SsgiStatus => _ssgi?.Status ?? "off";
    public string SsrtStatus => _ssrt?.Status ?? "off";
    readonly SessionSettings _settings;
    readonly IntPtr _source;          // IntPtr.Zero = built-in test scene
    readonly int _testInW, _testInH;
    readonly FrameSlot _slot = new();
    readonly Label _hud;
    readonly System.Windows.Forms.Timer _hudTimer;
    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _rtv;
    IntPtr _latencyWait;
    bool _waitableSwap;
    int _bbW, _bbH;
    Thread? _thread;
    volatile bool _run;
    volatile bool _resizePending;
    WindowCapture? _capture;
    long _processed, _lastProcessed, _lastShown;
    double _lastHudT;
    public string HudText => _hud.Text;
    string _status = "";
    int _inW, _inH, _outW, _outH;
    Tex? _lastDrawn;
    Tex? _lastGen;
    double _lastGenAtS = double.NaN;
    double _holdDelayS, _procMarginS, _procMs;
    CursorOverlay? _cursor;
    readonly OutputRes _res;
    bool OverlayFollowsWindow => Mode == OutputMode.Overlay && (_res == OutputRes.Auto || _res == OutputRes.Source);
    volatile bool _fgBypassed;
    /// <summary>1.3.4: multiplier used for the last source frame (after refresh clamp + safeguard), 0 = none.</summary>
    volatile int _fgEffective;
    readonly FgGuard _guard = new();
    readonly LatencyBudget _budget = new();
    double _capLatMs;
    // 1.3.4 frame pacing: intervals between presents of NEW frames (QPC, taken when Present returns at vsync).
    readonly double[] _ft = new double[240]; int _ftN, _ftI; double _lastShowT; bool _newShown;
    /// <summary>Added GPU time per captured frame the budget sees (processing incl. generated frames + present draw).</summary>
    public double ResponseMs { get; private set; }

    /// <summary>1.3.4: apply the latency-budget level to the pipeline; returns the backend to run.</summary>
    Backend BudgetPath(Backend b, bool oneToOne)
    {
        int L = _settings.LatencyBudget ? _budget.Level : 0;
        bool comp = _settings.Competitive;   // Competitive: the cheapest path from the start (budget levels 1-3 + no pick)
        _pipe.ForceFrameFxFg = comp || L >= 1;
        _pipe.Fast = comp || _settings.Performance || L >= 2;
        // Round 4: Performance also uses the fused pass from level 0, with the per-frame vector (two fixed-point motion taps
        // per step; harness identical to the per-frame passes, est. 1440p x4 1.64 -> 1.31 ms incl. the motion savings).
        // Competitive adds one motion tap for steps t <= 0.5 during fast pans (better big flicks, mode 4); budget levels
        // >= 2 keep the shared vector. UFX_FG_PERFRAME=1 = the old per-frame passes, UFX_FG_FUSEDV=0..4 overrides the vector mode.
        _pipe.FusedFg = comp || L >= 2 || (_settings.Performance && !FramePipeline.PerFrameEnv);
        _pipe.FusedVecMode = comp ? 4 : L >= 2 ? 0 : 3;
        b = ApplyLatencyStepDown(b, L, comp, oneToOne);
        _pipe.LowResMotion = L >= 4;
        _pipe.LowResGen = _settings.FgV2LowResGen || L >= 4;
        _pipe.FgV2ExtrapOn = !Fg20Requested && (comp || _settings.FgV2Extrap);
        _pipe.FgV2VsyncGen = _settings.FgV2 && _settings.FgV2Vsync;
        _pipe.PickVectors = !comp && L < 4;
        // Round 4: Competitive keeps the cheap border/HUD passes, a 3-candidate final pick and the 2nd median, and drops
        // the 1/4 propagation (harness 8x 29.3 -> 32.5 dB; est. 720p->1080p x8 ~1.0 ms). UFX_FG_COMPLEAN=1 = round-3 lean set.
        bool lean = comp && FramePipeline.CompLeanEnv;
        _pipe.Lean = lean;
        _pipe.PickLite = comp && !lean && L < 4;
        _pipe.SkipClean = comp;
        if (_settings.LatencyBudget || _settings.FrameGen) _gpu.TimingEnabled = true;   // non-blocking timestamps
        return b;
    }

    internal static Backend ApplyLatencyStepDown(Backend b, int level, bool competitive, bool oneToOne) =>
        Backends.ApplyLatencyStepDown(b, level, competitive, oneToOne);

    void BudgetUpdate(double nowS)
    {
        ResponseMs = Math.Max(_gpu.LastGpuMs, _pipe.VendorMs) + _gpu.LastPresentMs;
        if (_settings.LatencyBudget) _budget.Update(ResponseMs, nowS); else _budget.Reset();
    }
    int MaxMul => _settings.FgV2 && _settings.FgV2Vsync ? 20 : 8;
    int BudgetMulCap => MaxMul > 8 ? MaxMul : (_settings.LatencyBudget ? _budget.MulCap : MaxMul);
    int ReqMul() => _settings.FgMultiplier == 0 ? 0 : (MaxMul > 8 ? FgMul.ClampV2(_settings.FgMultiplier) : FgMul.Clamp(_settings.FgMultiplier));
    bool Fg20Requested => MaxMul > 8 && (_settings.FgMultiplier == 20 || (_settings.FgMultiplier == 0 && _slot.AutoCap >= 20 && (_fgEffective >= 20 || _slot.EffectiveMultiplier(0) >= 20)));
    void ApplyMulCaps()
    {
        _slot.AutoCap = Math.Min(MaxMul, 8);
        _guard.MaxCap = MaxMul;
        if (_pipe.V2 is FgV2 v2) v2.ForceCompute = ReqMul() > 8 || (ReqMul() == 0 && _fgEffective > 8);
    }
    public string FgStatus { get; private set; } = "";
    /// <summary>Beta2: shown in the main window too, because an exclusive-fullscreen game hides the HUD itself.</summary>
    public string Warning { get; private set; } = "";
    /// <summary>Beta2: source-fps lock hint (ASUS Silent / NVIDIA Whisper Mode ~40, Battery Boost ~30).</summary>
    public string Hint { get; private set; } = "";
    public string AntiCheatNote { get; set; } = "";
    /// <summary>Compare mode: processing is off for this output only. Saved settings and profiles are not touched.</summary>
    public bool CompareOff { get; private set; }
    public string CompareStatus => CompareOff ? "FrameFX OFF (compare)" : "FrameFX ON";
    public double SourceFps { get; private set; }
    public double OutputFps { get; private set; }
    readonly Queue<double> _srcHist = new();
    volatile bool _fgBudgetPaused;
    double _fgBudgetUntil; int _fgOverBudget;
    /// <summary>Frame generation pauses when the overlay's GPU time would exceed this share of the GPU (UFX_FG_BUDGET, 0 = off).</summary>
    static readonly double FgBudget = double.TryParse(Environment.GetEnvironmentVariable("UFX_FG_BUDGET"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fb) ? fb : 0.35;
    /// <summary>Old behaviour: present every vsync even when nothing changed (UFX_PRESENT_ALWAYS=1).</summary>
    static readonly bool PresentAlways = Environment.GetEnvironmentVariable("UFX_PRESENT_ALWAYS") == "1";
    volatile bool _forcePresent = true;
    string _adapterNote = "";

    public string? Error { get; private set; }
    /// <summary>GPU share measured when frame generation was last paused for the budget.</summary>
    public double LastFgShare { get; private set; }
    public OutputMode Mode { get; }
    public bool OverlayShown { get; private set; } = true;
    public string MotionSource => _pipe.MotionSource;
    public string SwapInfo { get; private set; } = "";
    /// <summary>Games-only watcher: the captured source window (IntPtr.Zero = built-in test scene).</summary>
    public IntPtr Source => _source;
    /// <summary>Frames processed so far (for the games-only watcher / test log).</summary>
    public long Processed => Interlocked.Read(ref _processed);
    /// <summary>Games-only: hidden and idle while the foreground is not a game (call SetPaused on the UI thread).</summary>
    public bool Paused { get; private set; }
    /// <summary>Set when the user stopped this output (Esc, Ctrl+Alt+Q, the Stop button) — as opposed
    /// to the source window closing itself. Read by MainForm when the form closes.</summary>
    public bool UserStop;
    /// <summary>Frames processed when the last resume happened: while armed, the window stays hidden
    /// until one more frame has been processed, so a stale frame never flashes on the game.</summary>
    long _resumeMark = -1;
    bool _transparent;   // overlay not yet made opaque (no frame processed yet)
    readonly System.Windows.Forms.Timer? _follow;
    Native.RECT _lastRect;
    bool _hotkeys;
    const int HkToggle = 0xF1, HkStop = 0xF2, HkCompare = 0xF3;

    public OutputForm(Gpu gpu, FramePipeline pipe, SessionSettings settings, IntPtr source, Screen screen,
                      OutputMode mode, int testInW, int testInH, int outW, int outH, OutputRes res = OutputRes.Auto)
    {
        _gpu = gpu; _pipe = pipe; _settings = settings; _source = source; _res = res;
        if (int.TryParse(Environment.GetEnvironmentVariable("UFX_FPS_CAP"), out var fpsCap)) _settings.FpsCap = fpsCap;
        _testInW = testInW; _testInH = testInH; _outW = outW; _outH = outH;
        if (mode == OutputMode.Overlay && source == IntPtr.Zero) mode = OutputMode.Window;   // the test scene has no window to cover
        Mode = mode;
        if (mode == OutputMode.Overlay) _resumeMark = 0;   // overlay: stay hidden until the first processed frame (no black flash on start)
        Text = mode == OutputMode.Overlay ? "Universal-FrameFX overlay" : "Universal-FrameFX — Output (Esc or Ctrl+Alt+Q to stop)";
        BackColor = Color.Black;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        if (mode == OutputMode.Overlay)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            var r = OverlayRect();
            Bounds = new Rectangle(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            _lastRect = r;
            _follow = new System.Windows.Forms.Timer { Interval = 33 };
            _follow.Tick += (_, _) => FollowSource();
        }
        else if (mode == OutputMode.Window)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            var wa = screen.WorkingArea;
            int w = Math.Min(1280, wa.Width * 8 / 10), h = w * 9 / 16;
            Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
        }
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        _hud = new Label
        {
            AutoSize = true, ForeColor = Color.White, BackColor = Color.FromArgb(20, 20, 20),
            Font = new Font(FontFamily.GenericMonospace, 9f), Location = new Point(12, 12), Padding = new Padding(6),
        };
        Controls.Add(_hud);
        _hudTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _hudTimer.Tick += (_, _) => UpdateHud();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { UserStop = true; Close(); } };
        Resize += (_, _) => { _resizePending = true; _slot.Wake.Set(); };
    }

    // Overlay: borderless, click-through, always on top, never activated, so the
    // source window keeps mouse and keyboard focus.
    protected override bool ShowWithoutActivation => Mode == OutputMode.Overlay;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (Mode == OutputMode.Overlay)
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW_;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Overlay starts fully transparent: Show() makes it visible before any frame exists, so it would
        // flash black over the game. FollowSource makes it opaque once the first frame has been processed.
        if (Mode == OutputMode.Overlay) { Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA); _transparent = true; }
        // Keep our own output out of any screen capture (Windows 10 2004+). UFX_ALLOW_CAPTURE=1 is for testing.
        if (Environment.GetEnvironmentVariable("UFX_ALLOW_CAPTURE") != "1")
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
        // Global hotkeys: they work while the source app has focus. Overlay stays click-through either way.
        bool hkF = Native.RegisterHotKey(Handle, HkToggle, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.F);
        bool hkQ = Native.RegisterHotKey(Handle, HkStop, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.Q);
        bool hkC = Native.RegisterHotKey(Handle, HkCompare, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.C);
        _hotkeys = hkF && hkQ && hkC;
        if (!_hotkeys)
        {
            var busy = new List<string>();
            if (!hkF) busy.Add("Ctrl+Alt+F");
            if (!hkQ) busy.Add("Ctrl+Alt+Q");
            if (!hkC) busy.Add("Ctrl+Alt+C");
            _status = string.Join(" / ", busy) + (busy.Count > 1 ? " are" : " is") + " in use by another app.";
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = (int)m.WParam;
            if (id == HkStop) { UserStop = true; BeginInvoke(new Action(Close)); return; }
            if (id == HkToggle) { ToggleOverlay(); return; }
            if (id == HkCompare) { ToggleCompare(); return; }
        }
        base.WndProc(ref m);
    }

    /// <summary>Ctrl+Alt+F: hide/show the overlay (window modes: toggle the HUD). While paused the
    /// window stays hidden; FollowSource shows it after the resume once a fresh frame is in.</summary>
    public void ToggleOverlay()
    {
        if (Mode != OutputMode.Overlay) { _settings.Hud = !_settings.Hud; return; }
        OverlayShown = !OverlayShown;
        if (!OverlayShown) Native.ShowWindow(Handle, Native.SW_HIDE);
        else if (!Paused) Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    /// <summary>Ctrl+Alt+C: show the captured frame scaled to the output, with frame generation and SSGI off.
    /// Does not write saved settings, profiles, or the Apply/dirty state. The safeguard is reset so the
    /// passthrough frames are not a baseline measurement.</summary>
    public void ToggleCompare() => SetCompareOff(!CompareOff);

    public void SetCompareOff(bool off)
    {
        if (off == CompareOff) return;
        CompareOff = off;
        lock (Gpu.Lock)
        {
            _pipe.Reset();
            _guard.Reset();
            _slot.Clear();
            _forcePresent = true;
            _fgEffective = 0;
            _fgBypassed = false;
        }
    }

    /// <summary>Games-only pause/resume (call on the UI thread). Pause hides the window immediately.
    /// Resume clears the pipeline's temporal history (under Gpu.Lock, so no ghosting) and lets
    /// FollowSource show the window only after the next processed frame.</summary>
    public void SetPaused(bool paused)
    {
        if (paused == Paused) return;
        if (paused)
        {
            Paused = true;
            if (IsHandleCreated) Native.ShowWindow(Handle, Native.SW_HIDE);
        }
        else
        {
            lock (Gpu.Lock) { _pipe.Reset(); _slot.Clear(); _forcePresent = true; }
            _resumeMark = Interlocked.Read(ref _processed);
            Paused = false;
        }
    }

    /// <summary>Overlay area: the source window itself (1:1), or — for a fixed 1440p/4K output — the whole monitor the
    /// source is on, with the upscaled image letterboxed (still click-through: clicks reach whatever is underneath).</summary>
    Native.RECT OverlayRect()
    {
        if (OverlayFollowsWindow) return Native.FrameBounds(_source);
        var b = Screen.FromHandle(_source).Bounds;
        return new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
    }

    void FollowSource()
    {
        if (!Native.IsWindow(_source)) { _status = "Source window closed."; Close(); return; }
        bool hidden = Native.IsIconic(_source) || !Native.IsWindowVisible(_source);
        // While paused (games only: foreground is not a game) the window stays hidden, and after a
        // resume it stays hidden until a fresh frame has been processed (no flash of a stale frame).
        bool frameReady = !Paused && Interlocked.Read(ref _processed) > _resumeMark;
        if (hidden || !OverlayShown || !frameReady) { if (Visible) Native.ShowWindow(Handle, Native.SW_HIDE); return; }
        var r = OverlayRect();
        bool moved = r.Left != _lastRect.Left || r.Top != _lastRect.Top || r.Right != _lastRect.Right || r.Bottom != _lastRect.Bottom;
        if (moved || !Native.IsWindowVisible(Handle))
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top),
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
            _lastRect = r;
            _resizePending = true;
            _slot.Wake.Set();
        }
        if (_transparent) { Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA); _transparent = false; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            lock (Gpu.Lock) CreateSwapChain();
            QueryRefresh();
            if (_source != IntPtr.Zero) _adapterNote = Gpu.DisplayAdapterNote(_gpu, _source);
            if (_source != IntPtr.Zero)
            {
                _capture = new WindowCapture(_gpu.Device, _source, OnCaptured, captureCursor: !(_settings.FgV2 && _settings.FgV2Cursor));
                _capture.Closed += () => BeginInvoke(new Action(() => { _status = "Source window closed."; Close(); }));
            }
            _run = true;
            _follow?.Start();
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "FrameFX present", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            _hudTimer.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            MessageBox.Show(this, ex.Message, "Universal-FrameFX", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE dm);

    /// <summary>Refresh rate of the monitor the output is on (the real vsync rate, independent of GPU load).</summary>
    void QueryRefresh()
    {
        try
        {
            var scr = Screen.FromHandle(_source != IntPtr.Zero && Mode == OutputMode.Overlay ? _source : Handle);
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(scr.DeviceName, -1, ref dm) && dm.dmDisplayFrequency > 1)
            {
                _slot.RefreshInterval = 1.0 / dm.dmDisplayFrequency;
                _slot.RefreshFromMonitor = true;
            }
        }
        catch { }
    }

    bool WantWaitableSwap()
    {
        if (!_settings.FgV2 || !_settings.FgV2Vsync || CompareOff) return false;
        int req = ReqMul();
        if (req > 8) return true;
        if (req == 0 && _slot.AutoCap > 8) return _fgEffective > 8 || _fgEffective == 0;
        return false;
    }

    void ReleaseSwapChain()
    {
        _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _rtv?.Dispose(); _rtv = null;
        _gpu.Context.Flush();
        _swap?.Dispose(); _swap = null;
        _latencyWait = IntPtr.Zero;
        _waitableSwap = false;
    }

    void EnsureSwapChainFlags()
    {
        bool want = WantWaitableSwap();
        if (_swap != null && want == _waitableSwap) return;
        ReleaseSwapChain();
        CreateSwapChain();
    }

    void CreateSwapChain()
    {
        using var dxgi = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _bbW = Math.Max(1, ClientSize.Width); _bbH = Math.Max(1, ClientSize.Height);
        bool waitable = WantWaitableSwap();
        var flags = waitable ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None;
        try
        {
            _swap = factory.CreateSwapChainForHwnd(_gpu.Device, Handle, new SwapChainDescription1
            {
                Width = (uint)_bbW, Height = (uint)_bbH, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
                Flags = flags,
            });
            SwapInfo = waitable ? "flip-discard waitable" : "flip-discard";
            _waitableSwap = waitable;
        }
        catch when (Mode == OutputMode.Overlay)
        {
            _swap = factory.CreateSwapChainForHwnd(_gpu.Device, Handle, new SwapChainDescription1
            {
                Width = (uint)_bbW, Height = (uint)_bbH, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 1, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.Discard, AlphaMode = AlphaMode.Ignore,
            });
            SwapInfo = "blt (layered-window fallback)";
            _waitableSwap = false;
        }
        factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);
        if (_waitableSwap)
        {
            try
            {
                using var s2 = _swap.QueryInterface<IDXGISwapChain2>();
                s2.MaximumFrameLatency = 1;
                _latencyWait = s2.FrameLatencyWaitableObject;
            }
            catch { _latencyWait = IntPtr.Zero; _waitableSwap = false; }
        }
        using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _gpu.Device.CreateRenderTargetView(bb);
    }

    void ResizeSwapChain()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (w == _bbW && h == _bbH) return;
        _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _rtv?.Dispose(); _rtv = null;
        _gpu.Context.Flush();
        _swap!.ResizeBuffers(0, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, _waitableSwap ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None);
        using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _gpu.Device.CreateRenderTargetView(bb);
        _bbW = w; _bbH = h;
    }

    Windows.Graphics.Capture.Direct3D11CaptureFrame? _pendingFrame;
    readonly object _frameLock = new();

    void OnCaptured(Windows.Graphics.Capture.Direct3D11CaptureFrame frame)
    {
        Windows.Graphics.Capture.Direct3D11CaptureFrame? old;
        lock (_frameLock) { old = _pendingFrame; _pendingFrame = _run ? frame : null; }
        old?.Dispose();                 // drop an unprocessed frame: newest wins
        if (!_run) frame.Dispose();
        _slot.Wake.Set();
    }

    /// <summary>Called on the present thread with Gpu.Lock held.</summary>
    void ProcessPendingCapture()
    {
        Windows.Graphics.Capture.Direct3D11CaptureFrame? frame;
        lock (_frameLock) { frame = _pendingFrame; _pendingFrame = null; }
        if (frame is null) return;
        try
        {
            using var tex = WindowCapture.TextureOf(frame);
            if (tex is null) return;
            var d = tex.Description;
            int w = Math.Min((int)d.Width, frame.ContentSize.Width), h = Math.Min((int)d.Height, frame.ContentSize.Height);
            int tw, th;
            if (_res == OutputRes.Source) { tw = w; th = h; }                       // 1:1
            else if (OverlayFollowsWindow) { tw = _bbW; th = _bbH; }                // overlay on the window
            else { tw = _outW; th = _outH; }                                        // fit monitor / 1440p / 4K
            // Vendor upscalers support up to 3x per axis; CSR up to 4x. Compare keeps this size and only changes the scale.
            double maxScale = Backends.IsVendor(_settings.Backend) ? 3.0 : 4.0;
            double scale = Math.Clamp(Math.Min((double)tw / w, (double)th / h), 1.0, maxScale);
            int ow = Math.Max(2, (int)(w * scale) & ~1), oh = Math.Max(2, (int)(h * scale) & ~1);
            using var srv = _gpu.Device.CreateShaderResourceView(tex);
            // SSGI r5: when on, the pipeline gets the SSGI output (same size/format) instead of the capture; when off this
            // is just a bool test and the capture SRV is passed exactly as before. Compare skips it.
            bool compare = CompareOff;
            var inSrv = srv;
            bool ssrtOn = !compare && Ssrt.Enabled(_settings.Ssrt);
            bool ssrtTemp = ssrtOn && Ssrt.TemporalEnabled(_settings.SsrtTemporal);
            bool steady = !compare && !ssrtOn && _settings.Ssgi && Ssgi.TemporalEnabled(_settings.SsgiTemporal);
            _pipe.WantGlobalPan = steady || ssrtTemp;
            _pipe.WantMotionField = ssrtTemp;
            if (ssrtOn)
            {
                _ssrt ??= new Ssrt(_gpu);
                inSrv = _ssrt.Apply(srv, tex, _settings.SsrtPreset, ow, oh, ssrtTemp ? _pipe.PrevMotionSrv : null, ssrtTemp ? _pipe.GlobalPanSrv : null, ssrtTemp) ?? srv;
            }
            else if (!compare && _settings.Ssgi) { _ssgi ??= new Ssgi(_gpu); inSrv = _ssgi.Apply(srv, tex, (Ssgi.Preset)_settings.SsgiPreset, steady ? _pipe.GlobalPanSrv : null, steady) ?? srv; }
            _pipe.Sharpness = _settings.Sharpness;
            _pipe.Motion = _settings.Motion;
            _pipe.UseV2 = _settings.FgV2;
            _pipe.UseCsrAiFg = _settings.FgCsrAi;
            _pipe.UseV2Async = _settings.FgV2Async;
            _pipe.FgV2VsyncGen = _settings.FgV2 && _settings.FgV2Vsync;
            _pipe.FgV2ExtrapOn = _settings.Competitive || _settings.FgV2Extrap;
            _pipe.LowResGen = _settings.FgV2LowResGen;
            _slot.FixedPacing = _settings.FixedPacing;
            _pipe.FrameGenKind = _settings.FgKind; _pipe.Fast = _settings.Performance; _gpu.TimingEnabled = _settings.Hud;
            double nowS = FgV2Pacing.QpcSeconds();
            double capS = FgV2Pacing.CaptureSeconds(frame.SystemRelativeTime);
            Tex outT;
            if (compare)
            {
                // Passthrough for this session: bilinear scale to the output size, no frame generation, no SSGI.
                // The safeguard is not fed, so these frames cannot become a baseline or a drop.
                _fgBypassed = false;
                _fgEffective = 0;
                (outT, _) = _pipe.Process(inSrv, w, h, ow, oh, Backend.Bilinear, 0, 0, false, 2);
            }
            else
            {
            // Adaptive: when the source already runs near the refresh rate, show real frames only.
            ApplyMulCaps();
            int req = ReqMul();
            _slot.FpsCap = _settings.FpsCap;
            int fit = _slot.EffectiveMultiplier(req);
            _fgBypassed = _settings.FrameGen && fit < 2;
            // 1.3.4: GPU budget. The overlay shares the GPU with the game: GPU ms per source frame x source fps is the
            // share of the GPU it takes. Past the budget, frame generation lowers the game's frame rate more than it adds,
            // so it pauses for 5 s and is then re-measured.
            if (_fgBudgetPaused && nowS >= _fgBudgetUntil) { _fgBudgetPaused = false; _fgOverBudget = 0; }
            // 1.3.4 base-fps safeguard: baseline with generation off, then lower the multiplier if the game loses >5%.
            _guard.Update(_settings.FrameGen, Interlocked.Read(ref _slot.Arrived), nowS, fit, _fgEffective, _pipe.LastSkippedUnchanged);
            int neff = Math.Min(Math.Min(fit, _guard.Cap), BudgetMulCap);
            bool fgOn = _settings.FrameGen && neff >= 2 && _guard.Allows && !_fgBudgetPaused;
            if (_settings.FrameGen) _gpu.TimingEnabled = true;   // non-blocking timestamp queries; needed for the budget
            var backend = BudgetPath(_settings.Backend, w == ow && h == oh);
            _pipe.MotionGapN = fgOn ? neff : 0;
            (outT, _) = _pipe.Process(inSrv, w, h, ow, oh, backend, 0, 0, fgOn, neff);
            BudgetUpdate(nowS);
            _fgEffective = !fgOn ? 0 : (_settings.FgV2 && _settings.FgV2Vsync ? neff : (_pipe.Generated.Count > 0 ? _pipe.Generated.Count + 1 : 0));
            if (fgOn && FgBudget > 0 && _gpu.LastGpuMs > 0)
            {
                double share = _gpu.LastGpuMs / 1000.0 / Math.Max(_slot.SourceInterval, 1e-3);
                _fgOverBudget = share > FgBudget ? _fgOverBudget + 1 : 0;
                if (_fgOverBudget > 30) { _fgBudgetPaused = true; _fgBudgetUntil = nowS + 5; LastFgShare = share; }
            }
            }
            _inW = w; _inH = h;
            double after = FgV2Pacing.QpcSeconds();
            double proc = Math.Max(0, after - nowS);
            _procMs = proc * 1000.0;
            double ready = capS > 0 && after - capS > 0 && after - capS < 0.25 ? after - capS : proc;
            _procMarginS = _procMarginS <= 0 ? ready : _procMarginS * 0.9 + ready * 0.1;
            _holdDelayS = FgV2Pacing.HoldDelay(_slot.SourceInterval, _procMarginS);
            if (_settings.FgV2 && _settings.FgV2Vsync)
                _slot.PushReal(outT, capS);
            else
                _slot.Push(outT, _pipe.Generated, capS);
            Interlocked.Increment(ref _processed);
            double lat = (after - capS) * 1000.0;
            if (lat > 0 && lat < 500) _capLatMs = _capLatMs <= 0 ? lat : _capLatMs * 0.9 + lat * 0.1;
        }
        catch (Exception ex) { _status = "GPU error: " + ex.Message; }
        finally { frame.Dispose(); }
    }

    void RenderLoop()
    {
        Tex? low = null;
        var sw = Stopwatch.StartNew();
        int frame = 0, phases = Jitter.PhaseCount(_testInW, _outW);
        int testPhase = 0;
        double lastPresent = 0;
        bool presented;
        try
        {
            while (_run)
            {
                if (Paused)
                {
                    // Games only, foreground is not a game: drop any captured frame, do no
                    // processing and no present — GPU and CPU idle.
                    lock (_frameLock) { _pendingFrame?.Dispose(); _pendingFrame = null; }
                    Thread.Sleep(10);
                    continue;
                }
                lock (Gpu.Lock)
                {
                    ApplyMulCaps();
                    EnsureSwapChainFlags();
                    if (_resizePending) { _resizePending = false; ResizeSwapChain(); _forcePresent = true; }
                    if (_source == IntPtr.Zero)
                    {
                        // Built-in test scene: a genuinely jittered low-res render.
                        // With frame generation on, a new source frame arrives every
                        // other vsync so the interpolated frame fills the gap.
                        // ×N: a new source frame every N vsyncs, the generated frames fill the gaps.
                        bool compare = CompareOff;
                        int tn = compare ? 1 : (_settings.FgMultiplier == 0 ? Math.Max(1, _slot.EffectiveMultiplier(0)) : ReqMul());
                        testPhase = (testPhase + 1) % Math.Max(1, tn);
                        bool newFrame = compare || !_settings.FrameGen || testPhase == 0;
                        if (newFrame)
                        {
                            low ??= new Tex(_gpu.Device, _testInW, _testInH, Format.R8G8B8A8_UNorm);
                            var b = _settings.Backend;
                            var (jx, jy) = !compare && Backends.IsTemporal(b) ? Jitter.Offset(frame, phases) : (0f, 0f);
                            double tS = FgV2Pacing.QpcSeconds();
                            frame++;
                            _pipe.EncodeTestScene(low, (float)sw.Elapsed.TotalSeconds, jx, jy);
                            bool ssrtOn = !compare && Ssrt.Enabled(_settings.Ssrt);
                            bool ssrtTemp = ssrtOn && Ssrt.TemporalEnabled(_settings.SsrtTemporal);
                            _pipe.WantGlobalPan = ssrtTemp;
                            _pipe.WantMotionField = ssrtTemp;
                            var inSrv = low.Srv;
                            if (ssrtOn)
                            {
                                _ssrt ??= new Ssrt(_gpu);
                                inSrv = _ssrt.Apply(low.Srv, low.Texture, _settings.SsrtPreset, _outW, _outH, ssrtTemp ? _pipe.PrevMotionSrv : null, ssrtTemp ? _pipe.GlobalPanSrv : null, ssrtTemp) ?? low.Srv;
                            }
                            _pipe.Sharpness = _settings.Sharpness;
                            _pipe.Motion = _settings.Motion;
                            _pipe.UseV2 = _settings.FgV2;
                            _pipe.UseCsrAiFg = _settings.FgCsrAi;
                            _pipe.UseV2Async = _settings.FgV2Async;
                            _pipe.FgV2VsyncGen = _settings.FgV2 && _settings.FgV2Vsync;
                            _pipe.FgV2ExtrapOn = _settings.Competitive || _settings.FgV2Extrap;
                            _pipe.LowResGen = _settings.FgV2LowResGen;
                            _slot.FixedPacing = _settings.FixedPacing;
                            _slot.FpsCap = _settings.FpsCap;
                            _pipe.FrameGenKind = _settings.FgKind; _pipe.Fast = _settings.Performance; _gpu.TimingEnabled = _settings.Hud;
                            Tex outT;
                            if (compare)
                            {
                                _fgEffective = 0;
                                (outT, _) = _pipe.Process(inSrv, _testInW, _testInH, _outW, _outH, Backend.Bilinear, 0, 0, false, 2);
                            }
                            else
                            {
                                b = BudgetPath(b, _testInW == _outW && _testInH == _outH);
                                int tcap = BudgetMulCap;
                                int tmul = Math.Min(tn, Math.Max(2, tcap));
                                _pipe.MotionGapN = _settings.FrameGen && tcap >= 2 ? tmul : 0;
                                (outT, _) = _pipe.Process(inSrv, _testInW, _testInH, _outW, _outH, b, jx, jy, _settings.FrameGen && tcap >= 2, tmul);
                                BudgetUpdate(tS);
                                _fgEffective = !_settings.FrameGen ? 0 : (_settings.FgV2 && _settings.FgV2Vsync ? tmul : (_pipe.Generated.Count > 0 ? _pipe.Generated.Count + 1 : 0));
                            }
                            _inW = _testInW; _inH = _testInH;
                            _procMs = (FgV2Pacing.QpcSeconds() - tS) * 1000.0;
                            _procMarginS = _procMarginS <= 0 ? _procMs / 1000.0 : _procMarginS * 0.9 + _procMs / 1000.0 * 0.1;
                            _holdDelayS = FgV2Pacing.HoldDelay(_slot.SourceInterval, _procMarginS);
                            if (_settings.FgV2 && _settings.FgV2Vsync)
                                _slot.PushReal(outT, tS);
                            else
                                _slot.Push(outT, _pipe.Generated, tS);
                            Interlocked.Increment(ref _processed);
                        }
                    }
                    else ProcessPendingCapture();
                    Tex? tex;
                    bool vsyncV2 = _settings.FgV2 && _settings.FgV2Vsync && !CompareOff;
                    bool shownGen = false;
                    float shownT = 1;
                    double shownCap = double.NaN;
                    if (vsyncV2)
                    {
                        var snap = _slot.SnapshotReal();
                        tex = VsyncGenerate();
                        shownGen = tex != null && snap.curr != null && !ReferenceEquals(tex, snap.curr);
                        shownT = LastV2T;
                        shownCap = snap.currCapS;
                        bool needed = PresentAlways || tex != null || _forcePresent || _resizePending || !ReferenceEquals(tex, _lastDrawn);
                        if (needed) { _forcePresent = false; DrawAndPresent(tex); presented = true; }
                        else presented = false;
                    }
                    else
                    {
                    var queued = _slot.Take();
                    tex = queued;
                    shownGen = _slot.LastGenerated;
                    shownT = _slot.LastT;
                    shownCap = _slot.LastContentTime;
                    double due = _slot.NextDueIn;
                    bool needed = PresentAlways || _source == IntPtr.Zero || _forcePresent || !ReferenceEquals(tex, _lastDrawn) || (due >= 0 && due <= _slot.RefreshInterval * 1.5);
                    if (needed) { _forcePresent = false; DrawAndPresent(tex); presented = true; }
                    else presented = false;
                    }
                if (presented)
                {
                    double nowP = FgV2Pacing.QpcSeconds();
                    double dtpP = lastPresent > 0 ? (nowP - lastPresent) * 1000.0 : double.NaN;
                    lastPresent = nowP;
                    if (!_slot.RefreshFromMonitor && dtpP > 3 && dtpP < 50) _slot.RefreshInterval = _slot.RefreshInterval * 0.95 + dtpP / 1000.0 * 0.05;
                    double gpuMs = shownGen ? _gpu.LastGenMs : _gpu.LastGpuMs;
                    StatsLog.Record(nowP, shownGen ? "generated" : "real", shownT, shownCap, gpuMs, dtpP);
                }
                }
                if (!presented)
                {
                    double due = _slot.NextDueIn;
                    int ms = due < 0 ? 50 : (int)Math.Clamp((due - _slot.RefreshInterval * 1.5) * 1000, 1, 50);
                    if (_latencyWait != IntPtr.Zero)
                    {
                        var handles = new[] { _latencyWait, _slot.Wake.SafeWaitHandle.DangerousGetHandle() };
                        Native.WaitForMultipleObjects(2, handles, false, (uint)ms);
                    }
                    else _slot.Wake.WaitOne(ms);
                    continue;
                }
                else if (_latencyWait != IntPtr.Zero)
                    Native.WaitForSingleObject(_latencyWait, 50);
            }
        }
        catch (Exception ex)
        {
            _status = "Present error: " + ex.Message;
        }
        finally
        {
            lock (Gpu.Lock) { low?.Dispose(); _ssgi?.Dispose(); _ssgi = null; _ssrt?.Dispose(); _ssrt = null; _cursor?.Dispose(); _cursor = null; }
        }
    }

    public float LastV2T { get; private set; } = 1;

    Tex? GenRateLimited(Func<Tex?> gen)
    {
        double nowS = FgV2Pacing.QpcSeconds();
        int cap = Math.Max(2, _slot.EffectiveMultiplier(ReqMul()));
        if (_lastGen == null || FgV2Pacing.ShouldGenerate(nowS, _lastGenAtS, !double.IsNaN(_lastGenAtS), _slot.SourceInterval, cap))
        {
            var t = gen();
            if (t != null) { _lastGen = t; _lastGenAtS = nowS; }
        }
        return _lastGen;
    }

    Tex? VsyncGenerate()
    {
        var (prev, curr, prevCapS, currCapS, haveTwo) = _slot.SnapshotReal();
        if (curr == null) return _slot.Take();
        double nowS = FgV2Pacing.QpcSeconds();
        bool extrap = !Fg20Requested && (_settings.Competitive || _settings.FgV2Extrap);
        double hold = extrap ? 0 : _holdDelayS;
        if (hold <= 0 && !extrap) hold = FgV2Pacing.HoldDelay(_slot.SourceInterval, _procMarginS);
        double play = FgV2Pacing.PlaybackTime(nowS, hold, extrap);
        float t = FgV2Pacing.InterpT(play, prevCapS, currCapS, out bool showCurr);
        LastV2T = t;
        if (!haveTwo || prev == null)
            return curr;
        if (extrap)
        {
            try
            {
                double span = Math.Max(currCapS - prevCapS, 1e-6);
                float te = (float)Math.Max(0, (nowS - currCapS) / span);
                LastV2T = te;
                if (te <= 1e-3) return curr;
                return GenRateLimited(() => _pipe.V2Extrapolate(curr, te)) ?? curr;
            }
            catch (Exception ex)
            {
                _status = "CSR 2.0: " + ex.Message;
                return curr;
            }
        }
        if (showCurr)
            return curr;
        try
        {
            LastV2T = t;
            int n = ReqMul() == 0 ? _slot.EffectiveMultiplier(0) : ReqMul();
            if (n <= 0) n = _fgEffective;
            return GenRateLimited(() => n > 8 ? _pipe.V2GenerateFastAt(prev, curr, t) : _pipe.V2GenerateAt(prev, curr, t)) ?? curr;
        }
        catch (Exception ex)
        {
            _status = "CSR 2.0: " + ex.Message;
            return curr;
        }
    }

    void DrawAndPresent(Tex? tex)
    {
        if (_swap is null || _rtv is null) return;
        var ctx = _gpu.Context;
        ctx.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
        if (tex != null)
        {
            float da = (float)_bbW / _bbH, ta = (float)tex.Width / tex.Height;
            float sx = 1, sy = 1;
            if (da > ta) sx = da / ta; else sy = ta / da;
            var m = ctx.Map(_gpu.PresentCB, 0, MapMode.WriteDiscard);
            unsafe { var f = (float*)m.DataPointer; f[0] = sx; f[1] = sy; f[2] = 0.5f - 0.5f * sx; f[3] = 0.5f - 0.5f * sy; }
            ctx.Unmap(_gpu.PresentCB, 0);
            ctx.OMSetRenderTargets(_rtv);
            ctx.RSSetViewport(new Viewport(0, 0, _bbW, _bbH));
            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_gpu.PresentVS);
            ctx.PSSetShader(_gpu.PresentPS);
            ctx.PSSetConstantBuffer(1, _gpu.PresentCB);
            ctx.PSSetSampler(0, _gpu.LinearSampler);
            ctx.PSSetShaderResource(0, tex.Srv);
            _gpu.BeginPresentTiming();
            ctx.Draw(3, 0);
            _gpu.EndPresentTiming();
            ctx.PSSetShaderResource(0, null);
            if (_settings.FgV2 && _settings.FgV2Cursor)
            {
                _cursor ??= new CursorOverlay(_gpu);
                if (_cursor.Capture() && CursorOverlay.TryWindowOrigin(_source, out int ox, out int oy))
                    _cursor.Draw(ctx, ox, oy, Math.Max(1, _inW), Math.Max(1, _inH), tex.Width, tex.Height, _bbW, _bbH, sx, sy, 0.5f - 0.5f * sx, 0.5f - 0.5f * sy);
            }
            if (!ReferenceEquals(tex, _lastDrawn)) { _lastDrawn = tex; Interlocked.Increment(ref _slot.Shown); _newShown = true; }
        }
        _swap.Present(1, PresentFlags.None);
        if (_newShown)
        {
            _newShown = false;
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            double d = now - _lastShowT; _lastShowT = now;
            if (d > 0 && d < 250) { _ft[_ftI] = d; _ftI = (_ftI + 1) % _ft.Length; if (_ftN < _ft.Length) _ftN++; }
        }
    }

    void UpdateHud()
    {
        _hud.Visible = _settings.Hud;
        double t = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double dt = _lastHudT > 0 ? t - _lastHudT : 0.5;
        _lastHudT = t;
        long p = Interlocked.Read(ref _processed), sh = Interlocked.Read(ref _slot.Shown);
        double srcFps = (p - _lastProcessed) / dt, shownFps = (sh - _lastShown) / dt;
        _lastProcessed = p; _lastShown = sh;
        SourceFps = srcFps; OutputFps = shownFps;
        UpdateWarnings(srcFps);
        var b = _settings.Backend;
        string keys = Mode == OutputMode.Overlay
            ? "Overlay · Ctrl+Alt+F hide/show · Ctrl+Alt+Q stop · Ctrl+Alt+C compare"
            : "Esc / Ctrl+Alt+Q stop · Ctrl+Alt+F HUD · Ctrl+Alt+C compare";
        if (CompareOff)
        {
            var cmp = new List<string>
            {
                "FrameFX OFF (compare)",
                $"output {shownFps:0} fps / game {srcFps:0} fps",
                "Showing the captured frame as-is.",
                keys,
            };
            if (Warning.Length > 0) cmp.Insert(2, "⚠ " + Warning);
            if (Hint.Length > 0) cmp.Insert(Warning.Length > 0 ? 3 : 2, "Hint: " + Hint);
            if (Paused) cmp.Add("Paused (not a game)");
            if (AntiCheatNote.Length > 0) cmp.Add(AntiCheatNote);
            if (_status.Length > 0) cmp.Add(_status);
            _hud.Text = string.Join(Environment.NewLine, cmp);
            FgStatus = "";
            return;
        }
        var lines = new List<string>
        {
            "FrameFX ON",
            $"output {shownFps:0} fps / game {srcFps:0} fps" + (shownFps > srcFps + 1 ? $"  (+{shownFps - srcFps:0} generated/s)" : ""),
            $"Universal-FrameFX {MainForm.DisplayVersion} — {(_pipe.ActiveUpscaler.Length > 0 ? _pipe.ActiveUpscaler : BackendNames.Short(b))}",
            $"Input {_inW}×{_inH} → Output {(_lastDrawn?.Width ?? 0)}×{(_lastDrawn?.Height ?? 0)}",
            "Game FPS counters (e.g. Fortnite's) only count the game's own frames, not FrameFX's.",
            $"GPU frame {_gpu.LastGpuMs:0.00} ms" + (_pipe.VendorMs > 0 ? $" (vendor runtime {_pipe.VendorMs:0.00} ms)" : ""),
        };
        // 1.3.4 response time: FrameFX's added GPU work per captured frame, by stage.
        var stages = _gpu.LastStages;
        string st = "";   // Beta2: per-stage breakdown is diagnostics-only (not shown to users)
        string verdict = _settings.LatencyBudget ? (ResponseMs <= _budget.BudgetMs ? $" ✓ under {_budget.BudgetMs:0.0} ms" : $" ✗ over {_budget.BudgetMs:0.0} ms") : "";
        lines.Add($"Response (GPU, measured): {ResponseMs:0.00} ms{st}{verdict}");
        if (_settings.LatencyBudget)
            lines.Add($"Latency budget {_budget.BudgetMs:0.0} ms: level {_budget.Level}/{LatencyBudget.MaxLevel} (median {_budget.MedianMs:0.00} ms)");
        if (_pipe.FgV2Active)
        {
            double holdMs = (_settings.Competitive || _settings.FgV2Extrap) ? 0 : _holdDelayS * 1000.0;
            string v2 = _pipe.ActiveFrameGen.Length > 0 ? _pipe.ActiveFrameGen : _pipe.Csr20Label;
            lines.Add($"{v2} added latency: hold {holdMs:0.1} ms + processing {_procMs:0.1} ms = {holdMs + _procMs:0.1} ms");
        }
        if (_ftN >= 30)
        {
            var f = _ft.Take(_ftN).ToArray(); Array.Sort(f);
            double mean = f.Average(), sd = Math.Sqrt(f.Select(x => (x - mean) * (x - mean)).Average());
            double target = _fgEffective >= 2 ? _slot.SourceInterval * 1000 / _fgEffective : _slot.SourceInterval * 1000;
            lines.Add($"Frame pacing (shown frames): mean {mean:0.00} ms · σ {sd:0.00} ms · p99 {f[(int)(f.Length * 0.99) - 1]:0.00} ms (target {Math.Max(target, _slot.RefreshInterval * 1000):0.00} ms)");
        }
        if (_source != IntPtr.Zero && _capLatMs > 0) lines.Add($"Capture → submitted (CPU, from DWM compose): {_capLatMs:0.0} ms");
        lines.Add(_settings.Competitive ? "Preset: Competitive (lowest latency)" : _settings.Performance ? "Preset: Performance" : "Preset: Quality");
        if (AntiCheatNote.Length > 0) lines.Add(AntiCheatNote);
        if (_adapterNote.Length > 0) lines.Add(_adapterNote);
        string fgLine = "";
        int effN = _fgEffective;
        int reqN = _settings.FgMultiplier == 0 ? (effN > 0 ? effN : _slot.EffectiveMultiplier(0)) : ReqMul();
        double hz = 1 / Math.Max(_slot.RefreshInterval, 1e-4);
        if (!_settings.FrameGen) { }
        else if (_fgBudgetPaused)
            fgLine = $"FG: paused — the overlay would use {LastFgShare * 100:0}% of the GPU (budget {FgBudget * 100:0}%), retrying in 5 s";
        else if (_source != IntPtr.Zero && _guard.State == FgGuard.Phase.Measuring)
            fgLine = $"FG ×{reqN}: measuring base fps with generation off ({FgGuard.Window:0} s)…";
        else if (_fgBypassed)
            fgLine = reqN == 2 ? $"FG: paused — source is already ≥75% of the {hz:0} Hz refresh"
                               : $"FG ×{reqN}: paused — {srcFps:0} fps × 2 already exceeds the {hz:0} Hz refresh";
        else if (_source != IntPtr.Zero && !_guard.Allows)
            fgLine = $"FG ×{reqN}: paused by the base-fps safeguard ({_guard.Note}); retry in {Math.Max(0, _guard.RetryAt - t):0} s";
        else if (_pipe.LastSkippedUnchanged)
            fgLine = $"FG ×{reqN}: idle — picture unchanged, nothing generated";
        else
        {
            string why = effN > 0 && effN < reqN
                ? (_source != IntPtr.Zero && _guard.Cap < reqN && _guard.Cap <= effN ? $" (requested ×{reqN}; lowered by the base-fps safeguard)"
                                                                                     : $" (requested ×{reqN}; clamped: {srcFps:0} fps × {effN} ≤ {hz:0} Hz)")
                : "";
            fgLine = $"FG: {(_pipe.ActiveFrameGen.Length > 0 ? _pipe.ActiveFrameGen : "starting")}, effective ×{(effN > 0 ? effN : reqN)}{why} (adds latency)";
        }
        if (fgLine.Length > 0) lines.Add(fgLine);
        if (_settings.FrameGen && reqN >= 10 && hz < 360)
        {
            string hzWarn = $"CSR 2.0 ×{reqN} wants ≥360 Hz; this display is {hz:0} Hz.";
            lines.Add(hzWarn);
            Warning = Warning.Length == 0 ? hzWarn : Warning + " " + hzWarn;
        }
        if (Ssrt.Enabled(_settings.Ssrt) && !CompareOff)
        {
            if (SsrtStatus == "unavailable") lines.Add("Ray-traced lighting unavailable");
            else
            {
                string label = SsrtStatus is "GTX 1050 Ti" or "GTX 980 Ti"
                    ? SsrtStatus
                    : Ssrt.PresetLabel(Ssrt.Resolve(_settings.SsrtPreset, _outW, _outH, _gpu.AdapterName));
                lines.Add("Ray-traced lighting: " + label);
            }
        }
        else if (_settings.Ssgi) lines.Add("SSGI (experimental): " + SsgiStatus);
        if (_settings.FrameGen && _source != IntPtr.Zero && _guard.Note.Length > 0 && _guard.Allows) lines.Add("FG safeguard: " + _guard.Note);
        if (_settings.FrameGen && _source != IntPtr.Zero && _guard.Baseline > 0) lines.Add($"Base fps: baseline {_guard.Baseline:0.0} · now {_guard.LastFps:0.0} (max drop {FgGuard.MaxDrop * 100:0}%)");
        FgStatus = fgLine;
        if (_pipe.VendorNote is { } vn) lines.Add(vn);
        lines.Add(keys);
        if (Warning.Length > 0) lines.Insert(1, "⚠ " + Warning);
        if (Hint.Length > 0) lines.Insert(Warning.Length > 0 ? 2 : 1, "Hint: " + Hint);
        if (Paused) lines.Add("Paused (not a game)");
        if (_status.Length > 0) lines.Add(_status);
        _hud.Text = string.Join(Environment.NewLine, lines);
    }

    public const string ExclusiveFullscreenWarning = "The game is in exclusive fullscreen, so FrameFX can't draw over it. Switch the game to Windowed Fullscreen (borderless) to see FrameFX.";

    /// <summary>Beta2: exclusive-fullscreen warning + fps-lock hints. Called from the 2 Hz HUD timer (cheap: two shell/kernel queries).</summary>
    void UpdateWarnings(double srcFps)
    {
        string w = "";
        if (_source != IntPtr.Zero && !Paused && Native.D3DExclusiveFullscreenActive())
        {
            var fg = Native.GetForegroundWindow();
            if (fg == _source || Native.GetAncestor(fg, 2) == _source) w = ExclusiveFullscreenWarning;
        }
        Warning = w;
        Hint = "";
        if (_source == IntPtr.Zero || Paused) { _srcHist.Clear(); return; }
        _srcHist.Enqueue(srcFps);
        while (_srcHist.Count > 24) _srcHist.Dequeue();   // ~12 s at 2 Hz
        if (_srcHist.Count < 20) return;
        double mean = _srcHist.Average(), sd = Math.Sqrt(_srcHist.Select(x => (x - mean) * (x - mean)).Average());
        Hint = FpsLock.Hint(mean, sd, Native.OnBattery(), _gpu.LastGpuMs);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _hudTimer.Stop();
        _follow?.Stop();
        Native.UnregisterHotKey(Handle, HkToggle); Native.UnregisterHotKey(Handle, HkStop); Native.UnregisterHotKey(Handle, HkCompare);
        _run = false;
        _slot.Wake.Set();
        _capture?.Dispose();
        _thread?.Join(2000);
        StatsLog.Flush();
        lock (_frameLock) { _pendingFrame?.Dispose(); _pendingFrame = null; }
        lock (Gpu.Lock)
        {
            _gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            _rtv?.Dispose(); _swap?.Dispose();
            _rtv = null; _swap = null;
            _pipe.Reset();
        }
        base.OnFormClosing(e);
    }
}
