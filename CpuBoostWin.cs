using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalFrameFX;

public static class CpuBoostWin
{
    const int ErrorCancelled = 1223;
    const uint ProcessSetInformation = 0x0200;
    const uint ProcessQueryInformation = 0x0400;
    const uint ProcessQueryLimited = 0x1000;
    const uint ProcessSetLimited = 0x2000;
    const int ProcessPowerThrottling = 4;
    const uint ThrottleExecutionSpeed = 0x1;

    static readonly object Gate = new();
    static CpuRestoreLedger Ledger = new();
    static int SessionPid;
    static long SessionStart;
    static string SessionExe = "";
    static string AppliedSig = "";
    static CpuBoostStatus Last = new();
    static string _powerAppliedKey = "";
    static string _powerDeniedKey = "";
    static bool _restoreDenied;
    static int _startupPending;
    static int _waitMs = 12000;

    public static string RestorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Universal-FrameFX", "cpu-restore.json");

    public static void StartupRecover()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(RestorePath)) return;
                Ledger = CpuRestoreLedger.FromJson(File.ReadAllText(RestorePath));
                if (Ledger.IsEmpty) { PersistLocked(); return; }
                _startupPending = 1;
            }
            catch { return; }
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (Gate)
                {
                    if (_startupPending == 0) return;
                    int prev = _waitMs;
                    _waitMs = 4000;
                    try { RestoreLocked(); }
                    finally { _waitMs = prev; _startupPending = 0; }
                }
            }
            catch { lock (Gate) { _startupPending = 0; _waitMs = 12000; } }
        });
    }

    public static void NotifyUserEdit()
    {
        lock (Gate) _restoreDenied = false;
    }

    public static void RestoreSession()
    {
        lock (Gate)
        {
            _startupPending = 0;
            _restoreDenied = false;
            RestoreLocked();
        }
    }

    public static CpuBoostStatus Sync(int foregroundPid, string? foregroundExe, CpuBoostSettings? settings)
    {
        settings = CpuBoost.Sanitize(settings);
        lock (Gate)
        {
            try
            {
                if (_startupPending != 0)
                {
                    Last = new CpuBoostStatus
                    {
                        WaitingForGame = CpuBoost.AnyEnabled(settings),
                        Notes = { "Putting CPU settings back…" },
                    };
                    return Last;
                }

                string powerKey = CpuBoost.PowerApplyKey(settings);
                if (!string.Equals(powerKey, _powerDeniedKey, StringComparison.Ordinal))
                    _powerDeniedKey = "";

                int target = foregroundPid;
                string exe = foregroundExe ?? "";
                if (target > 0 && target != SessionPid)
                {
                    if ((SessionPid != 0 || !Ledger.IsEmpty) && !RestoreLocked()) return Last;
                    SessionPid = target;
                    SessionExe = exe;
                    SessionStart = ReadStart(target);
                    AppliedSig = "";
                }
                else if (target <= 0 && SessionPid > 0)
                {
                    long live = ReadStart(SessionPid);
                    bool same = live != 0 && (SessionStart == 0 || live == SessionStart);
                    if (!same)
                    {
                        if (!RestoreLocked()) return Last;
                        target = 0;
                    }
                    else
                    {
                        target = SessionPid;
                        exe = SessionExe;
                    }
                }

                if (!CpuBoost.AnyEnabled(settings) || target <= 0)
                {
                    if ((!Ledger.IsEmpty || SessionPid != 0) && !RestoreLocked()) return Last;
                    Last = new CpuBoostStatus { WaitingForGame = CpuBoost.AnyEnabled(settings) };
                    PersistLocked();
                    return Last;
                }

                string sig = target + "\n" + CpuBoost.ToJson(settings);
                if (sig != AppliedSig)
                {
                    ApplyLocked(target, exe, settings);
                    AppliedSig = sig;
                }
                else
                {
                    TopUpBackground(settings);
                    if (CpuBoost.WantsPower(settings)) TouchPowerStatus(settings);
                }

                PersistLocked();
                return Last;
            }
            catch
            {
                return Last;
            }
        }
    }

    static bool RestoreLocked()
    {
        bool powerOk = true;
        if (HasPower())
        {
            if (_restoreDenied) powerOk = false;
            else powerOk = RestorePowerTogether();
            if (!powerOk) _restoreDenied = true;
        }

        bool otherFail = false;
        foreach (var step in Ledger.PlanRestore())
        {
            if (IsPowerStep(step.Action)) continue;
            bool ok;
            try { ok = ExecuteUndo(step); }
            catch { ok = false; }
            if (!ok) otherFail = true;
            else Ledger.Forget(step.Action, out _);
        }

        if (!powerOk || otherFail)
        {
            if (!powerOk) StampNeedsAdmin();
            PersistLocked();
            return false;
        }
        Ledger.Clear();
        SessionPid = 0;
        SessionStart = 0;
        SessionExe = "";
        AppliedSig = "";
        _powerAppliedKey = "";
        _powerDeniedKey = "";
        _restoreDenied = false;
        PersistLocked();
        return true;
    }

    static void StampNeedsAdmin()
    {
        if (!Last.NeedsAdmin.Contains("power mode")) Last.NeedsAdmin.Add("power mode");
    }

    static void ApplyLocked(int pid, string exe, CpuBoostSettings settings)
    {
        var st = new CpuBoostStatus { TotalLogical = Math.Max(1, Environment.ProcessorCount) };
        if (CpuBoost.WantsPower(settings)) EnsurePower(settings, st);
        else UndoPower(st);

        if (settings.TimerResolution1ms) EnsureTimer(st);
        else UndoTimer();

        var topo = QueryCores();
        st.TotalLogical = topo.Cores.Count > 0 ? topo.Cores.Count : st.TotalLogical;
        int[] gameSets = topo.Cores.Count > 0 ? CpuBoost.SelectCores(topo.Cores, settings) : Array.Empty<int>();
        int[] bgSets = topo.Cores.Count > 0 ? CpuBoost.SelectBackgroundCores(topo.Cores, settings) : Array.Empty<int>();

        if (settings.HighPriority) EnsurePriority(pid, exe, NotRealtime(ProcessPriorityClass.High), "game", st, game: true);
        else UndoKind("game", pid, "priority");

        if (WantsCores(settings))
            EnsureCpuSets(pid, exe, gameSets, topo, "game", st, settings);
        else
            UndoCpuPlacement("game", pid);

        if (settings.DisablePowerThrottling) EnsureThrottleOff(pid, exe, st);
        else UndoKind("game", pid, "throttle");

        int self = Environment.ProcessId;
        if (settings.LowerFrameFxPriority)
        {
            EnsurePriority(self, "Universal-FrameFX", ProcessPriorityClass.BelowNormal, "self", st, game: false);
            if (bgSets.Length > 0) EnsureCpuSets(self, "Universal-FrameFX", bgSets, topo, "self", st, settings, frameFx: true);
            st.FrameFxLowered = true;
        }
        else
        {
            UndoKind("self", self, "priority");
            UndoCpuPlacement("self", self);
        }

        ApplyBackground(settings, bgSets, topo, st);
        Last = st;
    }

    static bool WantsCores(CpuBoostSettings s) =>
        s.PreferFastCores || s.AvoidSmtSiblings || s.AllPhysicalCores;

    static bool IsUltimateTemplate() =>
        string.Equals(Ledger.State.PowerTemplate, "ultimate", StringComparison.OrdinalIgnoreCase);

    static void RememberScheme(Guid created, bool ultimate, Guid previous)
    {
        Ledger.State.CreatedScheme = created;
        Ledger.State.PowerTemplate = ultimate ? "ultimate" : "high";
        Ledger.Record("power.created", created.ToString("D"));
        if (previous != Guid.Empty && previous != created && Ledger.State.PreviousPowerScheme == null
            && !string.Equals(previous.ToString("D"), created.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            Ledger.State.PreviousPowerScheme = previous;
            Ledger.Record("power.previous", previous.ToString("D"));
        }
    }

    static void EnsurePower(CpuBoostSettings settings, CpuBoostStatus st)
    {
        string key = CpuBoost.PowerApplyKey(settings);
        var decision = CpuBoost.ShouldRetryPowerApply(key, _powerAppliedKey, _powerDeniedKey, true);
        if (decision.NeedsAdmin)
        {
            NoteAdmin(st, "power mode");
            return;
        }
        if (decision.VerifyOnly)
        {
            PublishVerified(settings, st);
            return;
        }

        Guid created = Ledger.State.CreatedScheme ?? Guid.Empty;
        bool ultimate = IsUltimateTemplate();
        bool configured = false;
        if (created == Guid.Empty || CpuBoost.IsWellKnownScheme(created))
        {
            if (!TryCreateScheme(settings, st, out created, out ultimate, out configured))
            {
                _powerDeniedKey = key;
                _powerAppliedKey = "";
                NoteAdmin(st, "power mode");
                return;
            }
        }

        if (!configured && !ConfigureScheme(created, settings, st))
        {
            _powerDeniedKey = key;
            _powerAppliedKey = "";
            NoteAdmin(st, "power mode");
            return;
        }
        _powerDeniedKey = "";
        _powerAppliedKey = key;
        FillPower(settings, st, ultimate, true);
    }

    static void TouchPowerStatus(CpuBoostSettings settings)
    {
        var st = Last;
        string key = CpuBoost.PowerApplyKey(settings);
        var decision = CpuBoost.ShouldRetryPowerApply(key, _powerAppliedKey, _powerDeniedKey, true);
        if (decision.NeedsAdmin)
        {
            NoteAdmin(st, "power mode");
            Last = st;
            return;
        }
        if (decision.VerifyOnly) PublishVerified(settings, st);
        Last = st;
    }

    static void PublishVerified(CpuBoostSettings settings, CpuBoostStatus st)
    {
        Guid created = Ledger.State.CreatedScheme ?? Guid.Empty;
        Guid active = ActiveScheme();
        if (created != Guid.Empty && active == created)
            FillPower(settings, st, IsUltimateTemplate(), true);
        else if (created != Guid.Empty)
            AddNote(st, "Power mode is no longer the gaming one.");
    }

    static void FillPower(CpuBoostSettings settings, CpuBoostStatus st, bool ultimate, bool indexesOk)
    {
        st.PowerApplied = true;
        st.PowerName = CpuBoost.PowerModeLabel(ultimate);
        if (settings.MinProcessorState100 && indexesOk) st.FullSpeed = true;
        if (settings.DisableCoreParking && indexesOk) st.CoresAwake = true;
        if (settings.BoostMode is 1 or 2 && indexesOk) st.BoostLabel = CpuBoost.BoostLabel(settings.BoostMode);
    }

    static bool TryCreateScheme(CpuBoostSettings settings, CpuBoostStatus st, out Guid created, out bool ultimate, out bool configured)
    {
        created = Guid.Empty;
        ultimate = false;
        configured = false;
        Guid previous = ActiveScheme();
        var before = ListSchemes();
        var dup = RunPower("/duplicatescheme " + CpuBoost.DuplicateTemplate(false).ToString("D"));
        if (dup.Cancelled)
        {
            AddNote(st, "Could not change the power mode.");
            return false;
        }
        if (LooksDenied(dup))
        {
            configured = true;
            return TryCreateSchemeElevated(settings, st, previous, before, out created, out ultimate);
        }

        if (dup.Code == 0)
        {
            ultimate = true;
            created = CpuBoost.PickCreatedScheme(CpuBoost.GuidsIn(dup.Output), CpuBoost.UltimatePerformanceScheme, ListSchemes(), before);
            if (created == Guid.Empty)
            {
                created = DuplicateOwnCopy(CpuBoost.UltimatePerformanceScheme, ref previous, out bool denied, out bool cancelled);
                if (cancelled) { AddNote(st, "Could not change the power mode."); return false; }
                if (denied)
                {
                    configured = true;
                    return TryCreateSchemeElevated(settings, st, previous, before, out created, out ultimate);
                }
            }
        }
        else
        {
            before = ListSchemes();
            previous = ActiveScheme();
            var high = RunPower("/duplicatescheme " + CpuBoost.DuplicateTemplate(true).ToString("D"));
            if (high.Cancelled)
            {
                AddNote(st, "Could not change the power mode.");
                return false;
            }
            if (LooksDenied(high))
            {
                configured = true;
                return TryCreateSchemeElevated(settings, st, previous, before, out created, out ultimate);
            }
            if (high.Code != 0)
            {
                AddNote(st, "Could not change the power mode.");
                return false;
            }
            ultimate = false;
            created = CpuBoost.PickCreatedScheme(CpuBoost.GuidsIn(high.Output), CpuBoost.HighPerformanceScheme, ListSchemes(), before);
            if (created == Guid.Empty)
            {
                created = DuplicateOwnCopy(CpuBoost.HighPerformanceScheme, ref previous, out bool denied, out bool cancelled);
                if (cancelled) { AddNote(st, "Could not change the power mode."); return false; }
                if (denied)
                {
                    configured = true;
                    return TryCreateSchemeElevated(settings, st, previous, before, out created, out ultimate);
                }
            }
        }

        if (created == Guid.Empty || CpuBoost.IsWellKnownScheme(created))
        {
            AddNote(st, "Could not change the power mode.");
            return false;
        }
        RememberScheme(created, ultimate, previous);
        return true;
    }

    static Guid DuplicateOwnCopy(Guid template, ref Guid previous, out bool denied, out bool cancelled)
    {
        denied = false;
        cancelled = false;
        var before = ListSchemes();
        if (previous == Guid.Empty) previous = ActiveScheme();
        var dup = RunPower("/duplicatescheme " + template.ToString("D"));
        if (dup.Cancelled) { cancelled = true; return Guid.Empty; }
        if (LooksDenied(dup)) { denied = true; return Guid.Empty; }
        if (dup.Code != 0) return Guid.Empty;
        return CpuBoost.PickCreatedScheme(CpuBoost.GuidsIn(dup.Output), template, ListSchemes(), before);
    }

    static bool TryCreateSchemeElevated(CpuBoostSettings settings, CpuBoostStatus st, Guid previous, HashSet<Guid> before, out Guid created, out bool ultimate)
    {
        created = Guid.Empty;
        ultimate = false;
        string tag = TempFile(".txt");
        string dup = TempFile(".txt");
        string script = TempFile(".cmd");
        try
        {
            File.WriteAllText(script, CreateScript(settings, tag, dup), new UTF8Encoding(false));
            var r = RunElevatedScript(script);
            created = DetectCreated(before, tag);
            ultimate = ReadTemplateUltimate(tag, before, ListSchemes());
            if (r.Cancelled)
            {
                AddNote(st, "Could not change the power mode.");
                if (created != Guid.Empty) RememberScheme(created, ultimate, previous);
                return false;
            }
            if (created == Guid.Empty || CpuBoost.IsWellKnownScheme(created) || r.Code != 0)
            {
                AddNote(st, "Could not change the power mode.");
                if (created != Guid.Empty && !CpuBoost.IsWellKnownScheme(created))
                    RememberScheme(created, ultimate, previous);
                return false;
            }
            RememberScheme(created, ultimate, previous);
            return true;
        }
        finally
        {
            TryDelete(script);
            TryDelete(tag);
            TryDelete(dup);
        }
    }

    static bool ConfigureScheme(Guid created, CpuBoostSettings settings, CpuBoostStatus st)
    {
        var cmds = BuildConfigureCommands(created, settings);
        var r = RunPowerSequence(cmds);
        if (r.Cancelled || r.Code != 0)
        {
            AddNote(st, "Could not change the power mode.");
            return false;
        }
        return true;
    }

    static List<string> BuildConfigureCommands(Guid scheme, CpuBoostSettings settings)
    {
        var cmds = new List<string>();
        string sch = scheme.ToString("D");
        string sub = CpuBoost.ProcessorSubgroup.ToString("D");
        cmds.Add($"/changename {sch} \"FrameFX Gaming\"");
        void Add(Guid setting, int value)
        {
            string set = setting.ToString("D");
            cmds.Add($"/setacvalueindex {sch} {sub} {set} {value}");
            cmds.Add($"/setdcvalueindex {sch} {sub} {set} {value}");
        }
        if (settings.MinProcessorState100) Add(CpuBoost.MinProcessorStateSetting, 100);
        if (settings.DisableCoreParking) Add(CpuBoost.MinCoresSetting, 100);
        int boost = CpuBoost.PerfBoostModeValue(settings.BoostMode);
        if (boost >= 0) Add(CpuBoost.PerfBoostModeSetting, boost);
        cmds.Add($"/setactive {sch}");
        return cmds;
    }

    static string CreateScript(CpuBoostSettings settings, string tag, string dup)
    {
        string pc = PowerCfgPath();
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions EnableDelayedExpansion");
        sb.AppendLine("set \"PC=" + pc + "\"");
        sb.AppendLine("set \"ULT=" + CpuBoost.UltimatePerformanceScheme.ToString("D") + "\"");
        sb.AppendLine("set \"HIGH=" + CpuBoost.HighPerformanceScheme.ToString("D") + "\"");
        sb.AppendLine("set \"SUB=" + CpuBoost.ProcessorSubgroup.ToString("D") + "\"");
        sb.AppendLine("set \"DUP=" + dup + "\"");
        sb.AppendLine("set \"TAG=" + tag + "\"");
        sb.AppendLine("\"%PC%\" /duplicatescheme \"%ULT%\" > \"%DUP%\" 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  >\"%TAG%\" echo high");
        sb.AppendLine("  \"%PC%\" /duplicatescheme \"%HIGH%\" > \"%DUP%\" 2>&1");
        sb.AppendLine("  if errorlevel 1 exit /b 1");
        sb.AppendLine(") else (");
        sb.AppendLine("  >\"%TAG%\" echo ultimate");
        sb.AppendLine(")");
        sb.AppendLine("set \"GUID=\"");
        sb.AppendLine("for /f \"tokens=4\" %%G in ('findstr /I \"GUID:\" \"%DUP%\"') do set \"GUID=%%G\"");
        sb.AppendLine("if not defined GUID exit /b 2");
        sb.AppendLine("call :ifstock \"!GUID!\"");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  \"%PC%\" /duplicatescheme \"!GUID!\" > \"%DUP%\" 2>&1");
        sb.AppendLine("  if errorlevel 1 exit /b 3");
        sb.AppendLine("  set \"GUID=\"");
        sb.AppendLine("  for /f \"tokens=4\" %%G in ('findstr /I \"GUID:\" \"%DUP%\"') do set \"GUID=%%G\"");
        sb.AppendLine(")");
        sb.AppendLine("if not defined GUID exit /b 2");
        sb.AppendLine("call :ifstock \"!GUID!\"");
        sb.AppendLine("if not errorlevel 1 exit /b 4");
        sb.AppendLine(">>\"%TAG%\" echo !GUID!");
        sb.Append(ConfigureScriptBody());
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":ifstock");
        foreach (var g in new[] { CpuBoost.UltimatePerformanceScheme, CpuBoost.HighPerformanceScheme, CpuBoost.BalancedScheme, CpuBoost.PowerSaverScheme })
        {
            sb.AppendLine("if /I \"%~1\"==\"" + g.ToString("D") + "\" exit /b 0");
            sb.AppendLine("if /I \"%~1\"==\"" + g.ToString("B") + "\" exit /b 0");
        }
        sb.AppendLine("exit /b 1");
        return sb.ToString();

        string ConfigureScriptBody()
        {
            var body = new StringBuilder();
            void Line(string args) => body.AppendLine("\"%PC%\" " + args + " || exit /b 1");
            Line("/changename \"!GUID!\" \"FrameFX Gaming\"");
            void Index(Guid setting, int value)
            {
                string set = setting.ToString("D");
                Line("/setacvalueindex \"!GUID!\" \"%SUB%\" \"" + set + "\" " + value);
                Line("/setdcvalueindex \"!GUID!\" \"%SUB%\" \"" + set + "\" " + value);
            }
            if (settings.MinProcessorState100) Index(CpuBoost.MinProcessorStateSetting, 100);
            if (settings.DisableCoreParking) Index(CpuBoost.MinCoresSetting, 100);
            int boost = CpuBoost.PerfBoostModeValue(settings.BoostMode);
            if (boost >= 0) Index(CpuBoost.PerfBoostModeSetting, boost);
            Line("/setactive \"!GUID!\"");
            return body.ToString();
        }
    }

    static Guid DetectCreated(HashSet<Guid> before, string tagPath)
    {
        Guid tagged = Guid.Empty;
        try
        {
            if (File.Exists(tagPath))
            {
                var lines = File.ReadAllLines(tagPath);
                if (lines.Length > 1) Guid.TryParse(lines[1].Trim(), out tagged);
            }
        }
        catch { }
        var fromOutput = tagged == Guid.Empty ? Array.Empty<Guid>() : new[] { tagged };
        return CpuBoost.PickCreatedScheme(fromOutput, Guid.Empty, ListSchemes(), before);
    }

    static bool ReadTemplateUltimate(string tagPath, HashSet<Guid> before, HashSet<Guid> after)
    {
        try
        {
            if (File.Exists(tagPath))
            {
                var lines = File.ReadAllLines(tagPath);
                if (lines.Length > 0)
                {
                    string t = lines[0].Trim();
                    if (t.Equals("high", StringComparison.OrdinalIgnoreCase)) return false;
                    if (t.Equals("ultimate", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch { }
        return before.Contains(CpuBoost.UltimatePerformanceScheme) || after.Contains(CpuBoost.UltimatePerformanceScheme);
    }

    static void UndoPower(CpuBoostStatus st)
    {
        if (!HasPower()) return;
        if (_restoreDenied)
        {
            NoteAdmin(st, "power mode");
            return;
        }
        if (!RestorePowerTogether())
        {
            _restoreDenied = true;
            NoteAdmin(st, "power mode");
        }
    }

    static bool HasPower() =>
        Ledger.State.CreatedScheme != null || Ledger.State.PreviousPowerScheme != null
        || Ledger.Steps.Any(s => IsPowerStep(s.Action));

    static bool IsPowerStep(string? action) =>
        action != null && action.StartsWith("power.", StringComparison.Ordinal);

    static bool RestorePowerTogether()
    {
        Guid? previous = Ledger.State.PreviousPowerScheme;
        Guid? created = Ledger.State.CreatedScheme;
        if (created is Guid stock && CpuBoost.IsWellKnownScheme(stock)) created = null;
        var cmds = new List<string>();
        if (previous is Guid prev && prev != Guid.Empty)
            cmds.Add("/setactive " + prev.ToString("D"));
        if (created is Guid copy && copy != Guid.Empty)
            cmds.Add("/delete " + copy.ToString("D"));

        if (cmds.Count > 0)
        {
            bool needElevate = false;
            var rest = new List<string>();
            for (int i = 0; i < cmds.Count; i++)
            {
                var r = RunPower(cmds[i]);
                if (r.Code == 0) continue;
                if (cmds[i].StartsWith("/delete", StringComparison.Ordinal) && IsMissingScheme(r)) continue;
                if (r.Cancelled) return false;
                if (LooksDenied(r)) { needElevate = true; rest = cmds.GetRange(i, cmds.Count - i); break; }
                return false;
            }
            if (needElevate)
            {
                var r = RunElevatedRestore(rest);
                if (r.Cancelled) return false;
                if (r.Code != 0 && !PowerLooksRestored(previous, created)) return false;
            }
            else if (!PowerLooksRestored(previous, created) && created != null && ListSchemes().Contains(created.Value))
                return false;
        }

        Ledger.Forget("power.previous", out _);
        Ledger.Forget("power.created", out _);
        Ledger.Forget("power.template", out _);
        Ledger.Forget("power.label", out _);
        Ledger.State.PreviousPowerScheme = null;
        Ledger.State.CreatedScheme = null;
        Ledger.State.PowerTemplate = "";
        _powerAppliedKey = "";
        _powerDeniedKey = "";
        return true;
    }

    static bool PowerLooksRestored(Guid? previous, Guid? created)
    {
        if (created is Guid copy && copy != Guid.Empty && !CpuBoost.IsWellKnownScheme(copy) && ListSchemes().Contains(copy))
            return false;
        if (previous is Guid prev && prev != Guid.Empty)
        {
            var active = ActiveScheme();
            if (active != Guid.Empty && active != prev) return false;
        }
        return true;
    }

    static void EnsureTimer(CpuBoostStatus st)
    {
        if (Ledger.State.TimerRaised) { st.TimerApplied = true; return; }
        uint cur;
        int status = NtSetTimerResolution(10000, true, out cur);
        if (status >= 0)
        {
            Ledger.State.TimerRaised = true;
            Ledger.State.TimerMethod = "nt";
            Ledger.Record("timer", "nt");
            st.TimerApplied = true;
            return;
        }
        if (timeBeginPeriod(1) == 0)
        {
            Ledger.State.TimerRaised = true;
            Ledger.State.TimerMethod = "winmm";
            Ledger.Record("timer", "winmm");
            st.TimerApplied = true;
        }
        _ = cur;
    }

    static void UndoTimer()
    {
        if (!Ledger.Forget("timer", out var method))
        {
            if (!Ledger.State.TimerRaised) return;
            method = Ledger.State.TimerMethod;
        }
        EndTimer(method);
        Ledger.State.TimerRaised = false;
        Ledger.State.TimerMethod = "";
    }

    static void EndTimer(string? method)
    {
        try
        {
            if (method == "winmm") timeEndPeriod(1);
            else NtSetTimerResolution(10000, false, out _);
        }
        catch { }
    }

    static void EnsurePriority(int pid, string name, ProcessPriorityClass desired, string kind, CpuBoostStatus st, bool game)
    {
        desired = NotRealtime(desired);
        var snap = Snap(pid, name);
        if (snap.StartTimeUtcTicks == 0) { if (game) Deny(st); return; }
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!snap.HasPriority)
            {
                var cur = NotRealtime(p.PriorityClass);
                snap.HasPriority = true;
                snap.PriorityClass = (int)cur;
                Ledger.Record($"{kind}:{pid}:priority", snap.PriorityClass.ToString());
            }
            if (p.PriorityClass != desired) p.PriorityClass = desired;
            if (game) st.PriorityApplied = true;
        }
        catch (Win32Exception)
        {
            if (game) Deny(st);
        }
        catch
        {
            if (game) Deny(st);
        }
    }

    static void EnsureCpuSets(int pid, string name, int[] ids, Topology topo, string kind, CpuBoostStatus st, CpuBoostSettings settings, bool frameFx = false)
    {
        if (ids.Length == 0) return;
        var snap = Snap(pid, name);
        if (snap.StartTimeUtcTicks == 0) { if (!frameFx && kind == "game") Deny(st); return; }
        IntPtr h = OpenProcess(ProcessSetInformation | ProcessQueryInformation | ProcessQueryLimited | ProcessSetLimited, false, pid);
        if (h == IntPtr.Zero) { if (kind == "game") Deny(st); return; }
        try
        {
            if (!snap.HasCpuSets && topo.RealCpuSets)
            {
                snap.HasCpuSets = true;
                if (TryGetCpuSets(h, out var existing))
                {
                    snap.CpuSetIds = existing;
                    snap.CpuSetsWereEmpty = existing.Count == 0;
                }
                else snap.CpuSetsWereEmpty = true;
                Ledger.Record($"{kind}:{pid}:cpuset", snap.CpuSetsWereEmpty ? "" : string.Join(",", snap.CpuSetIds));
            }
            bool setOk = false;
            if (topo.RealCpuSets)
            {
                var arr = ids.Select(i => (uint)i).ToArray();
                setOk = SetProcessDefaultCpuSets(h, arr, (uint)arr.Length);
            }
            if (!setOk)
            {
                var chosen = topo.Cores.Where(c => ids.Contains(c.CpuSetId)).ToList();
                int groups = chosen.Select(c => c.Group).Distinct().Count();
                if (groups <= 1 && chosen.Count > 0 && chosen.All(c => c.LogicalIndex is >= 0 and < 64))
                {
                    ulong mask = 0;
                    foreach (var c in chosen) mask |= 1UL << c.LogicalIndex;
                    if (!snap.HasAffinity)
                    {
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            snap.AffinityMask = unchecked((ulong)p.ProcessorAffinity.ToInt64());
                            snap.HasAffinity = true;
                            Ledger.Record($"{kind}:{pid}:affinity", snap.AffinityMask.ToString());
                        }
                        catch { }
                    }
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        p.ProcessorAffinity = new IntPtr(unchecked((long)mask));
                        setOk = true;
                    }
                    catch (Win32Exception) { setOk = false; }
                    catch { setOk = false; }
                }
            }
            if (!setOk) { if (kind == "game") Deny(st); return; }
            if (kind == "game")
            {
                if (settings.PreferFastCores) { st.FastCoresApplied = true; st.FastCount = ids.Length; }
                if (settings.AvoidSmtSiblings) st.OneThreadPerCore = true;
                if (settings.AllPhysicalCores) st.SpreadApplied = true;
            }
        }
        finally { CloseHandle(h); }
    }

    static void EnsureThrottleOff(int pid, string name, CpuBoostStatus st)
    {
        var snap = Snap(pid, name);
        if (snap.StartTimeUtcTicks == 0) { Deny(st); return; }
        IntPtr h = OpenProcess(ProcessSetInformation | ProcessQueryInformation | ProcessQueryLimited | ProcessSetLimited, false, pid);
        if (h == IntPtr.Zero) { Deny(st); return; }
        try
        {
            if (!snap.HasThrottle)
            {
                snap.HasThrottle = true;
                if (TryGetThrottle(h, out uint control, out uint state))
                {
                    snap.ThrottleControl = control;
                    snap.ThrottleState = state;
                }
                Ledger.Record($"game:{pid}:throttle", snap.ThrottleControl + "," + snap.ThrottleState);
            }
            if (!WriteThrottle(h, ThrottleExecutionSpeed, 0)) Deny(st);
            else st.ThrottleOff = true;
        }
        finally { CloseHandle(h); }
    }

    static void ApplyBackground(CpuBoostSettings settings, int[] bgSets, Topology topo, CpuBoostStatus st)
    {
        var wanted = new HashSet<string>(settings.BackgroundProcesses, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in BackgroundPids().ToList())
        {
            if (!Ledger.Steps.Any(s => s.Action.StartsWith($"bg:{pid}:", StringComparison.Ordinal))) continue;
            if (!Ledger.State.Processes.TryGetValue(pid, out var snap)) continue;
            string exe = ExeOf(snap.Name);
            if (exe.Length == 0 || !wanted.Contains(exe)) UndoPid("bg", pid);
        }
        if (wanted.Count == 0) { st.BackgroundCount = 0; return; }
        int n = 0;
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { Last = st; return; }
        foreach (var p in procs)
        {
            try
            {
                if (LeaveProcessAlone(p.Id, p.ProcessName)) continue;
                string exe = ExeOf(p.ProcessName);
                if (exe.Length == 0 || !wanted.Contains(exe)) continue;
                if (CpuBoost.IsProtectedProcess(exe) || CpuBoost.IsProtectedProcess(p.ProcessName)) continue;
                EnsurePriority(p.Id, p.ProcessName, ProcessPriorityClass.BelowNormal, "bg", st, game: false);
                if (bgSets.Length > 0) EnsureCpuSets(p.Id, p.ProcessName, bgSets, topo, "bg", st, settings);
                n++;
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        st.BackgroundCount = n;
    }

    static void TopUpBackground(CpuBoostSettings settings)
    {
        if (settings.BackgroundProcesses.Count == 0 && !settings.LowerFrameFxPriority) return;
        var topo = QueryCores();
        int[] bg = topo.Cores.Count > 0 ? CpuBoost.SelectBackgroundCores(topo.Cores, settings) : Array.Empty<int>();
        var st = Last;
        ApplyBackground(settings, bg, topo, st);
        Last = st;
    }

    static List<int> BackgroundPids()
    {
        var ids = new List<int>();
        foreach (var s in Ledger.Steps)
        {
            if (TrySplit(s.Action, out var kind, out int pid, out _) && kind == "bg" && !ids.Contains(pid))
                ids.Add(pid);
        }
        return ids;
    }

    static void UndoCpuPlacement(string kind, int pid)
    {
        UndoKind(kind, pid, "cpuset");
        UndoKind(kind, pid, "affinity");
    }

    static void UndoKind(string kind, int pid, string what)
    {
        string action = $"{kind}:{pid}:{what}";
        var step = Ledger.Steps.FirstOrDefault(s => s.Action == action);
        if (step == null) return;
        try { ExecuteUndo(step); } catch { }
        Ledger.Forget(action, out _);
        DropProcessIfDone(pid);
    }

    static void UndoPid(string kind, int pid)
    {
        foreach (var what in new[] { "cpuset", "affinity", "priority", "throttle" })
            UndoKind(kind, pid, what);
    }

    static void DropProcessIfDone(int pid)
    {
        if (Ledger.Steps.Any(s => s.Action.Contains($":{pid}:", StringComparison.Ordinal))) return;
        Ledger.State.Processes.Remove(pid);
    }

    static bool ExecuteUndo(CpuRestoreStep step)
    {
        if (IsPowerStep(step.Action)) return false;
        if (step.Action == "timer")
        {
            EndTimer(step.Original);
            Ledger.State.TimerRaised = false;
            Ledger.State.TimerMethod = "";
            return true;
        }
        if (!TrySplit(step.Action, out _, out int pid, out string what)) return true;
        if (!Ledger.State.Processes.TryGetValue(pid, out var snap)) return true;
        long live = ReadStart(pid);
        if (!CpuBoost.ShouldRestoreProcess(snap, pid, live)) return true;
        try
        {
            if (what == "priority" && snap.HasPriority)
            {
                var pri = NotRealtime((ProcessPriorityClass)snap.PriorityClass);
                using var p = Process.GetProcessById(pid);
                p.PriorityClass = pri;
            }
            else if (what == "affinity" && snap.HasAffinity)
            {
                using var p = Process.GetProcessById(pid);
                p.ProcessorAffinity = new IntPtr(unchecked((long)snap.AffinityMask));
            }
            else if (what == "cpuset" && snap.HasCpuSets)
            {
                IntPtr h = OpenProcess(ProcessSetLimited | ProcessQueryLimited, false, pid);
                if (h == IntPtr.Zero) return true;
                try
                {
                    if (snap.CpuSetsWereEmpty || snap.CpuSetIds.Count == 0)
                        SetProcessDefaultCpuSets(h, Array.Empty<uint>(), 0);
                    else
                        SetProcessDefaultCpuSets(h, snap.CpuSetIds.Select(i => (uint)i).ToArray(), (uint)snap.CpuSetIds.Count);
                }
                finally { CloseHandle(h); }
            }
            else if (what == "throttle" && snap.HasThrottle)
            {
                IntPtr h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, pid);
                if (h == IntPtr.Zero) return true;
                try { WriteThrottle(h, snap.ThrottleControl == 0 ? ThrottleExecutionSpeed : snap.ThrottleControl, snap.ThrottleState); }
                finally { CloseHandle(h); }
            }
        }
        catch { return true; }
        return true;
    }

    static ProcSnapshot Snap(int pid, string name)
    {
        if (!Ledger.State.Processes.TryGetValue(pid, out var s) || s == null)
        {
            s = new ProcSnapshot { Pid = pid, Name = name ?? "", StartTimeUtcTicks = ReadStart(pid) };
            Ledger.State.Processes[pid] = s;
        }
        if (s.StartTimeUtcTicks == 0) s.StartTimeUtcTicks = ReadStart(pid);
        if (string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(name)) s.Name = name;
        s.CpuSetIds ??= new List<int>();
        return s;
    }

    static long ReadStart(int pid)
    {
        if (pid <= 0) return 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch { return 0; }
    }

    static string ExeOf(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        var list = CpuBoost.NormalizeBackgroundList(new[] { processName });
        return list.Count == 0 ? "" : list[0];
    }

    static void Deny(CpuBoostStatus st)
    {
        if (!st.Notes.Contains("Not allowed by the game")) st.Notes.Add("Not allowed by the game");
        NoteAdmin(st, "game settings");
    }

    static void NoteAdmin(CpuBoostStatus st, string what)
    {
        if (!st.NeedsAdmin.Contains(what)) st.NeedsAdmin.Add(what);
    }

    static void AddNote(CpuBoostStatus st, string note)
    {
        if (!st.Notes.Contains(note)) st.Notes.Add(note);
    }

    static ProcessPriorityClass NotRealtime(ProcessPriorityClass c) =>
        c == ProcessPriorityClass.RealTime ? ProcessPriorityClass.High : c;

    static bool LeaveProcessAlone(int pid, string? name)
    {
        if (pid <= 0 || pid == SessionPid || pid == Environment.ProcessId) return true;
        if (CpuBoost.IsProtectedProcess(name)) return true;
        string exe = ExeOf(name);
        if (exe.Length == 0 || CpuBoost.IsProtectedProcess(exe)) return true;
        string game = ExeOf(SessionExe);
        if (game.Length > 0 && exe.Equals(game, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void PersistLocked()
    {
        try
        {
            if (Ledger.IsEmpty)
            {
                if (File.Exists(RestorePath)) File.Delete(RestorePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(RestorePath)!);
            string tmp = RestorePath + ".tmp";
            File.WriteAllText(tmp, Ledger.ToJson());
            File.Move(tmp, RestorePath, true);
        }
        catch { }
    }

    readonly record struct PowerCmd(int Code, string Output, bool Cancelled);

    static string PowerCfgPath()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
        return File.Exists(sys) ? sys : "powercfg.exe";
    }

    static bool LooksDenied(PowerCmd r)
    {
        if (r.Cancelled) return false;
        if (r.Code is 5 or 740) return true;
        string o = r.Output ?? "";
        return o.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
            || o.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || o.Contains("privilege", StringComparison.OrdinalIgnoreCase)
            || o.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
            || o.Contains("administrator", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsMissingScheme(PowerCmd r)
    {
        string o = r.Output ?? "";
        return o.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || o.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || o.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    static PowerCmd RunPowerSequence(IReadOnlyList<string> argsList)
    {
        for (int i = 0; i < argsList.Count; i++)
        {
            var r = RunPower(argsList[i]);
            if (r.Code == 0) continue;
            if (r.Cancelled) return r;
            if (!LooksDenied(r)) return r;
            return RunElevatedCommands(argsList.Skip(i).ToList());
        }
        return new PowerCmd(0, "", false);
    }

    static PowerCmd RunElevatedCommands(IReadOnlyList<string> argsList)
    {
        if (argsList.Count == 0) return new PowerCmd(0, "", false);
        string script = TempFile(".cmd");
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions");
            foreach (var a in argsList)
                sb.AppendLine(FormatPowerLine(a) + " || exit /b 1");
            sb.AppendLine("exit /b 0");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
            return RunElevatedScript(script);
        }
        finally { TryDelete(script); }
    }

    static PowerCmd RunElevatedRestore(IReadOnlyList<string> argsList)
    {
        string script = TempFile(".cmd");
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions");
            foreach (var a in argsList)
            {
                string line = FormatPowerLine(a);
                if (a.StartsWith("/setactive", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine(line + " || exit /b 1");
                else
                    sb.AppendLine(line);
            }
            sb.AppendLine("exit /b 0");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
            return RunElevatedScript(script);
        }
        finally { TryDelete(script); }
    }

    static string FormatPowerLine(string args)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(PowerCfgPath()).Append('"');
        foreach (var t in SplitArgs(args))
        {
            sb.Append(' ');
            if (t.StartsWith('/')) sb.Append(t);
            else sb.Append('"').Append(t.Replace("\"", "")).Append('"');
        }
        return sb.ToString();
    }

    static List<string> SplitArgs(string args)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (char c in args)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    static string TempFile(string ext) =>
        Path.Combine(Path.GetTempPath(), "ufx-cpu-" + Guid.NewGuid().ToString("N") + ext);

    static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    static PowerCmd RunPower(string args) => RunProcess(PowerCfgPath(), args, elevate: false, _waitMs);

    static PowerCmd RunElevatedScript(string scriptPath)
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        int wait = Math.Max(_waitMs, 8000);
        return RunProcess(cmd, "/d /c \"" + scriptPath + "\"", elevate: true, wait);
    }

    static PowerCmd RunProcess(string fileName, string args, bool elevate, int waitMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) psi.Verb = "runas";
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            using var p = Process.Start(psi);
            if (p == null) return new PowerCmd(-1, "", false);
            var output = new StringBuilder();
            if (!elevate)
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(true); } catch { }
                return new PowerCmd(-1, output.ToString(), false);
            }
            if (!elevate) p.WaitForExit();
            return new PowerCmd(p.ExitCode, output.ToString(), false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new PowerCmd(ErrorCancelled, ex.Message, true);
        }
        catch (Exception ex)
        {
            return new PowerCmd(-1, ex.Message, false);
        }
    }

    static Guid ActiveScheme()
    {
        var r = RunPower("/getactivescheme");
        var ids = CpuBoost.GuidsIn(r.Output);
        return ids.Length > 0 ? ids[0] : Guid.Empty;
    }

    static HashSet<Guid> ListSchemes()
    {
        var r = RunPower("/L");
        return new HashSet<Guid>(CpuBoost.GuidsIn(r.Output));
    }

    readonly record struct Topology(List<CpuCoreInfo> Cores, bool RealCpuSets);

    static Topology QueryCores()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0 || needed > 1024 * 1024) return new Topology(FallbackCores(), false);
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buf, needed, out uint got, IntPtr.Zero, 0) || got < 16)
                    return new Topology(FallbackCores(), false);
                var list = new List<CpuCoreInfo>();
                int offset = 0;
                int guard = 0;
                while (offset + 16 <= (int)got && guard++ < 4096)
                {
                    int size = Marshal.ReadInt32(buf, offset);
                    int type = Marshal.ReadInt32(buf, offset + 4);
                    if (size < 16) break;
                    if (type == 0 && size >= 20 && offset + 16 <= (int)got)
                    {
                        int id = Marshal.ReadInt32(buf, offset + 8);
                        short group = Marshal.ReadInt16(buf, offset + 12);
                        byte logical = Marshal.ReadByte(buf, offset + 14);
                        byte core = Marshal.ReadByte(buf, offset + 15);
                        byte eff = offset + 19 < offset + size ? Marshal.ReadByte(buf, offset + 18) : (byte)0;
                        byte sched = offset + 21 <= offset + size ? Marshal.ReadByte(buf, offset + 20) : (byte)0;
                        list.Add(new CpuCoreInfo(logical, id, core, eff, sched, group));
                    }
                    offset += size;
                }
                if (list.Count > 0) return new Topology(list, true);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return new Topology(FallbackCores(), false);
    }

    static List<CpuCoreInfo> FallbackCores()
    {
        int n = Math.Max(1, Environment.ProcessorCount);
        var list = new List<CpuCoreInfo>(n);
        for (int i = 0; i < n; i++) list.Add(new CpuCoreInfo(i, i, i, 0, 0, 0));
        return list;
    }

    static bool TryGetCpuSets(IntPtr h, out List<int> ids)
    {
        ids = new List<int>();
        if (!GetProcessDefaultCpuSets(h, null, 0, out uint required))
        {
            int err = Marshal.GetLastWin32Error();
            if (required == 0 && err == 0) return true;
            if (required == 0) return true;
        }
        if (required == 0) return true;
        var buf = new uint[required];
        if (!GetProcessDefaultCpuSets(h, buf, required, out uint n)) return false;
        int count = (int)Math.Min(n, (uint)buf.Length);
        for (int i = 0; i < count; i++) ids.Add((int)buf[i]);
        return true;
    }

    static bool TryGetThrottle(IntPtr h, out uint control, out uint state)
    {
        control = 0; state = 0;
        int size = Marshal.SizeOf<ThrottleState>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new ThrottleState { Version = 1 }, mem, false);
            if (!GetProcessInformation(h, ProcessPowerThrottling, mem, (uint)size)) return false;
            var read = Marshal.PtrToStructure<ThrottleState>(mem);
            control = read.ControlMask;
            state = read.StateMask;
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static bool WriteThrottle(IntPtr h, uint control, uint state)
    {
        int size = Marshal.SizeOf<ThrottleState>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new ThrottleState { Version = 1, ControlMask = control, StateMask = state }, mem, false);
            return SetProcessInformation(h, ProcessPowerThrottling, mem, (uint)size);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static bool TrySplit(string action, out string kind, out int pid, out string what)
    {
        kind = ""; pid = 0; what = "";
        var parts = action.Split(':');
        if (parts.Length != 3) return false;
        kind = parts[0];
        if (kind is not ("game" or "self" or "bg")) return false;
        if (!int.TryParse(parts[1], out pid)) return false;
        what = parts[2];
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ThrottleState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[] cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, IntPtr information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr process, int informationClass, IntPtr information, uint informationSize);

    [DllImport("ntdll.dll")]
    static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    static extern uint timeEndPeriod(uint period);
}
