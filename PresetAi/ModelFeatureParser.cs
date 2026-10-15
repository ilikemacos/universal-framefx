namespace UniversalFrameFX.PresetAi;

public static class ModelFeatureParser
{
    static readonly HashSet<string> PrefLegal = ["blur", "latency", "quality", "battery"];
    static readonly HashSet<string> AskLegal = ["fsr1", "fsr2", "fsr3", "xess", "fsr4", "fsr3fg", "ssgi", "rt", "cpu", "csr12"];

    public static SessionFeatures? ToFeatures(IReadOnlyDictionary<string, string> d)
    {
        var f = new SessionFeatures();
        if (d.TryGetValue("g", out var g) && g is not ("-" or ""))
        {
            var exe = g.ToLowerInvariant();
            if (!exe.EndsWith(".exe", StringComparison.Ordinal)) exe += ".exe";
            f.Game = KnowledgeBase.FindGame(exe) ?? KnowledgeBase.FindGameByAlias(g);
        }
        if (d.TryGetValue("gpu", out var gpu) && gpu is not ("-" or ""))
            f.Gpu = KnowledgeBase.FindGpuByAlias(gpu);
        f.Laptop = d.GetValueOrDefault("lap") == "1";
        var res = d.GetValueOrDefault("res");
        f.Res = res is "1080" or "1440" or "4k" ? res : null;
        f.Fps = ParseInt(d.GetValueOrDefault("fps"));
        f.Target = ParseInt(d.GetValueOrDefault("tgt"));
        foreach (var p in ParseSet(d.GetValueOrDefault("pref"), PrefLegal)) f.Prefs.Add(p);
        foreach (var a in ParseSet(d.GetValueOrDefault("ask"), AskLegal)) f.Asks.Add(a);
        return f;
    }

    public static SessionFeatures Mfeats(string modelLine)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in modelLine.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return ToFeatures(d) ?? new SessionFeatures();
    }

    static int? ParseInt(string? v) => v is not null && int.TryParse(v, out var n) ? n : null;

    static HashSet<string> ParseSet(string? v, HashSet<string> legal)
    {
        if (v is null or "" or "-") return [];
        return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(legal.Contains).ToHashSet(StringComparer.Ordinal);
    }
}
