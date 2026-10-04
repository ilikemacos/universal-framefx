using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalFrameFX;

/// <summary>Classifies a foreground window as "game or not" for the 1.3.2 apply-to-games-only feature.</summary>
public class GameDetector
{
    public sealed record GameVerdict(bool IsGame, IntPtr Hwnd, int Pid, string Exe, string Path, string Reason);

    /// <summary>Exe file names (e.g. "game.exe") that are always games, case-insensitive.</summary>
    public HashSet<string> Always { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Exe file names that are never games, case-insensitive.</summary>
    public HashSet<string> Never { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double LastClassifyMs { get; private set; }
    public double AvgClassifyMs { get; private set; }
    int _avgN;

    // Built-in exclusions: exe file names, lower-case.
    static readonly HashSet<string> Exclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        // shell / system
        "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "searchapp.exe", "textinputhost.exe", "lockapp.exe", "applicationframehost.exe",
        "systemsettings.exe", "taskmgr.exe", "dwm.exe", "csrss.exe",
        // browsers
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "opera.exe", "opera_gx.exe",
        "brave.exe", "vivaldi.exe", "iexplore.exe", "arc.exe",
        // launcher UIs themselves
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe",
        "battle.net.exe", "agent.exe", "riotclientservices.exe", "riotclientux.exe",
        "riotclientuxrender.exe", "eadesktop.exe", "eabackgroundservice.exe", "origin.exe",
        "galaxyclient.exe", "galaxyclient helper.exe", "upc.exe", "ubisoftconnect.exe",
        "xboxpcapp.exe", "gamingservices.exe", "gamebar.exe",
        // common desktop apps where fullscreen is normal
        "vlc.exe", "mpc-hc64.exe", "mpc-be64.exe", "potplayermini64.exe", "code.exe", "devenv.exe",
        "winword.exe", "excel.exe", "powerpnt.exe", "teams.exe", "ms-teams.exe", "discord.exe",
        "spotify.exe", "obs64.exe", "windowsterminal.exe", "powershell.exe", "cmd.exe",
        "conhost.exe", "notepad.exe",
        // 1.3.2: more browsers, IDEs, Office, chat/streaming, media, remote desktop, creative tools
        "chromium.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe", "thorium.exe", "yandex.exe",
        "rider64.exe", "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "goland64.exe", "studio64.exe",
        "cursor.exe", "windsurf.exe", "zed.exe", "sublime_text.exe", "notepad++.exe", "unity.exe", "unityhub.exe",
        "unrealeditor.exe", "ue4editor.exe", "blender.exe", "godot.exe",
        "outlook.exe", "olk.exe", "onenote.exe", "msaccess.exe", "mspub.exe", "visio.exe", "winproj.exe",
        "acrobat.exe", "acrord32.exe",
        "discordptb.exe", "discordcanary.exe", "slack.exe", "zoom.exe", "telegram.exe", "whatsapp.exe",
        "obs32.exe", "streamlabs obs.exe", "streamlabs desktop.exe", "xsplit.core.exe",
        "wmplayer.exe", "video.ui.exe", "microsoft.media.player.exe", "mpv.exe", "kodi.exe", "photos.exe", "mspaint.exe",
        "mstsc.exe", "vmconnect.exe", "wallpaper32.exe", "wallpaper64.exe", "ui32.exe", "grok bot.exe",
    };

    /// <summary>In order of preference for the reason string: the most specific API wins over dxgi.dll.</summary>
    static readonly string[] GraphicsModules =
    {
        "d3d12.dll", "vulkan-1.dll", "d3d11.dll", "opengl32.dll",
        "d3d10_1.dll", "d3d10.dll", "d3d9.dll", "dxgi.dll",
    };

    static readonly string[] LauncherPaths =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\",
        @"\xboxgames\", @"\riot games\", @"\ea games\", @"\origin games\",
        @"\ubisoft game launcher\games\",
    };

    static readonly HashSet<string> LauncherParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "epicgameslauncher.exe", "battle.net.exe", "riotclientservices.exe",
        "eadesktop.exe", "origin.exe", "galaxyclient.exe", "upc.exe",
    };

    sealed class CacheEntry
    {
        public DateTime StartTime;          // process start time (cache key, PID-reuse safe)
        public string Exe = "", Path = "";
        public bool Graphics;               // any D3D/Vulkan/OpenGL module loaded
        public string GraphicsModule = ""; // which one matched (for the reason string)
        public bool GraphicsChecked;        // at least one snapshot taken
        public int ModulesScanned, SnapError; // diagnostics for the reason string
        public bool GraphicsUnreadable;     // module list denied (anti-cheat / protected / elevated process)
        public long GraphicsCheckedAt;      // Environment.TickCount64 of last check
        public string? Launcher;            // launcher reason (null = not a launcher)
        public bool LauncherChecked;
        public bool Excluded;               // hit a built-in exclusion / our own process
        public string Exclusion = "";
    }

    readonly Dictionary<int, CacheEntry> _cache = new();
    long _lastPrune;

    const long GraphicsRetryMs = 2000;   // re-check a "no graphics yet" process at most every 2 s
    const int PruneThreshold = 64;
    const long PruneIntervalMs = 30000;

    /// <summary>Never throws. Classifies the foreground window (or any window) as game / not game.</summary>
    public GameVerdict Classify(IntPtr foregroundHwnd)
    {
        var sw = Stopwatch.StartNew();
        try { return ClassifyCore(foregroundHwnd, sw); }
        catch
        {
            var fallback = new GameVerdict(false, foregroundHwnd, 0, "", "", "classify failed");
            Tick(sw);
            return fallback;
        }
    }

    GameVerdict ClassifyCore(IntPtr foregroundHwnd, Stopwatch sw)
    {
        IntPtr root = foregroundHwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(foregroundHwnd, 2 /* GA_ROOT */);
        if (root == IntPtr.Zero) { Tick(sw); return new(false, foregroundHwnd, 0, "", "", "no window"); }
        Native.GetWindowThreadProcessId(root, out uint pid);
        int p = (int)pid;
        if (p == 0) { Tick(sw); return new(false, root, 0, "", "", "no process"); }
        if (p == Environment.ProcessId) { Tick(sw); return new(false, root, p, "Universal-FrameFX.exe", Process.GetCurrentProcess().MainModule?.FileName ?? "", "FrameFX itself"); }

        var e = GetEntry(p, root);
        string exeLower = e.Exe.ToLowerInvariant();

        // (a) our own binary, renamed or otherwise
        if (exeLower.StartsWith("universal-framefx")) { Tick(sw); return new(false, root, p, e.Exe, e.Path, "FrameFX itself"); }
        // (b) user lists
        if (Never.Contains(e.Exe)) { Tick(sw); return new(false, root, p, e.Exe, e.Path, "never list"); }
        if (Always.Contains(e.Exe)) { Tick(sw); return new(true, root, p, e.Exe, e.Path, "always list"); }
        // (c) built-in exclusions
        if (e.Excluded) { Tick(sw); return new(false, root, p, e.Exe, e.Path, e.Exclusion); }
        // (d) graphics modules
        CheckGraphics(p, e);
        if (!e.Graphics && !e.GraphicsUnreadable) { Tick(sw); return new(false, root, p, e.Exe, e.Path, $"no D3D/Vulkan/OpenGL modules ({e.ModulesScanned} scanned{(e.SnapError != 0 ? $", error {e.SnapError}" : "")})"); }
        // (e) launcher
        CheckLauncher(p, e);
        // (f) fullscreen
        bool fullscreen = IsFullscreen(root);

        // A protected (anti-cheat) or elevated game hides its module list: then fullscreen/launcher alone decides.
        bool isGame = e.Launcher != null || fullscreen;
        string gfx = e.Graphics ? e.GraphicsModule : "modules unreadable (protected process)";
        string reason = isGame
            ? $"{e.Launcher ?? "fullscreen"} + {gfx}"
            : "not fullscreen, no launcher";
        Tick(sw);
        return new(isGame, root, p, e.Exe, e.Path, reason);
    }

    void Tick(Stopwatch sw)
    {
        sw.Stop();
        LastClassifyMs = sw.Elapsed.TotalMilliseconds;
        _avgN++;
        AvgClassifyMs += (LastClassifyMs - AvgClassifyMs) / _avgN;
    }

    CacheEntry GetEntry(int pid, IntPtr hwnd)
    {
        DateTime startTime = GetProcessStartTime(pid, hwnd);
        if (_cache.TryGetValue(pid, out var e) && e.StartTime == startTime) return e;

        e = new CacheEntry { StartTime = startTime };
        GetExe(pid, e);
        e.Excluded = Exclusions.Contains(e.Exe);
        if (e.Excluded) e.Exclusion = $"excluded ({Path.GetFileNameWithoutExtension(e.Exe)})";
        _cache[pid] = e;
        PruneIfNeeded();
        return e;
    }

    void PruneIfNeeded()
    {
        long now = Environment.TickCount64;
        if (_cache.Count <= PruneThreshold && now - _lastPrune < PruneIntervalMs) return;
        _lastPrune = now;
        List<int>? dead = null;
        foreach (var kv in _cache)
        {
            try
            {
                using var proc = Process.GetProcessById(kv.Key);
                if (proc.StartTime.ToUniversalTime() != kv.Value.StartTime) (dead ??= new()).Add(kv.Key);
            }
            catch { (dead ??= new()).Add(kv.Key); } // ArgumentException = exited, Win32Exception = gone
        }
        if (dead != null) foreach (int pid in dead) _cache.Remove(pid);
    }

    void CheckGraphics(int pid, CacheEntry e)
    {
        long now = Environment.TickCount64;
        if (e.Graphics) return;                     // once true, never re-check
        if (e.GraphicsChecked && now - e.GraphicsCheckedAt < GraphicsRetryMs) return;
        e.GraphicsChecked = true;
        e.GraphicsCheckedAt = now;

        IntPtr snap = N.CreateToolhelp32Snapshot(N.TH32CS_SNAPMODULE | N.TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == new IntPtr(-1)) { e.GraphicsUnreadable = true; return; }   // access denied (protected/elevated) or gone
        e.GraphicsUnreadable = false;
        try
        {
            // Raw MODULEENTRY32W buffer (x64 layout: dwSize @0, modBaseAddr @24, modBaseSize @32, hModule @40, szModule @48, 1080 bytes); the
            // earlier struct had hModule/modBaseSize swapped, which shifted szModule and garbled every name.
            IntPtr buf = Marshal.AllocHGlobal(N.ModuleEntrySize);
            try
            {
                Marshal.WriteInt32(buf, 0, N.ModuleEntrySize);
                e.ModulesScanned = 0;
                if (!N.Module32FirstW(snap, buf)) { e.SnapError = Marshal.GetLastWin32Error(); return; }
                int best = int.MaxValue;
                do
                {
                    e.ModulesScanned++;
                    string name = Marshal.PtrToStringUni(buf + N.ModuleNameOffset) ?? "";
                    for (int i = 0; i < GraphicsModules.Length && i < best; i++)
                        if (string.Equals(name, GraphicsModules[i], StringComparison.OrdinalIgnoreCase)) { best = i; break; }
                    if (best == 0) break;
                } while (N.Module32NextW(snap, buf));
                if (best != int.MaxValue) { e.Graphics = true; e.GraphicsModule = GraphicsModules[best]; }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { N.CloseHandle(snap); }
    }

    void CheckLauncher(int pid, CacheEntry e)
    {
        if (e.LauncherChecked) return;
        e.LauncherChecked = true;

        string pathLower = e.Path.ToLowerInvariant();
        foreach (string dir in LauncherPaths)
            if (pathLower.Contains(dir))
            {
                e.Launcher = LabelForPath(dir);
                return;
            }
        // Game Pass / Xbox PC games ship a MicrosoftGame.config next to the exe
        try
        {
            string dir = Path.GetDirectoryName(e.Path) ?? "";
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "MicrosoftGame.config")))
            { e.Launcher = "MicrosoftGame.config (Xbox/Game Pass)"; return; }
        }
        catch { }

        string? parent = ParentExeName(pid);
        if (parent != null && LauncherParents.Contains(parent))
            e.Launcher = $"parent {parent}";
    }

    static string LabelForPath(string dir) => dir switch
    {
        @"\steamapps\common\" => "Steam library",
        @"\epic games\" => "Epic Games library",
        @"\gog games\" => "GOG library",
        @"\gog galaxy\games\" => "GOG Galaxy library",
        @"\xboxgames\" => "Xbox library",
        @"\riot games\" => "Riot Games library",
        @"\ea games\" => "EA library",
        @"\origin games\" => "Origin library",
        @"\ubisoft game launcher\games\" => "Ubisoft Connect library",
        _ => "game library",
    };

    static string? ParentExeName(int pid)
    {
        IntPtr snap = N.CreateToolhelp32Snapshot(N.TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return null;
        try
        {
            // Raw PROCESSENTRY32W buffer (x64 layout: th32ProcessID @8, th32ParentProcessID @32, szExeFile @44, 568 bytes).
            IntPtr buf = Marshal.AllocHGlobal(N.ProcessEntrySize);
            try
            {
                Marshal.WriteInt32(buf, 0, N.ProcessEntrySize);
                if (!N.Process32FirstW(snap, buf)) return null;
                uint parentPid = 0;
                do
                {
                    if ((uint)Marshal.ReadInt32(buf, 8) == (uint)pid) { parentPid = (uint)Marshal.ReadInt32(buf, 32); break; }
                } while (N.Process32NextW(snap, buf));
                if (parentPid == 0) return null;
                Marshal.WriteInt32(buf, 0, N.ProcessEntrySize);
                if (!N.Process32FirstW(snap, buf)) return null;
                do
                {
                    if ((uint)Marshal.ReadInt32(buf, 8) == parentPid) return (Marshal.PtrToStringUni(buf + 44) ?? "").ToLowerInvariant();
                } while (N.Process32NextW(snap, buf));
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { N.CloseHandle(snap); }
    }

    static bool IsFullscreen(IntPtr root)
    {
        try
        {
            if (!Native.IsWindowVisible(root) || Native.IsIconic(root)) return false;
            var r = Native.FrameBounds(root);            // DWM extended frame bounds
            if (r.Right <= r.Left || r.Bottom <= r.Top)
            {
                if (!N.GetWindowRect(root, out r)) return false;
            }
            IntPtr mon = N.MonitorFromWindow(root, N.MONITOR_DEFAULTTONEAREST);
            var mi = new N.MONITORINFO { cbSize = (uint)Marshal.SizeOf<N.MONITORINFO>() };
            if (!N.GetMonitorInfoW(mon, ref mi)) return false;
            const int tol = 1;
            return r.Left <= mi.rcMonitor.Left + tol && r.Top <= mi.rcMonitor.Top + tol
                && r.Right >= mi.rcMonitor.Right - tol && r.Bottom >= mi.rcMonitor.Bottom - tol;
        }
        catch { return false; }
    }

    static DateTime GetProcessStartTime(int pid, IntPtr hwnd)
    {
        IntPtr h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                if (N.GetProcessTimes(h, out N.FILETIME create, out _, out _, out _))
                    return DateTime.FromFileTimeUtc(create.dwHighDateTime * 4294967296L + create.dwLowDateTime);
            }
            finally { N.CloseHandle(h); }
        }
        try { return Process.GetProcessById(pid).StartTime.ToUniversalTime(); }
        catch { return DateTime.MinValue; }
    }

    static void GetExe(int pid, CacheEntry e)
    {
        IntPtr h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                if (N.QueryFullProcessImageNameW(h, 0, sb, out int len) && len > 0)
                {
                    e.Path = sb.ToString();
                    e.Exe = Path.GetFileName(e.Path);
                    return;
                }
            }
            finally { N.CloseHandle(h); }
        }
        try { e.Exe = Process.GetProcessById(pid).ProcessName + ".exe"; }
        catch { e.Exe = ""; }
    }

    /// <summary>New P/Invokes kept here so Native.cs stays untouched (existing signatures unchanged).</summary>
    static class N
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TH32CS_SNAPPROCESS = 2, TH32CS_SNAPMODULE = 8, TH32CS_SNAPMODULE32 = 16;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, out int len);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool Module32FirstW(IntPtr snap, IntPtr me);
        [DllImport("kernel32.dll")] public static extern bool Module32NextW(IntPtr snap, IntPtr me);
        [DllImport("kernel32.dll")] public static extern bool Process32FirstW(IntPtr snap, IntPtr pe);
        [DllImport("kernel32.dll")] public static extern bool Process32NextW(IntPtr snap, IntPtr pe);
        public const int ModuleEntrySize = 1080, ModuleNameOffset = 48, ProcessEntrySize = 568;
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out FILETIME create, out FILETIME exit, out FILETIME kernel, out FILETIME user);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT r);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);

        [StructLayout(LayoutKind.Sequential)] public struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public Native.RECT rcMonitor, rcWork;
            public uint dwFlags;
        }
    }
}
