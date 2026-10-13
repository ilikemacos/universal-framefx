using System.Text.RegularExpressions;

namespace UniversalFrameFX.PresetAi;

public static partial class FeatureExtractor
{
    static readonly Dictionary<string, string> PrefPatterns = new()
    {
        ["blur"] = @"blur|smear|ghost|sharp",
        ["latency"] = @"latency|input lag|input delay|responsive|low lag",
        ["quality"] = @"max(imum)? quality|best quality|high(est)? quality|gorgeous|eye candy|look(s)? (as good|amazing|great|incredible|stunning)|best look|max settings|ultra quality|pretty as possible",
        ["battery"] = @"battery|unplugged|quiet|cool and quiet|plugged out",
    };

    static readonly Dictionary<string, string> AskPatterns = new()
    {
        ["fsr3fg"] = @"fsr ?3 (frame ?gen|fg)|amd frame gen",
        ["fsr4"] = @"fsr ?4",
        ["fsr1"] = @"fsr ?1(?!\d)",
        ["fsr2"] = @"fsr ?2(?!\d)",
        ["fsr3"] = @"fsr ?3(?! ?(frame|fg))",
        ["xess"] = @"xess",
        ["ssgi"] = @"ssgi",
        ["rt"] = @"ray.?trac|\brt lighting",
        ["cpu"] = @"cpu boost|boost (my |the )?cpu",
        ["csr12"] = @"csr ?1\.2",
    };

    const string CurPat = @"(getting|get|at|sitting at|around|about|roughly|like|currently|runs at|running at|base|baseline|i'?m at|stuck at|only|now|hovering)";
    const string TgtPat = @"(run at|can (i|we) get|i'?d take|push it to|want|target|targeting|aim|aiming( for)?|need|to|for|happy with|love|hit|reach|get me|->|→|into)";

    public static SessionFeatures Extract(string text)
    {
        var t = Norm(text);
        var f = new SessionFeatures { Laptop = Laptop().IsMatch(t) };
        var gHit = FindBest(KnowledgeBase.Games, g => [g.Name, .. g.Aliases, g.Exe[..^4]], t);
        var pHit = FindBest(KnowledgeBase.Gpus, g => [g.Name, .. g.Aliases], t);
        var s = t;
        if (gHit != null) { f.Game = gHit.Entry; s = MaskSpan(s, gHit.Start, gHit.End); }
        if (pHit != null) { f.Gpu = pHit.Entry; s = MaskSpan(s, pHit.Start, pHit.End); }

        if (Res1080().IsMatch(s)) f.Res = "1080";
        else if (Res1440().IsMatch(s)) f.Res = "1440";
        else if (Res4k().IsMatch(s)) f.Res = "4k";

        s = ResNoise().Replace(s, "  ");
        foreach (var (k, pat) in PrefPatterns)
            if (Regex.IsMatch(s, pat, RegexOptions.IgnoreCase)) f.Prefs.Add(k);
        foreach (var (k, pat) in AskPatterns)
            if (Regex.IsMatch(s, pat, RegexOptions.IgnoreCase)) f.Asks.Add(k);

        var nums = new List<(int Pos, int N)>();
        foreach (Match m in FpsNum().Matches(s))
        {
            int n = int.Parse(m.Groups[1].Value);
            if (n is >= 15 and <= 500) nums.Add((m.Index, n));
        }

        int? cur = null, tgt = null;
        foreach (var (pos, n) in nums)
        {
            var before = s[Math.Max(0, pos - 22)..pos];
            var after = s[pos..Math.Min(s.Length, pos + 30)];
            if (Regex.IsMatch(before, @"(i'?d|would) (like|love|be happy with)\s*$|" + TgtPat + @"\s*(like |about |around )?$", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(before, @"(runs at|at|getting|sitting at|currently)\s*(like )?$", RegexOptions.IgnoreCase))
                tgt = n;
            else if (Regex.IsMatch(before, CurPat + @"\s*$", RegexOptions.IgnoreCase)
                     || Regex.IsMatch(after, @"^\d+\s*(fps)?\s*(now|currently|base|right now|locked|->|→|to \d)", RegexOptions.IgnoreCase))
                cur = n;
            else if (Regex.IsMatch(after, @"^\d+\s*(fps)?\s*would be (ideal|nice|great)", RegexOptions.IgnoreCase))
                tgt = n;
            else if (tgt is null && cur is not null && n != cur) tgt = n;
            else if (tgt is null) tgt = n;
        }

        if (cur is not null && tgt is null && !Regex.IsMatch(s, @"(want|aim|target)", RegexOptions.IgnoreCase)) { }
        f.Fps = cur;
        f.Target = tgt;
        return f;
    }

    static string Norm(string text) =>
        Regex.Replace(text.Replace('’', ' ').Replace('\'', ' ').Replace('`', ' ').ToLowerInvariant(), @"\s+", " ").Trim();

    static string MaskSpan(string s, int a, int b) => s[..a] + new string(' ', b - a) + s[b..];

    sealed record Hit<T>(T Entry, int Start, int End);

    static Hit<T>? FindBest<T>(T[] items, Func<T, IEnumerable<string>> names, string t)
    {
        Hit<T>? best = null;
        foreach (var it in items)
        {
            foreach (var raw in names(it))
            {
                var a = Norm(raw);
                var m = WordBoundary(a).Match(t);
                if (m.Success && (best == null || a.Length > best.End - best.Start))
                    best = new Hit<T>(it, m.Index, m.Index + m.Length);
            }
        }
        return best;
    }

    [GeneratedRegex(@"laptop|mobile|notebook", RegexOptions.IgnoreCase)]
    private static partial Regex Laptop();

    [GeneratedRegex(@"1080p|\bfhd\b|\b1080\b", RegexOptions.IgnoreCase)]
    private static partial Regex Res1080();

    [GeneratedRegex(@"1440p|\bqhd\b|\b1440\b|\b2k\b", RegexOptions.IgnoreCase)]
    private static partial Regex Res1440();

    [GeneratedRegex(@"\b4k\b|2160p|\buhd\b", RegexOptions.IgnoreCase)]
    private static partial Regex Res4k();

    [GeneratedRegex(@"1080p?|1440p?|2160p?|\d+ ?hz|\d+ ?gb|\b20\d\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResNoise();

    [GeneratedRegex(@"(?<![\w.])(\d{2,3})(?!\d|\.\d)", RegexOptions.IgnoreCase)]
    private static partial Regex FpsNum();

    static Regex WordBoundary(string literal) =>
        new(@"(?<![\w])" + Regex.Escape(literal) + @"(?![\w])", RegexOptions.CultureInvariant);
}
