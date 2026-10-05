using System.Text.Json;

namespace UniversalFrameFX;

/// <summary>What the CPU section asks for. Everything defaults off. Pure data: no Windows calls.</summary>
public sealed class CpuBoostSettings
{
    /// <summary>High or Ultimate performance power mode while a game is running.</summary>
    public bool PowerPlan { get; set; }
    /// <summary>Minimum processor state 100% on the FrameFX power plan.</summary>
    public bool MinProcessorState100 { get; set; }
    /// <summary>Keep cores unparked (minimum cores 100%).</summary>
    public bool DisableCoreParking { get; set; }
    /// <summary>0 leave boost alone, 1 aggressive, 2 efficient aggressive. See <see cref="CpuBoost.PerfBoostModeValue"/>.</summary>
    public int BoostMode { get; set; }
    /// <summary>Game priority High. Never Realtime.</summary>
    public bool HighPriority { get; set; }
    /// <summary>Prefer the highest efficiency class, then the highest scheduling class.</summary>
    public bool PreferFastCores { get; set; }
    /// <summary>One logical processor per physical core (lowest logical index).</summary>
    public bool AvoidSmtSiblings { get; set; }
    /// <summary>Request a 1 ms timer while a game is running.</summary>
    public bool TimerResolution1ms { get; set; }
    /// <summary>Use every physical core (all logicals, or one per core when avoiding siblings).</summary>
    public bool AllPhysicalCores { get; set; }
    /// <summary>Move FrameFX itself to a lower priority and off the game's cores.</summary>
    public bool LowerFrameFxPriority { get; set; }
    /// <summary>Process names to slow down while a game is running. Empty by default.</summary>
    public List<string> BackgroundProcesses { get; set; } = new();
    /// <summary>Ask Windows not to throttle the game.</summary>
    public bool DisablePowerThrottling { get; set; }
}

/// <summary>One logical processor. <see cref="CpuSetId"/> is what we hand the scheduler; <see cref="CoreIndex"/> is the physical core inside <see cref="Group"/>.</summary>
public sealed record CpuCoreInfo(int LogicalIndex, int CpuSetId, int CoreIndex, byte EfficiencyClass, byte SchedulingClass, int Group);

/// <summary>Original priority, affinity, CPU sets and throttling for one process, plus identity so a reused PID is not restored.</summary>
public sealed class ProcSnapshot
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public long StartTimeUtcTicks { get; set; }
    public int PriorityClass { get; set; }
    public bool HasPriority { get; set; }
    public ulong AffinityMask { get; set; }
    public bool HasAffinity { get; set; }
    public List<int> CpuSetIds { get; set; } = new();
    public bool HasCpuSets { get; set; }
    public bool CpuSetsWereEmpty { get; set; }
    public uint ThrottleControl { get; set; }
    public uint ThrottleState { get; set; }
    public bool HasThrottle { get; set; }
}

/// <summary>What to put back. Persisted so a crash can be undone on the next start.</summary>
public sealed class CpuRestoreState
{
    public Guid? PreviousPowerScheme { get; set; }
    public Guid? CreatedScheme { get; set; }
    public bool TimerRaised { get; set; }
    public string TimerMethod { get; set; } = "";
    /// <summary>"ultimate" or "high": which template the FrameFX copy was made from.</summary>
    public string PowerTemplate { get; set; } = "";
    public Dictionary<int, ProcSnapshot> Processes { get; set; } = new();
}

/// <summary>One undo step. <see cref="Action"/> is the target key; the first recorded original wins.</summary>
public sealed class CpuRestoreStep
{
    public string Action { get; set; } = "";
    public string Original { get; set; } = "";
}

/// <summary>Ordered book of originals. Re-applying does not overwrite the first value. <see cref="PlanRestore"/> is reverse application order.</summary>
public sealed class CpuRestoreLedger
{
    public List<CpuRestoreStep> Steps { get; set; } = new();
    public CpuRestoreState State { get; set; } = new();

    public bool IsEmpty =>
        Steps.Count == 0
        && State.PreviousPowerScheme == null
        && State.CreatedScheme == null
        && !State.TimerRaised
        && string.IsNullOrEmpty(State.TimerMethod)
        && State.Processes.Count == 0;

    /// <summary>Records <paramref name="original"/> the first time <paramref name="action"/> is seen.</summary>
    public void Record(string action, string original)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        if (Steps.Any(s => s.Action == action)) return;
        Steps.Add(new CpuRestoreStep { Action = action, Original = original ?? "" });
    }

    public bool Forget(string action, out string original)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Action == action)
            {
                original = Steps[i].Original ?? "";
                Steps.RemoveAt(i);
                return true;
            }
        }
        original = "";
        return false;
    }

    public List<CpuRestoreStep> PlanRestore()
    {
        var list = new List<CpuRestoreStep>(Steps.Count);
        for (int i = Steps.Count - 1; i >= 0; i--) list.Add(Steps[i]);
        return list;
    }

    public void Clear()
    {
        Steps.Clear();
        State = new CpuRestoreState();
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static CpuRestoreLedger FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new CpuRestoreLedger();
        try
        {
            var led = JsonSerializer.Deserialize<CpuRestoreLedger>(json, JsonOpts) ?? new CpuRestoreLedger();
            led.Steps ??= new List<CpuRestoreStep>();
            led.State ??= new CpuRestoreState();
            led.State.Processes ??= new Dictionary<int, ProcSnapshot>();
            led.State.TimerMethod ??= "";
            led.State.PowerTemplate ??= "";
            foreach (var snap in led.State.Processes.Values)
                if (snap != null) snap.CpuSetIds ??= new List<int>();
            return led;
        }
        catch { return new CpuRestoreLedger(); }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}

/// <summary>What the CPU section is currently doing, in words the status lines can show.</summary>
public sealed class CpuBoostStatus
{
    public bool PowerApplied { get; set; }
    public string PowerName { get; set; } = "";
    public bool FullSpeed { get; set; }
    public bool CoresAwake { get; set; }
    public string BoostLabel { get; set; } = "";
    public bool PriorityApplied { get; set; }
    public bool FastCoresApplied { get; set; }
    public int FastCount { get; set; }
    public int TotalLogical { get; set; }
    public bool OneThreadPerCore { get; set; }
    public bool TimerApplied { get; set; }
    public bool SpreadApplied { get; set; }
    public bool FrameFxLowered { get; set; }
    public int BackgroundCount { get; set; }
    public bool ThrottleOff { get; set; }
    public bool WaitingForGame { get; set; }
    public List<string> NeedsAdmin { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>Pure CPU-boost decisions: core selection, protected processes, settings cleanup, status text. No P/Invoke.</summary>
public static class CpuBoost
{
    public static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaverScheme = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid MinProcessorStateSetting = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid MinCoresSetting = new("0cc5b647-c1df-4637-891a-dec35c318583");
    public static readonly Guid PerfBoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>Standard PERFBOOSTMODE index for <paramref name="boostMode"/>, or -1 to leave the setting alone.
    /// 1 → 2 (Aggressive), 2 → 4 (Efficient Aggressive).</summary>
    public static int PerfBoostModeValue(int boostMode) => boostMode switch
    {
        1 => 2,
        2 => 4,
        _ => -1,
    };

    public static string BoostLabel(int boostMode) => boostMode switch
    {
        1 => "strong",
        2 => "efficient",
        _ => "",
    };

    static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "system idle process", "secure system", "registry", "memory compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "fontdrvhost", "msmpeng", "audiodg", "conhost", "sihost", "ctfmon",
        "universal-framefx", "universalframefx",
        "taskhostw", "taskhost", "runtimebroker", "dllhost", "spoolsv",
        "searchindexer", "securityhealthservice", "securityhealthsystray",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp",
        "textinputhost", "applicationframehost", "lockapp", "dashost", "lsaiso",
        "csrss", "wininit", "services", "lsass", "smss", "winlogon",
        "sihost", "ctfmon", "fontdrvhost", "dwm", "explorer", "conhost",
        "msmpeng", "audiodg", "nissrv", "securityhealthhost", "sgrmbroker",
        "wmiprvse", "unsecapp", "backgroundtaskhost", "compattelrunner",
    };

    public static CpuBoostSettings Sanitize(CpuBoostSettings? s)
    {
        s ??= new CpuBoostSettings();
        return new CpuBoostSettings
        {
            PowerPlan = s.PowerPlan,
            MinProcessorState100 = s.MinProcessorState100,
            DisableCoreParking = s.DisableCoreParking,
            BoostMode = s.BoostMode is 1 or 2 ? s.BoostMode : 0,
            HighPriority = s.HighPriority,
            PreferFastCores = s.PreferFastCores,
            AvoidSmtSiblings = s.AvoidSmtSiblings,
            TimerResolution1ms = s.TimerResolution1ms,
            AllPhysicalCores = s.AllPhysicalCores,
            LowerFrameFxPriority = s.LowerFrameFxPriority,
            BackgroundProcesses = NormalizeBackgroundList(s.BackgroundProcesses),
            DisablePowerThrottling = s.DisablePowerThrottling,
        };
    }

    public static bool AnyEnabled(CpuBoostSettings? s)
    {
        s = Sanitize(s);
        return WantsPower(s)
            || s.HighPriority || s.PreferFastCores || s.AvoidSmtSiblings || s.TimerResolution1ms
            || s.AllPhysicalCores || s.LowerFrameFxPriority || s.DisablePowerThrottling
            || s.BackgroundProcesses.Count > 0;
    }

    public static bool WantsPower(CpuBoostSettings? s)
    {
        s = Sanitize(s);
        return s.PowerPlan || s.MinProcessorState100 || s.DisableCoreParking || s.BoostMode != 0;
    }

    /// <summary>Identity of the power-related options only. Other options do not change it.</summary>
    public static string PowerApplyKey(CpuBoostSettings? s)
    {
        s = Sanitize(s);
        return (s.PowerPlan ? "1" : "0") + "."
            + (s.MinProcessorState100 ? "1" : "0") + "."
            + (s.DisableCoreParking ? "1" : "0") + "."
            + s.BoostMode.ToString();
    }

    /// <summary>Whether this sync should run power commands again.
    /// Same key as a cancel or a failed apply: do not retry. A different power key: retry.
    /// The same key as a successful apply: only check that the gaming power mode is still active.</summary>
    public readonly record struct PowerRetry(bool Retry, bool VerifyOnly, bool NeedsAdmin);

    public static PowerRetry ShouldRetryPowerApply(string? powerKey, string? appliedKey, string? deniedKey, bool wantsPower)
    {
        if (!wantsPower) return new PowerRetry(false, false, false);
        powerKey ??= "";
        appliedKey ??= "";
        deniedKey ??= "";
        if (deniedKey.Length > 0 && string.Equals(powerKey, deniedKey, StringComparison.Ordinal))
            return new PowerRetry(false, false, true);
        if (appliedKey.Length > 0 && string.Equals(powerKey, appliedKey, StringComparison.Ordinal))
            return new PowerRetry(false, true, false);
        return new PowerRetry(true, false, false);
    }

    /// <summary>Try the Ultimate template first, even when it is not listed. After that attempt fails, use High performance.</summary>
    public static Guid DuplicateTemplate(bool ultimateAttemptFailed) =>
        ultimateAttemptFailed ? HighPerformanceScheme : UltimatePerformanceScheme;

    public static string PowerModeLabel(bool ultimate) =>
        ultimate ? "Ultimate performance" : "High performance";

    /// <summary>The FrameFX-owned copy: a new scheme that is not the template and not a built-in scheme.
    /// Prefers a GUID parsed from command output, then one that appeared in the after-list.</summary>
    public static Guid PickCreatedScheme(IEnumerable<Guid>? fromOutput, Guid template, IEnumerable<Guid>? after, IEnumerable<Guid>? before)
    {
        if (fromOutput != null)
        {
            foreach (var g in fromOutput)
                if (g != Guid.Empty && g != template && !IsWellKnownScheme(g)) return g;
        }
        var seen = new HashSet<Guid>(before ?? Array.Empty<Guid>());
        if (after != null)
        {
            foreach (var g in after)
                if (g != Guid.Empty && !seen.Contains(g) && !IsWellKnownScheme(g)) return g;
        }
        return Guid.Empty;
    }

    public static string ToJson(CpuBoostSettings? s) =>
        JsonSerializer.Serialize(Sanitize(s), new JsonSerializerOptions { WriteIndented = true });

    public static CpuBoostSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new CpuBoostSettings();
        try { return Sanitize(JsonSerializer.Deserialize<CpuBoostSettings>(json)); }
        catch { return new CpuBoostSettings(); }
    }

    public static bool IsProtectedProcess(string? name)
    {
        string baseName = BaseName(name);
        if (baseName.Length == 0) return true;
        if (ProtectedNames.Contains(baseName)) return true;
        if (baseName.Contains("framefx", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Drops protected, blank, FrameFX, and <paramref name="excludeExe"/> names, appends ".exe", lowercases, dedupes.</summary>
    public static List<string> NormalizeBackgroundList(IEnumerable<string>? names, string? excludeExe = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        string skip = BaseName(excludeExe);
        if (names == null) return list;
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var line in raw.Split('\n'))
            {
                string s = line.Trim().Trim('"');
                if (s.Length == 0) continue;
                s = s.Replace('/', '\\');
                int slash = s.LastIndexOf('\\');
                if (slash >= 0) s = s[(slash + 1)..];
                if (IsProtectedProcess(s)) continue;
                string norm = BaseName(s);
                if (norm.Length == 0 || IsProtectedProcess(norm)) continue;
                if (skip.Length > 0 && string.Equals(norm, skip, StringComparison.Ordinal)) continue;
                norm += ".exe";
                if (seen.Add(norm)) list.Add(norm);
            }
        }
        return list;
    }

    static string BaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = name.Trim().Trim('"').Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.ToLowerInvariant();
        if (s.EndsWith(".exe", StringComparison.Ordinal)) s = s[..^4];
        return s.Trim();
    }

    /// <summary>CPU set ids for the game. Never empty when <paramref name="cores"/> is not empty
    /// (falls back to every logical processor). A single core is always returned as-is.</summary>
    public static int[] SelectCores(IReadOnlyList<CpuCoreInfo>? cores, CpuBoostSettings? settings)
    {
        if (cores == null || cores.Count == 0) return Array.Empty<int>();
        if (cores.Count == 1) return new[] { cores[0].CpuSetId };
        settings = Sanitize(settings);

        List<CpuCoreInfo> pool = cores.ToList();
        if (settings.PreferFastCores)
            pool = Fastest(pool);
        else if (!settings.AllPhysicalCores && !settings.AvoidSmtSiblings)
            pool = cores.ToList();

        if (settings.AvoidSmtSiblings)
            pool = OnePerCore(pool);

        if (pool.Count == 0)
            pool = settings.AvoidSmtSiblings ? OnePerCore(cores.ToList()) : cores.ToList();

        return pool
            .OrderBy(c => c.Group).ThenBy(c => c.LogicalIndex).ThenBy(c => c.CpuSetId)
            .Select(c => c.CpuSetId)
            .Distinct()
            .ToArray();
    }

    /// <summary>CPU set ids not chosen for the game. If the game uses every logical processor, the slowest
    /// (or the last) cores are returned instead. Never empty when <paramref name="cores"/> is not empty.</summary>
    public static int[] SelectBackgroundCores(IReadOnlyList<CpuCoreInfo>? cores, CpuBoostSettings? settings)
    {
        if (cores == null || cores.Count == 0) return Array.Empty<int>();
        var game = new HashSet<int>(SelectCores(cores, settings));
        var rest = cores
            .Where(c => !game.Contains(c.CpuSetId))
            .OrderBy(c => c.Group).ThenBy(c => c.LogicalIndex).ThenBy(c => c.CpuSetId)
            .Select(c => c.CpuSetId)
            .Distinct()
            .ToArray();
        if (rest.Length > 0) return rest;
        return Slowest(cores.ToList())
            .OrderBy(c => c.Group).ThenBy(c => c.LogicalIndex).ThenBy(c => c.CpuSetId)
            .Select(c => c.CpuSetId)
            .Distinct()
            .ToArray();
    }

    /// <summary>True when <paramref name="snap"/> is still the same process. A different start time means the PID was reused and must be skipped.</summary>
    public static bool ShouldRestoreProcess(ProcSnapshot? snap, int pid, long liveStartUtcTicks)
    {
        if (snap == null || pid <= 0) return false;
        if (snap.Pid != 0 && snap.Pid != pid) return false;
        if (snap.StartTimeUtcTicks == 0 || liveStartUtcTicks == 0) return false;
        return snap.StartTimeUtcTicks == liveStartUtcTicks;
    }

    public static List<string> StatusLines(CpuBoostStatus? st)
    {
        st ??= new CpuBoostStatus();
        var lines = new List<string>();
        if (st.PowerApplied && !string.IsNullOrWhiteSpace(st.PowerName))
            lines.Add($"Power: {st.PowerName} (while gaming)");
        if (st.FullSpeed) lines.Add("Processor: full speed while gaming");
        if (st.CoresAwake) lines.Add("Cores: kept awake");
        if (!string.IsNullOrWhiteSpace(st.BoostLabel)) lines.Add("Boost: " + st.BoostLabel);
        if (st.PriorityApplied) lines.Add("Game priority: High");
        if (st.FastCoresApplied)
            lines.Add($"Fast cores: {st.FastCount} of {Math.Max(st.TotalLogical, st.FastCount)}");
        if (st.OneThreadPerCore) lines.Add("One thread per core");
        if (st.TimerApplied) lines.Add("Timer: 1 ms");
        if (st.SpreadApplied) lines.Add("Spread across all cores");
        if (st.FrameFxLowered) lines.Add("FrameFX: kept out of the way");
        if (st.BackgroundCount > 0) lines.Add($"Background apps slowed: {st.BackgroundCount}");
        if (st.ThrottleOff) lines.Add("Windows throttling: off for the game");
        if (st.Notes != null)
        {
            foreach (var n in st.Notes.Distinct(StringComparer.Ordinal))
                if (!string.IsNullOrWhiteSpace(n) && !lines.Contains(n)) lines.Add(n);
        }
        if (st.NeedsAdmin != null)
        {
            foreach (var n in st.NeedsAdmin.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                string line = n.StartsWith("Needs admin", StringComparison.OrdinalIgnoreCase) ? n : "Needs admin: " + n;
                if (!lines.Contains(line)) lines.Add(line);
            }
        }
        if (lines.Count == 0)
            lines.Add(st.WaitingForGame
                ? "Waiting for a game. These options apply while a game is running."
                : "CPU options are off.");
        return lines;
    }

    /// <summary>Every GUID written as 8-4-4-4-12 in <paramref name="text"/>.</summary>
    public static Guid[] GuidsIn(string? text)
    {
        var list = new List<Guid>();
        if (string.IsNullOrEmpty(text)) return Array.Empty<Guid>();
        for (int i = 0; i + 36 <= text.Length; i++)
        {
            char c = text[i];
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) continue;
            if (!Guid.TryParseExact(text.Substring(i, 36), "D", out var g)) continue;
            list.Add(g);
            i += 35;
        }
        return list.ToArray();
    }

    public static bool IsWellKnownScheme(Guid scheme) =>
        scheme == HighPerformanceScheme || scheme == UltimatePerformanceScheme
        || scheme == BalancedScheme || scheme == PowerSaverScheme;

    static List<CpuCoreInfo> Fastest(List<CpuCoreInfo> cores)
    {
        byte maxEff = 0;
        foreach (var c in cores) if (c.EfficiencyClass > maxEff) maxEff = c.EfficiencyClass;
        var pool = cores.Where(c => c.EfficiencyClass == maxEff).ToList();
        if (pool.Count == 0) return cores.ToList();
        byte maxSched = 0;
        foreach (var c in pool) if (c.SchedulingClass > maxSched) maxSched = c.SchedulingClass;
        var pref = pool.Where(c => c.SchedulingClass == maxSched).ToList();
        return pref.Count > 0 ? pref : pool;
    }

    static List<CpuCoreInfo> OnePerCore(List<CpuCoreInfo> pool) =>
        pool.GroupBy(c => (c.Group, c.CoreIndex))
            .Select(g => g.OrderBy(c => c.LogicalIndex).ThenBy(c => c.CpuSetId).First())
            .ToList();

    static List<CpuCoreInfo> Slowest(List<CpuCoreInfo> cores)
    {
        byte minEff = 255;
        foreach (var c in cores) if (c.EfficiencyClass < minEff) minEff = c.EfficiencyClass;
        var pool = cores.Where(c => c.EfficiencyClass == minEff).ToList();
        if (pool.Count == 0) pool = cores.ToList();
        byte minSched = 255;
        foreach (var c in pool) if (c.SchedulingClass < minSched) minSched = c.SchedulingClass;
        var slower = pool.Where(c => c.SchedulingClass == minSched).ToList();
        if (slower.Count > 0 && slower.Count < cores.Count) return slower;
        int lastGroup = cores.Max(c => c.Group);
        var inGroup = cores.Where(c => c.Group == lastGroup).ToList();
        int lastCore = inGroup.Max(c => c.CoreIndex);
        var last = inGroup.Where(c => c.CoreIndex == lastCore).ToList();
        return last.Count > 0 ? last : cores.Take(1).ToList();
    }
}

/// <summary>Deterministic integer and float work used by the quick benchmark.</summary>
public static class CpuBench
{
    public readonly record struct Scores(double Single, double Multi);

    public static int RunUnit(int seed, int iterations)
    {
        if (iterations < 0) iterations = 0;
        uint x = unchecked((uint)seed * 747796405u + 2891336453u);
        uint h = 2166136261u;
        double f = 1.0;
        for (int i = 0; i < iterations; i++)
        {
            x = unchecked(x * 1664525u + 1013904223u);
            h ^= x;
            h *= 16777619u;
            f = f * 0.5 + (x & 1023) * (1.0 / 2048.0);
        }
        return unchecked((int)(h ^ (uint)(f * 1000000.0)));
    }

    /// <summary>Higher when more work finished or the same work took less time. Non-positive inputs score 0.</summary>
    public static double Score(long iterationsDone, double seconds)
    {
        if (iterationsDone <= 0 || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
        return iterationsDone / seconds;
    }

    public static string Compare(double before, double after)
    {
        double pct = before > 0 ? (after - before) / before * 100.0 : 0;
        string body = pct > 0.05 ? $"+{pct:0}%" : pct < -0.05 ? $"{pct:0}%" : "+0%";
        return $"Before: {before:0} · After: {after:0} ({body})";
    }

    /// <summary>About <paramref name="milliseconds"/> of single-thread work, then the same with one worker per logical processor.</summary>
    public static Scores Measure(int logicalCpus, int milliseconds)
    {
        int ms = Math.Clamp(milliseconds, 200, 30_000);
        int n = Math.Clamp(logicalCpus, 1, 256);
        double single = TimeLoop(ms, 1);
        double multi = TimeLoop(ms, n);
        return new Scores(single, multi);
    }

    static double TimeLoop(int milliseconds, int workers)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long total;
        if (workers <= 1)
        {
            total = Loop(milliseconds);
        }
        else
        {
            var counts = new long[workers];
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                int idx = i;
                threads[i] = new Thread(() => counts[idx] = Loop(milliseconds)) { IsBackground = true };
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            total = 0;
            foreach (var c in counts) total += c;
        }
        sw.Stop();
        return Score(total, Math.Max(sw.Elapsed.TotalSeconds, 0.0001));
    }

    static long Loop(int milliseconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long iters = 0;
        const int batch = 4000;
        int seed = 1;
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            RunUnit(seed, batch);
            seed++;
            iters += batch;
        }
        return iters;
    }
}
