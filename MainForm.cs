using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalFrameFX;

public sealed class MainForm : Form
{
    /// <summary>From the assembly's InformationalVersion (the csproj Version), so a build can be stamped with -p:Version=x.y.z.</summary>
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
    /// <summary>1.3.4 frame-generation multiplier (index into FgMul.Allowed: 2×, 3×, 4×, 8×).</summary>
    readonly DarkCombo _fgMul = new() { Width = 300 };
    /// <summary>SSGI (experimental, off by default).</summary>
    readonly ToggleSwitch _ssgi = new() { Text = "SSGI (experimental)" };
    readonly DarkCombo _ssgiPreset = new() { Width = 300 };
    readonly ToggleSwitch _ssgiSteady = new() { Text = "Steadier SSGI lighting" };
    /// <summary>1.3.4 latency budget toggle (live, saved in ui.json).</summary>
    readonly ToggleSwitch _lowLat = new() { Text = $"Latency budget: keep FrameFX under {LatencyBudget.DefaultMs:0.0} ms per frame" };
    readonly DarkCombo _res = new() { Width = 300 };
    readonly DarkCombo _preset = new() { Width = 300 };
    readonly Label _upNote = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 4, 0, 4) };
    readonly PillButton _coffee = new() { Text = "☕ Buy me a coffee", AutoSize = true, Kind = PillKind.Ghost };
    /// <summary>Dropdown order: our own engine (CSR 1.3, CSR 1.2) first, then the vendor upscalers.</summary>
    static readonly Backend[] BackendOrder = { Backend.Temporal, Backend.Spatial, Backend.Fsr1, Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS, Backend.Bilinear };
    public string? AutoBackend, AutoRes, AutoFgKind, AutoPreset, AutoFgMul;
    /// <summary>--compare-off: this run starts with processing off. A later hotkey toggle is kept across output restarts.</summary>
    public bool CliCompareOff { get => _compareOff; set { if (value) _compareOff = true; } }
    readonly ToggleSwitch _hud = new() { Text = "Performance HUD", Checked = true };
    readonly DarkCombo _mode = new() { Width = 300 };
    readonly DarkCombo _motion = new() { Width = 300 };
    readonly DarkCombo _gpuChoice = new() { Width = 300 };
    readonly Label _gpuNote = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted };
    /// <summary>--gpu on the command line (this run only).</summary>
    public static GpuChoice? CliGpu;
    readonly Label _motionLbl = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Green };
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };
    string _motionAvail = "";
    readonly PillButton _start = new() { Text = "Start", AutoSize = true };
    readonly PillButton _apply = new() { Text = "Apply", AutoSize = true, Kind = PillKind.Primary };
    readonly Label _pending = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Amber, Margin = new Padding(2, 8, 0, 0) };

    // ── 1.3.2 "Apply to games only": capture the foreground game, pause everywhere else.
    readonly GameDetector _detector = new();
    /// <summary>Runs only while games-only is on, so the idle cost is zero when it is off.</summary>
    readonly System.Windows.Forms.Timer _gameTimer = new() { Interval = 500 };   // ~2 polls/s; the detector caches per PID
    readonly PillButton _gamesOnly = new() { Text = "Apply to games only", AutoSize = true, Kind = PillKind.Ghost };
    /// <summary>Games status line in the action bar; visible only while games-only is on.</summary>
    readonly Label _gameStatus = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 2, 0, 0), Visible = false };
    readonly TextBox _gamesAlwaysBox = NewGameListBox();
    readonly TextBox _gamesNeverBox = NewGameListBox();
    readonly Label _gamesListNote = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 6, 0, 0) };
    bool _gamesArmed;                     // games-only on AND the user pressed Apply/Start (or switched it on while running)
    /// <summary>Settings used for a game that has no saved profile. Updated only by explicit edits while no game is the source.</summary>
    GameProfile _baseline = new();
    bool _profilesReady;
    string _profileBanner = "";
    /// <summary>Compare hotkey state for this run (not saved). Restored onto the next output after a restart.</summary>
    bool _compareOff;
    readonly TableLayoutPanel _profileRows = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0), BackColor = Theme.Card };
    readonly Label _profileEmpty = new() { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 4, 0, 2), Text = "No saved profiles yet. They appear when you change settings while a game is running." };
    readonly PillButton _profileResetAll = new() { Text = "Reset all", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 4, 0, 0) };
    readonly List<Label> _profileLabels = new();
    GameDetector.GameVerdict? _lastExternal; // last foreground window that was not FrameFX itself (for "Add current game")
    GameDetector.GameVerdict? _lastVerdict;
    IntPtr _gameHwnd;                     // window the armed output is on
    string _gameLabel = "";
    IntPtr _pendingHwnd;                  // restart target while armed (game switch or Apply)
    string _pendingLabel = "";
    // Test hooks (the test PC may be locked): UFX_GAMELOG=<file>, UFX_FG_SCRIPT="title:secs;..."
    static readonly string? GameLogPath = Environment.GetEnvironmentVariable("UFX_GAMELOG") is { Length: > 0 } p ? p : null;
    static readonly string? FgScript = Environment.GetEnvironmentVariable("UFX_FG_SCRIPT") is { Length: > 0 } s ? s : null;
    static readonly List<(string title, int sec)> ScriptSteps = ParseScript(FgScript);
    int _scriptStep = -1;
    string _lastLoggedVerdict = "", _lastLoggedState = "";
    int _hbTicks;
    bool GamesOnlyOn => _ui.GamesOnly || FgScript != null;

    /// <summary>Settings as last applied to the running output (null = nothing running).</summary>
    sealed record Applied(IntPtr Source, int Backend, int Quality, int Sharp, bool Fg, int Mode, int Motion, int FgKind, int Res, int Preset, int FgMul);
    Applied? _applied;
    bool _restartPending;
    bool _suppressDirty, _suppressMulSave, _suppressProfile;
    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(2, 6, 0, 0) };
    // Updates: banner in the header, settings in the "Updates" section.
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

    // ── Layout: header (top) · scrolling stack of collapsible sections (fill) · pinned action bar (bottom)
    readonly TableLayoutPanel _stack = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(18, 2, 18, 16), BackColor = Theme.Bg };
    readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly List<Label> _wrap = new();
    readonly Dictionary<string, (SectionHeader head, Control body)> _sections = new();
    readonly Label _notice = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Amber, Margin = new Padding(2, 4, 0, 0), Visible = false };
    readonly Label _active = new() { AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(2, 2, 0, 0) };
    UiState _ui = UiState.Load();

    public MainForm()
    {
        // Layout values below are in 96-dpi pixels; WinForms scales them to the monitor DPI (100-200%).
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

        // Header
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8), BackColor = Theme.Bg };
        var title = new Label { Text = "Universal-FrameFX", Font = new Font(Theme.DisplayFamily, 18f, Theme.DisplayFamily == "Segoe UI Semibold" ? FontStyle.Regular : FontStyle.Bold), AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0) };
        var sub = new Label { Text = $"{DisplayVersion} · GPU: {_gpu?.AdapterName ?? "unavailable"}", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(2, 2, 0, 4) };
        _updBar.Controls.Add(_updText); _updBar.Controls.Add(_updInstall); _updBar.Controls.Add(_updLater);
        header.Controls.Add(title); header.Controls.Add(sub); header.Controls.Add(_updBar);

        // Scrolling settings stack (stretches with the window)
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) => Reflow();

        // Source
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

        // Upscaling
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

        // Frame generation
        var fgs = Section("Frame generation", true);
        Add(fgs, _fg);
        _fgKind.Items.Add("FrameFX frame generation");
        _fgKind.Items.Add("AMD FSR 3 frame generation");
        _fgKind.DisabledReason = i => i == (int)FgKind.Fsr3 && !VendorSupport.FsrFrameGen.Ok ? (VendorSupport.Probed ? VendorSupport.FsrFrameGen.Why : "Checking this GPU…") : null;
        _fgKind.SelectedIndex = 0;
        Add(fgs, Stretch(_fgKind));
        _fgMul.Items.Add("2× (1 generated frame per real frame)");
        _fgMul.Items.Add("3× (2 generated frames)");
        _fgMul.Items.Add("4× (3 generated frames, default)");
        // 8× is advanced: shown only with UFX_FG_ADVANCED=1 (or when already selected / passed with --fgx 8).
        if (FgMul.Advanced || FgMul.Clamp(_ui.FgMultiplier) == 8)
            _fgMul.Items.Add("8× (7 generated frames, advanced)");
        _fgMul.SelectedIndex = FgMul.IndexOf(_ui.FgMultiplier);
        Add(fgs, Stretch(_fgMul));
        _lowLat.Checked = _ui.LatencyBudget;
        Add(fgs, _lowLat);
        Add(fgs, Note($"Latency budget: keeps FrameFX's added response time under {LatencyBudget.DefaultMs:0.0} ms (shown as \"Response\" in the HUD) by automatically trading a little quality for speed, and restores quality when there is headroom. Frame generation itself still adds up to about one captured frame of latency."));
        Add(fgs, Note("FrameFX frame generation: FrameFX's own frame generation. AMD FSR 3 frame generation: AMD's frame generation. Either pauses automatically while the source already runs at ≥75% of your refresh rate."));
        Add(fgs, Note("Multiplier: 2×, 3×, 4× (and 8× advanced) multiply the frames you see. Output is capped at your refresh rate and the multiplier is lowered automatically when needed (the HUD shows the effective multiplier). Frame generation never raises the game's real fps, adds about one captured frame of latency, and steps down automatically if the game's fps drops more than 5% while it runs."));
        Add(fgs, Note("Smoother motion when the game runs below half your display's refresh rate; adds about one frame of latency."));

        // Output
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

        // Games (1.3.2, collapsed by default): the "Apply to games only" lists.
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
        Add(games, Note("Game profiles: FrameFX remembers preset, frame generation, SSGI, steadier lighting, upscaler and output resolution for each game, and applies them when that game is detected."));
        _wrap.Add(_profileEmpty);
        _profileRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _profileRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Add(games, _profileEmpty);
        Add(games, _profileRows);
        _profileResetAll.Click += (_, _) => { GameProfiles.ResetAll(_ui.Profiles); SaveUi(); RebuildProfileList(); };
        Add(games, _profileResetAll);
        RebuildProfileList();

        // Advanced (collapsed by default)
        var adv = Section("Advanced", false);
        // SSGI (experimental): live toggle + preset, saved in ui.json.
        _ssgi.Checked = _ui.Ssgi;
        Add(adv, _ssgi);
        foreach (var n in Ssgi.PresetNames) _ssgiPreset.Items.Add("SSGI preset: " + n);
        _ssgiPreset.SelectedIndex = Math.Clamp(_ui.SsgiPreset, 0, Ssgi.PresetNames.Length - 1);
        Add(adv, Stretch(_ssgiPreset));
        _ssgiSteady.Checked = _ui.SsgiTemporal;
        Add(adv, _ssgiSteady);
        Add(adv, Note("Steadier lighting while moving."));
        Add(adv, Note("SSGI (experimental): adds coloured bounce light from bright areas and contact shadows to the captured frame, before upscaling and frame generation. It only sees the 2D image (no depth), so it is an approximation: it can light or darken things a real renderer would not. Estimated GPU cost ~0.9–1.2 ms (GTX 1050 Ti at 1080p / GTX 980 Ti at 1440p presets). Auto picks the 980 Ti settings for 1440p+ on faster GPUs, otherwise the 1050 Ti settings."));
        foreach (var m in Enum.GetValues<MotionPreference>()) _motion.Items.Add(MotionEngines.PrefName(m));
        _motion.SelectedIndex = 0;
        _motion.SelectedIndexChanged += (_, _) => UpdateDirty();
        Add(adv, Note("Motion source:"));
        Add(adv, Stretch(_motion));
        _motionLbl.MaximumSize = Size.Empty; _wrap.Add(_motionLbl);
        Add(adv, _motionLbl);
        Add(adv, Note("The motion source setting applies to the Quality preset. The built-in test scene shows the full-quality result."));
        // 1.3.4 GPU switch.
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

        // Updates (collapsed by default)
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
                if (ActiveGameExe() is null) { _ui.FgMultiplier = FgMul.Allowed[Math.Max(0, _fgMul.SelectedIndex)]; SaveUi(); }
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

        // Pinned action bar: Apply is always visible
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

        // Dock order: fill first, then edges.
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
                _motionLbl.Text = $"Active motion source: {_pipe.MotionSource}";
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
        UpdateDirty();
        _baseline = CaptureUiProfile();
        _profilesReady = true;
        Reflow();
        _updTimer.Tick += (_, _) => UpdateTick();
        _updTimer.Start();
        Diag.Mark("form: ctor done");
    }

    /// <summary>After launch: report an update that just happened (or was rolled back), confirm a good start to the
    /// updater, and run the first background check.</summary>
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
        // UFX_FG_SCRIPT: turn games-only on for this run only (nothing is saved), arm it and
        // step the "foreground" through the scripted windows instead of the real foreground.
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
        // An update found while capturing is installed automatically once capture has stopped.
        if (_updAvail != null && _ui.AutoInstallUpdates && _out == null && _updAvail.Version != _ui.FailedUpdate && !Updater.NeedsManualInstall(_updAvail)) _ = InstallUpdateAsync(true);
    }

    string _motionShort = "";

    // Restore the saved size/position after WinForms' DPI/font auto-scaling has run,
    // otherwise the saved size would be scaled again on every launch.
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
        // UFX_SHOT=path.png: render this window (off-screen, never activated, so nothing on the desktop is disturbed)
        // with PrintWindow(PW_RENDERFULLCONTENT) including the DWM title bar, save it, then quit. Used for docs/site screenshots.
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

    /// <summary>Collapsible section: a rounded card with a clickable header; open/closed state is remembered.</summary>
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

    /// <summary>Re-wraps the text labels to the current width so everything reflows when the window is resized.</summary>
    void Reflow()
    {
        int w = Math.Max(200, _scroll.ClientSize.Width - (int)(80 * DeviceDpi / 96f));
        int bw = Math.Max(200, ClientSize.Width - 40);
        SuspendLayout();
        foreach (var l in _wrap) l.MaximumSize = new Size(l == _pending || l == _status ? bw : w, 0);
        ResumeLayout(true);
    }

    /// <summary>The motion-hardware check (part of the engine) runs in the background so the window appears at once.</summary>
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
        // The hardware motion check is part of the closed engine.
        return ("Motion hardware: checked by the FrameFX engine (not included in this build).", "Motion hardware: engine not included");
    }

    Label Note(string t) { var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 6, 0, 4) }; _wrap.Add(l); return l; }

    void UpdateSharp() => _sharpLbl.Text = $"{_sharp.Value / 100f:0.00} stops";

    Applied Current()
    {
        IntPtr hwnd = (_source.SelectedItem as SourceItem)?.Hwnd ?? IntPtr.Zero;
        int modeIdx = _mode.SelectedIndex;
        if (_gamesArmed) { hwnd = _gameHwnd; modeIdx = (int)OutputMode.Overlay; }   // games mode ignores the Source section
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

    /// <summary>Marks changed-but-unapplied settings: Apply turns bright and lists what is pending.</summary>
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

    /// <summary>Apply: push the chosen settings to the running output, or start it if nothing is running.
    /// Upscaler, sharpness, frame generation and motion source switch live; source, quality and output
    /// mode need a new output window, so the output restarts.</summary>
    void Apply()
    {
        if (_gpu is null || _pipe is null) return;
        if (_out == null)
        {
            if (_gamesArmed) return;   // armed, waiting for a game: there is nothing to apply yet
            Toggle();                  // starts the output — or arms games mode when games-only is on
            return;
        }
        var c = Current(); var a = _applied!;
        bool restart = c.Source != a.Source || c.Quality != a.Quality || c.Mode != a.Mode || c.Res != a.Res || (c.Preset == 2) != (a.Preset == 2);
        UserProfileTouch();
        if (restart)
        {
            _restartPending = true;
            _status.Text = "Applying: restarting output…";
            if (_gamesArmed) { _pendingHwnd = _out.Source; _pendingLabel = _gameLabel; }   // restart the current game output with the new settings
            _out.Close();          // FormClosed handler starts the new output
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
        _settings.FgMultiplier = FgMul.Allowed[Math.Max(0, _fgMul.SelectedIndex)];
        _settings.Motion = (MotionPreference)_motion.SelectedIndex;
        _settings.Performance = _preset.SelectedIndex != 1;
        _settings.LatencyBudget = _lowLat.Checked;
        _settings.Ssgi = _ssgi.Checked;
        _settings.SsgiPreset = Math.Max(0, _ssgiPreset.SelectedIndex);
        _settings.SsgiTemporal = _ssgiSteady.Checked;
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
        if (GamesOnlyOn) { ArmGames(); return; }   // Start while games-only is on arms games mode instead
        if (_gpu is null || _pipe is null || _source.SelectedItem is not SourceItem src) return;
        StartOutput(src.Hwnd, (OutputMode)_mode.SelectedIndex, src.Label);
    }

    /// <summary>Starts the output on the given source with the given mode — the normal path passes the
    /// Source section's choices; games mode passes the game window and always the overlay.</summary>
    void StartOutput(IntPtr hwnd, OutputMode mode, string label)
    {
        if (_gpu is null || _pipe is null) return;
        var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.FromControl(this);
        var res = _preset.SelectedIndex == 2 ? OutputRes.P1080 : (OutputRes)Math.Max(0, _res.SelectedIndex);   // Competitive: 1080p
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
        // While armed, a close that the user asked for (Stop, Ctrl+Alt+Q, Esc) disarms; the game
        // window closing itself does not — we stay armed and wait for the next game. A failed start
        // disarms too, so it is not retried every tick.
        if (_gamesArmed && !_restartPending && (userStop || f.Error != null)) DisarmGames();
        _start.Text = _gamesArmed ? "Stop" : "Start";
        if (_restartPending)
        {
            _restartPending = false;
            BeginInvoke(new Action(() =>
            {
                if (_gamesArmed)
                {
                    // Internal restart (game switch or Apply with changed settings): the overlay goes
                    // back onto the game, paused if the foreground is not a game right now.
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

    // ── Games-only mode (1.3.2)

    void SetGamesOnly(bool on)
    {
        _ui.GamesOnly = on;
        SaveUi();
        GamesOnlyLook(on);
        if (on)
        {
            _gameTimer.Start();
            if (_out != null) ArmGames();   // switched on while an output was already running: armed
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
                    if (_out.Paused) _out.SetPaused(false);   // keep it running on its window as a normal capture
                    _start.Text = "Stop";
                }
                else _start.Text = "Start";
            }
        }
        _gameStatus.Visible = on;
    }

    void GamesOnlyLook(bool on)
    {
        _gamesOnly.Kind = on ? PillKind.Secondary : PillKind.Ghost;   // secondary + amber ring: on, but distinguishable from Apply
        _gamesOnly.Ring = on;
        _gamesOnly.Text = on ? "✓ Apply to games only" : "Apply to games only";
    }

    void ArmGames()
    {
        if (_gamesArmed || !GamesOnlyOn) return;
        _gamesArmed = true;
        _start.Text = "Stop";   // armed, even with no game up yet
        _gameTimer.Start();
        GameTick();             // classify and act immediately instead of waiting for the first tick
    }

    void DisarmGames()
    {
        if (!_gamesArmed) return;
        _gamesArmed = false;
        _gameHwnd = IntPtr.Zero; _gameLabel = "";
        GameLog("state " + GameLogState());
        UpdateGamesStatus();
    }

    /// <summary>Watcher tick (every 500 ms, only while games-only is on): classify the foreground, start,
    /// switch or pause the armed output, refresh the status line and the test log.</summary>
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
                    // A restart already in flight (Apply or a game switch) starts the output from OnOutputClosed.
                    if (!_restartPending)
                    {
                        PrepareGameSettings(v.Exe);
                        _gameHwnd = v.Hwnd; _gameLabel = v.Exe;
                        StartOutput(v.Hwnd, OutputMode.Overlay, v.Exe);
                    }
                }
                else if (o.Source != v.Hwnd)
                {
                    // A different game came to the front: restart the overlay on it (close first, so
                    // the hotkeys are unregistered before the new output registers them).
                    PrepareGameSettings(v.Exe);
                    _pendingHwnd = v.Hwnd; _pendingLabel = v.Exe;
                    _restartPending = true;
                    o.Close();
                }
                else if (o.Paused) o.SetPaused(false);
            }
            else if (_out is { Paused: false } o)
            {
                o.SetPaused(true);   // desktop, browser, normal app or FrameFX itself: pause, keep the output alive
            }
        }
        UpdateGamesStatus(v);

        if (GameLogPath != null)
        {
            string verdict = $"verdict exe={v.Exe} game={v.IsGame} reason={v.Reason}";
            if (verdict != _lastLoggedVerdict) { _lastLoggedVerdict = verdict; GameLog(verdict); }
            string state = GameLogState();
            if (state != _lastLoggedState) { _lastLoggedState = state; GameLog("state " + state); }
            if (++_hbTicks >= 10)   // ~5 s at a 500 ms tick
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
            _gameStatus.Text = "Waiting for a game…";   // desktop, browser, normal app: overlay hidden, no GPU work
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

    /// <summary>UFX_GAMELOG: one timestamped line per change; never throws.</summary>
    static void GameLog(string line)
    {
        if (GameLogPath == null) return;
        try { File.AppendAllText(GameLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {line}\n"); } catch { }
    }

    /// <summary>UFX_FG_SCRIPT stand-in for GetForegroundWindow: the first visible top-level window whose
    /// title contains the step's substring; "desktop" means the shell's desktop window.</summary>
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

    /// <summary>Exe of the game the output is on, or null when the source is not a detected game.
    /// Paused games-only output still counts: the user is editing that game's settings.</summary>
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
        FgMultiplier = _fgMul.SelectedIndex >= 0 && _fgMul.SelectedIndex < FgMul.Allowed.Length
            ? FgMul.Allowed[_fgMul.SelectedIndex]
            : FgMul.Allowed[Math.Clamp(_fgMul.SelectedIndex, 0, FgMul.Allowed.Length - 1)],
        Ssgi = _ssgi.Checked,
        SsgiPreset = Math.Max(0, _ssgiPreset.SelectedIndex),
        SsgiTemporal = _ssgiSteady.Checked,
        Backend = (int)BackendOrder[Math.Clamp(_backend.SelectedIndex, 0, BackendOrder.Length - 1)],
        Res = Math.Max(0, _res.SelectedIndex),
    });

    /// <summary>Store an explicit edit on the active game, or refresh the global baseline when no game is the source.</summary>
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
        var (settings, fromProfile) = GameProfiles.Select(_ui.Profiles, exe, _baseline);
        ApplyProfileToUi(settings);
        _profileBanner = fromProfile ? GameProfiles.AppliedStatus(exe) : "";
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
            int mi = FgMul.IndexOf(GameProfiles.Mul(p.FgMultiplier));
            if (mi >= _fgMul.Items.Count) _fgMul.Items.Add("8× (7 generated frames, advanced)");
            _fgMul.SelectedIndex = Math.Min(mi, _fgMul.Items.Count - 1);
            _ssgi.Checked = p.Ssgi;
            if (_ssgiPreset.Items.Count > 0) _ssgiPreset.SelectedIndex = Math.Clamp(p.SsgiPreset, 0, _ssgiPreset.Items.Count - 1);
            _ssgiSteady.Checked = p.SsgiTemporal;
            if (p.Res >= 0 && p.Res < _res.Items.Count) _res.SelectedIndex = p.Res;
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
        b.Height = (int)(b.Font.Height * 4.6f);   // about four lines
        Theme.DarkNative(b);
        return b;
    }

    /// <summary>Trim, drop empty lines, append ".exe" where missing, dedupe case-insensitively.</summary>
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

    /// <summary>Parse the two list boxes and push them into the detector (on TextChanged); on Leave also
    /// rewrite the normalized text and save.</summary>
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

    /// <summary>"Add current game" / "Never treat current app as game": the exe of the last foreground
    /// window that was not FrameFX itself, as tracked by the watcher.</summary>
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

    /// <summary>--demo: start output (test scene, or --source "title"); --overlay / --window; --motion auto|nvof|d3d12|sw;
    /// --fg; --exit-after N --out file: log status and quit. With --overlay and a source, also checks that the
    /// source keeps focus, that clicks pass through, and the hotkeys.</summary>
    public void Automate(bool demo, int exitAfterSec, string? outPath, string? sourceTitle = null,
                         string? mode = null, string? motion = null, bool fg = false)
    {
        var checks = new List<string>();
        IntPtr srcHwnd = IntPtr.Zero;
        if (demo)
            Shown += (_, _) =>
            {
                _mode.SelectedIndex = (int)(mode == "overlay" ? OutputMode.Overlay : mode == "fullscreen" ? OutputMode.Fullscreen : OutputMode.Window);
                _motion.SelectedIndex = (int)(motion switch { "nvof" => MotionPreference.Nvof, "d3d12" => MotionPreference.D3D12, "sw" => MotionPreference.Software, _ => MotionPreference.Auto });
                _fg.Checked = fg;
                if (AutoBackend is { } ab)
                {
                    var want = BackendNames.Parse(ab);
                    if (Backends.IsVendor(want) || want == Backend.Fsr1) VendorSupport.Probe(_gpu!);
                    _backend.SelectedIndex = Array.IndexOf(BackendOrder, want);
                }
                if (AutoPreset is { } apr) _preset.SelectedIndex = apr.StartsWith("q", StringComparison.OrdinalIgnoreCase) ? 1 : apr.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                if (AutoRes is { } ar) _res.SelectedIndex = (int)(ar.ToLowerInvariant() switch { "1080" or "1080p" => OutputRes.P1080, "1440" or "1440p" => OutputRes.P1440, "4k" or "2160" => OutputRes.P2160, "source" => OutputRes.Source, _ => OutputRes.Auto });
                if (AutoFgKind is "fsr3") { VendorSupport.Probe(_gpu!); _fgKind.SelectedIndex = (int)FgKind.Fsr3; }
                if (AutoFgMul is { } fx && int.TryParse(fx.TrimEnd('x', 'X'), out var fxn)) { _suppressMulSave = true; int mi = FgMul.IndexOf(fxn); if (mi >= _fgMul.Items.Count) _fgMul.Items.Add("8× (7 generated frames, advanced)"); _fgMul.SelectedIndex = Math.Min(mi, _fgMul.Items.Count - 1); _suppressMulSave = false; }
                _source.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(sourceTitle))
                    for (int i = 1; i < _source.Items.Count; i++)
                        if (_source.Items[i]!.ToString()!.Contains(sourceTitle, StringComparison.OrdinalIgnoreCase)) { _source.SelectedIndex = i; break; }
                srcHwnd = (_source.SelectedItem as SourceItem)?.Hwnd ?? IntPtr.Zero;
                // Hand focus to the source first (as if the user clicked back into it), then start.
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
                             $"Requested: backend={SelectedBackend} res={(OutputRes)_res.SelectedIndex} fgkind={(FgKind)_fgKind.SelectedIndex} fgx={FgMul.Allowed[Math.Max(0, _fgMul.SelectedIndex)]} preset={(_preset.SelectedIndex == 1 ? "quality" : _preset.SelectedIndex == 2 ? "competitive" : "performance")}\n" +
                             $"HUD:\n{_out?.HudText}\n" + (checks.Count > 0 ? "Overlay checks:\n" + string.Join("\n", checks) + "\n" : "");
                if (outPath != null) File.WriteAllText(outPath, report);
                if (_out != null && mode == "overlay" && srcHwnd != IntPtr.Zero)
                {
                    // Stop with the hotkey, as a user would.
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
                    // A real click through the overlay.
                    Native.SetCursorPos(cx, cy);
                    var inp = new Native.INPUT[2];
                    inp[0].type = 0; inp[0].u.mi.dwFlags = 0x0002;   // left down
                    inp[1].type = 0; inp[1].u.mi.dwFlags = 0x0004;   // left up
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

    /// <summary>Stop capture, download + verify, hand over to the updater and close. Auto mode never interrupts capture.</summary>
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
            if (auto) _updAvail = null;   // don't loop; the next scheduled check tries again
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _ui.CaptureFrom(this);
        SaveUi();   // tests don't overwrite the user's layout
        _out?.Close();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        lock (Gpu.Lock) { _pipe?.Dispose(); _gpu?.Dispose(); }
        base.OnFormClosed(e);
    }
}

/// <summary>Remembered window size/position and expanded sections (%APPDATA%\Universal-FrameFX\ui.json).</summary>
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
    /// <summary>1.3.2 "Apply to games only": capture the foreground game, pause everywhere else.</summary>
    public bool GamesOnly { get; set; } = true;
    /// <summary>Settings schema: below 132 means written by 1.3.1 or an early 1.3.2 test build; Load() then
    /// switches GamesOnly on once (the 1.3.2 default) and stamps 132.</summary>
    public int SettingsVersion { get; set; }
    /// <summary>Exe names always treated as games (one per line in the Games section).</summary>
    public List<string> GamesAlways { get; set; } = new();
    /// <summary>Exe names never treated as games.</summary>
    public List<string> GamesNever { get; set; } = new();
    /// <summary>A version whose install was rolled back: never auto-installed again (Install still works).</summary>
    public string FailedUpdate { get; set; } = "";
    /// <summary>1.3.4 GPU switch (GpuChoice index: 0 Auto, 1 Integrated, 2 Dedicated).</summary>
    public int GpuChoice { get; set; }
    /// <summary>1.3.4 frame-generation multiplier (2, 3, 4 or 8; anything else reads as 2). Default 4×.</summary>
    public int FgMultiplier { get; set; } = 4;
    /// <summary>1.3.4: 1 once the ×2 → ×4 default change has been applied to this ui.json.</summary>
    public int FgDefault4 { get; set; }
    /// <summary>1.3.4 latency budget (default on).</summary>
    public bool LatencyBudget { get; set; } = true;
    /// <summary>SSGI (experimental): off by default; preset 0 Auto, 1 GTX 1050 Ti, 2 GTX 980 Ti.</summary>
    public bool Ssgi { get; set; }
    public int SsgiPreset { get; set; }
    /// <summary>Steadier lighting while moving (with SSGI).</summary>
    public bool SsgiTemporal { get; set; } = global::UniversalFrameFX.Ssgi.TemporalDefault;
    /// <summary>Per-game profiles, keyed by lowercase exe name.</summary>
    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Universal-FrameFX", "ui.json");

    public static UiState Load()
    {
        UiState s = new();
        try { if (File.Exists(PathOf)) s = JsonSerializer.Deserialize<UiState>(File.ReadAllText(PathOf)) ?? new(); } catch { }
        if (s.SettingsVersion < 132) { s.GamesOnly = true; s.SettingsVersion = 132; }
        if (s.FgDefault4 == 0) { if (s.FgMultiplier == 2) s.FgMultiplier = 4; s.FgDefault4 = 1; }   // old default 2× -> new default 4×
        s.Profiles = GameProfiles.NormalizeMap(s.Profiles);
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
        // Only restore if the title bar would be on a connected screen.
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
