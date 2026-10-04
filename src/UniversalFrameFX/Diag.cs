using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UniversalFrameFX;

/// <summary>Startup timing trace (startup.log) and crash logging (crash.log) under %LOCALAPPDATA%\Universal-FrameFX.</summary>
public static class Diag
{
    static readonly Stopwatch _sw = Stopwatch.StartNew();
    static readonly StringBuilder _trace = new();
    static readonly object _lk = new();
    public static string Dir => Updater.DataDir;
    public static string StartupLog => Path.Combine(Dir, "startup.log");
    public static string CrashLog => Path.Combine(Dir, "crash.log");
    public static double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

    /// <summary>Records a named startup step with the time since process start.</summary>
    public static void Mark(string step)
    {
        lock (_lk) _trace.AppendLine($"{_sw.Elapsed.TotalMilliseconds,9:F1} ms  {step}");
    }

    /// <summary>Writes the startup trace (overwriting the previous one).</summary>
    public static void FlushStartup()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string head = $"Universal-FrameFX {MainForm.DisplayVersion} ({MainForm.Version}) startup {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (process start to step)\n";
            lock (_lk) File.WriteAllText(StartupLog, head + _trace);
        }
        catch { }
    }

    /// <summary>Hooks every unhandled-exception path so crashes leave a readable log.</summary>
    public static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("unhandled (fatal=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash("unobserved task", e.Exception); e.SetObserved(); };
    }

    public static void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var fi = new FileInfo(CrashLog);
            if (fi.Exists && fi.Length > 512 * 1024) File.Move(CrashLog, CrashLog + ".old", true);
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {kind}");
            sb.AppendLine($"version {MainForm.Version}  os {Environment.OSVersion.VersionString}  64bit {Environment.Is64BitProcess}  uptime {_sw.Elapsed.TotalSeconds:F1}s");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();
            File.AppendAllText(CrashLog, sb.ToString());
        }
        catch { }
    }
}

/// <summary>On-disk cache of compiled GPU programs used by the (closed) engine, so only the first launch after an
/// install pays for compilation. This is the only file the engine writes: %LOCALAPPDATA%\Universal-FrameFX\shadercache.
/// Programs shipped with the app are read from the program folder.</summary>
public static class ShaderCache
{
    const string Salt = "ufx-shadercache-v1";
    public static string? OverrideDir;   // build-time only: write the cache here (shipped next to the exe)
    static string CacheDir => OverrideDir ?? Path.Combine(Updater.DataDir, "shadercache");
    public static int Hits, Misses;

    public static string Key(string src, string entry, string profile, string fileName)
    {
        using var h = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes($"{Salt}\n{AppVersion.Version}\n{fileName}\n{entry}\n{profile}\n{src}");
        return Convert.ToHexString(h.ComputeHash(bytes)).Substring(0, 32);
    }

    public static byte[] Get(string src, string entry, string profile, string fileName)
    {
        string key = Key(src, entry, profile, fileName);
        string path = Path.Combine(CacheDir, key + ".cso");
        // Cache shipped with the app (generated at publish time).
        try
        {
            string shipped = OverrideDir != null ? "" : Path.Combine(AppContext.BaseDirectory, "shadercache", key + ".cso");
            if (File.Exists(shipped)) { var sb = File.ReadAllBytes(shipped); if (sb.Length > 16) { Interlocked.Increment(ref Hits); return sb; } }
        }
        catch { }
        try
        {
            if (File.Exists(path))
            {
                var b = File.ReadAllBytes(path);
                if (b.Length > 16) { Interlocked.Increment(ref Hits); return b; }
            }
        }
        catch { }
        Interlocked.Increment(ref Misses);
        byte[] blob;
        using (var bl = Gpu.CompileSource(src, entry, profile, fileName)) blob = bl.AsSpan().ToArray();
        try
        {
            Directory.CreateDirectory(CacheDir);
            string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, true);
        }
        catch { }
        return blob;
    }

    /// <summary>Compiles (or loads) many entry points of one source in parallel.</summary>
    public static Dictionary<string, byte[]> GetMany(string src, IEnumerable<(string entry, string profile)> items, string fileName)
    {
        var list = items.ToList();
        var res = new byte[list.Count][];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            i => res[i] = Get(src, list[i].entry, list[i].profile, fileName));
        var d = new Dictionary<string, byte[]>();
        for (int i = 0; i < list.Count; i++) d[list[i].entry] = res[i];
        return d;
    }
}
