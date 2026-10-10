using System.Drawing;
using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace UniversalFrameFX;

public sealed class OutputForm : Form
{
    readonly Gpu _gpu;
    readonly FramePipeline _pipe;
    readonly SessionSettings _settings;
    readonly IntPtr _source;
    readonly OutputRes _res;
    readonly int _outW, _outH;
    readonly Label _hud;
    readonly System.Windows.Forms.Timer _hudTimer;
    readonly System.Windows.Forms.Timer? _follow;
    IFrameEngine? _engine;
    WindowCapture? _capture;
    Native.RECT _lastRect;
    bool _hotkeys, _transparent;
    long _resumeMark = -1;
    string _status = "";
    readonly Queue<double> _srcHist = new();
    const int HkToggle = 0xF1, HkStop = 0xF2, HkCompare = 0xF3;

    public const string ExclusiveFullscreenWarning = "The game is in exclusive fullscreen, so FrameFX can't draw over it. Switch the game to Windowed Fullscreen (borderless) to see FrameFX.";

    public OutputMode Mode { get; }
    public bool OverlayShown { get; private set; } = true;
    public string? Error { get; private set; }
    public string Warning { get; private set; } = "";
    public string Hint { get; private set; } = "";
    public bool CompareOff { get; private set; }
    public string CompareStatus => CompareOff ? "FrameFX OFF (compare)" : "FrameFX ON";
    public string FgStatus { get; private set; } = "";
    public double SourceFps => _engine?.SourceFps ?? 0;
    public double OutputFps => _engine?.OutputFps ?? 0;
    public string SwapInfo => _engine?.SwapInfo ?? "";
    public string MotionSource => _pipe.MotionSource;
    public string HudText => _hud.Text;
    public IntPtr Source => _source;
    public long Processed => _engine?.Processed ?? 0;
    public bool Paused { get; private set; }
    public bool UserStop;

    bool OverlayFollowsWindow => Mode == OutputMode.Overlay && (_res == OutputRes.Auto || _res == OutputRes.Source);

    public OutputForm(Gpu gpu, FramePipeline pipe, SessionSettings settings, IntPtr source, Screen screen,
                      OutputMode mode, int testInW, int testInH, int outW, int outH, OutputRes res = OutputRes.Auto)
    {
        _gpu = gpu; _pipe = pipe; _settings = settings; _source = source; _res = res;
        _outW = outW; _outH = outH;
        if (mode == OutputMode.Overlay && source == IntPtr.Zero) mode = OutputMode.Window;
        Mode = mode;
        if (mode == OutputMode.Overlay) _resumeMark = 0;
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
        Resize += (_, _) => { try { lock (Gpu.Lock) _engine?.ResizeOutput(ClientSize.Width, ClientSize.Height); } catch { } };
    }

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
        if (Mode == OutputMode.Overlay) { Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA); _transparent = true; }
        if (Environment.GetEnvironmentVariable("UFX_ALLOW_CAPTURE") != "1")
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
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

    public void ToggleOverlay()
    {
        if (Mode != OutputMode.Overlay) { _settings.Hud = !_settings.Hud; return; }
        OverlayShown = !OverlayShown;
        if (!OverlayShown) Native.ShowWindow(Handle, Native.SW_HIDE);
        else if (!Paused) Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    public void ToggleCompare() => SetCompareOff(!CompareOff);

    public void SetCompareOff(bool off)
    {
        if (off == CompareOff) return;
        CompareOff = off;
        _settings.CompareOff = off;
        lock (Gpu.Lock)
        {
            _pipe.Reset();
            _engine?.Reset();
        }
    }

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
            lock (Gpu.Lock) { _pipe.Reset(); _engine?.Reset(); }
            _resumeMark = Processed;
            Paused = false;
        }
    }

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
        bool frameReady = !Paused && Processed > _resumeMark;
        if (hidden || !OverlayShown || !frameReady) { if (Visible) Native.ShowWindow(Handle, Native.SW_HIDE); return; }
        var r = OverlayRect();
        bool moved = r.Left != _lastRect.Left || r.Top != _lastRect.Top || r.Right != _lastRect.Right || r.Bottom != _lastRect.Bottom;
        if (moved || !Native.IsWindowVisible(Handle))
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top),
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
            _lastRect = r;
        }
        if (_transparent) { Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA); _transparent = false; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _engine = FrameEngine.Create(_gpu, _pipe, _settings);
            lock (Gpu.Lock) _engine.AttachOutput(Handle, ClientSize.Width, ClientSize.Height, Mode == OutputMode.Overlay);
            if (_source != IntPtr.Zero)
            {
                _capture = new WindowCapture(_gpu.Device, _source, OnCaptured, captureCursor: !(_settings.FgV2 && _settings.FgV2Cursor));
                _capture.Closed += () => BeginInvoke(new Action(() => { _status = "Source window closed."; Close(); }));
            }
            _follow?.Start();
            _hudTimer.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            MessageBox.Show(this, ex.Message, "Universal-FrameFX", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void OnCaptured(Windows.Graphics.Capture.Direct3D11CaptureFrame frame)
    {
        using (frame)
        {
            if (Paused || _engine is null) return;
            using var tex = WindowCapture.TextureOf(frame);
            if (tex is null) return;
            lock (Gpu.Lock) _engine.SubmitFrame(tex, frame.ContentSize.Width, frame.ContentSize.Height);
        }
    }

    void UpdateHud()
    {
        UpdateWarnings(SourceFps);
        string keys = Mode == OutputMode.Overlay
            ? "Overlay · Ctrl+Alt+F hide/show · Ctrl+Alt+Q stop · Ctrl+Alt+C compare"
            : "Esc / Ctrl+Alt+Q stop · Ctrl+Alt+F HUD · Ctrl+Alt+C compare";
        if (CompareOff)
        {
            var cmp = new List<string>
            {
                "FrameFX OFF (compare)",
                $"output {OutputFps:0} fps / game {SourceFps:0} fps",
                "Showing the captured frame as-is.",
                keys,
            };
            if (Warning.Length > 0) cmp.Insert(2, "⚠ " + Warning);
            if (Hint.Length > 0) cmp.Insert(Warning.Length > 0 ? 3 : 2, "Hint: " + Hint);
            if (Paused) cmp.Add("Paused (not a game)");
            if (_status.Length > 0) cmp.Add(_status);
            _hud.Text = string.Join(Environment.NewLine, cmp);
            FgStatus = "";
            _hud.Visible = _settings.Hud;
            return;
        }
        var lines = new List<string>
        {
            "FrameFX ON",
            $"output {OutputFps:0} fps / game {SourceFps:0} fps" + (OutputFps > SourceFps + 1 ? $"  (+{OutputFps - SourceFps:0} generated/s)" : ""),
            $"Universal-FrameFX {MainForm.DisplayVersion}",
        };
        if (_engine?.Status is { Length: > 0 } st) lines.Add(st);
        if (Ssrt.Enabled(_settings.Ssrt))
        {
            string label = Ssrt.PresetLabel(Ssrt.Resolve(_settings.SsrtPreset, _outW, _outH, _gpu.AdapterName));
            lines.Add("Ray-traced lighting: " + label);
        }
        int reqN = _settings.FgMultiplier;
        if (_settings.FrameGen && _settings.FgV2 && reqN >= 10)
        {
            int hz = Native.RefreshHz(Handle);
            if (hz > 0 && hz < 360)
            {
                string hzWarn = $"CSR 2.0 ×{reqN} wants ≥360 Hz; this display is {hz:0} Hz.";
                lines.Add(hzWarn);
                Warning = Warning.Length == 0 ? hzWarn : Warning + " " + hzWarn;
            }
        }
        FgStatus = _engine?.Status ?? "";
        lines.Add(keys);
        if (Warning.Length > 0) lines.Insert(1, "⚠ " + Warning);
        if (Hint.Length > 0) lines.Insert(Warning.Length > 0 ? 2 : 1, "Hint: " + Hint);
        if (Paused) lines.Add("Paused (not a game)");
        if (_status.Length > 0) lines.Add(_status);
        _hud.Text = string.Join(Environment.NewLine, lines);
        _hud.Visible = _settings.Hud;
    }

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
        while (_srcHist.Count > 24) _srcHist.Dequeue();
        if (_srcHist.Count < 20) return;
        double mean = _srcHist.Average(), sd = Math.Sqrt(_srcHist.Select(x => (x - mean) * (x - mean)).Average());
        Hint = FpsLock.Hint(mean, sd, Native.OnBattery(), _engine?.GpuMs ?? 0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _hudTimer.Stop();
        _follow?.Stop();
        Native.UnregisterHotKey(Handle, HkToggle); Native.UnregisterHotKey(Handle, HkStop); Native.UnregisterHotKey(Handle, HkCompare);
        _capture?.Dispose();
        lock (Gpu.Lock) { _engine?.Dispose(); _engine = null; _pipe.Reset(); }
        base.OnFormClosing(e);
    }
}
