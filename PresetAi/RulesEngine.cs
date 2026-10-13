namespace UniversalFrameFX.PresetAi;

public static class RulesEngine
{
    static readonly Dictionary<string, HashSet<string>> Legal = new()
    {
        ["p"] = ["0", "1", "2"],
        ["fg"] = ["0", "1", "2", "3", "4"],
        ["m"] = ["0", "2", "3", "4", "8"],
        ["b"] = Enumerable.Range(0, 10).Select(i => i.ToString()).ToHashSet(),
        ["r"] = ["0", "1", "2", "3", "4"],
        ["gi"] = ["0", "1"],
        ["rt"] = ["0", "1"],
        ["cpu"] = ["0", "1"],
    };

    static readonly HashSet<string> KnownExes = KnowledgeBase.Games
        .Select(g => g.Exe.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public static int BaseFps(GameEntry game, GpuEntry gpu, bool laptop, string? res)
    {
        double s = gpu.Score * (laptop ? 0.75 : 1.0);
        double rf = res switch { "1080" => 1.0, "1440" => 0.65, "4k" => 0.32, _ => 0.8 };
        return Math.Max(8, (int)Math.Round(s * rf * 6.0 / Math.Pow(game.Cost, 1.1)));
    }

    public static SlotDecision Decide(SessionFeatures f)
    {
        var g = f.Game ?? throw new InvalidOperationException("game required");
        var gpu = f.Gpu ?? KnowledgeBase.DefaultGpu;
        var prefs = f.Prefs;
        var asks = f.Asks;
        var why = new List<string>();

        int baseFps = f.Fps ?? BaseFps(g, gpu, f.Laptop, f.Res);
        int target = f.Target ?? (g.Competitive ? 144 : 120);

        var o = new SlotDecision
        {
            Exe = g.Exe.ToLowerInvariant(),
            P = "0",
            Fg = "0",
            M = "0",
            B = "0",
            R = ResToSlot(f.Res),
            Gi = "0",
            Rt = "0",
            Cpu = "0",
            BaseFps = baseFps,
            TargetFps = target,
        };

        if (prefs.Contains("quality") && !prefs.Contains("battery")) { o.P = "1"; why.Add("quality"); }
        else if (gpu.Score >= 45 && !f.Laptop && baseFps >= target / 2) { o.P = "1"; why.Add("headroom"); }
        else why.Add("perf");

        if (f.Laptop) why.Add("laptop");

        if (g.Competitive || g.AntiCheat)
        {
            why.Add(g.AntiCheat ? "anticheat" : "competitive");
            if (g.AntiCheat && !g.Competitive) why.Add("ac_warn");
        }
        else if (prefs.Contains("latency"))
            why.Add("latency");
        else if (baseFps >= (int)(target * 0.9))
            why.Add("at_target");
        else
        {
            int m = Math.Clamp((int)Math.Ceiling(target / (double)baseFps), 2, 4);
            if (prefs.Contains("blur") && m > 2) { m = 2; why.Add("blur"); }
            o.Fg = "1";
            o.M = m.ToString();
            if (asks.Contains("fsr3fg")) { o.Fg = "3"; why.Add("asked_fsr3fg"); }
            if (baseFps < 35) why.Add("lowbase");
            if (g.HudHeavy) why.Add("hud");
        }

        foreach (var (k, v) in new (string k, string v)[] { ("fsr1", "3"), ("fsr2", "4"), ("fsr3", "5"), ("xess", "7"), ("csr12", "1") })
            if (asks.Contains(k)) { o.B = v; why.Add("asked_" + k); }

        if (asks.Contains("fsr4") || (prefs.Contains("quality") && gpu.Series == "rx9000"))
        {
            if (gpu.Series == "rx9000") { o.B = "6"; why.Add("fsr4"); }
            else why.Add("no_fsr4");
        }
        if (asks.Contains("ssgi")) { o.Gi = "1"; why.Add("ssgi"); }
        if (asks.Contains("rt")) { o.Rt = "1"; o.Gi = "0"; why.Add("rt"); }
        if (asks.Contains("cpu")) { o.Cpu = "1"; why.Add("cpu"); }

        o.Why = string.Join(",", why);
        return o;
    }

    static string ResToSlot(string? res) => res switch
    {
        "1080" => "4",
        "1440" => "1",
        "4k" => "2",
        _ => "0",
    };

    public static bool Valid(SlotDecision d)
    {
        if (d.Exe != "?" && !KnownExes.Contains(d.Exe)) return false;
        foreach (var (k, set) in Legal)
        {
            var v = k switch
            {
                "p" => d.P, "fg" => d.Fg, "m" => d.M, "b" => d.B, "r" => d.R,
                "gi" => d.Gi, "rt" => d.Rt, "cpu" => d.Cpu,
                _ => "",
            };
            if (!set.Contains(v)) return false;
        }
        if (d.B == "6" && !d.Why.Split(',').Contains("fsr4")) return false;
        return true;
    }

    public static SessionFeatures? Settle(SessionFeatures f)
    {
        if (f.Game == null) return null;
        f.Gpu ??= KnowledgeBase.DefaultGpu;
        return f;
    }
}
