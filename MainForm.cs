using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalFrameFX;

public sealed class MainForm : Form
{
    public static string Version => AppVersion.Version;
    public static string DisplayVersion => AppVersion.Display;
    const string SiteUrl = "https://chopstickshq.com/universal-framefx/";
    const string Coffee = "https://buymeacoffee.com/chopstickshq";

    readonly Gpu? _gpu;
    readonly FramePipeline? _pipe;
    readonly SessionSettings _settings = new();
    readonly DarkCombo _source = new() { Width = 420 };
    readonly DarkCombo _backend = new() { Width = 300 };
    readonly DarkCombo _quality = new() { Width = 300 };
    readonly FlatSlider _sharp = new() { Minimum = 0, Maximum = 200, Value = 25, Width = 300 };
    readonly Label _sharpLbl = new() { AutoSize = true, ForeColor = Theme.TextMuted };
    readonly ToggleSwitch _fg = new() { Text = "Frame generation (adds latency)" };
    readonly DarkCombo _fgKind = new() { Width = 300 };
    readonly DarkCombo _fgMul = new() { Width = 300 };
    readonly ToggleSwitch _ssgi = new() { Text = "SSGI (experimental)" };
    readonly DarkCombo _ssgiPreset = new() { Width = 300 };
    readonly ToggleSwitch _ssgiSteady = new() { Text = "Steadier SSGI lighting" };
    readonly ToggleSwitch _ssrt = new() { Text = "Ray-traced lighting (experimental)" };
    readonly ToggleSwitch _fgV2 = new() { Text = "CSR 2.0 frame generation (experimental)" };
    readonly ToggleSwitch _fgV2Vsync = new() { Text = "Smooth pacing at any frame rate (CSR 2.0)" };
    readonly ToggleSwitch _fgV2Extrap = new() { Text = "Lowest-latency mode (CSR 2.0)" };
    readonly ToggleSwitch _fgV2LowRes = new() { Text = "Lighter frame generation for weaker GPUs (CSR 2.0)" };
    readonly ToggleSwitch _fgV2Cursor = new() { Text = "Show the cursor on generated frames (CSR 2.0)" };
    readonly DarkCombo _fpsCap = new() { Width = 300 };
    readonly ToggleSwitch _fixedPace = new() { Text = "Fixed frame pacing" };
    static readonly int[] FpsCapVals = { 0, 60, 90, 120, 144, 165, 240 };
    readonly DarkCombo _ssrtPreset = new() { Width = 300 };
    readonly ToggleSwitch _lowLat = new() { Text = $"Latency budget: keep FrameFX under {LatencyBudget.DefaultMs:0.0} ms per frame" };
    readonly DarkCombo _res = new() { Width = 300 };
    readonly DarkCombo _preset = new() { Width = 300 };
    readonly Label _upNote = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 4, 0, 4) };
    readonly PillButton _coffee = new() { Text = "☕ Buy me a coffee", AutoSize = true, Kind = PillKind.Ghost };
    static readonly Backend[] BackendOrder = { Backend.Temporal, Backend.Spatial, Backend.Fsr1, Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS, Backend.Bilinear };
    public string? AutoBackend, AutoRes, AutoFgKind, AutoPreset, AutoFgMul;
    public bool CliCompareOff { get => _compareOff; set { if (value) _compareOff = true; } }
    public bool CliCsr2, CliVsyncFg, CliFixedPacing;
    public int? CliFpsCap;
    readonly ToggleSwitch _hud = new() { Text = "Performance HUD", Checked = true };
    readonly DarkCombo _mode = new() { Width = 300 };
    readonly DarkCombo _motion = new() { Width = 300 };
    readonly DarkCombo _gpuChoice = new() { Width = 300 };
    readonly Label _gpuNote = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted };
    public static GpuChoice? CliGpu;
    readonly Label _motionLbl = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Green };
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };
    string _motionAvail = "";
    readonly PillButton _start = new() { Text = "Start", AutoSize = true };
    readonly PillButton _apply = new() { Text = "Apply", AutoSize = true, Kind = PillKind.Primary };
    readonly Label _pending = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Amber, Margin = new Padding(2, 8, 0, 0) };

    readonly GameDetector _detector = new();
    readonly System.Windows.Forms.Timer _gameTimer = new() { Interval = 500 };
    readonly PillButton _gamesOnly = new() { Text = "Apply to games only", AutoSize = true, Kind = PillKind.Ghost };
    readonly Label _gameStatus = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 2, 0, 0), Visible = false };
    readonly TextBox _gamesAlwaysBox = NewGameListBox();
    readonly TextBox _gamesNeverBox = NewGameListBox();
    readonly Label _gamesListNote = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 6, 0, 0) };
    bool _gamesArmed;
    GameProfile _baseline = new();
    bool _profilesReady;
    string _profileBanner = "";
    bool _compareOff;
    readonly TableLayoutPanel _profileRows = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0), BackColor = Theme.Card };
    readonly Label _profileEmpty = new() { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 4, 0, 2), Text = "No saved profiles yet. They appear when you change settings while a game is running." };
    readonly PillButton _profileResetAll = new() { Text = "Reset all", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 4, 0, 0) };
    readonly List<Label> _profileLabels = new();
    GameDetector.GameVerdict? _lastExternal;
    GameDetector.GameVerdict? _lastVerdict;
    IntPtr _gameHwnd;
    string _gameLabel = "";
    IntPtr _pendingHwnd;
    string _pendingLabel = "";
    static readonly string? GameLogPath = Environment.GetEnvironmentVariable("UFX_GAMELOG") is { Length: > 0 } p ? p : null;
    static readonly string? FgScript = Environment.GetEnvironmentVariable("UFX_FG_SCRIPT") is { Length: > 0 } s ? s : null;
    static readonly List<(string title, int sec)> ScriptSteps = ParseScript(FgScript);
    int _scriptStep = -1;
    string _lastLoggedVerdict = "", _lastLoggedState = "";
    int _hbTicks;
    bool GamesOnlyOn => _ui.GamesOnly || FgScript != null;

    sealed record Applied(IntPtr Source, int Backend, int Quality, int Sharp, bool Fg, int Mode, int Motion, int FgKind, int Res, int Preset, int FgMul);
    Applied? _applied;
    bool _restartPending;
    bool _suppressDirty, _suppressMulSave, _suppressProfile;
    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(2, 6, 0, 0) };
    readonly FlowLayoutPanel _updBar = new() { AutoSize = true, WrapContents = true, Visible = false, Margin = new Padding(0, 4, 0, 2), BackColor = Theme.Bg };
    readonly Label _updText = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 7, 8, 0) };
    readonly PillButton _updInstall = new() { Text = "Install", AutoSize = true, Kind = PillKind.Primary, Margin = new Padding(0, 0, 6, 0) };
    readonly PillButton _updLater = new() { Text = "Later", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0) };
    readonly ToggleSwitch _autoCheck = new() { Text = "Automatically check for updates" };
    readonly ToggleSwitch _autoInstall = new() { Text = "Install updates automatically (only while not capturing)" };
    readonly PillButton _checkNow = new() { Text = "Check now", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 4, 8, 0) };
    readonly Label _updStatus = new() { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(2, 10, 0, 0) };
    readonly System.Windows.Forms.Timer _updTimer = new() { Interval = 60_000 };
    UpdateInfo? _updAvail;
    DateTime _lastCheck = DateTime.MinValue;
    bool _updBusy;
    public string? UpdateMarker, UpdatedFrom, UpdateFailed;
    OutputForm? _out;

    static readonly (string name, double ratio)[] Qualities =
        { ("Quality (1.5x)", 1.5), ("Balanced (1.7x)", 1.7), ("Performance (2.0x)", 2.0), ("Ultra Performance (3.0x)", 3.0) };

    readonly TableLayoutPanel _stack = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(18, 2, 18, 16), BackColor = Theme.Bg };
    readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly List<Label> _wrap = new();
    readonly Dictionary<string, (SectionHeader head, Control body)> _sections = new();
    readonly Label _notice = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Amber, Margin = new Padding(2, 4, 0, 0), Visible = false };
    readonly Label _active = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 2, 0, 0) };
    readonly ToggleSwitch _cpuPower = new() { Text = "Use the high performance power mode while gaming (may ask for admin)" };
    readonly ToggleSwitch _cpuMin = new() { Text = "Keep the processor at full speed while gaming (may ask for admin)" };
    readonly ToggleSwitch _cpuPark = new() { Text = "Keep all cores awake (may ask for admin)" };
    readonly DarkCombo _cpuBoost = new() { Width = 300 };
    readonly ToggleSwitch _cpuPrio = new() { Text = "Run the game at high priority" };
    readonly ToggleSwitch _cpuFast = new() { Text = "Prefer your fastest cores" };
    readonly ToggleSwitch _cpuSmt = new() { Text = "Use one thread per core (can help some games on AMD and Intel CPUs)" };
    readonly ToggleSwitch _cpuTimer = new() { Text = "Smoother timing (1 ms)" };
    readonly ToggleSwitch _cpuAll = new() { Text = "Spread the game across all cores" };
    readonly ToggleSwitch _cpuSelf = new() { Text = "Keep FrameFX out of the game's way" };
    readonly ToggleSwitch _cpuThrottle = new() { Text = "Don't let Windows throttle the game" };
    readonly TextBox _cpuBg = NewGameListBox();
    readonly Label _cpuStatus = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 6, 0, 2) };
    readonly Label _cpuBenchLbl = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Green, Margin = new Padding(0, 4, 0, 2) };
    readonly PillButton _cpuBench = new() { Text = "Run quick benchmark", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 0, 8, 0) };
    readonly PillButton _cpuDefaults = new() { Text = "Restore defaults", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0) };
    readonly System.Windows.Forms.Timer _cpuPoll = new() { Interval = 500 };
    bool _suppressCpu;
    volatile bool _cpuClosing;
    int _cpuJob;
    bool _cpuAgain;
    string _cpuKey = "";
    long _cpuPollAt;
    string _cpuShownExe = "";
    double? _benchSingle0, _benchMulti0;
    UiState _ui = UiState.Load();

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Universal-FrameFX {DisplayVersion}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(460, 480);
        ClientSize = new Size(560, 760);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body();

        try { using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (s != null) Icon = new Icon(s); } catch { }

        Diag.Mark("form: ctor start");
        try
        {
            Gpu.Choice = CliGpu ?? (GpuChoice)Math.Clamp(_ui.GpuChoice, 0, 2);
            _gpu = new Gpu();
            _pipe = new FramePipeline(_gpu);
            Diag.Mark("form: gpu + pipeline ready");
        }
        catch (Exception ex)
        {
            Diag.WriteCrash("gpu init", ex);
            _status.Text = "GPU pipeline failed: " + ex.Message;
            _start.Enabled = false;
        }

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8), BackColor = Theme.Bg };
        var title = new Label { Text = "Universal-FrameFX", Font = new Font(Theme.DisplayFamily, 18f, Theme.DisplayFamily == "Segoe UI Semibold" ? FontStyle.Regular : FontStyle.Bold), AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0) };
        var sub = new Label { Text = $"{DisplayVersion} · GPU: {_gpu?.AdapterName ?? "unavailable"}", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(2, 2, 0, 4) };
        _updBar.Controls.Add(_updText); _updBar.Controls.Add(_updInstall); _updBar.Controls.Add(_updLater);
        header.Controls.Add(title); header.Controls.Add(sub); header.Controls.Add(_updBar);

        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) => Reflow();

        var src = Section("Source", true);
        var srcRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0) };
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var refresh = new PillButton { Text = "Refresh", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(8, 0, 0, 0) };
        refresh.Click += (_, _) => RefreshSources();
        _source.Anchor = AnchorStyles.Left | AnchorStyles.Right; _source.Width = 200;
        srcRow.Controls.Add(_source, 0, 0); srcRow.Controls.Add(refresh, 1, 0);
        Add(src, srcRow);
        Add(src, Note("Windows are captured with Windows.Graphics.Capture at their native size, then upscaled to the output resolution you pick below (up to 4×; vendor upscalers up to 3×)."));

        var up = Section("Upscaling", true);
        foreach (var b in BackendOrder) _backend.Items.Add(BackendNames.Long(b));
        _backend.DisabledReason = i => VendorSupport.Get(BackendOrder[i]) is { Ok: false } v ? (VendorSupport.Probed ? v.Why : "Checking this GPU…") : null;
        _backend.SelectedIndex = 0;
        _backend.SelectedIndexChanged += (_, _) => { UserProfileTouch(); UpdateUpNote(); UpdateDirty(); };
        Add(up, Note("Upscaler:"));
        Add(up, Stretch(_backend));
        _upNote.MaximumSize = Size.Empty; _wrap.Add(_upNote);
        Add(up, _upNote);
        _preset.Items.Add("Performance: about 1 ms GPU per frame (default)");
        _preset.Items.Add("Quality: best image quality");
        _preset.Items.Add("Competitive: 1080p output, 8× frame generation, lowest latency");
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) => { UserProfileTouch(); UpdateUpNote(); UpdateDirty(); };
        Add(up, Note("Preset:"));
        Add(up, Stretch(_preset));
        foreach (var q in Qualities) _quality.Items.Add(q.name);
        _quality.SelectedIndex = 2;
        Add(up, Note("Quality mode (test scene render scale):"));
        Add(up, Stretch(_quality));
        Add(up, Note("Sharpness (0 = sharpest):"));
        var sharpRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0) };
        sharpRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sharpRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sharp.Anchor = AnchorStyles.Left | AnchorStyles.Right; _sharp.Width = 200;
        _sharpLbl.Anchor = AnchorStyles.Left;
        sharpRow.Controls.Add(_sharp, 0, 0); sharpRow.Controls.Add(_sharpLbl, 1, 0);
        Add(up, sharpRow);
        _sharp.Scroll += (_, _) => { UpdateSharp(); UpdateDirty(); };
        UpdateSharp();

        var fgs = Section("Frame generation", true);
        Add(fgs, _fg);
        _fgKind.Items.Add("CSR 1.3 frame generation (classic)");
        _fgKind.Items.Add("AMD FSR 3 frame generation");
        _fgKind.DisabledReason = i => i == (int)FgKind.Fsr3 && !VendorSupport.FsrFrameGen.Ok ? (VendorSupport.Probed ? VendorSupport.FsrFrameGen.Why : "Checking this GPU…") : null;
        _fgKind.SelectedIndex = 0;
        Add(fgs, Stretch(_fgKind));
        _fgMul.Items.Add("Auto");
        _fgMul.Items.Add("2× (1 generated frame per real frame)");
        _fgMul.Items.Add("3× (2 generated frames)");
        _fgMul.Items.Add("4× (3 generated frames, default)");
        if (FgMul.Advanced || FgMul.Clamp(_ui.FgMultiplier) == 8)
            _fgMul.Items.Add("8× (7 generated frames, advanced)");
        _fgMul.SelectedIndex = FgMul.MenuIndex(_ui.FgMultiplier);
        Add(fgs, Stretch(_fgMul));
        _lowLat.Checked = _ui.LatencyBudget;
        Add(fgs, _lowLat);
        Add(fgs, Note($"Latency budget: keeps FrameFX's added response time under {LatencyBudget.DefaultMs:0.0} ms (shown as \"Response\" in the HUD) by automatically trading a little quality for speed, and restores quality when there is headroom. Frame generation itself still adds up to about one captured frame of latency."));
        Add(fgs, Note("CSR 1.3 frame generation (classic): FrameFX's own classic frame generation. AMD FSR 3 frame generation: AMD's frame generation. Either pauses automatically while the source already runs at ≥75% of your refresh rate."));
        Add(fgs, Note("Multiplier: 2×, 3×, 4× (and 8× advanced) multiply the frames you see. Output is capped at your refresh rate and the multiplier is lowered automatically when needed (the HUD shows the effective multiplier). Frame generation never raises the game's real fps, adds about one captured frame of latency, and steps down automatically if the game's fps drops more than 5% while it runs."));
        Add(fgs, Note("Smoother motion when the game runs below half your display's refresh rate; adds about one frame of latency."));

        var outs = Section("Output", true);
        foreach (var m in Enum.GetValues<OutputMode>()) _mode.Items.Add(OutputModeNames.Long(m));
        _mode.SelectedIndex = 0;
        Add(outs, Stretch(_mode));
        foreach (var r in Enum.GetValues<OutputRes>()) _res.Items.Add(OutputResNames.Long(r));
        _res.SelectedIndex = 0;
        Add(outs, Note("Output resolution:"));
        Add(outs, Stretch(_res));
        Add(outs, Note("1440p / 4K with the overlay: the overlay covers the source's monitor and shows the upscaled image letterboxed; it stays click-through, so clicks reach whatever is underneath at its real position. 4K on a smaller monitor is rendered at 4K and scaled to fit."));
        Add(outs, Note("Overlay: a click-through, always-on-top window over the source that follows it; the source keeps mouse and keyboard focus. Ctrl+Alt+F hides/shows it, Ctrl+Alt+Q stops, Ctrl+Alt+C compares the picture with FrameFX off. Separate/fullscreen output: Esc stops, Ctrl+Alt+C compares."));
        Add(outs, _hud);

        var games = Section("Games", false);
        Add(games, Note("When \"Apply to games only\" is on, FrameFX captures the foreground game with the overlay and pauses on the desktop, browsers and normal apps: the overlay is hidden and no GPU work is done. A game is a fullscreen or borderless-fullscreen D3D/Vulkan/OpenGL window, or a game started from Steam, Epic, Xbox/Game Pass, GOG, Battle.net, Riot, EA or Ubisoft. The lists below override that."));
        Add(games, Note("Always treat as game (one .exe per line):"));
        _gamesAlwaysBox.Text = string.Join(Environment.NewLine, _ui.GamesAlways ?? new List<string>());
        Add(games, Stretch(_gamesAlwaysBox));
        Add(games, Note("Never treat as game (one .exe per line):"));
        _gamesNeverBox.Text = string.Join(Environment.NewLine, _ui.GamesNever ?? new List<string>());
        Add(games, Stretch(_gamesNeverBox));
        var gamesRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), BackColor = Theme.Card };
        var addGame = new PillButton { Text = "Add current game", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 0, 8, 0) };
        var neverGame = new PillButton { Text = "Never treat current app as game", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 0, 8, 0) };
        addGame.Click += (_, _) => AddToGameList(always: true);
        neverGame.Click += (_, _) => AddToGameList(always: false);
        gamesRow.Controls.Add(addGame); gamesRow.Controls.Add(neverGame); gamesRow.Controls.Add(_gamesListNote);
        Add(games, gamesRow);
        PushGameDetector();
        _gamesAlwaysBox.TextChanged += (_, _) => SyncGameLists(false);
        _gamesNeverBox.TextChanged += (_, _) => SyncGameLists(false);
        _gamesAlwaysBox.Leave += (_, _) => SyncGameLists(true);
        _gamesNeverBox.Leave += (_, _) => SyncGameLists(true);
        Add(games, Note("Game profiles: FrameFX remembers preset, frame generation, SSGI, steadier lighting, ray-traced lighting, upscaler, output resolution and CPU options for each game, and applies them when that game is detected."));
        _wrap.Add(_profileEmpty);
        _profileRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _profileRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Add(games, _profileEmpty);
        Add(games, _profileRows);
        _profileResetAll.Click += (_, _) => { GameProfiles.ResetAll(_ui.Profiles); SaveUi(); RebuildProfileList(); };
        Add(games, _profileResetAll);
        RebuildProfileList();

        var adv = Section("Advanced", false);
        _ssgi.Checked = _ui.Ssgi;
        Add(adv, _ssgi);
        foreach (var n in Ssgi.PresetNames) _ssgiPreset.Items.Add("SSGI preset: " + n);
        _ssgiPreset.SelectedIndex = Math.Clamp(_ui.SsgiPreset, 0, Ssgi.PresetNames.Length - 1);
        Add(adv, Stretch(_ssgiPreset));
        _ssgiSteady.Checked = _ui.SsgiTemporal;
        Add(adv, _ssgiSteady);
        Add(adv, Note("Steadier lighting while moving."));
        Add(adv, Note("SSGI (experimental): adds coloured bounce light from bright areas and contact shadows to the captured frame, before upscaling and frame generation. It only sees the 2D image (no depth), so it is an approximation: it can light or darken things a real renderer would not. Estimated GPU cost ~0.9–1.2 ms (GTX 1050 Ti at 1080p / GTX 980 Ti at 1440p presets). Auto picks the 980 Ti settings for 1440p+ on faster GPUs, otherwise the 1050 Ti settings."));
        _ssrt.Checked = _ui.Ssrt;
        Add(adv, _ssrt);
        foreach (var n in Ssrt.PresetNames) _ssrtPreset.Items.Add(n);
        _ssrtPreset.SelectedIndex = Math.Clamp(_ui.SsrtPreset, 0, Ssrt.PresetNames.Length - 1);
        _ssrtPreset.Visible = _ssrt.Checked;
        Add(adv, Stretch(_ssrtPreset));
        Add(adv, Note("Ray-traced lighting (experimental) is off unless you turn it on. It adds extra light, reflections and contact shadows, including on cards without hardware ray tracing such as the GTX 1050 Ti and GTX 980 Ti. While it is on, it is used instead of SSGI. Auto picks lighter settings at 1080p and on a GTX 1050 Ti or a slower card."));
        _fgV2.Checked = _ui.FgV2 || Environment.GetEnvironmentVariable("UFX_FG_V2") == "1";
        Add(adv, _fgV2);
        _settings.FgV2 = _fgV2.Checked;
        _fgV2Vsync.Checked = _ui.FgV2Vsync || Environment.GetEnvironmentVariable("UFX_FG_V2_VSYNC") == "1";
        Add(adv, _fgV2Vsync);
        _settings.FgV2Vsync = _fgV2Vsync.Checked;
        _fgV2Extrap.Checked = _ui.FgV2Extrap;
        Add(adv, _fgV2Extrap);
        _settings.FgV2Extrap = _fgV2Extrap.Checked;
        _fgV2LowRes.Checked = _ui.FgV2LowResGen || Environment.GetEnvironmentVariable("UFX_FG_V2_LOWRES") == "1";
        Add(adv, _fgV2LowRes);
        _settings.FgV2LowResGen = _fgV2LowRes.Checked;
        _fgV2Cursor.Checked = _ui.FgV2Cursor;
        Add(adv, _fgV2Cursor);
        _settings.FgV2Cursor = _fgV2Cursor.Checked;
        _fpsCap.Items.Add("Output fps cap: Off");
        _fpsCap.Items.Add("Output fps cap: 60");
        _fpsCap.Items.Add("Output fps cap: 90");
        _fpsCap.Items.Add("Output fps cap: 120");
        _fpsCap.Items.Add("Output fps cap: 144");
        _fpsCap.Items.Add("Output fps cap: 165");
        _fpsCap.Items.Add("Output fps cap: 240");
        int capI = Array.IndexOf(FpsCapVals, _ui.FpsCap);
        _fpsCap.SelectedIndex = capI < 0 ? 0 : capI;
        _settings.FpsCap = FpsCapVals[Math.Max(0, _fpsCap.SelectedIndex)];
        Add(adv, Stretch(_fpsCap));
        _fixedPace.Checked = _ui.FixedPacing || Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
        Add(adv, _fixedPace);
        _settings.FixedPacing = _fixedPace.Checked;
        foreach (var m in Enum.GetValues<MotionPreference>()) _motion.Items.Add(MotionEngines.PrefName(m));
        _motion.SelectedIndex = 0;
        _motion.SelectedIndexChanged += (_, _) => UpdateDirty();
        Add(adv, Note("Motion source:"));
        Add(adv, Stretch(_motion));
        _motionLbl.MaximumSize = Size.Empty; _wrap.Add(_motionLbl);
        Add(adv, _motionLbl);
        Add(adv, Note("The motion source setting applies to the Quality preset. The built-in test scene shows the full-quality result."));
        string hiName = Gpu.AdapterNameFor(GpuChoice.Dedicated) ?? "?", loName = Gpu.AdapterNameFor(GpuChoice.Integrated) ?? "?";
        _gpuChoice.Items.Add($"Auto (dedicated GPU when there is one): {hiName}");
        _gpuChoice.Items.Add($"Integrated (power saving): {loName}");
        _gpuChoice.Items.Add($"Dedicated (high performance): {hiName}");
        _gpuChoice.SelectedIndex = Math.Clamp(_ui.GpuChoice, 0, 2);
        var gpuRestart = new PillButton { Text = "Restart FrameFX now", AutoSize = true, Kind = PillKind.Ghost, Visible = false, Margin = new Padding(0, 4, 0, 0) };
        void GpuNote()
        {
            bool pending = _gpu != null && _gpuChoice.SelectedIndex != (int)_gpu.ActiveChoice && CliGpu == null;
            _gpuNote.Text = $"Running on: {_gpu?.AdapterName ?? "?"} ({_gpu?.KindName})" + (CliGpu != null ? " — set by --gpu for this run" : "") +
                            (pending ? ". The new choice applies after a restart." : "") +
                            " Processing on a GPU that does not drive the game's display adds a copy between GPUs for every frame (shown in the overlay HUD). The integrated GPU keeps FrameFX off the GPU the game uses, but it is much slower.";
            gpuRestart.Visible = pending;
        }
        _gpuChoice.SelectedIndexChanged += (_, _) => { _ui.GpuChoice = _gpuChoice.SelectedIndex; SaveUi(); GpuNote(); };
        gpuRestart.Click += (_, _) => { _out?.Close(); _ui.CaptureFrom(this); SaveUi(); Application.Restart(); };
        _gpuNote.MaximumSize = Size.Empty; _wrap.Add(_gpuNote);
        Add(adv, Note("Processing GPU:"));
        Add(adv, Stretch(_gpuChoice));
        Add(adv, _gpuNote);
        Add(adv, gpuRestart);
        GpuNote();

        var cpu = Section("CPU", false);
        Add(cpu, Note("Get more performance out of your CPU for games. This can't make your CPU faster than its hardware allows; results depend on the game and your PC. Everything is undone when the game closes, when FrameFX closes, or with Restore defaults."));
        Add(cpu, CpuGroup("Power"));
        Add(cpu, _cpuPower);
        Add(cpu, _cpuMin);
        Add(cpu, _cpuPark);
        Add(cpu, Note("Processor boost while gaming (may ask for admin):"));
        _cpuBoost.Items.Add("Leave as it is");
        _cpuBoost.Items.Add("Strong boost");
        _cpuBoost.Items.Add("Efficient boost");
        _cpuBoost.SelectedIndex = 0;
        Add(cpu, Stretch(_cpuBoost));
        Add(cpu, _cpuThrottle);
        Add(cpu, CpuGroup("Single-core"));
        Add(cpu, _cpuPrio);
        Add(cpu, _cpuFast);
        Add(cpu, _cpuSmt);
        Add(cpu, _cpuTimer);
        Add(cpu, CpuGroup("Multi-core"));
        Add(cpu, _cpuAll);
        Add(cpu, _cpuSelf);
        Add(cpu, Note("Slow down these background apps while gaming (one .exe per line):"));
        Add(cpu, Stretch(_cpuBg));
        var cpuRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0), BackColor = Theme.Card };
        _cpuBench.Click += (_, _) => _ = RunCpuBenchAsync();
        _cpuDefaults.Click += (_, _) =>
        {
            WriteCpuUi(new CpuBoostSettings());
            _cpuKey = "";
            OnCpuEdited();
        };
        cpuRow.Controls.Add(_cpuBench);
        cpuRow.Controls.Add(_cpuDefaults);
        Add(cpu, cpuRow);
        _cpuBenchLbl.MaximumSize = Size.Empty; _wrap.Add(_cpuBenchLbl);
        _cpuStatus.MaximumSize = Size.Empty; _wrap.Add(_cpuStatus);
        Add(cpu, _cpuBenchLbl);
        Add(cpu, _cpuStatus);
        WriteCpuUi(_ui.Cpu);
        _cpuStatus.Text = string.Join(Environment.NewLine, CpuBoost.StatusLines(new CpuBoostStatus()));
        foreach (var sw in new[] { _cpuPower, _cpuMin, _cpuPark, _cpuPrio, _cpuFast, _cpuSmt, _cpuTimer, _cpuAll, _cpuSelf, _cpuThrottle })
            sw.CheckedChanged += (_, _) => OnCpuEdited();
        _cpuBoost.SelectedIndexChanged += (_, _) => OnCpuEdited();
        _cpuBg.Leave += (_, _) => CommitCpuList();

        var upd = Section("Updates", false);
        _autoCheck.Checked = _ui.AutoCheckUpdates;
        _autoInstall.Checked = _ui.AutoInstallUpdates;
        _autoCheck.CheckedChanged += (_, _) => { _ui.AutoCheckUpdates = _autoCheck.Checked; SaveUi(); if (_autoCheck.Checked && DateTime.Now - _lastCheck > Updater.Interval) _ = CheckForUpdateAsync(false); };
        _autoInstall.CheckedChanged += (_, _) => { _ui.AutoInstallUpdates = _autoInstall.Checked; SaveUi(); };
        Add(upd, _autoCheck);
        Add(upd, _autoInstall);
        var updRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), BackColor = Theme.Card };
        _checkNow.Click += (_, _) => _ = CheckForUpdateAsync(true);
        var logs = new PillButton { Text = "Open logs folder", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 0, 8, 0) };
        logs.Click += (_, _) => { try { Directory.CreateDirectory(Diag.Dir); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Diag.Dir + "\"") { UseShellExecute = true }); } catch { } };
        updRow.Controls.Add(_checkNow); updRow.Controls.Add(logs); updRow.Controls.Add(_updStatus);
        Add(upd, updRow);
        Add(upd, Note("Crash reports (crash.log) and the startup timing trace (startup.log) are written to the logs folder; nothing is sent anywhere."));
        Add(upd, Note("Checks chopstickshq.com on launch and every 6 hours. Updates download over HTTPS, are verified against their published SHA-256 checksum before anything is installed, and the previous version is kept so it can be restored automatically if the new one fails to start."));
        _updStatus.Text = $"{DisplayVersion}" + (Updater.TestMode ? $" · test manifest: {Updater.ManifestUrl}" : "");
        _updInstall.Click += (_, _) => _ = InstallUpdateAsync(false);
        _updLater.Click += (_, _) => { _updBar.Visible = false; };

        _fg.CheckedChanged += (_, _) => { UserProfileTouch(); UpdateDirty(); };
        _fgKind.SelectedIndexChanged += (_, _) => UpdateDirty();
        _fgMul.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressMulSave && !_suppressProfile)
            {
                if (ActiveGameExe() is null) { _ui.FgMultiplier = FgMul.FromMenu(_fgMul.SelectedIndex); SaveUi(); }
                UserProfileTouch();
            }
            UpdateDirty();
        };
        _res.SelectedIndexChanged += (_, _) => { UserProfileTouch(); UpdateDirty(); };
        _quality.SelectedIndexChanged += (_, _) => UpdateDirty();
        _mode.SelectedIndexChanged += (_, _) => UpdateDirty();
        _source.SelectedIndexChanged += (_, _) => UpdateDirty();
        _hud.CheckedChanged += (_, _) => _settings.Hud = _hud.Checked;
        _lowLat.CheckedChanged += (_, _) => { _settings.LatencyBudget = _lowLat.Checked || _settings.Competitive; _ui.LatencyBudget = _lowLat.Checked; SaveUi(); };
        _settings.LatencyBudget = _ui.LatencyBudget;
        _ssgi.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            _settings.Ssgi = _ssgi.Checked;
            if (ActiveGameExe() is null) { _ui.Ssgi = _ssgi.Checked; SaveUi(); }
            UserProfileTouch();
        };
        _ssgiPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            _settings.SsgiPreset = Math.Max(0, _ssgiPreset.SelectedIndex);
            if (ActiveGameExe() is null) { _ui.SsgiPreset = _settings.SsgiPreset; SaveUi(); }
            UserProfileTouch();
        };
        _ssgiSteady.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            _settings.SsgiTemporal = _ssgiSteady.Checked;
            if (ActiveGameExe() is null) { _ui.SsgiTemporal = _ssgiSteady.Checked; SaveUi(); }
            UserProfileTouch();
        };
        _settings.Ssgi = _ui.Ssgi; _settings.SsgiPreset = Math.Clamp(_ui.SsgiPreset, 0, 2); _settings.SsgiTemporal = _ui.SsgiTemporal;
        _ssrt.CheckedChanged += (_, _) =>
        {
            _ssrtPreset.Visible = _ssrt.Checked;
            if (_suppressProfile) return;
            _settings.Ssrt = _ssrt.Checked;
            if (ActiveGameExe() is null) { _ui.Ssrt = _ssrt.Checked; SaveUi(); }
            UserProfileTouch();
        };
        _ssrtPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            _settings.SsrtPreset = Math.Max(0, _ssrtPreset.SelectedIndex);
            if (ActiveGameExe() is null) { _ui.SsrtPreset = _settings.SsrtPreset; SaveUi(); }
            UserProfileTouch();
        };
        _settings.Ssrt = _ui.Ssrt; _settings.SsrtPreset = Math.Clamp(_ui.SsrtPreset, 0, 2); _settings.SsrtTemporal = _ui.SsrtTemporal;
        _fgV2.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FgV2 = _fgV2.Checked; SaveUi(); }
            _settings.FgV2 = _fgV2.Checked;
        };
        _fgV2Vsync.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FgV2Vsync = _fgV2Vsync.Checked; SaveUi(); }
            _settings.FgV2Vsync = _fgV2Vsync.Checked;
        };
        _fgV2Extrap.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FgV2Extrap = _fgV2Extrap.Checked; SaveUi(); }
            _settings.FgV2Extrap = _fgV2Extrap.Checked;
        };
        _fgV2LowRes.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FgV2LowResGen = _fgV2LowRes.Checked; SaveUi(); }
            _settings.FgV2LowResGen = _fgV2LowRes.Checked;
        };
        _fgV2Cursor.CheckedChanged += (_, _) =>
        {
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FgV2Cursor = _fgV2Cursor.Checked; SaveUi(); }
            _settings.FgV2Cursor = _fgV2Cursor.Checked;
        };
        _fpsCap.SelectedIndexChanged += (_, _) =>
        {
            int v = FpsCapVals[Math.Max(0, _fpsCap.SelectedIndex)];
            _settings.FpsCap = v;
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FpsCap = v; SaveUi(); }
        };
        _fixedPace.CheckedChanged += (_, _) =>
        {
            _settings.FixedPacing = _fixedPace.Checked || Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
            if (_suppressProfile) return;
            if (ActiveGameExe() is null) { _ui.FixedPacing = _fixedPace.Checked; SaveUi(); }
        };

        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 12, 20, 14), BackColor = Theme.Bg };
        bar.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _start.Click += (_, _) => Toggle();
        var about = new PillButton { Text = "About / licenses", AutoSize = true, Kind = PillKind.Ghost };
        about.Click += (_, _) => ShowAbout();
        _apply.Click += (_, _) => Apply();
        _coffee.Click += (_, _) => Open(Coffee);
        _gamesOnly.Click += (_, _) => SetGamesOnly(!_ui.GamesOnly);
        row.Controls.Add(_apply); row.Controls.Add(_gamesOnly); row.Controls.Add(_start); row.Controls.Add(_coffee); row.Controls.Add(about);
        bar.Controls.Add(row);
        _pending.MaximumSize = Size.Empty; _status.MaximumSize = Size.Empty;
        _wrap.Add(_pending); _wrap.Add(_status); _wrap.Add(_notice);
        bar.Controls.Add(_pending); bar.Controls.Add(_status); bar.Controls.Add(_notice); bar.Controls.Add(_active); bar.Controls.Add(_gameStatus);
        bar.Resize += (_, _) => Reflow();

        Controls.Add(_scroll);
        Controls.Add(header);
        Controls.Add(bar);

        Diag.Mark("form: controls built");
        RefreshSources();
        Diag.Mark("form: sources listed");
        ProbeMotionAsync();
        UpdateUpNote();
        _gameTimer.Tick += (_, _) => GameTick();
        if (_ui.GamesOnly) SetGamesOnly(true);
        if (_gpu != null)
        {
            var g = _gpu;
            Task.Run(() => { try { VendorSupport.Probe(g); } catch { } })
                .ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(new Action(() => { _backend.Invalidate(); _fgKind.Invalidate(); UpdateUpNote(); })); });
        }
        _uiTimer.Tick += (_, _) =>
        {
            if (_out != null && _pipe != null)
            {
                _motionLbl.Text = _pipe.FgV2Active
                    ? $"Active motion source: {_pipe.Csr20Label}"
                    : $"Active motion source: {_pipe.MotionSource}";
                _active.ForeColor = _out.CompareOff ? Theme.Amber : Theme.Green;
                _active.Text = $"{_out.CompareStatus}  ·  output {_out.OutputFps:0} fps / game {_out.SourceFps:0} fps";
                string n = _out.CompareOff ? "FrameFX OFF (compare)" : "";
                if (_out.Warning.Length > 0) n += (n.Length > 0 ? "\n" : "") + "⚠ " + _out.Warning;
                if (_out.Hint.Length > 0) n += (n.Length > 0 ? "\n" : "") + "Hint: " + _out.Hint;
                if (_notice.Text != n) _notice.Text = n;
                _notice.Visible = n.Length > 0;
            }
            else { _motionLbl.Text = _motionAvail; _active.ForeColor = Theme.Green; _active.Text = _motionShort; _notice.Visible = false; }
        };
        _uiTimer.Start();
        _cpuPoll.Tick += (_, _) => CpuTick();
        _cpuPoll.Start();
        UpdateDirty();
        _baseline = CaptureUiProfile();
        _profilesReady = true;
        Reflow();
        _updTimer.Tick += (_, _) => UpdateTick();
        _updTimer.Start();
        Diag.Mark("form: ctor done");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Diag.Mark("form: shown");
        BeginInvoke(new Action(() => { Diag.Mark("form: first idle after show"); Diag.FlushStartup(); }));
        if (UpdatedFrom != null) ShowUpdBanner($"Updated to {DisplayVersion} (from {UpdatedFrom}).", false);
        if (UpdateFailed != null) { ShowUpdBanner($"The update to {UpdateFailed} did not start correctly, so version {Version} was restored.", false); _ui.FailedUpdate = UpdateFailed; SaveUi(); }
        if (UpdateMarker != null)
        {
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_, _) => { t.Stop(); try { File.WriteAllText(UpdateMarker, Version); } catch { } };
            t.Start();
        }
        if (_ui.AutoCheckUpdates && !QuietShot && Environment.GetEnvironmentVariable("UFX_UITEST") is not { Length: > 0 })
        {
            var t2 = new System.Windows.Forms.Timer { Interval = 4000 };
            t2.Tick += (_, _) => { t2.Stop(); _ = CheckForUpdateAsync(false); };
            t2.Start();
        }
        if (FgScript != null && ScriptSteps.Count > 0)
        {
            GamesOnlyLook(true);
            _gameStatus.Visible = true;
            _scriptStep = 0;
            ArmGames();
            RunScriptStep();
        }
    }

    bool PersistOk => Environment.GetEnvironmentVariable("UFX_UITEST") is not { Length: > 0 } && !QuietShot && FgScript == null;
    void SaveUi() { if (PersistOk) _ui.Save(); }

    void ShowUpdBanner(string text, bool buttons, string install = "Install")
    {
        _updText.Text = text;
        _updInstall.Text = install;
        _updInstall.Visible = buttons; _updLater.Visible = buttons;
        _updBar.Visible = true;
        Reflow();
    }

    void UpdateTick()
    {
        if (_updBusy) return;
        if (_ui.AutoCheckUpdates && DateTime.Now - _lastCheck >= Updater.Interval) { _ = CheckForUpdateAsync(false); return; }
        if (_updAvail != null && _ui.AutoInstallUpdates && _out == null && _updAvail.Version != _ui.FailedUpdate && !Updater.NeedsManualInstall(_updAvail)) _ = InstallUpdateAsync(true);
    }

    string _motionShort = "";

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    static readonly bool QuietShot = Environment.GetEnvironmentVariable("UFX_SHOT") is { Length: > 0 };
    protected override bool ShowWithoutActivation => QuietShot || base.ShowWithoutActivation;

    protected override void OnHandleCreated(EventArgs e)
    {
        Diag.Mark("form: handle created");
        base.OnHandleCreated(e);
        Theme.DarkChrome(this);
        Theme.DarkNative(_scroll);
    }

    protected override void OnLoad(EventArgs e)
    {
        Diag.Mark("form: load start");
        base.OnLoad(e);
        if (Environment.GetEnvironmentVariable("UFX_SHOT") is { Length: > 0 } shot)
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (Environment.GetEnvironmentVariable("UFX_SHOT_H") is { } sh2 && int.TryParse(sh2, out var h2)) { Height = h2; PerformLayout(); Reflow(); }
                Application.DoEvents();
                Refresh();
                using var bmp = new Bitmap(Width, Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { PrintWindow(Handle, hdc, 2); } finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(shot);
                Close();
            };
            t.Start();
        }
        _ui.ApplyTo(this);
        Reflow();
        Diag.Mark("form: load done");
    }

    TableLayoutPanel Section(string name, bool defaultOpen)
    {
        bool open = _ui.Expanded.TryGetValue(name, out var o) ? o : defaultOpen;
        var card = new CardPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 6, 16, 10) };
        var head = new SectionHeader(name) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0), Open = open, BackColor = Theme.Card };
        var body = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(0, 0, 0, 4), Margin = new Padding(0), Visible = open, BackColor = Theme.Card };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.Click += (_, _) => ToggleSection(name);
        card.Controls.Add(head); card.Controls.Add(body);
        _stack.Controls.Add(card);
        _sections[name] = (head, body);
        return body;
    }

    void ToggleSection(string name)
    {
        var (head, body) = _sections[name];
        body.Visible = !body.Visible;
        head.Open = body.Visible;
        _ui.Expanded[name] = body.Visible;
        Reflow();
    }

    static void Add(TableLayoutPanel body, Control c) => body.Controls.Add(c);
    static Control Stretch(Control c) { c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Width = 200; return c; }

    void Reflow()
    {
        int w = Math.Max(200, _scroll.ClientSize.Width - (int)(80 * DeviceDpi / 96f));
        int bw = Math.Max(200, ClientSize.Width - 40);
        SuspendLayout();
        foreach (var l in _wrap) l.MaximumSize = new Size(l == _pending || l == _status ? bw : w, 0);
        ResumeLayout(true);
    }

    void ProbeMotionAsync()
    {
        if (_gpu is null) return;
        _motionAvail = "Checking motion hardware…"; _motionShort = "Motion hardware: checking…"; _motionLbl.Text = _motionAvail;
        Task.Run(() =>
        {
            string a, b;
            try { (a, b) = ProbeMotionCore(); } catch (Exception ex) { Diag.WriteCrash("motion probe", ex); (a, b) = ("Motion hardware probe failed; the built-in estimator is used.", "Motion hardware: unavailable"); }
            Diag.Mark("motion probe done");
            void Set() { _motionAvail = a; _motionShort = b; if (_out == null) { _motionLbl.Text = a; _active.Text = b; } }
            try { if (IsHandleCreated) BeginInvoke(new Action(Set)); else { _motionAvail = a; _motionShort = b; } } catch { }
        });
    }

    (string avail, string shrt) ProbeMotionCore()
    {
        return ("Motion hardware: checked by the FrameFX engine (not included in this build).", "Motion hardware: engine not included");
    }

    Label Note(string t) { var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 6, 0, 4) }; _wrap.Add(l); return l; }

    void UpdateSharp() => _sharpLbl.Text = $"{_sharp.Value / 100f:0.00} stops";

    Applied Current()
    {
        IntPtr hwnd = (_source.SelectedItem as SourceItem)?.Hwnd ?? IntPtr.Zero;
        int modeIdx = _mode.SelectedIndex;
        if (_gamesArmed) { hwnd = _gameHwnd; modeIdx = (int)OutputMode.Overlay; }
        return new(hwnd, _backend.SelectedIndex, _quality.SelectedIndex,
                   _sharp.Value, _fg.Checked, modeIdx, _motion.SelectedIndex, _fgKind.SelectedIndex, _res.SelectedIndex, _preset.SelectedIndex, _fgMul.SelectedIndex);
    }

    Backend SelectedBackend => _backend.ReasonFor(_backend.SelectedIndex) is null ? BackendOrder[Math.Max(0, _backend.SelectedIndex)] : Backend.Temporal;

    void UpdateUpNote()
    {
        var b = SelectedBackend;
        var i = VendorSupport.Get(b);
        _upNote.Text = b switch
        {
                        _ when _preset.SelectedIndex == 2 => "Competitive: highest performance and lowest latency. 1080p output with 8× frame generation (lowered automatically when needed). Overrides the upscaler, multiplier, resolution and latency-budget choices while selected.",
            Backend.Temporal => _preset.SelectedIndex == 0
                ? "CSR 1.3 (Chopsticks Super Resolution), our own upscaler. Performance preset: fastest."
                : "CSR 1.3 (Chopsticks Super Resolution), our own upscaler. Quality preset: best image quality.",
            Backend.Spatial => "CSR 1.2 (Chopsticks Super Resolution), our own lightweight upscaler and sharpener.",
            Backend.Bilinear => "Plain bilinear scaling (reference).",
            _ when !VendorSupport.Probed => "Checking which vendor upscalers this GPU supports…",
            Backend.Fsr1 => i.Detail + ".",
            _ => i.Ok ? $"{i.Detail}, run by the vendor's runtime." : "Unavailable: " + i.Why,
        };
    }

    void UpdateDirty()
    {
        if (_suppressDirty) return;
        bool running = _out != null && _applied != null;
        var c = Current();
        var changed = new List<string>();
        if (running)
        {
            var a = _applied!;
            if (c.Source != a.Source) changed.Add("source");
            if (c.Backend != a.Backend) changed.Add("upscaler");
            if (c.Quality != a.Quality) changed.Add("quality");
            if (c.Sharp != a.Sharp) changed.Add("sharpness");
            if (c.Fg != a.Fg) changed.Add("frame generation");
            if (c.Mode != a.Mode) changed.Add("output mode");
            if (c.Motion != a.Motion) changed.Add("motion source");
            if (c.FgKind != a.FgKind) changed.Add("frame generation type");
            if (c.FgMul != a.FgMul) changed.Add("frame generation multiplier");
            if (c.Res != a.Res) changed.Add("output resolution");
            if (c.Preset != a.Preset) changed.Add("preset");
        }
        bool dirty = changed.Count > 0;
        bool enabled = _gpu != null && (!running || dirty);
        _apply.Enabled = enabled;
        _apply.Text = running ? (dirty ? "Apply ●" : "Applied") : "Apply && start";
        _apply.Kind = PillKind.Primary;
        _apply.Ring = dirty;
        _pending.Text = dirty
            ? "Pending changes: " + string.Join(", ", changed) + ". Press Apply." +
              (c.Source != _applied!.Source || c.Quality != _applied.Quality || c.Mode != _applied.Mode || c.Res != _applied.Res || (c.Preset == 2) != (_applied.Preset == 2) ? " (Output restarts briefly.)" : "")
            : "";
        _pending.Visible = dirty;
    }

    void Apply()
    {
        if (_gpu is null || _pipe is null) return;
        if (_out == null)
        {
            if (_gamesArmed) return;
            Toggle();
            return;
        }
        var c = Current(); var a = _applied!;
        bool restart = c.Source != a.Source || c.Quality != a.Quality || c.Mode != a.Mode || c.Res != a.Res || (c.Preset == 2) != (a.Preset == 2);
        UserProfileTouch();
        if (restart)
        {
            _restartPending = true;
            _status.Text = "Applying: restarting output…";
            if (_gamesArmed) { _pendingHwnd = _out.Source; _pendingLabel = _gameLabel; }
            _out.Close();
            return;
        }
        PushLive();
        _applied = c;
        _status.Text = "Settings applied.";
        UpdateDirty();
    }

    void PushLive()
    {
        if (_settings.Backend != SelectedBackend || _settings.FgKind != (FgKind)_fgKind.SelectedIndex) lock (Gpu.Lock) _pipe?.ClearVendorFailures();
        _settings.Backend = SelectedBackend;
        _settings.Sharpness = _sharp.Value / 100f;
        _settings.FrameGen = _fg.Checked;
        _settings.FgKind = (FgKind)Math.Max(0, _fgKind.SelectedIndex);
        _settings.FgMultiplier = FgMul.FromMenu(_fgMul.SelectedIndex);
        _settings.Motion = (MotionPreference)_motion.SelectedIndex;
        _settings.Performance = _preset.SelectedIndex != 1;
        _settings.LatencyBudget = _lowLat.Checked;
        _settings.Ssgi = _ssgi.Checked;
        _settings.SsgiPreset = Math.Max(0, _ssgiPreset.SelectedIndex);
        _settings.SsgiTemporal = _ssgiSteady.Checked;
        _settings.Ssrt = _ssrt.Checked;
        _settings.SsrtPreset = Math.Max(0, _ssrtPreset.SelectedIndex);
        _settings.SsrtTemporal = _ui.SsrtTemporal;
        _settings.FgV2 = CliCsr2 || _fgV2.Checked;
        _settings.FgV2Vsync = CliVsyncFg || _fgV2Vsync.Checked || Environment.GetEnvironmentVariable("UFX_FG_V2_VSYNC") == "1";
        _settings.FgV2Extrap = _fgV2Extrap.Checked;
        _settings.FgV2LowResGen = _fgV2LowRes.Checked || Environment.GetEnvironmentVariable("UFX_FG_V2_LOWRES") == "1";
        _settings.FgV2Cursor = _fgV2Cursor.Checked;
        _settings.FpsCap = CliFpsCap ?? FpsCapVals[Math.Max(0, _fpsCap.SelectedIndex)];
        _settings.FixedPacing = CliFixedPacing || _fixedPace.Checked || Environment.GetEnvironmentVariable("UFX_FG_V2_PACING") == "fixed";
        bool comp = _preset.SelectedIndex == 2;
        _settings.Competitive = comp;
        if (comp)
        {
            _settings.Backend = Backend.Temporal; _settings.FgKind = FgKind.FrameFX;
            _settings.FrameGen = true; _settings.FgMultiplier = 8; _settings.LatencyBudget = true;
        }
    }

    sealed record SourceItem(IntPtr Hwnd, string Label) { public override string ToString() => Label; }

    void RefreshSources()
    {
        _source.Items.Clear();
        _source.Items.Add(new SourceItem(IntPtr.Zero, "Built-in test scene (demo)"));
        if (WindowCapture.IsSupported())
            foreach (var w in Native.ListWindows()) _source.Items.Add(new SourceItem(w.Handle, w.ToString()));
        else
            _status.Text = "Windows.Graphics.Capture is not available on this system; only the test scene can be used.";
        _source.SelectedIndex = 0;
    }

    void Toggle()
    {
        if (_out != null) { _out.UserStop = true; _out.Close(); return; }
        if (_gamesArmed) { DisarmGames(); _start.Text = "Start"; return; }
        if (GamesOnlyOn) { ArmGames(); return; }
        if (_gpu is null || _pipe is null || _source.SelectedItem is not SourceItem src) return;
        StartOutput(src.Hwnd, (OutputMode)_mode.SelectedIndex, src.Label);
    }

    void StartOutput(IntPtr hwnd, OutputMode mode, string label)
    {
        if (_gpu is null || _pipe is null) return;
        var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.FromControl(this);
        var res = _preset.SelectedIndex == 2 ? OutputRes.P1080 : (OutputRes)Math.Max(0, _res.SelectedIndex);
        int outW = screen.Bounds.Width & ~1, outH = screen.Bounds.Height & ~1;
        if (OutputResNames.Size(res) is { w: > 0 } fixedSize) { outW = fixedSize.w; outH = fixedSize.h; }
        double ratio = Qualities[_quality.SelectedIndex].ratio;
        int inW = Math.Max(64, (int)(outW / ratio) & ~1), inH = Math.Max(64, (int)(outH / ratio) & ~1);
        PushLive();
        _applied = Current();
        _out = new OutputForm(_gpu, _pipe, _settings, hwnd, screen, mode, inW, inH, outW, outH, res);
        if (_compareOff) _out.SetCompareOff(true);
        _out.FormClosed += OnOutputClosed;
        _start.Text = "Stop";
        _status.Text = hwnd == IntPtr.Zero
            ? $"Running test scene: {inW}×{inH} → {outW}×{outH}"
            : _gamesArmed
                ? $"Game overlay on \"{label}\"" + (res is OutputRes.P1080 or OutputRes.P1440 or OutputRes.P2160 ? $" → {outW}×{outH}" : "")
                : $"Capturing \"{label}\"" + (res is OutputRes.P1080 or OutputRes.P1440 or OutputRes.P2160 ? $" → {outW}×{outH}" : "");
        if (_profileBanner.Length > 0) { _status.Text = _profileBanner; _profileBanner = ""; }
        _out.Show();
        UpdateDirty();
    }

    void OnOutputClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not OutputForm f) return;
        _compareOff = f.CompareOff;
        _status.Text = f.Error is { } err ? "Stopped: " + err : "Stopped.";
        bool userStop = f.UserStop;
        _out = null; _applied = null;
        if (_gamesArmed && !_restartPending && (userStop || f.Error != null)) DisarmGames();
        _start.Text = _gamesArmed ? "Stop" : "Start";
        if (_restartPending)
        {
            _restartPending = false;
            BeginInvoke(new Action(() =>
            {
                if (_gamesArmed)
                {
                    _gameHwnd = _pendingHwnd; _gameLabel = _pendingLabel;
                    bool paused = _lastVerdict is { IsGame: false };
                    StartOutput(_pendingHwnd, OutputMode.Overlay, _pendingLabel);
                    if (paused) _out?.SetPaused(true);
                }
                else Toggle();
            }));
        }
        UpdateGamesStatus();
        UpdateDirty();
    }

    void SetGamesOnly(bool on)
    {
        _ui.GamesOnly = on;
        SaveUi();
        GamesOnlyLook(on);
        if (on)
        {
            _gameTimer.Start();
            if (_out != null) ArmGames();
            UpdateGamesStatus();
        }
        else
        {
            _gameTimer.Stop();
            if (_gamesArmed)
            {
                DisarmGames();
                if (_out != null)
                {
                    if (_out.Paused) _out.SetPaused(false);
                    _start.Text = "Stop";
                }
                else _start.Text = "Start";
            }
        }
        _gameStatus.Visible = on;
    }

    void GamesOnlyLook(bool on)
    {
        _gamesOnly.Kind = on ? PillKind.Secondary : PillKind.Ghost;
        _gamesOnly.Ring = on;
        _gamesOnly.Text = on ? "✓ Apply to games only" : "Apply to games only";
    }

    void ArmGames()
    {
        if (_gamesArmed || !GamesOnlyOn) return;
        _gamesArmed = true;
        _start.Text = "Stop";
        _gameTimer.Start();
        GameTick();
    }

    void DisarmGames()
    {
        if (!_gamesArmed) return;
        _gamesArmed = false;
        _gameHwnd = IntPtr.Zero; _gameLabel = "";
        GameLog("state " + GameLogState());
        UpdateGamesStatus();
    }

    void GameTick()
    {
        var v = _detector.Classify(FgScript != null ? ScriptForeground() : Native.GetForegroundWindow());
        _lastVerdict = v;
        if (v.Pid != 0 && v.Pid != Environment.ProcessId) _lastExternal = v;

        if (_gamesArmed)
        {
            if (v.IsGame && Native.IsWindow(v.Hwnd))
            {
                var o = _out;
                if (o == null)
                {
                    if (!_restartPending)
                    {
                        PrepareGameSettings(v.Exe);
                        _gameHwnd = v.Hwnd; _gameLabel = v.Exe;
                        StartOutput(v.Hwnd, OutputMode.Overlay, v.Exe);
                    }
                }
                else if (o.Source != v.Hwnd)
                {
                    PrepareGameSettings(v.Exe);
                    _pendingHwnd = v.Hwnd; _pendingLabel = v.Exe;
                    _restartPending = true;
                    o.Close();
                }
                else if (o.Paused) o.SetPaused(false);
            }
            else if (_out is { Paused: false } o)
            {
                o.SetPaused(true);
            }
        }
        UpdateGamesStatus(v);

        if (GameLogPath != null)
        {
            string verdict = $"verdict exe={v.Exe} game={v.IsGame} reason={v.Reason}";
            if (verdict != _lastLoggedVerdict) { _lastLoggedVerdict = verdict; GameLog(verdict); }
            string state = GameLogState();
            if (state != _lastLoggedState) { _lastLoggedState = state; GameLog("state " + state); }
            if (++_hbTicks >= 10)
            {
                _hbTicks = 0;
                GameLog($"stats avg={_detector.AvgClassifyMs:0.000}ms last={_detector.LastClassifyMs:0.000}ms processed={_out?.Processed ?? 0}");
            }
        }
    }

    void UpdateGamesStatus(GameDetector.GameVerdict? v = null)
    {
        if (!GamesOnlyOn) { _gameStatus.Visible = false; return; }
        _gameStatus.Visible = true;
        v ??= _lastVerdict;
        var o = _out;
        if (!_gamesArmed)
            _gameStatus.Text = "Games only: press Apply to start";
        else if (o == null || o.Paused)
            _gameStatus.Text = "Waiting for a game…";
        else
        {
            string exe = v is { IsGame: true, Exe.Length: > 0 } ? v.Exe : _gameLabel;
            _gameStatus.Text = $"Active: {exe}";
        }
        _gameStatus.ForeColor = _gamesArmed && o is { Paused: false } ? Theme.Green : Theme.TextMuted;
    }

    string GameLogState() =>
        $"armed={_gamesArmed} running={_out != null} source={_out?.Source.ToString() ?? "0"} paused={_out?.Paused ?? false} " +
        $"visible={_out != null && _out.IsHandleCreated && Native.IsWindowVisible(_out.Handle)} processed={_out?.Processed ?? 0}";

    static void GameLog(string line)
    {
        if (GameLogPath == null) return;
        try { File.AppendAllText(GameLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {line}\n"); } catch { }
    }

    IntPtr ScriptForeground()
    {
        if (_scriptStep < 0 || _scriptStep >= ScriptSteps.Count) return IntPtr.Zero;
        string title = ScriptSteps[_scriptStep].title;
        if (title.Equals("desktop", StringComparison.OrdinalIgnoreCase)) return Native.GetShellWindow();
        foreach (var w in Native.ListWindows())
            if (w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) return w.Handle;
        return IntPtr.Zero;
    }

    void RunScriptStep()
    {
        if (_scriptStep >= ScriptSteps.Count) { FinishScript(); return; }
        var (title, sec) = ScriptSteps[_scriptStep];
        GameLog($"script step {_scriptStep + 1}/{ScriptSteps.Count}: foreground \"{title}\" for {sec}s");
        var t = new System.Windows.Forms.Timer { Interval = sec * 1000 };
        t.Tick += (_, _) => { t.Stop(); _scriptStep++; RunScriptStep(); };
        t.Start();
    }

    void FinishScript()
    {
        GameLog($"script done: {GameLogState()} avg={_detector.AvgClassifyMs:0.000}ms last={_detector.LastClassifyMs:0.000}ms");
        Close();
    }

    static List<(string title, int sec)> ParseScript(string? script)
    {
        var list = new List<(string, int)>();
        if (script == null) return list;
        foreach (var part in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(part[(i + 1)..].Trim(), out int sec) || sec <= 0) continue;
            list.Add((part[..i].Trim(), sec));
        }
        return list;
    }

    string? ActiveGameExe()
    {
        try
        {
            var o = _out;
            if (o == null || o.Source == IntPtr.Zero) return null;
            if (_gamesArmed && _gameLabel.Length > 0 && o.Source == _gameHwnd) return _gameLabel;
            var c = _detector.Classify(o.Source);
            return c.IsGame && c.Exe.Length > 0 ? c.Exe : null;
        }
        catch { return null; }
    }

    GameProfile CaptureUiProfile() => GameProfiles.Sanitize(new GameProfile
    {
        Preset = Math.Max(0, _preset.SelectedIndex),
        FrameGen = _fg.Checked,
        FgMultiplier = FgMul.FromMenu(_fgMul.SelectedIndex),
        Ssgi = _ssgi.Checked,
        SsgiPreset = Math.Max(0, _ssgiPreset.SelectedIndex),
        SsgiTemporal = _ssgiSteady.Checked,
        Ssrt = _ssrt.Checked,
        SsrtPreset = Math.Max(0, _ssrtPreset.SelectedIndex),
        SsrtTemporal = _ui.SsrtTemporal,
        Backend = (int)BackendOrder[Math.Clamp(_backend.SelectedIndex, 0, BackendOrder.Length - 1)],
        Res = Math.Max(0, _res.SelectedIndex),
        Cpu = ReadCpuSettings(),
    });

    void UserProfileTouch()
    {
        if (!_profilesReady || _suppressProfile) return;
        var snap = CaptureUiProfile();
        if (ActiveGameExe() is { } exe && GameProfiles.ShouldRemember(true, !PersistOk, exe))
        {
            GameProfiles.Remember(_ui.Profiles, exe, snap);
            SaveUi();
            RebuildProfileList();
        }
        else if (ActiveGameExe() is null)
            _baseline = snap;
    }

    void PrepareGameSettings(string exe)
    {
        string name = exe ?? "";
        var (settings, fromProfile) = GameProfiles.Select(_ui.Profiles, name, _baseline);
        ApplyProfileToUi(settings);
        _cpuShownExe = name;
        _profileBanner = fromProfile ? GameProfiles.AppliedStatus(name) : "";
    }

    void ApplyProfileToUi(GameProfile p)
    {
        _suppressProfile = true;
        _suppressDirty = true;
        _suppressMulSave = true;
        try
        {
            int bi = Array.IndexOf(BackendOrder, (Backend)p.Backend);
            if (bi >= 0) _backend.SelectedIndex = bi;
            _preset.SelectedIndex = p.Preset is 1 or 2 ? p.Preset : 0;
            _fg.Checked = p.FrameGen;
            int mi = FgMul.MenuIndex(GameProfiles.Mul(p.FgMultiplier));
            if (mi >= _fgMul.Items.Count) _fgMul.Items.Add("8× (7 generated frames, advanced)");
            _fgMul.SelectedIndex = Math.Min(mi, _fgMul.Items.Count - 1);
            _ssgi.Checked = p.Ssgi;
            if (_ssgiPreset.Items.Count > 0) _ssgiPreset.SelectedIndex = Math.Clamp(p.SsgiPreset, 0, _ssgiPreset.Items.Count - 1);
            _ssgiSteady.Checked = p.SsgiTemporal;
            _ssrt.Checked = p.Ssrt;
            if (_ssrtPreset.Items.Count > 0) _ssrtPreset.SelectedIndex = Math.Clamp(p.SsrtPreset, 0, _ssrtPreset.Items.Count - 1);
            _ssrtPreset.Visible = p.Ssrt;
            _ui.SsrtTemporal = p.SsrtTemporal;
            if (p.Res >= 0 && p.Res < _res.Items.Count) _res.SelectedIndex = p.Res;
            WriteCpuUi(p.Cpu);
            UpdateUpNote();
        }
        finally
        {
            _suppressProfile = false;
            _suppressDirty = false;
            _suppressMulSave = false;
        }
    }

    void RebuildProfileList()
    {
        foreach (var l in _profileLabels) _wrap.Remove(l);
        _profileLabels.Clear();
        for (int i = _profileRows.Controls.Count - 1; i >= 0; i--)
        {
            var c = _profileRows.Controls[i];
            _profileRows.Controls.RemoveAt(i);
            c.Dispose();
        }
        _profileRows.RowStyles.Clear();
        _profileRows.RowCount = 0;
        var keys = _ui.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        _profileEmpty.Visible = keys.Count == 0;
        _profileResetAll.Enabled = keys.Count > 0;
        int row = 0;
        foreach (var key in keys)
        {
            if (!_ui.Profiles.TryGetValue(key, out var prof) || prof == null) continue;
            var lbl = new Label
            {
                Text = $"{key}  —  {GameProfiles.Summary(prof)}",
                AutoSize = true,
                ForeColor = Theme.Text,
                Margin = new Padding(0, 4, 8, 2),
                MaximumSize = new Size(420, 0),
            };
            _profileLabels.Add(lbl);
            _wrap.Add(lbl);
            string k = key;
            var btn = new PillButton { Text = "Reset", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 2, 0, 0) };
            btn.Click += (_, _) => BeginInvoke(new Action(() =>
            {
                if (!GameProfiles.Reset(_ui.Profiles, k)) return;
                SaveUi();
                RebuildProfileList();
            }));
            _profileRows.Controls.Add(lbl, 0, row);
            _profileRows.Controls.Add(btn, 1, row);
            row++;
        }
    }

    static TextBox NewGameListBox()
    {
        var b = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
            BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body(),
        };
        b.Height = (int)(b.Font.Height * 4.6f);
        Theme.DarkNative(b);
        return b;
    }

    static List<string> NormalizeGameList(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            string s = line.Trim();
            if (s.Length == 0) continue;
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            if (seen.Add(s)) list.Add(s);
        }
        return list;
    }

    void PushGameDetector()
    {
        _ui.GamesAlways ??= new List<string>();
        _ui.GamesNever ??= new List<string>();
        _detector.Always.Clear(); foreach (var e in _ui.GamesAlways) _detector.Always.Add(e);
        _detector.Never.Clear(); foreach (var e in _ui.GamesNever) _detector.Never.Add(e);
    }

    void SyncGameLists(bool rewrite)
    {
        _ui.GamesAlways = NormalizeGameList(_gamesAlwaysBox.Text);
        _ui.GamesNever = NormalizeGameList(_gamesNeverBox.Text);
        PushGameDetector();
        if (rewrite)
        {
            _gamesAlwaysBox.Text = string.Join(Environment.NewLine, _ui.GamesAlways);
            _gamesNeverBox.Text = string.Join(Environment.NewLine, _ui.GamesNever);
        }
        SaveUi();
    }

    void AddToGameList(bool always)
    {
        if (_lastExternal is not { Exe.Length: > 0 })
        {
            _gamesListNote.Text = "No other window seen yet (the watcher runs while games-only is on).";
            return;
        }
        string exe = NormalizeGameList(_lastExternal.Exe)[0];
        string other = always ? string.Join("\n", _ui.GamesNever) : string.Join("\n", _ui.GamesAlways);
        if (always) _ui.GamesAlways = NormalizeGameList(string.Join("\n", _ui.GamesAlways) + "\n" + exe);
        else _ui.GamesNever = NormalizeGameList(string.Join("\n", _ui.GamesNever) + "\n" + exe);
        if (always) _ui.GamesNever = NormalizeGameList(other).Where(s => !s.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
        else _ui.GamesAlways = NormalizeGameList(other).Where(s => !s.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
        _gamesAlwaysBox.Text = string.Join(Environment.NewLine, _ui.GamesAlways);
        _gamesNeverBox.Text = string.Join(Environment.NewLine, _ui.GamesNever);
        PushGameDetector();
        _gamesListNote.Text = $"Added {exe} to {(always ? "Always" : "Never")}.";
        SaveUi();
    }

    public void Automate(bool demo, int exitAfterSec, string? outPath, string? sourceTitle = null,
                         string? mode = null, string? motion = null, bool fg = false)
    {
        var checks = new List<string>();
        IntPtr srcHwnd = IntPtr.Zero;
        Shown += (_, _) => ApplyCliRunFlags();
        if (demo)
            Shown += (_, _) =>
            {
                _mode.SelectedIndex = (int)(mode == "overlay" ? OutputMode.Overlay : mode == "fullscreen" ? OutputMode.Fullscreen : OutputMode.Window);
                _motion.SelectedIndex = (int)(motion switch { "nvof" => MotionPreference.Nvof, "d3d12" => MotionPreference.D3D12, "sw" => MotionPreference.Software, _ => MotionPreference.Auto });
                _fg.Checked = fg || CliCsr2 || CliVsyncFg;
                if (AutoBackend is { } ab)
                {
                    var want = BackendNames.Parse(ab);
                    if (Backends.IsVendor(want) || want == Backend.Fsr1) VendorSupport.Probe(_gpu!);
                    _backend.SelectedIndex = Array.IndexOf(BackendOrder, want);
                }
                if (AutoPreset is { } apr) _preset.SelectedIndex = apr.StartsWith("q", StringComparison.OrdinalIgnoreCase) ? 1 : apr.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                if (AutoRes is { } ar) _res.SelectedIndex = (int)(ar.ToLowerInvariant() switch { "1080" or "1080p" => OutputRes.P1080, "1440" or "1440p" => OutputRes.P1440, "4k" or "2160" => OutputRes.P2160, "source" => OutputRes.Source, _ => OutputRes.Auto });
                if (AutoFgKind is "fsr3") { VendorSupport.Probe(_gpu!); _fgKind.SelectedIndex = (int)FgKind.Fsr3; }
                if (AutoFgMul is { } fx)
                {
                    _suppressMulSave = true;
                    int mi = fx.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : FgMul.MenuIndex(int.TryParse(fx.TrimEnd('x', 'X'), out var fxn) ? fxn : 4);
                    if (mi >= _fgMul.Items.Count) _fgMul.Items.Add("8× (7 generated frames, advanced)");
                    _fgMul.SelectedIndex = Math.Min(mi, _fgMul.Items.Count - 1);
                    _suppressMulSave = false;
                }
                _source.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(sourceTitle))
                    for (int i = 1; i < _source.Items.Count; i++)
                        if (_source.Items[i]!.ToString()!.Contains(sourceTitle, StringComparison.OrdinalIgnoreCase)) { _source.SelectedIndex = i; break; }
                srcHwnd = (_source.SelectedItem as SourceItem)?.Hwnd ?? IntPtr.Zero;
                if (srcHwnd != IntPtr.Zero && mode == "overlay") Native.SetForegroundWindow(srcHwnd);
                Toggle();
                if (srcHwnd != IntPtr.Zero && mode == "overlay" && exitAfterSec >= 8)
                    RunOverlayChecks(srcHwnd, checks);
                if (Environment.GetEnvironmentVariable("UFX_APPLYTEST") == "1") RunApplyChecks(checks);
                if (Environment.GetEnvironmentVariable("UFX_UITEST") is { Length: > 0 } dir) RunUiChecks(checks, dir);
            };
        if (exitAfterSec > 0)
        {
            var t = new System.Windows.Forms.Timer { Interval = exitAfterSec * 1000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var report = $"Universal-FrameFX {DisplayVersion} ({Version})\nGPU: {_gpu?.AdapterName}\nStatus: {_status.Text}\nGPU choice: {_gpu?.ActiveChoice} ({_gpu?.KindName})\nOutput running: {_out != null}\n" +
                             $"Output mode: {_out?.Mode}\nSwap chain: {_out?.SwapInfo}\nMotion source: {_out?.MotionSource}\nError: {_out?.Error ?? "none"}\n" +
                             $"Requested: backend={SelectedBackend} res={(OutputRes)_res.SelectedIndex} fgkind={(FgKind)_fgKind.SelectedIndex} fgx={FgMul.FromMenu(_fgMul.SelectedIndex)} preset={(_preset.SelectedIndex == 1 ? "quality" : _preset.SelectedIndex == 2 ? "competitive" : "performance")}\n" +
                             $"HUD:\n{_out?.HudText}\n" + (checks.Count > 0 ? "Overlay checks:\n" + string.Join("\n", checks) + "\n" : "");
                if (outPath != null) File.WriteAllText(outPath, report);
                if (_out != null && mode == "overlay" && srcHwnd != IntPtr.Zero)
                {
                    SendKeys3(Keys.ControlKey, Keys.Menu, Keys.Q);
                    var t2 = new System.Windows.Forms.Timer { Interval = 800 };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        if (outPath != null) File.AppendAllText(outPath, $"Ctrl+Alt+Q stopped output: {_out == null}\n");
                        _out?.Close(); Close();
                    };
                    t2.Start();
                    return;
                }
                _out?.Close();
                Close();
            };
            t.Start();
        }
    }

    void ApplyCliRunFlags()
    {
        _suppressProfile = true;
        _suppressMulSave = true;
        try
        {
            if (CliCsr2)
            {
                _fgV2.Checked = true;
                _fg.Checked = true;
                _settings.FgV2 = true;
                _settings.FrameGen = true;
            }
            if (CliVsyncFg)
            {
                _fgV2Vsync.Checked = true;
                _settings.FgV2Vsync = true;
            }
            if (CliFixedPacing)
            {
                _fixedPace.Checked = true;
                _settings.FixedPacing = true;
            }
            if (CliFpsCap is int cap)
            {
                _settings.FpsCap = cap;
                int ix = Array.IndexOf(FpsCapVals, cap);
                if (ix >= 0) _fpsCap.SelectedIndex = ix;
            }
        }
        finally
        {
            _suppressMulSave = false;
            _suppressProfile = false;
        }
    }

    static IntPtr Root(IntPtr h) => Native.GetAncestor(h, 2);

    void RunOverlayChecks(IntPtr src, List<string> checks)
    {
        int step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            var o = _out;
            if (o == null) { t.Stop(); checks.Add("output closed early"); return; }
            var r = Native.FrameBounds(src);
            var cx = (r.Left + r.Right) / 2; var cy = (r.Top + r.Bottom) / 2;
            switch (step++)
            {
                case 0:
                    var ob = o.Bounds;
                    checks.Add($"overlay bounds {ob.Left},{ob.Top} {ob.Width}x{ob.Height} vs source {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} (match: {ob.Left == r.Left && ob.Top == r.Top && ob.Width == r.Right - r.Left && ob.Height == r.Bottom - r.Top})");
                    checks.Add($"foreground is source after overlay shown: {Root(Native.GetForegroundWindow()) == src}");
                    var hit = Root(Native.WindowFromPoint(new Native.POINT { X = cx, Y = cy }));
                    checks.Add($"window under source centre is the source (click-through): {hit == src}{(hit == o.Handle ? " (overlay!)" : "")}");
                    Native.SetCursorPos(cx, cy);
                    var inp = new Native.INPUT[2];
                    inp[0].type = 0; inp[0].u.mi.dwFlags = 0x0002;
                    inp[1].type = 0; inp[1].u.mi.dwFlags = 0x0004;
                    Native.SendInput(2, inp, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
                    break;
                case 1:
                    checks.Add($"foreground is source after a click through the overlay: {Root(Native.GetForegroundWindow()) == src}");
                    SendKeys3(Keys.ControlKey, Keys.Menu, Keys.F);
                    break;
                case 2:
                    checks.Add($"Ctrl+Alt+F hid the overlay: {!o.OverlayShown && !Native.IsWindowVisible(o.Handle)}; source still foreground: {Root(Native.GetForegroundWindow()) == src}");
                    SendKeys3(Keys.ControlKey, Keys.Menu, Keys.F);
                    break;
                case 3:
                    checks.Add($"Ctrl+Alt+F showed it again: {o.OverlayShown && Native.IsWindowVisible(o.Handle)}; source still foreground: {Root(Native.GetForegroundWindow()) == src}");
                    t.Stop();
                    break;
            }
        };
        t.Start();
    }

    void RunUiChecks(List<string> checks, string dir)
    {
        int step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 900 };
        void Shot(string name)
        {
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(System.IO.Path.Combine(dir, name + ".png"));
            var ab = _apply.Parent!.RectangleToScreen(_apply.Bounds); var cb = RectangleToScreen(ClientRectangle);
            checks.Add($"ui {name}: size {Width}x{Height} state {WindowState}; Apply visible={_apply.Visible && cb.Contains(ab)}; scroll {( _scroll.VerticalScroll.Visible ? "on" : "off")}; source combo width {_source.Width}");
        }
        t.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0: checks.Add($"ui: border {FormBorderStyle}, maximize {MaximizeBox}, min {MinimumSize.Width}x{MinimumSize.Height}; sections " + string.Join(", ", _sections.Select(kv => $"{kv.Key}={(kv.Value.body.Visible ? "open" : "closed")}"))); Shot("default"); break;
                case 1: Size = MinimumSize; break;
                case 2: Shot("minimum"); WindowState = FormWindowState.Maximized; break;
                case 3: Shot("maximized"); ToggleSection("Advanced"); ToggleSection("Frame generation"); break;
                case 4: Shot("toggled"); WindowState = FormWindowState.Normal; break;
                case 5: t.Stop(); break;
            }
        };
        t.Start();
    }

    void RunApplyChecks(List<string> checks)
    {
        int step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0:
                    checks.Add($"apply: running={_out != null}, button='{_apply.Text}' enabled={_apply.Enabled}, pending='{_pending.Text}'");
                    _backend.SelectedIndex = (int)Backend.Spatial; _sharp.Value = 80; UpdateSharp(); UpdateDirty();
                    checks.Add($"apply: after edits button='{_apply.Text}' enabled={_apply.Enabled}, pending='{_pending.Text}', live backend still {_settings.Backend}");
                    _apply.PerformClick();
                    checks.Add($"apply: after Apply backend={_settings.Backend} sharp={_settings.Sharpness:0.00} button='{_apply.Text}' enabled={_apply.Enabled} pending='{_pending.Text}'");
                    break;
                case 1:
                    checks.Add($"apply: HUD shows spatial: {(_out?.HudText ?? "").Contains("CSR 1.2")}");
                    _backend.SelectedIndex = 0; _quality.SelectedIndex = 1; UpdateDirty();
                    checks.Add($"apply: quality change pending='{_pending.Text}'");
                    var before = _out;
                    _apply.PerformClick();
                    checks.Add($"apply: output restarting (old closed: {_out == null || _out != before})");
                    break;
                case 3:
                    checks.Add($"apply: after restart running={_out != null}, status='{_status.Text}', backend={_settings.Backend}, button='{_apply.Text}'");
                    t.Stop();
                    break;
            }
        };
        t.Start();
    }

    static void SendKeys3(Keys a, Keys b, Keys c)
    {
        var keys = new[] { a, b, c };
        var inp = new Native.INPUT[6];
        for (int i = 0; i < 3; i++) { inp[i].type = 1; inp[i].u.ki.wVk = (ushort)keys[i]; }
        for (int i = 0; i < 3; i++) { inp[3 + i].type = 1; inp[3 + i].u.ki.wVk = (ushort)keys[2 - i]; inp[3 + i].u.ki.dwFlags = 2; }
        Native.SendInput(6, inp, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    async Task CheckForUpdateAsync(bool manual)
    {
        if (_updBusy) return;
        _updBusy = true; _checkNow.Enabled = false;
        _updStatus.Text = "Checking for updates…";
        bool autoInstall = false;
        try
        {
            var u = await Updater.CheckAsync();
            _lastCheck = DateTime.Now;
            if (u == null)
            {
                _updAvail = null;
                _updStatus.Text = $"{DisplayVersion} is up to date · checked {DateTime.Now:HH:mm}";
                if (manual) ShowUpdBanner($"Universal-FrameFX {DisplayVersion} is up to date.", false);
                return;
            }
            _updAvail = u;
            _updStatus.Text = $"Version {u.Version} is available · checked {DateTime.Now:HH:mm}";
            Updater.Log($"check: {u.Version} available (running {Version})");
            if (Updater.NeedsManualInstall(u)) { ShowUpdBanner($"Update to {u.Version}: this version can't update itself; download it from the website.", true, "Download"); return; }
            string notes = u.Notes.Length > 0 ? $" — {u.Notes}" : "";
            if (u.Version == _ui.FailedUpdate)
                ShowUpdBanner($"Update to {u.Version} is available, but it did not start correctly last time. Retry?", true, "Retry");
            else
                ShowUpdBanner($"Update to {u.Version}{(notes.Length > 90 ? notes[..90] + "…" : notes)}", true);
            autoInstall = _ui.AutoInstallUpdates && _out == null && u.Version != _ui.FailedUpdate;
        }
        catch (Exception ex)
        {
            _lastCheck = DateTime.Now;
            _updStatus.Text = "Update check failed: " + ex.Message;
            if (manual) ShowUpdBanner("Update check failed: " + ex.Message, false);
        }
        finally { _updBusy = false; _checkNow.Enabled = true; }
        if (autoInstall) await InstallUpdateAsync(true);
    }

    async Task InstallUpdateAsync(bool auto)
    {
        if (_updAvail is not { } u || _updBusy) return;
        if (Updater.NeedsManualInstall(u)) { Open(SiteUrl); return; }
        if (auto && _out != null) return;
        _updBusy = true;
        _updInstall.Enabled = false; _updLater.Enabled = false; _checkNow.Enabled = false;
        try
        {
            if (_out != null) { _restartPending = false; _out.UserStop = true; _out.Close(); }
            ShowUpdBanner($"Downloading {u.Version}…", false);
            var prog = new Progress<double>(f => _updText.Text = $"Downloading {u.Version}… {f * 100:0}%");
            string zip = await Updater.DownloadAsync(u, prog);
            ShowUpdBanner($"Installing {u.Version}; Universal-FrameFX will restart…", false);
            Updater.StartInstaller(zip, u);
            _ui.CaptureFrom(this); SaveUi();
            await Task.Delay(300);
            Close();
        }
        catch (Exception ex)
        {
            Updater.Log($"{u.Version}: install failed: {ex.Message}");
            ShowUpdBanner($"Update to {u.Version} failed: {ex.Message}", true, "Retry");
            if (auto) _updAvail = null;
        }
        finally { _updBusy = false; _updInstall.Enabled = true; _updLater.Enabled = true; _checkNow.Enabled = true; }
    }

    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    void ShowAbout()
    {
        string notice = "NOTICE.md missing";
        try
        {
            var loose = Path.Combine(AppContext.BaseDirectory, "NOTICE.md");
            if (File.Exists(loose)) notice = File.ReadAllText(loose);
            else
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NOTICE.md");
                if (s != null) notice = new StreamReader(s).ReadToEnd();
            }
        }
        catch { }
        var f = new Form { Text = "About Universal-FrameFX", ClientSize = new Size(700, 560), BackColor = BackColor, ForeColor = ForeColor, Font = Font, StartPosition = FormStartPosition.CenterParent };
        var head = new Label
        {
            Dock = DockStyle.Top, Height = 116, Padding = new Padding(10),
            Text = $"Universal-FrameFX {DisplayVersion} ({Version}) — Copyright © 2026 Chopsticks HQ\r\n" +
                   "CSR (Chopsticks Super Resolution) is Universal-FrameFX's own upscaling engine; it contains MIT-licensed code portions © Advanced Micro Devices, Inc. (notice below). The optional AMD FSR and Intel XeSS upscalers run AMD's and Intel's own runtimes, redistributed unmodified under their licences below. " +
                   "Not an AMD or Intel product; not endorsed by AMD or Intel. " +
                   "Uses the NVIDIA Optical Flow runtime included with NVIDIA drivers (interface per NVIDIA's MIT-licensed headers) where available.",
        };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = notice.Replace("\n", "\r\n"), Font = new Font("Cascadia Mono", 8.5f), BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
        Theme.DarkNative(box);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 10, 12, 12), BackColor = Theme.Bg };
        var web = new PillButton { Text = "Website", AutoSize = true, Kind = PillKind.Ghost }; web.Click += (_, _) => Open(SiteUrl);
        var close = new PillButton { Text = "Close", AutoSize = true, Kind = PillKind.Primary }; close.Click += (_, _) => f.Close();
        f.HandleCreated += (_, _) => Theme.DarkChrome(f);
        head.ForeColor = Theme.TextMuted; head.Padding = new Padding(14);
        bar.Controls.AddRange(new Control[] { web, close });
        f.Controls.Add(box); f.Controls.Add(bar); f.Controls.Add(head);
        f.ShowDialog(this);
    }

    Label CpuGroup(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 10, 0, 2) };

    string CurrentCpuGameExe()
    {
        try { ResolveCpuGame(out _, out string exe); return exe ?? ""; }
        catch { return ""; }
    }

    CpuBoostSettings ReadCpuSettings() => CpuBoost.Sanitize(new CpuBoostSettings
    {
        PowerPlan = _cpuPower.Checked,
        MinProcessorState100 = _cpuMin.Checked,
        DisableCoreParking = _cpuPark.Checked,
        BoostMode = _cpuBoost.SelectedIndex is 1 or 2 ? _cpuBoost.SelectedIndex : 0,
        HighPriority = _cpuPrio.Checked,
        PreferFastCores = _cpuFast.Checked,
        AvoidSmtSiblings = _cpuSmt.Checked,
        TimerResolution1ms = _cpuTimer.Checked,
        AllPhysicalCores = _cpuAll.Checked,
        LowerFrameFxPriority = _cpuSelf.Checked,
        DisablePowerThrottling = _cpuThrottle.Checked,
        BackgroundProcesses = CpuBoost.NormalizeBackgroundList(_cpuBg.Text.Split('\n'), CurrentCpuGameExe()),
    });

    void WriteCpuUi(CpuBoostSettings? s)
    {
        s = CpuBoost.Sanitize(s);
        bool prev = _suppressCpu;
        _suppressCpu = true;
        try
        {
            _cpuPower.Checked = s.PowerPlan;
            _cpuMin.Checked = s.MinProcessorState100;
            _cpuPark.Checked = s.DisableCoreParking;
            if (_cpuBoost.Items.Count > 0) _cpuBoost.SelectedIndex = s.BoostMode is 1 or 2 ? s.BoostMode : 0;
            _cpuPrio.Checked = s.HighPriority;
            _cpuFast.Checked = s.PreferFastCores;
            _cpuSmt.Checked = s.AvoidSmtSiblings;
            _cpuTimer.Checked = s.TimerResolution1ms;
            _cpuAll.Checked = s.AllPhysicalCores;
            _cpuSelf.Checked = s.LowerFrameFxPriority;
            _cpuThrottle.Checked = s.DisablePowerThrottling;
            _cpuBg.Text = string.Join(Environment.NewLine, s.BackgroundProcesses);
        }
        finally { _suppressCpu = prev; }
    }

    void CommitCpuList()
    {
        if (_suppressCpu || _suppressProfile) return;
        var list = CpuBoost.NormalizeBackgroundList(_cpuBg.Text.Split('\n'), CurrentCpuGameExe());
        string text = string.Join(Environment.NewLine, list);
        if (!string.Equals(_cpuBg.Text.Replace("\r\n", "\n"), text.Replace("\r\n", "\n"), StringComparison.Ordinal))
        {
            _suppressCpu = true;
            _cpuBg.Text = text;
            _suppressCpu = false;
        }
        OnCpuEdited();
    }

    void OnCpuEdited()
    {
        if (_suppressCpu || _suppressProfile || !_profilesReady) return;
        CpuBoostWin.NotifyUserEdit();
        var snap = CaptureUiProfile();
        ResolveCpuGame(out _, out string fgExe);
        string? exe = ActiveGameExe();
        if (string.IsNullOrEmpty(exe) && fgExe.Length > 0) exe = fgExe;
        if (!string.IsNullOrEmpty(exe) && GameProfiles.ShouldRemember(true, !PersistOk, exe))
        {
            GameProfiles.Remember(_ui.Profiles, exe, snap);
            RebuildProfileList();
        }
        else
        {
            _ui.Cpu = CpuBoost.Sanitize(snap.Cpu);
            _baseline = snap;
        }
        SaveUi();
        _cpuKey = "";
        QueueCpuSync();
    }

    void CpuTick()
    {
        if (_suppressProfile || _suppressCpu || _cpuClosing) return;
        if (!CpuBoost.AnyEnabled(ReadCpuSettings()) && _cpuKey.Length == 0 && _cpuJob == 0) return;
        if (!_ui.GamesOnly && !_gamesArmed)
        {
            try
            {
                var v = _detector.Classify(FgScript != null ? ScriptForeground() : Native.GetForegroundWindow());
                _lastVerdict = v;
                if (v.Pid != 0 && v.Pid != Environment.ProcessId) _lastExternal = v;
            }
            catch { }
        }
        MaybeLoadCpu();
        QueueCpuSync();
    }

    void MaybeLoadCpu()
    {
        if (_cpuBg.ContainsFocus) return;
        ResolveCpuGame(out _, out string exe);
        if (exe.Length == 0)
        {
            _cpuShownExe = "";
            return;
        }
        if (string.Equals(exe, _cpuShownExe, StringComparison.OrdinalIgnoreCase)) return;
        var (settings, _) = GameProfiles.Select(_ui.Profiles, exe, _baseline);
        _cpuShownExe = exe;
        WriteCpuUi(settings.Cpu);
        _cpuKey = "";
    }

    void ResolveCpuGame(out int pid, out string exe)
    {
        pid = 0;
        exe = "";
        try
        {
            if (_lastVerdict is { IsGame: true, Pid: > 0, Exe.Length: > 0 } v)
            {
                pid = v.Pid;
                exe = v.Exe;
                return;
            }
            var o = _out;
            if (o != null && o.Source != IntPtr.Zero)
            {
                var c = _detector.Classify(o.Source);
                if (c.IsGame && c.Pid > 0 && c.Exe.Length > 0) { pid = c.Pid; exe = c.Exe; }
            }
        }
        catch { pid = 0; exe = ""; }
    }

    void QueueCpuSync()
    {
        if (_cpuClosing || _suppressCpu || _suppressProfile) return;
        ResolveCpuGame(out int fgPid, out string fgExe);
        var settings = ReadCpuSettings();
        string key = fgPid + "\n" + fgExe + "\n" + CpuBoost.ToJson(settings);
        long now = Environment.TickCount64;
        if (key == _cpuKey && now - _cpuPollAt < 2000) return;
        if (Interlocked.CompareExchange(ref _cpuJob, 1, 0) != 0)
        {
            _cpuAgain = true;
            return;
        }
        _cpuPollAt = now;
        _cpuKey = key;
        Task.Run(() =>
        {
            CpuBoostStatus st;
            try { st = CpuBoostWin.Sync(fgPid, fgExe, settings); }
            catch { st = new CpuBoostStatus { Notes = { "Could not finish applying CPU options." } }; }
            try
            {
                if (!_cpuClosing && IsHandleCreated)
                    BeginInvoke(new Action(() =>
                    {
                        _cpuStatus.Text = string.Join(Environment.NewLine, CpuBoost.StatusLines(st));
                        Interlocked.Exchange(ref _cpuJob, 0);
                        if (!CpuBoost.AnyEnabled(ReadCpuSettings())) _cpuKey = "";
                        if (_cpuAgain && !_cpuClosing) { _cpuAgain = false; _cpuKey = ""; QueueCpuSync(); }
                    }));
                else Interlocked.Exchange(ref _cpuJob, 0);
            }
            catch { Interlocked.Exchange(ref _cpuJob, 0); }
        });
    }

    async Task RunCpuBenchAsync()
    {
        _cpuBench.Enabled = false;
        _cpuBenchLbl.Text = "Running a quick benchmark…";
        try
        {
            int n = Math.Max(1, Environment.ProcessorCount);
            var score = await Task.Run(() => CpuBench.Measure(n, 3000));
            if (IsDisposed) return;
            if (_benchSingle0 is null || _benchMulti0 is null)
            {
                _benchSingle0 = score.Single;
                _benchMulti0 = score.Multi;
                _cpuBenchLbl.Text = $"Single-core: {score.Single:0}{Environment.NewLine}Multi-core: {score.Multi:0}{Environment.NewLine}Run again after you turn options on to compare.";
            }
            else
            {
                _cpuBenchLbl.Text = "Single-core: " + CpuBench.Compare(_benchSingle0.Value, score.Single)
                    + Environment.NewLine + "Multi-core: " + CpuBench.Compare(_benchMulti0.Value, score.Multi);
            }
        }
        catch
        {
            _cpuBenchLbl.Text = "The benchmark could not finish.";
        }
        finally { if (!IsDisposed) _cpuBench.Enabled = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cpuClosing = true;
        _cpuPoll.Stop();
        try { CpuBoostWin.RestoreSession(); } catch { }
        _ui.CaptureFrom(this);
        SaveUi();
        _out?.Close();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        lock (Gpu.Lock) { _pipe?.Dispose(); _gpu?.Dispose(); }
        base.OnFormClosed(e);
    }
}

public sealed class UiState
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Maximized { get; set; }
    public Dictionary<string, bool> Expanded { get; set; } = new();
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    public bool GamesOnly { get; set; } = true;
    public int SettingsVersion { get; set; }
    public List<string> GamesAlways { get; set; } = new();
    public List<string> GamesNever { get; set; } = new();
    public string FailedUpdate { get; set; } = "";
    public int GpuChoice { get; set; }
    public int FgMultiplier { get; set; } = 4;
    public int FgDefault4 { get; set; }
    public bool LatencyBudget { get; set; } = true;
    public bool Ssgi { get; set; }
    public int SsgiPreset { get; set; }
    public bool SsgiTemporal { get; set; } = global::UniversalFrameFX.Ssgi.TemporalDefault;
    public bool Ssrt { get; set; }
    public int SsrtPreset { get; set; }
    public bool SsrtTemporal { get; set; } = global::UniversalFrameFX.Ssrt.TemporalDefault;
    public bool FgV2 { get; set; }
    public bool FgV2Vsync { get; set; }
    public bool FgV2Extrap { get; set; }
    public bool FgV2LowResGen { get; set; }
    public bool FgV2Cursor { get; set; }
    public int FpsCap { get; set; }
    public bool FixedPacing { get; set; }
    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CpuBoostSettings Cpu { get; set; } = new();

    static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Universal-FrameFX", "ui.json");

    public static UiState Load()
    {
        UiState s = new();
        try { if (File.Exists(PathOf)) s = JsonSerializer.Deserialize<UiState>(File.ReadAllText(PathOf)) ?? new(); } catch { }
        if (s.SettingsVersion < 132) { s.GamesOnly = true; s.SettingsVersion = 132; }
        if (s.FgDefault4 == 0) { if (s.FgMultiplier == 2) s.FgMultiplier = 4; s.FgDefault4 = 1; }
        s.Profiles = GameProfiles.NormalizeMap(s.Profiles);
        s.Cpu = CpuBoost.Sanitize(s.Cpu);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void ApplyTo(Form f)
    {
        if (X == int.MinValue || W < f.MinimumSize.Width || H < f.MinimumSize.Height) return;
        var r = new Rectangle(X, Y, W, H);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X, r.Y, r.Width, 40)))) return;
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = r;
        if (Maximized) f.WindowState = FormWindowState.Maximized;
    }

    public void CaptureFrom(Form f)
    {
        var r = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
        if (f.WindowState == FormWindowState.Minimized && W > 0) { Maximized = false; }
        else Maximized = f.WindowState == FormWindowState.Maximized;
        if (r.Width > 0 && r.Height > 0) { X = r.X; Y = r.Y; W = r.Width; H = r.Height; }
    }
}
