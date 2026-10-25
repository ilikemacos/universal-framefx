using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace UniversalFrameFX;

public sealed partial class MainForm
{
    readonly List<ToggleSwitch> _toggles = new();

    const string MoreSrc =
        "Windows are captured with Windows.Graphics.Capture at their native size, then upscaled to the output resolution you pick below (up to 4×; vendor upscalers up to 3×).";
    const string MoreRes1440 =
        "1440p / 4K with the overlay: the overlay covers the source's monitor and shows the upscaled image letterboxed; it stays click-through, so clicks reach whatever is underneath at its real position. 4K on a smaller monitor is rendered at 4K and scaled to fit.";
    const string MoreMode =
        "Overlay: a click-through, always-on-top window over the source that follows it; the source keeps mouse and keyboard focus. Ctrl+Alt+F hides/shows it, Ctrl+Alt+Q stops, Ctrl+Alt+C compares the picture with FrameFX off. Separate/fullscreen output: Esc stops, Ctrl+Alt+C compares.";
    const string MoreFgMethod =
        "CSR 1.3 frame generation (classic): FrameFX's own classic frame generation. CSR 2.0 frame generation: FrameFX's newer engine, with the options below. CSR Flux (AI, experimental): FrameFX's AI frame generation, still experimental. AMD FSR 3 frame generation: AMD's own. All pause automatically while the source already runs at ≥75% of your refresh rate.";
    const string MoreFgMul =
        "Multiplier: 2×, 3×, 4× (and 8× advanced) multiply the frames you see. CSR 2.0 also lists 5×, 6×, 10× and 20×. Output is capped at your refresh rate and the multiplier is lowered automatically when needed (the HUD shows the effective multiplier). Frame generation never raises the game's real fps, adds about one captured frame of latency, and steps down automatically if the game's fps drops more than 5% while it runs.";
    const string MoreLowLat =
        "Latency budget: keeps FrameFX's added response time under {0} ms (shown as \"Response\" in the HUD) by automatically trading a little quality for speed, and restores quality when there is headroom. Frame generation itself still adds up to about one captured frame of latency.";
    const string MoreVsync =
        "Smoother motion when the game runs below half your display's refresh rate; adds about one frame of latency.";
    const string MoreGames =
        "When \"Apply to games only\" is on, FrameFX captures the foreground game with the overlay and pauses on the desktop, browsers and normal apps: the overlay is hidden and no GPU work is done. A game is a fullscreen or borderless-fullscreen D3D/Vulkan/OpenGL window, or a game started from Steam, Epic, Xbox/Game Pass, GOG, Battle.net, Riot, EA or Ubisoft. The lists below override that.";
    const string MoreProfiles =
        "Game profiles: FrameFX remembers preset, frame generation, SSGI, steadier lighting, ray-traced lighting, upscaler, output resolution and CPU options for each game, and applies them when that game is detected.";
    const string MoreCpu =
        "Get more performance out of your CPU for games. This can't make your CPU faster than its hardware allows; results depend on the game and your PC. Everything is undone when the game closes, when FrameFX closes, or with Restore defaults.";
    const string MoreSsgi =
        "SSGI (experimental): adds coloured bounce light from bright areas and contact shadows to the captured frame, before upscaling and frame generation. It only sees the 2D image (no depth), so it is an approximation: it can light or darken things a real renderer would not. Estimated GPU cost ~0.9–1.2 ms (GTX 1050 Ti at 1080p / GTX 980 Ti at 1440p presets). Auto picks the 980 Ti settings for 1440p+ on faster GPUs, otherwise the 1050 Ti settings.";
    const string MoreSsrt =
        "Ray-traced lighting (experimental) is off unless you turn it on. It adds extra light, reflections and contact shadows, including on cards without hardware ray tracing such as the GTX 1050 Ti and GTX 980 Ti. While it is on, it is used instead of SSGI. Auto picks lighter settings at 1080p and on a GTX 1050 Ti or a slower card.";
    const string MoreMotion =
        "The motion source setting applies to the Quality preset. The built-in test scene shows the full-quality result.";
    const string MoreGpu =
        "Processing on a GPU that does not drive the game's display adds a copy between GPUs for every frame (shown in the overlay HUD). The integrated GPU keeps FrameFX off the GPU the game uses, but it is much slower.";
    const string MoreLogs =
        "Crash reports (crash.log) and the startup timing trace (startup.log) are written to the logs folder; nothing is sent anywhere.";
    const string MoreUpdates =
        "Checks chopstickshq.com on launch and every 6 hours. Updates download over HTTPS, are verified against their published SHA-256 checksum before anything is installed, and the previous version is kept so it can be restored automatically if the new one fails to start.";

    void BuildLayout()
    {
        MinimumSize = new Size(720, 520);
        ClientSize = new Size(920, 700);
        _stack.Padding = new Padding(0);
        _stack.ColumnStyles.Clear();
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) => Reflow();
        _scroll.ClientSizeChanged += (_, _) => Reflow();

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 224,
            ColumnCount = 1,
            BackColor = Theme.Side,
            Padding = new Padding(Theme.Space.M, Theme.Space.Xxl, Theme.Space.M, Theme.Space.L),
            TabIndex = 0,
        };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        side.RowCount = 4;
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var name = new Label
        {
            Text = "Universal-FrameFX",
            Font = Theme.Semibold(11.5f),
            AutoSize = true,
            ForeColor = Theme.Text,
            TabStop = false,
            Margin = new Padding(Theme.Space.M, 0, 0, 0),
        };
        var sub = new Label
        {
            Text = $"{DisplayVersion} · {_gpu?.AdapterName ?? "unavailable"}",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.Small(),
            TabStop = false,
            Margin = new Padding(Theme.Space.M, Theme.Space.Xs, 0, Theme.Space.L),
        };
        var sideRule = new Rule { Quiet = true, Margin = new Padding(0, 0, 0, Theme.Space.S) };
        _nav.Dock = DockStyle.Fill;
        _nav.Add(new NavList.Item("home", "Home"));
        _nav.Add(new NavList.Item("upscaling", "Upscaling"));
        _nav.Add(new NavList.Item("frame-generation", "Frame generation"));
        _nav.Add(new NavList.Item("games", "Games"));
        _nav.Add(new NavList.Item("cpu", "CPU"));
        _nav.Add(new NavList.Item("advanced", "Advanced"));
        _nav.Add(new NavList.Item("about", "About"));
        _nav.SelectedChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0 && _nav.SelectedIndex < _nav.Items.Count)
                ShowPage(_nav.Items[_nav.SelectedIndex].Key);
        };
        _nav.EnterPressed += (_, _) => FocusPageFirst();
        side.Controls.Add(name, 0, 0);
        side.Controls.Add(sub, 0, 1);
        side.Controls.Add(sideRule, 0, 2);
        side.Controls.Add(_nav, 0, 3);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, TabIndex = 1 };
        _updBar.Dock = DockStyle.Top;
        _updBar.Controls.Add(_updText);
        _updBar.Controls.Add(_updInstall);
        _updBar.Controls.Add(_updLater);
        content.Controls.Add(_scroll);
        content.Controls.Add(_updBar);

        BuildHome();
        BuildUpscaling();
        BuildFg();
        BuildGames();
        BuildCpu();
        BuildAdvanced();
        BuildAbout();

        _bar.Dock = DockStyle.Bottom;
        _bar.AutoSize = true;
        _bar.ColumnCount = 1;
        _bar.Padding = new Padding(Theme.Space.Xxl, Theme.Space.M + 2, Theme.Space.Xxl, Theme.Space.L);
        _bar.BackColor = Theme.Bg;
        _bar.TabIndex = 2;
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bar.Paint += (_, e) => { using var p = new Pen(Theme.Rule); e.Graphics.DrawLine(p, 0, 0, _bar.Width, 0); };
        var barRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Bg };
        barRow.Controls.Add(_apply);
        _bar.Controls.Add(barRow);
        _pending.MaximumSize = Size.Empty;
        _status.MaximumSize = Size.Empty;
        _active.Font = Theme.Small();
        _pending.Font = Theme.Small();
        _status.Font = Theme.Small();
        _barWarn.Font = Theme.Small();
        _wrap.Add(_pending); _wrap.Add(_status); _wrap.Add(_barWarn); _wrap.Add(_notice); _wrap.Add(_fgWarn); _wrap.Add(_csr2Only);
        _bar.Controls.Add(_active);
        _bar.Controls.Add(_pending);
        _bar.Controls.Add(_status);
        _bar.Controls.Add(_barWarn);
        _bar.Resize += (_, _) => Reflow();

        Controls.Add(content);
        Controls.Add(_bar);
        Controls.Add(side);

        _refreshBtn.Click += (_, _) => RefreshSources();
        _addGame.Click += (_, _) => AddToGameList(always: true);
        _neverGame.Click += (_, _) => AddToGameList(always: false);
        _viewAc.Click += (_, _) => ShowAcList();
        _gpuRestart.Click += (_, _) => { _out?.Close(); _ui.CaptureFrom(this); SaveUi(); Application.Restart(); };
        _logsBtn.Click += (_, _) => { try { Directory.CreateDirectory(Diag.Dir); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Diag.Dir + "\"") { UseShellExecute = true }); } catch { } };
        _aboutBtn.Click += (_, _) => ShowAbout();
        _barWarn.Click += (_, _) => ShowPage("home");

        _aboutBtn.Kind = PillKind.Secondary;
        _checkNow.Kind = PillKind.Secondary;
        _logsBtn.Kind = PillKind.Secondary;
        _coffee.Kind = PillKind.Secondary;
        _cpuBench.Kind = PillKind.Secondary;
        _cpuDefaults.Kind = PillKind.Secondary;
        _refreshBtn.Kind = PillKind.Secondary;
        _gpuRestart.Kind = PillKind.Secondary;
        _lowLat.Text = "Latency budget";
        _start.MinimumSize = new Size(160, 44);
        _start.AutoSize = true;
        _start.Size = _start.GetPreferredSize(Size.Empty);
        _gamesOnly.AutoSize = true;
        _gamesOnly.Size = _gamesOnly.GetPreferredSize(Size.Empty);
        _fgSelect.SelectedIndexChanged += (_, _) => { SyncRows(); UpdateFgWarn(); UpdateHome(); };
        _fgMul.SelectedIndexChanged += (_, _) => { UpdateFgWarn(); UpdateHome(); };
        _backend.SelectedIndexChanged += (_, _) => UpdateHome();
        _preset.SelectedIndexChanged += (_, _) => UpdateHome();
        _source.SelectedIndexChanged += (_, _) => UpdateHome();
        _ssrt.CheckedChanged += (_, _) => SyncRows();
        CollectToggles();
        UpdateGpuNote();
        SyncRows();
        UpdateFgWarn();
        string? envPage = Environment.GetEnvironmentVariable("UFX_UI_PAGE");
        _pageFromEnv = envPage is { Length: > 0 };
        ShowPage(_pageFromEnv ? envPage! : (_ui.Page.Length > 0 ? _ui.Page : "home"));
        if (Environment.GetEnvironmentVariable("UFX_UI_OPEN") == "1")
        {
            foreach (var (head, body) in _sections.Values)
            {
                body.Visible = true;
                head.Open = true;
            }
            OpenMore(this);
        }
        UpdateHome();
    }

    static void OpenMore(Control c)
    {
        if (c is MoreInfo m) m.Open = true;
        foreach (Control ch in c.Controls) OpenMore(ch);
    }

    PageView Page(string key, string title, string? subtitle)
    {
        var p = new PageView(key, title, subtitle);
        p.Visible = false;
        _pages[key] = p;
        _stack.Controls.Add(p);
        foreach (Control c in p.Controls)
            if (c is Label l && l.MaximumSize.Width > 0) _wrap.Add(l);
        return p;
    }

    SettingRow Row(Control c, string? label, string? one, string? more)
    {
        Stretch(c);
        var r = new SettingRow(c, label, one, more);
        foreach (Control x in r.Controls)
        {
            if (x is Label l && l.MaximumSize.Width > 0) _wrap.Add(l);
            if (x is MoreInfo m) _wrap.Add(m.Body);
        }
        return r;
    }

    static TableLayoutPanel Pair(Control a, Control b)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0), Padding = Padding.Empty, BackColor = Theme.Bg };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        a.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        a.Margin = new Padding(0, 0, Theme.Space.S, 0);
        b.Anchor = AnchorStyles.Left;
        b.Margin = new Padding(0);
        t.Controls.Add(a, 0, 0);
        t.Controls.Add(b, 1, 0);
        return t;
    }

    static Control BtnRow(params Control[] cs)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = Padding.Empty,
            BackColor = Theme.Bg,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        Label? trailing = null;
        var buttons = new List<Control>();
        foreach (var c in cs)
        {
            if (c is Label l && trailing == null) trailing = l;
            else buttons.Add(c);
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            var c = buttons[i];
            if (c is PillButton pb)
            {
                pb.AutoSize = true;
                pb.Anchor = AnchorStyles.Left;
                pb.Size = pb.GetPreferredSize(Size.Empty);
            }
            int gap = i < buttons.Count - 1 ? Theme.Space.M : 0;
            c.Margin = new Padding(0, 0, gap, 0);
            f.Controls.Add(c);
        }
        if (trailing == null) return f;
        var t = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
            Padding = Padding.Empty,
            BackColor = Theme.Bg,
            TabStop = false,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        trailing.Anchor = AnchorStyles.Left;
        trailing.Margin = new Padding(0, Theme.Space.Xs, 0, 0);
        t.Controls.Add(f, 0, 0);
        t.Controls.Add(trailing, 0, 1);
        return t;
    }

    static void FlushLeft(Control c, int bottom = 0)
    {
        c.Margin = new Padding(0, 0, 0, bottom);
        c.Padding = Padding.Empty;
        if (c is ToggleSwitch) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    Label Line(string t, Color? c = null)
    {
        var l = new Label
        {
            Text = t,
            AutoSize = true,
            MaximumSize = new Size(200, 0),
            ForeColor = c ?? Theme.TextMuted,
            Font = Theme.Small(),
            TabStop = false,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, Theme.Space.M),
        };
        _wrap.Add(l);
        return l;
    }

    void BuildHome()
    {
        var home = Page("home", "Home", null);
        _building = home;
        _start.Kind = PillKind.Primary;
        _start.AutoSize = true;
        _start.Anchor = AnchorStyles.Left;
        _start.Margin = new Padding(0, 0, Theme.Space.M, 0);
        _start.Size = _start.GetPreferredSize(Size.Empty);
        _gamesOnly.Kind = PillKind.Secondary;
        _gamesOnly.AutoSize = true;
        _gamesOnly.Anchor = AnchorStyles.Left;
        _gamesOnly.Margin = new Padding(0);
        _gamesOnly.Size = _gamesOnly.GetPreferredSize(Size.Empty);
        var power = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, Theme.Space.S),
            Padding = Padding.Empty,
            BackColor = Theme.Bg,
        };
        power.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        power.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        power.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        power.Controls.Add(_start, 0, 0);
        power.Controls.Add(_gamesOnly, 1, 0);
        home.Controls.Add(power);
        _gameStatus.Font = Theme.Small();
        _gameStatus.ForeColor = Theme.TextMuted;
        _gameStatus.Padding = Padding.Empty;
        _gameStatus.Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.S);
        _gameStatus.MaximumSize = new Size(200, 0);
        _gameStatus.AutoSize = true;
        _wrap.Add(_gameStatus);
        home.Controls.Add(_gameStatus);
        home.Controls.Add(Line("Start captures the source below. With Games only on, FrameFX waits for a game and pauses everywhere else."));
        var src = home.Group("Source");
        src.Controls.Add(Row(Pair(_source, _refreshBtn), "Capture source", "Window to capture. Ignored while Games only is on.", MoreSrc));
        var now = home.Group("Now");
        var stats = new StatGrid();
        _homeGame = stats.Add("Game");
        _homeUp = stats.Add("Upscaler");
        _homeFg = stats.Add("Frame generation");
        _homeFps = stats.Add("Output / game fps");
        _homeLat = stats.Add("Added latency");
        _homeAc = stats.Add("Anti-cheat safe mode");
        _homeMotion = stats.Add("Motion hardware");
        foreach (var l in new[] { _homeGame, _homeUp, _homeFg, _homeFps, _homeLat, _homeAc, _homeMotion }) _wrap.Add(l);
        _acTip.SetToolTip(_homeAc, "");
        now.Controls.Add(stats);
        _homeWarn = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
            Visible = false,
            BackColor = Theme.Bg,
        };
        _homeWarn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _homeWarn.Controls.Add(PageView.GroupRule());
        _homeWarn.Controls.Add(new GroupLabel("Warnings"));
        FlushLeft(_notice, Theme.Space.S);
        _notice.Font = Theme.Small();
        _homeWarn.Controls.Add(_notice);
        home.Controls.Add(_homeWarn);
        _building = null;
    }

    void BuildUpscaling()
    {
        var p = Page("upscaling", "Upscaling", "Render less, show more: the captured window is upscaled to the output resolution.");
        _building = p;
        var up = p.Group("Upscaler");
        up.Controls.Add(Row(_backend, "Upscaler", null, null));
        _upNote.MaximumSize = new Size(200, 0);
        _upNote.Font = Theme.Small();
        FlushLeft(_upNote, Theme.Space.M);
        _upNote.Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.M);
        _wrap.Add(_upNote);
        up.Controls.Add(_upNote);
        up.Controls.Add(Row(_preset, "Preset", "Performance is fastest, Quality looks best, Competitive overrides the other choices.", null));
        up.Controls.Add(Row(Pair(_sharp, _sharpLbl), "Sharpness", "0 is sharpest.", null));
        var output = p.Group("Output");
        output.Controls.Add(Row(_mode, "Output mode", "Overlay draws over the game and lets clicks through.", MoreMode));
        output.Controls.Add(Row(_res, "Output resolution", "Size to upscale to. Auto matches the source's monitor.", MoreSrc + "\n\n" + MoreRes1440));
        output.Controls.Add(Row(_hud, null, "Shows fps, latency and the multiplier on the output.", null));
        _building = null;
    }

    void BuildFg()
    {
        var p = Page("frame-generation", "Frame generation", "Shows extra generated frames between real ones. Never raises the game's own fps.");
        _building = p;
        var basics = _fgBasics = p.Group("Basics");
        basics.Controls.Add(Row(_fgSelect, "Method", "FrameFX's own engines or AMD FSR 3. Pauses while the game already runs near your refresh rate.", MoreFgMethod));
        basics.Controls.Add(Row(_fgMul, "Multiplier", "Frames shown per real frame, capped at your refresh rate.", MoreFgMul));
        FlushLeft(_fgWarn, Theme.Space.S);
        _fgWarn.Font = Theme.Small();
        basics.Controls.Add(_fgWarn);
        var adv = Section("Frame generation · Advanced", false);
        var advHead = _sections["Frame generation · Advanced"].head;
        advHead.Text = "Advanced";
        advHead.AccessibleName = "Advanced section";
        var rVsync = Row(_fgV2Vsync, null, "Evens out frame timing. Adds about one frame of latency.", MoreVsync);
        var rExtrap = Row(_fgV2Extrap, null, "Lower latency: predicts frames instead of waiting. Not available at 20×.", null);
        var rLow = Row(_fgV2LowRes, null, "Generates at a lower resolution to save GPU time.", null);
        var rCur = Row(_fgV2Cursor, null, "Draws the mouse cursor on generated frames.", null);
        adv.Controls.Add(rVsync);
        adv.Controls.Add(rExtrap);
        adv.Controls.Add(rLow);
        adv.Controls.Add(rCur);
        _csr2Only.Text = "More options appear with CSR 2.0 frame generation.";
        _csr2Only.Font = Theme.Small();
        FlushLeft(_csr2Only, Theme.Space.S);
        adv.Controls.Add(_csr2Only);
        _rowVis.Add((rVsync, () => Csr20On));
        _rowVis.Add((rExtrap, () => Csr20On));
        _rowVis.Add((rLow, () => Csr20On));
        _rowVis.Add((rCur, () => Csr20On));
        adv.Controls.Add(Row(_lowLat, null, $"Keeps FrameFX under {LatencyBudget.DefaultMs:0.0} ms per frame by trading a little quality.", string.Format(MoreLowLat, $"{LatencyBudget.DefaultMs:0.0}")));
        adv.Controls.Add(Row(_fixedPace, null, "Presents at a fixed interval instead of following the game.", null));
        adv.Controls.Add(Row(_fpsCap, "Output fps cap", "Limits generated output fps. Off follows your refresh rate.", null));
        _building = null;
    }

    void BuildGames()
    {
        var p = Page("games", "Games", "What counts as a game for Games only, anti-cheat safe mode, and per-game profiles.");
        _building = p;
        p.Controls.Add(Line("Games only captures the foreground game and pauses on the desktop and normal apps."));
        var moreG = new MoreInfo(MoreGames);
        _wrap.Add(moreG.Body);
        p.Controls.Add(moreG);
        var det = p.Group("Game detection");
        det.Controls.Add(Row(_gamesAlwaysBox, "Always treat as game", "One .exe per line.", null));
        det.Controls.Add(Row(_gamesNeverBox, "Never treat as game", "One .exe per line.", null));
        _gamesListNote.Font = Theme.Small();
        FlushLeft(_gamesListNote);
        _wrap.Add(_gamesListNote);
        det.Controls.Add(BtnRow(_addGame, _neverGame, _gamesListNote));
        var ac = p.Group("Anti-cheat safe mode");
        ac.Controls.Add(Line("In protected games FrameFX still draws its overlay but leaves the game process alone (no CPU tweaks)."));
        ac.Controls.Add(Row(_acAlwaysBox, "Always treat as protected", "One .exe per line.", null));
        ac.Controls.Add(Row(_acNeverBox, "Not protected", "One .exe per line.", null));
        FlushLeft(_viewAc);
        ac.Controls.Add(_viewAc);
        var prof = p.Group("Profiles");
        prof.Controls.Add(Line("Settings are remembered per game and applied when it is detected."));
        var moreP = new MoreInfo(MoreProfiles);
        _wrap.Add(moreP.Body);
        prof.Controls.Add(moreP);
        _profileEmpty.Font = Theme.Small();
        _profileEmpty.MaximumSize = new Size(200, 0);
        FlushLeft(_profileEmpty, Theme.Space.S);
        _wrap.Add(_profileEmpty);
        _profileRows.BackColor = Theme.Bg;
        FlushLeft(_profileRows);
        FlushLeft(_profileResetAll);
        prof.Controls.Add(_profileEmpty);
        prof.Controls.Add(_profileRows);
        prof.Controls.Add(_profileResetAll);
        _building = null;
    }

    void BuildCpu()
    {
        var p = Page("cpu", "CPU", "Optional, reversible CPU help for the detected game. Everything is off until you turn it on.");
        _building = p;
        var more = new MoreInfo(MoreCpu);
        _wrap.Add(more.Body);
        p.Controls.Add(more);
        var power = p.Group("Power");
        FlushLeft(_cpuPower, Theme.Space.S); power.Controls.Add(_cpuPower);
        FlushLeft(_cpuMin, Theme.Space.S); power.Controls.Add(_cpuMin);
        FlushLeft(_cpuPark, Theme.Space.S); power.Controls.Add(_cpuPark);
        power.Controls.Add(Row(_cpuBoost, "Processor boost", "May ask for admin.", null));
        FlushLeft(_cpuThrottle, Theme.Space.S); power.Controls.Add(_cpuThrottle);
        var single = p.Group("Single-core");
        FlushLeft(_cpuPrio, Theme.Space.S); single.Controls.Add(_cpuPrio);
        FlushLeft(_cpuFast, Theme.Space.S); single.Controls.Add(_cpuFast);
        FlushLeft(_cpuSmt, Theme.Space.S); single.Controls.Add(_cpuSmt);
        FlushLeft(_cpuTimer, Theme.Space.S); single.Controls.Add(_cpuTimer);
        var multi = p.Group("Multi-core");
        FlushLeft(_cpuAll, Theme.Space.S); multi.Controls.Add(_cpuAll);
        FlushLeft(_cpuSelf, Theme.Space.S); multi.Controls.Add(_cpuSelf);
        var bg = p.Group("Background apps");
        bg.Controls.Add(Row(_cpuBg, "Slow down while gaming", "One .exe per line.", null));
        var st = p.Group("Status");
        st.Controls.Add(BtnRow(_cpuBench, _cpuDefaults));
        _cpuBenchLbl.Font = Theme.Small();
        _cpuBenchLbl.MaximumSize = new Size(200, 0);
        FlushLeft(_cpuBenchLbl, Theme.Space.S);
        _wrap.Add(_cpuBenchLbl);
        _cpuStatus.Font = Theme.Small();
        _cpuStatus.MaximumSize = new Size(200, 0);
        FlushLeft(_cpuStatus, Theme.Space.S);
        _wrap.Add(_cpuStatus);
        st.Controls.Add(_cpuBenchLbl);
        st.Controls.Add(_cpuStatus);
        _building = null;
    }

    void BuildAdvanced()
    {
        var p = Page("advanced", "Advanced", "Experimental lighting, motion source, GPU choice.");
        _building = p;
        var lit = p.Group("Lighting (experimental)");
        lit.Controls.Add(Row(_ssgi, null, "Adds bounce light and contact shadows from the 2D image. About 1 ms of GPU time.", MoreSsgi));
        lit.Controls.Add(Row(_ssgiPreset, "SSGI preset", null, null));
        lit.Controls.Add(Row(_ssgiSteady, null, "Steadier lighting while moving.", null));
        lit.Controls.Add(Row(_ssrt, null, "Extra light, reflections and contact shadows. Replaces SSGI while on.", MoreSsrt));
        var ssrtRow = Row(_ssrtPreset, "Ray-traced lighting preset", null, null);
        lit.Controls.Add(ssrtRow);
        _rowVis.Add((ssrtRow, () => _ssrt.Checked));
        var mot = p.Group("Motion");
        mot.Controls.Add(Row(_motion, "Motion source", "Where motion vectors come from. Auto picks the best available.", MoreMotion));
        _motionLbl.Font = Theme.Small();
        _motionLbl.MaximumSize = new Size(200, 0);
        FlushLeft(_motionLbl, Theme.Space.S);
        _wrap.Add(_motionLbl);
        mot.Controls.Add(_motionLbl);
        mot.Controls.Add(Row(_quality, "Test scene render scale", "Only used by the built-in test scene.", null));
        var gpu = p.Group("Gpu");
        gpu.Controls.Add(Row(_gpuChoice, "Processing GPU", null, MoreGpu));
        _gpuNote.Font = Theme.Small();
        _gpuNote.MaximumSize = new Size(200, 0);
        FlushLeft(_gpuNote, Theme.Space.S);
        _wrap.Add(_gpuNote);
        gpu.Controls.Add(_gpuNote);
        FlushLeft(_gpuRestart);
        gpu.Controls.Add(_gpuRestart);
        _building = null;
    }

    void BuildAbout()
    {
        var p = Page("about", "About", $"Universal-FrameFX {DisplayVersion}");
        _building = p;
        p.Controls.Add(Line("Copyright © 2026 Chopsticks HQ. CSR is FrameFX's own upscaling engine."));
        var web = new PillButton { Text = "Website", AutoSize = true, Kind = PillKind.Secondary };
        web.Click += (_, _) => Open(SiteUrl);
        p.Controls.Add(BtnRow(_aboutBtn, web));
        var sup = p.Group("Support");
        FlushLeft(_coffee, Theme.Space.S);
        sup.Controls.Add(_coffee);
        var upd = p.Group("Updates");
        FlushLeft(_autoCheck, Theme.Space.S);
        upd.Controls.Add(_autoCheck);
        FlushLeft(_autoInstall, Theme.Space.S);
        upd.Controls.Add(_autoInstall);
        _updStatus.Font = Theme.Small();
        _updStatus.Margin = new Padding(0);
        FlushLeft(_updStatus);
        _wrap.Add(_updStatus);
        upd.Controls.Add(BtnRow(_checkNow, _updStatus));
        var moreU = new MoreInfo(MoreUpdates);
        _wrap.Add(moreU.Body);
        upd.Controls.Add(moreU);
        var logs = p.Group("Logs");
        FlushLeft(_logsBtn);
        logs.Controls.Add(_logsBtn);
        logs.Controls.Add(Line("Crash and startup logs stay on this PC; nothing is sent anywhere."));
        var moreL = new MoreInfo(MoreLogs);
        _wrap.Add(moreL.Body);
        logs.Controls.Add(moreL);
        _building = null;
    }

    static string NormPage(string s) => s.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    string ResolvePage(string? want)
    {
        if (string.IsNullOrWhiteSpace(want)) return "home";
        string n = NormPage(want);
        foreach (var kv in _pages)
        {
            if (NormPage(kv.Key) == n) return kv.Key;
            string title = kv.Value.Controls.OfType<Label>().FirstOrDefault()?.Text ?? "";
            if (NormPage(title) == n) return kv.Key;
        }
        foreach (var it in _nav.Items)
            if (NormPage(it.Key) == n || NormPage(it.Text) == n) return it.Key;
        return "home";
    }

    void ShowPage(string key)
    {
        var page = _pages.GetValueOrDefault(ResolvePage(key)) ?? _pages.GetValueOrDefault("home");
        if (page == null) return;
        foreach (var kv in _pages) kv.Value.Visible = kv.Value == page;
        _scroll.AutoScrollPosition = Point.Empty;
        int i = -1;
        for (int n = 0; n < _nav.Items.Count; n++)
            if (_nav.Items[n].Key == page.Key) { i = n; break; }
        if (i >= 0 && _nav.SelectedIndex != i) _nav.SelectedIndex = i;
        var dest = page.Key == "home" ? _homeWarn : _fgBasics;
        if (dest != null && _fgWarn.Parent != dest) dest.Controls.Add(_fgWarn);
        if (!_pageFromEnv) _ui.Page = page.Key;
        UpdateBarWarn();
        Reflow();
    }

    void SyncRows()
    {
        foreach (var (row, vis) in _rowVis) row.Visible = vis();
        _csr2Only.Visible = !Csr20On;
    }

    void UpdateFgWarn()
    {
        int m = MenuMul();
        if (m == 20)
        {
            _fgWarn.Text = "20× needs a 360 Hz or faster display. Low-latency mode is off at 20×.";
            _fgWarn.Visible = true;
        }
        else if (m is 8 or 10)
        {
            _fgWarn.Text = "8× and above are advanced and need a high-refresh display.";
            _fgWarn.Visible = true;
        }
        else
        {
            _fgWarn.Text = "";
            _fgWarn.Visible = false;
        }
    }

    void UpdateBarWarn()
    {
        bool home = _pages.TryGetValue("home", out var h) && h.Visible;
        _barWarn.Visible = _notice.Visible && !home;
        _barWarn.Text = _barWarn.Visible ? "Warnings: see Home" : "";
    }

    void UpdateHome()
    {
        if (_out == null && !_gamesArmed) _homeGame.Text = "Not running";
        else if (_gamesArmed) _homeGame.Text = _gameLabel.Length > 0 ? _gameLabel : "Waiting for a game…";
        else _homeGame.Text = _source.SelectedItem?.ToString() ?? "–";

        _homeUp.Text = _preset.SelectedIndex == 2
            ? "CSR 1.3 (Competitive preset)"
            : (_backend.SelectedItem?.ToString() ?? "–");

        if (SelectedFg == FgSelect.Off) _homeFg.Text = "Off";
        else
        {
            string method = _fgSelect.SelectedItem?.ToString() ?? "–";
            int m = _preset.SelectedIndex == 2 ? 8 : MenuMul();
            string mul = m == 0 ? "Auto" : $"{m}×";
            _homeFg.Text = method + " · " + mul;
        }

        try
        {
            if (_out != null) _homeFps.Text = $"{_out.OutputFps:0} / {_out.SourceFps:0}";
            else _homeFps.Text = "–";
        }
        catch { _homeFps.Text = "–"; }

        try
        {
            if (_out != null)
            {
                double proc = _out.ResponseMs;
                bool fgOn = SelectedFg != FgSelect.Off;
                double src = _out.SourceFps;
                if (fgOn && src > 1)
                {
                    double frame = 1000.0 / src;
                    _homeLat.Text = $"≈ {proc + frame:0.0} ms (processing {proc:0.0} ms + frame generation ≈ {frame:0.0} ms)";
                }
                else _homeLat.Text = $"≈ {proc:0.0} ms";
            }
            else _homeLat.Text = "–";
        }
        catch { _homeLat.Text = "–"; }

        string engine = _lastVerdict?.AntiCheatEngine ?? "";
        if (engine.Length > 0)
        {
            _homeAc.Text = $"On ({engine}): game-process CPU tweaks paused";
            _acTip.SetToolTip(_homeAc, _acTip.GetToolTip(_gameStatus));
        }
        else
        {
            _homeAc.Text = "Off: no protected game detected";
            _acTip.SetToolTip(_homeAc, "");
        }

        _homeMotion.Text = _motionShort.Length > 0 ? _motionShort : "–";
        _start.Kind = (_out == null && !_gamesArmed) ? PillKind.Primary : PillKind.Secondary;
        _homeWarn.Visible = _notice.Visible || _fgWarn.Visible;
        UpdateBarWarn();
    }

    void UpdateGpuNote()
    {
        bool pending = _gpu != null && _gpuChoice.SelectedIndex != (int)_gpu.ActiveChoice && CliGpu == null;
        _gpuNote.Text = $"Running on: {_gpu?.AdapterName ?? "?"} ({_gpu?.KindName})" + (CliGpu != null ? " — set by --gpu for this run" : "") +
                        (pending ? ". The new choice applies after a restart." : "");
        _gpuRestart.Visible = pending;
    }

    void CollectToggles()
    {
        _toggles.Clear();
        void Walk(Control c)
        {
            if (c is ToggleSwitch t) _toggles.Add(t);
            foreach (Control ch in c.Controls) Walk(ch);
        }
        foreach (var p in _pages.Values) Walk(p);
    }

    int ContentWidth(Control c)
    {
        bool bar = false;
        for (Control? p = c; p != null; p = p.Parent)
            if (p == _bar) { bar = true; break; }
        Control stop = bar ? _bar : _stack;
        int w = bar ? _bar.ClientSize.Width : _scroll.ClientSize.Width;
        for (Control? a = c.Parent; a != null; a = a.Parent)
        {
            w -= a.Padding.Horizontal + a.Margin.Horizontal;
            if (a == stop) break;
        }
        w -= c.Margin.Horizontal;
        return Math.Max(80, w);
    }

    void FitContent()
    {
        foreach (var l in _wrap)
        {
            int w = ContentWidth(l);
            if (l.Parent is StatGrid)
                w = Math.Max(80, (int)(ContentWidth(l) * 0.62f) - Theme.Space.S);
            else if (l.Parent is TableLayoutPanel tlp && tlp.ColumnCount == 2
                     && tlp.GetControlFromPosition(0, 0) is FlowLayoutPanel col0)
                w = Math.Max(80, ContentWidth(tlp) - col0.Width - Theme.Space.M);
            if (l.MaximumSize.Width != w) l.MaximumSize = new Size(w, 0);
            if (l.Tag is "fill" && l.MinimumSize.Width != w) l.MinimumSize = new Size(w, 0);
        }
        foreach (var t in _toggles)
        {
            int w = ContentWidth(t);
            if (t.MaximumSize.Width != w)
            {
                t.MaximumSize = new Size(w, 0);
                t.Size = t.GetPreferredSize(Size.Empty);
            }
        }
    }

    PageView? CurrentPage()
    {
        foreach (var kv in _pages) if (kv.Value.Visible) return kv.Value;
        return null;
    }

    void ShiftPage(int delta)
    {
        int n = _nav.Items.Count;
        if (n == 0) return;
        int i = (_nav.SelectedIndex + delta + n) % n;
        bool stay = ActiveControl == _nav || _nav.ContainsFocus;
        _nav.SelectedIndex = i;
        if (!stay) FocusPageFirst();
    }

    void FocusPageFirst()
    {
        var page = CurrentPage();
        if (page == null) return;
        page.SelectNextControl(page, true, true, true, false);
        if (ActiveControl != null) _scroll.ScrollControlIntoView(ActiveControl);
    }

    static void CollectStops(Control root, List<Control> list)
    {
        foreach (Control ch in root.Controls)
        {
            if (ch.Visible && ch.Enabled && ch.TabStop && ch.CanSelect) list.Add(ch);
            CollectStops(ch, list);
        }
    }

    static bool ArrowTarget(Control? c)
    {
        if (c == null || c is TextBox) return false;
        if (c is DarkCombo dc) return !dc.DroppedDown;
        if (c is ButtonBase or FlatSlider or SectionHeader) return true;
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p is MoreInfo) return true;
        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Next)) { ShiftPage(1); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab) || keyData == (Keys.Control | Keys.Prior)) { ShiftPage(-1); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        var k = keyData & Keys.KeyCode;
        if ((keyData & (Keys.Control | Keys.Alt)) != 0) return base.ProcessDialogKey(keyData);
        if (k == Keys.Escape)
        {
            if (ActiveControl is DarkCombo { DroppedDown: true }) return base.ProcessDialogKey(keyData);
            if (ActiveControl != _nav) _nav.FocusFromKeyboard();
            return true;
        }
        if ((k is Keys.Up or Keys.Down) && ArrowTarget(ActiveControl))
        {
            var page = CurrentPage();
            if (page == null) return base.ProcessDialogKey(keyData);
            var stops = new List<Control>();
            CollectStops(page, stops);
            CollectStops(_bar, stops);
            Control? cur = ActiveControl;
            while (cur != null && !stops.Contains(cur)) cur = cur.Parent;
            int idx = cur == null ? -1 : stops.IndexOf(cur);
            bool down = k == Keys.Down;
            if (!down && idx <= 0) { _nav.FocusFromKeyboard(); return true; }
            int next = down ? idx + 1 : idx - 1;
            if (next >= 0 && next < stops.Count)
            {
                stops[next].Focus();
                _scroll.ScrollControlIntoView(stops[next]);
            }
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}
