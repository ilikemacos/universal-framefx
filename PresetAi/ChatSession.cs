using System.Text.RegularExpressions;

namespace UniversalFrameFX.PresetAi;

public sealed partial class ChatSession
{
    readonly OnnxPresetModel? _model;
    SessionFeatures _f = new();
    bool _fgOff;
    SlotDecision? _last;

    public ChatSession(OnnxPresetModel? model = null) => _model = model;

    public void Reset()
    {
        _f = new SessionFeatures();
        _fgOff = false;
        _last = null;
    }

    public SlotDecision? LastDecision => _last;

    public string? LastExe => _last?.Exe;

    public string Turn(string text)
    {
        var intents = ParseIntents(text);
        if (Greeting().IsMatch(text.Trim()))
            return "Hi! I build FrameFX presets. Tell me your game, GPU, and what fps you get vs. want (e.g. \"Elden Ring on a 3060, 50 fps, want 100\").";
        if (intents.Count == 1 && intents[0] == "reset")
            return "OK, starting over. Which game, and what GPU do you have?";

        var kw = FeatureExtractor.Extract(text);
        SessionFeatures? mf = _model != null ? ModelFeatureParser.Mfeats(_model.Generate(text)) : null;
        bool hasNum = HasNum().IsMatch(text);
        var old = _f.Clone();
        var changed = new List<string>();

        bool onTopic = kw.Game != null || kw.Gpu != null || kw.Res != null || kw.Fps != null || kw.Target != null
                       || kw.Prefs.Count > 0 || kw.Asks.Count > 0 || kw.Laptop
                       || OnTopicWord().IsMatch(text);
        if (!onTopic) mf = null;

        var tl = text.ToLowerInvariant();
        foreach (var gg in KnowledgeBase.Games)
        {
            foreach (var al in new[] { gg.Name }.Concat(gg.Aliases))
                tl = tl.Replace(al.ToLowerInvariant(), " ");
        }
        bool gpuHint = GpuHint().IsMatch(tl);

        TryMergeGame(kw.Game, mf?.Game, ref changed);
        TryMergeGpu(kw.Gpu, mf?.Gpu, gpuHint, ref changed);
        TryMergeRes(kw.Res, mf?.Res, ref changed);
        if (hasNum)
        {
            TryMergeInt("fps", kw.Fps, mf?.Fps, ref changed, v => _f.Fps = v, () => _f.Fps);
            TryMergeInt("target", kw.Target, mf?.Target, ref changed, v => _f.Target = v, () => _f.Target);
        }

        if (kw.Laptop && !_f.Laptop) { _f.Laptop = true; changed.Add("laptop"); }
        foreach (var p in kw.Prefs.Except(_f.Prefs))
        {
            _f.Prefs.Add(p);
            changed.Add("pref:" + p);
            if (p == "quality") _f.Prefs.Remove("battery");
            if (p == "battery") _f.Prefs.Remove("quality");
        }
        foreach (var a in kw.Asks.Except(_f.Asks))
        {
            _f.Asks.Add(a);
            changed.Add("ask:" + a);
        }

        if (_f.Fps is int cf && _f.Target is int ct && ct <= cf)
        {
            if (changed.Contains("fps") && !changed.Contains("target")) _f.Target = null;
            else if (changed.Contains("target") && !changed.Contains("fps")) _f.Fps = null;
        }

        changed.AddRange(intents);

        if (changed.Count == 0)
            return _f.Game == null ? OffTopic : "I didn't catch a change there. You can say things like \"what about 4K\", \"make it quieter\", \"turn frame gen off\" or \"actually I have a 4070\".";

        if (_f.Game == null)
            return "Got it. Which game is this for?" + (_f.Gpu == null ? " And what GPU do you have?" : "");

        bool assumed = _f.Gpu == null;
        var decideF = _f.Clone();
        if (assumed) decideF.Gpu = KnowledgeBase.DefaultGpu;

        var settled = RulesEngine.Settle(decideF);
        if (settled == null) return OffTopic;
        var o = RulesEngine.Decide(settled);
        if (_fgOff && o.Fg != "0")
        {
            o.Fg = "0";
            o.M = "0";
            o.Why += ",user_fg_off";
        }

        var prev = _last;
        _last = o;
        return BuildReply(o, prev, changed, assumed);
    }

    void TryMergeGame(GameEntry? rule, GameEntry? model, ref List<string> changed)
    {
        GameEntry? pick = rule;
        if (pick == null && model != null && _f.Game == null) pick = model;
        if (pick is not GameEntry ge) return;
        if (_f.Game is { } curG && curG.Exe == ge.Exe) return;
        _f.Game = ge;
        changed.Add("game");
    }

    void TryMergeGpu(GpuEntry? rule, GpuEntry? model, bool gpuHint, ref List<string> changed)
    {
        GpuEntry? pick = rule;
        if (pick == null && model != null && gpuHint) pick = model;
        if (pick is not GpuEntry ge) return;
        if (_f.Gpu is { } curP && curP.Name == ge.Name) return;
        _f.Gpu = ge;
        changed.Add("gpu");
    }

    void TryMergeRes(string? rule, string? model, ref List<string> changed)
    {
        var v = rule ?? model;
        if (v == null || v == _f.Res) return;
        _f.Res = v;
        changed.Add("res");
    }

    void TryMergeInt(string key, int? rule, int? model, ref List<string> changed, Action<int> set, Func<int?> current)
    {
        var v = rule ?? model;
        if (v == null || v == current()) return;
        set(v.Value);
        changed.Add(key);
    }

    List<string> ParseIntents(string text)
    {
        var t = text.ToLowerInvariant();
        var ch = new List<string>();
        if (ResetIntent().IsMatch(t)) { Reset(); return ["reset"]; }
        if (FgOffIntent().IsMatch(t)) { _fgOff = true; ch.Add("fg_off"); }
        else if (FgOnIntent().IsMatch(t)) { _fgOff = false; ch.Add("fg_on"); }
        if (QuietIntent().IsMatch(t))
        {
            _f.Prefs.Remove("quality");
            _f.Prefs.Add("battery");
            ch.Add("quiet");
        }
        if (QualityIntent().IsMatch(t))
        {
            _f.Prefs.Remove("battery");
            _f.Prefs.Add("quality");
            ch.Add("quality");
        }
        if (BlurOffIntent().IsMatch(t)) { _f.Prefs.Remove("blur"); ch.Add("blur_off"); }
        if (LatOffIntent().IsMatch(t)) { _f.Prefs.Remove("latency"); ch.Add("lat_off"); }
        if (DesktopIntent().IsMatch(t)) { _f.Laptop = false; _f.Prefs.Remove("battery"); ch.Add("desktop"); }
        return ch;
    }

    string BuildReply(SlotDecision o, SlotDecision? prev, List<string> changed, bool assumed)
    {
        if (KnowledgeBase.FindGame(o.Exe) is not GameEntry g)
            throw new InvalidOperationException("unknown game exe " + o.Exe);
        string fgLabel = FrameGenLabel(o);
        var heard = new List<string>();
        foreach (var c in changed)
        {
            heard.Add(c switch
            {
                "game" => $"game {g.Name}",
                "gpu" => $"GPU {(_f.Gpu ?? KnowledgeBase.DefaultGpu).Name}" + (_f.Laptop ? " (laptop)" : ""),
                "res" => $"resolution {_f.Res}".Replace("4k", "4K", StringComparison.Ordinal),
                "fps" => $"you're getting ~{_f.Fps} fps",
                "target" => $"target {_f.Target} fps",
                "laptop" => "it's a laptop",
                var p when p.StartsWith("pref:") => PrefHeard(p[5..]),
                var a when a.StartsWith("ask:") => $"you asked for {a[4..]}",
                "fg_off" => "frame generation off",
                "fg_on" => "frame generation allowed again",
                "quiet" => "quieter / cooler",
                "quality" => "better looking",
                "desktop" => "desktop, not laptop",
                "blur_off" or "lat_off" => "dropping that preference",
                _ => "",
            });
        }
        heard.RemoveAll(string.IsNullOrEmpty);

        var lines = new List<string>();
        if (heard.Count > 0) lines.Add("Noted: " + string.Join(", ", heard) + ".");

        var now = SummaryLine(o, fgLabel);
        if (prev == null)
            lines.Add($"Here's a preset for {g.Name}: {now}.");
        else
        {
            var pv = SummaryLine(prev, FrameGenLabel(prev));
            if (!string.Equals(prev.Exe, o.Exe, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Switched to {g.Name}: {now}.");
            else
            {
                var diff = DiffSummary(prev, o);
                lines.Add(diff.Count > 0
                    ? "Changes: " + string.Join("; ", diff) + "."
                    : "No settings change needed. The current preset already covers that: " + ShortSummary(o) + ".");
            }
        }

        var why = o.Why.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (why.Contains("user_fg_off")) lines.Add("Frame generation is off because you asked.");
        foreach (var w in why)
        {
            if (w is "perf" or "quality" or "headroom" && prev != null && prev.P == o.P) continue;
            if (PresetAssistantTexts.WhyText.TryGetValue(w, out var txt)
                && (w.StartsWith("anticheat", StringComparison.Ordinal) || w.StartsWith("competitive", StringComparison.Ordinal)
                    || prev == null || prev.Exe != o.Exe || !prev.Why.Split(',').Contains(w)))
                lines.Add(txt);
        }
        if (why.Contains("user_fg_off"))
            lines.RemoveAll(l => l == PresetAssistantTexts.WhyText.GetValueOrDefault("at_target"));
        if (assumed)
            lines.Add("(I assumed an RTX 3060 since you didn't say your GPU. Tell me yours for a better fit.)");
        return string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    static string PrefHeard(string p) => p switch
    {
        "blur" => "you dislike blur",
        "latency" => "latency matters to you",
        "quality" => "you want max quality",
        "battery" => "you want it quiet / battery-friendly",
        _ => p,
    };

    static string FrameGenLabel(SlotDecision o) =>
        PresetAssistantTexts.FrameGenLabels[int.Parse(o.Fg)] + (o.Fg != "0" ? $" {o.M}x" : "");

    static string SummaryLine(SlotDecision o, string fg) =>
        string.Join(", ", new[]
        {
            $"Preset {PresetAssistantTexts.PresetLabels[int.Parse(o.P)]}",
            $"Frame gen {fg}",
            $"Upscaler {PresetAssistantTexts.BackendLabels[int.Parse(o.B)]}",
            $"Resolution {PresetAssistantTexts.ResLabels[o.R]}",
        });

    static string ShortSummary(SlotDecision o) =>
        string.Join(", ", SummaryLine(o, FrameGenLabel(o)).Split(", ").Take(3));

    static List<string> DiffSummary(SlotDecision prev, SlotDecision now)
    {
        var pv = new Dictionary<string, string>
        {
            ["Preset"] = PresetAssistantTexts.PresetLabels[int.Parse(prev.P)],
            ["Frame gen"] = FrameGenLabel(prev),
            ["Upscaler"] = PresetAssistantTexts.BackendLabels[int.Parse(prev.B)],
            ["Resolution"] = PresetAssistantTexts.ResLabels[prev.R],
            ["SSGI"] = prev.Gi == "1" ? "on" : "off",
            ["RT lighting"] = prev.Rt == "1" ? "on" : "off",
            ["CPU boost"] = prev.Cpu == "1" ? "on" : "off",
        };
        var nw = new Dictionary<string, string>
        {
            ["Preset"] = PresetAssistantTexts.PresetLabels[int.Parse(now.P)],
            ["Frame gen"] = FrameGenLabel(now),
            ["Upscaler"] = PresetAssistantTexts.BackendLabels[int.Parse(now.B)],
            ["Resolution"] = PresetAssistantTexts.ResLabels[now.R],
            ["SSGI"] = now.Gi == "1" ? "on" : "off",
            ["RT lighting"] = now.Rt == "1" ? "on" : "off",
            ["CPU boost"] = now.Cpu == "1" ? "on" : "off",
        };
        return pv.Keys.Where(k => pv[k] != nw[k]).Select(k => $"{k}: {pv[k]} → {nw[k]}").ToList();
    }

    const string OffTopic = PresetAssistantTexts.OffTopic;

    [GeneratedRegex(@"^\W*(hi|hello|hey|yo|help|can you help( me)?|what can you do)\W*(can you help( me)?)?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"\b(start over|reset|new game|forget it)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResetIntent();

    [GeneratedRegex(@"(frame ?gen\w*|fg|generated frames)\W+(off|disabled?)|(turn|switch|shut) off (the )?(frame ?gen|fg)|no (frame ?gen|fg)|disable (frame ?gen|fg)", RegexOptions.IgnoreCase)]
    private static partial Regex FgOffIntent();

    [GeneratedRegex(@"(frame ?gen|fg)\W+(on|back)|(turn|switch) (on|back on) (the )?(frame ?gen|fg)|enable (frame ?gen|fg)", RegexOptions.IgnoreCase)]
    private static partial Regex FgOnIntent();

    [GeneratedRegex(@"quiet|cooler|less (noise|heat|power)|fans? (are )?(too )?loud|save power", RegexOptions.IgnoreCase)]
    private static partial Regex QuietIntent();

    [GeneratedRegex(@"prettier|better looking|more quality|look better|max(imum)? quality|looks? (bad|ugly)", RegexOptions.IgnoreCase)]
    private static partial Regex QualityIntent();

    [GeneratedRegex(@"(don'?t|do not|no longer) care about (blur|ghosting)|blur is (fine|ok)", RegexOptions.IgnoreCase)]
    private static partial Regex BlurOffIntent();

    [GeneratedRegex(@"(don'?t|do not|no longer) care about (latency|input lag|lag)", RegexOptions.IgnoreCase)]
    private static partial Regex LatOffIntent();

    [GeneratedRegex(@"\b(desktop|not a laptop|plugged in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopIntent();

    [GeneratedRegex(@"\d|hundred|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety", RegexOptions.IgnoreCase)]
    private static partial Regex HasNum();

    [GeneratedRegex(@"\b(fps|frames?|gpu|preset|settings|frame ?gen|upscal\w*|rtx|gtx|rx|arc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnTopicWord();

    [GeneratedRegex(@"\b(rtx|gtx|rx|arc|radeon|geforce|b580|a770)\b|\b(?!1440\b|2160\b)\d{2}[0-9]0\b(?!\s?(p|fps|hz)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex GpuHint();
}
