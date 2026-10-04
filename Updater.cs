using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace UniversalFrameFX;

/// <summary>One entry of latest.json.</summary>
public sealed record UpdateInfo(string Version, string Url, string Sha256, long Size, string Notes, string MinVersion);

/// <summary>
/// Auto-update. The site publishes https://chopstickshq.com/universal-framefx/latest.json
/// ({version, url, sha256, size, notes, minVersion}), generated from the real zip at deploy time.
/// The app checks it in the background on launch and every 6 hours; downloads go over HTTPS only to
/// %LOCALAPPDATA%\Universal-FrameFX\updates\ and are refused unless size and SHA-256 match.
/// Installing hands over to a copy of this exe running in "--apply-update" mode: it waits for the app
/// to exit, checks that the zip's Universal-FrameFX.exe is a valid x64 PE image (refusing the update
/// before anything is touched if not), backs the install folder up to "&lt;folder&gt;.prev", extracts the
/// new files, starts the new version and waits for it to report a successful start; if it doesn't, the
/// backup is restored and the previous version is started again.
/// UFX_UPDATE_URL overrides the manifest URL for testing and is the only way to allow a non-HTTPS
/// (file:// or http://localhost) manifest or download.
/// </summary>
public static class Updater
{
    public const string ProductionManifest = "https://chopstickshq.com/universal-framefx/latest.json";
    static readonly string? TestManifest = Environment.GetEnvironmentVariable("UFX_UPDATE_URL") is { Length: > 0 } u ? u : null;
    public static string ManifestUrl => TestManifest ?? ProductionManifest;
    public static bool TestMode => TestManifest != null;
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Universal-FrameFX");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");
    public static string LogPath => Path.Combine(UpdatesDir, "update.log");
    /// <summary>The folder the running exe lives in (normally %LOCALAPPDATA%\Programs\Universal-FrameFX).</summary>
    public static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Universal-FrameFX.exe"))!;

    public static void Log(string line)
    {
        try { Directory.CreateDirectory(UpdatesDir); File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    static bool Allowed(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps ||
        (TestMode && (u.IsFile || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)));

    static async Task<Stream> OpenAsync(HttpClient http, string url, CancellationToken ct)
    {
        var u = new Uri(url);
        if (!Allowed(u)) throw new InvalidOperationException($"refusing a non-HTTPS update URL ({u.Scheme})");
        if (u.IsFile) return File.OpenRead(u.LocalPath);
        var resp = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.RequestMessage?.RequestUri is { } final && !Allowed(final)) throw new InvalidOperationException("update download was redirected off HTTPS");
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Semver-aware: "1.4.0" > "1.4.0-beta.2" > "1.4.0-beta.1" > "1.3.3". Build metadata (+...) is ignored.</summary>
    public static bool IsNewer(string a, string b) => CompareVersions(a, b) > 0;

    public static int CompareVersions(string a, string b)
    {
        static (int[] core, string[]? pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V').Split('+')[0];
            int dash = v.IndexOf('-');
            string core = dash >= 0 ? v[..dash] : v;
            string[]? pre = dash >= 0 ? v[(dash + 1)..].Split('.') : null;
            return (core.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray(), pre);
        }
        var (x, xp) = Split(a); var (y, yp) = Split(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        if (xp == null || yp == null) return xp == null ? (yp == null ? 0 : 1) : -1;   // release > prerelease
        for (int i = 0; i < Math.Max(xp.Length, yp.Length); i++)
        {
            if (i >= xp.Length) return -1; if (i >= yp.Length) return 1;
            bool xn = int.TryParse(xp[i], out int xi), yn = int.TryParse(yp[i], out int yi);
            int c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(xp[i].ToLowerInvariant(), yp[i].ToLowerInvariant());
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Fetch latest.json; returns the entry if it is newer than the running version.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Universal-FrameFX/{MainForm.Version}");
        string url = ManifestUrl;
        if (!new Uri(url).IsFile) url += (url.Contains('?') ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string json;
        using (var s = await OpenAsync(http, url, ct)) using (var r = new StreamReader(s)) json = await r.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long size = root.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szv) ? szv : 0;
        string minVer = Str("minVersion"); if (minVer.Length == 0) minVer = Str("min_version");
        var info = new UpdateInfo(Str("version"), Str("url"), Str("sha256").ToLowerInvariant(), size, Str("notes"), minVer);
        if (info.Version.Length == 0 || info.Url.Length == 0 || info.Sha256.Length != 64 || info.Size <= 0) throw new InvalidDataException("latest.json is incomplete");
        return IsNewer(info.Version, MainForm.Version) ? info : null;
    }

    /// <summary>The running version is too old to update itself to this one (the user must reinstall).</summary>
    public static bool NeedsManualInstall(UpdateInfo u) => u.MinVersion.Length > 0 && IsNewer(u.MinVersion, MainForm.Version);

    /// <summary>Download to the updates folder and verify size + SHA-256. Throws (and deletes the file) on mismatch.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDir);
        string zip = Path.Combine(UpdatesDir, $"Universal-FrameFX-{u.Version}-win-x64.zip");
        if (File.Exists(zip) && await HashFileAsync(zip, ct) == u.Sha256) { Log($"{u.Version}: already downloaded and verified"); return zip; }
        string part = zip + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Universal-FrameFX/{MainForm.Version}");
        using (var src = await OpenAsync(http, u.Url, ct))
        using (var dst = File.Create(part))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buf = new byte[1 << 20];
            long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.AppendData(buf, 0, n);
                total += n;
                if (u.Size > 0) progress?.Report(Math.Min(1.0, total / (double)u.Size));
                if (u.Size > 0 && total > u.Size) break;
            }
            string got = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if ((u.Size > 0 && total != u.Size) || got != u.Sha256)
            {
                dst.Close();
                try { File.Delete(part); } catch { }
                Log($"{u.Version}: REFUSED download - size {total} (expected {u.Size}), sha256 {got} (expected {u.Sha256})");
                throw new InvalidDataException("the downloaded update does not match its published SHA-256 checksum; it was deleted and not installed");
            }
        }
        File.Move(part, zip, true);
        Log($"{u.Version}: downloaded and verified ({u.Sha256})");
        return zip;
    }

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
    }

    /// <summary>Start the helper (a copy of this exe) that swaps the files once this process has exited.
    /// The caller must then close the app.</summary>
    public static void StartInstaller(string zip, UpdateInfo u)
    {
        string helperDir = Path.Combine(UpdatesDir, "helper");
        Directory.CreateDirectory(helperDir);
        string helper = Path.Combine(helperDir, "Universal-FrameFX-updater.exe");
        File.Copy(Environment.ProcessPath!, helper, true);
        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = helperDir };
        foreach (var a in new[] { "--apply-update", zip, "--pid", Environment.ProcessId.ToString(), "--dir", InstallDir, "--from", MainForm.Version, "--to", u.Version })
            psi.ArgumentList.Add(a);
        Log($"{u.Version}: starting installer (from {MainForm.Version}, folder {InstallDir})");
        Process.Start(psi);
    }

    // ───────────────────────── helper side (--apply-update) ─────────────────────────

    public static string MarkerFor(string version) => Path.Combine(UpdatesDir, $"started-{version}.ok");

    /// <summary>Runs in the helper process. Returns the process exit code.</summary>
    public static int ApplyUpdate(string zip, int pid, string dir, string from, string to)
    {
        Log($"helper: applying {to} over {from} in {dir}");
        string prev = dir.TrimEnd('\\', '/') + ".prev";
        string exe = Path.Combine(dir, "Universal-FrameFX.exe");
        bool backedUp = false; // only a backup made by this run may be rolled back
        try
        {
            try { using var p = Process.GetProcessById(pid); if (!p.WaitForExit(60000)) { Log("helper: the app did not exit within 60 s; update cancelled"); return 2; } }
            catch (ArgumentException) { /* already gone */ }
            Thread.Sleep(500);

            // Validate the new exe before anything in the install folder is touched: a file that is not
            // a real x64 PE would make Process.Start fail (or hang on a modal OS error dialog) later.
            using (var za = ZipFile.OpenRead(zip))
            {
                var exeEntry = za.GetEntry("Universal-FrameFX.exe") ?? za.Entries.FirstOrDefault(e => e.FullName.EndsWith("/Universal-FrameFX.exe"));
                if (exeEntry == null || !IsX64PeImage(exeEntry))
                {
                    Log($"helper: {to}: REFUSED update - the zip's Universal-FrameFX.exe is not a valid x64 Windows executable; the install folder was not touched");
                    TryStart(exe, "--update-failed", to);
                    return 3;
                }
            }

            // Backup (kept afterwards as the rollback copy).
            if (Directory.Exists(prev)) Directory.Delete(prev, true);
            CopyDir(dir, prev);
            backedUp = true;
            Log($"helper: backed up to {prev}");

            // Extract (flat zip; tolerate a single top-level folder); guard against paths escaping the folder.
            using (var za = ZipFile.OpenRead(zip))
            {
                string strip = "";
                if (za.GetEntry("Universal-FrameFX.exe") == null && za.Entries.FirstOrDefault(e => e.FullName.EndsWith("/Universal-FrameFX.exe")) is { } ex)
                    strip = ex.FullName[..^"Universal-FrameFX.exe".Length];
                if (za.GetEntry(strip + "Universal-FrameFX.exe") == null) throw new InvalidDataException("the update zip has no Universal-FrameFX.exe");
                string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                foreach (var e in za.Entries)
                {
                    if (!e.FullName.StartsWith(strip) || e.FullName.EndsWith("/")) continue;
                    string dest = Path.GetFullPath(Path.Combine(dir, e.FullName[strip.Length..]));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unsafe path in update zip: " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    for (int attempt = 0; ; attempt++)
                    {
                        try { e.ExtractToFile(dest, true); break; }
                        catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
                    }
                }
            }
            Log("helper: files replaced");

            // Start the new version and wait for it to report a good start.
            string marker = MarkerFor(to);
            try { File.Delete(marker); } catch { }
            Process np;
            try { np = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir, ArgumentList = { "--update-marker", marker, "--updated-from", from } })!; }
            catch (Exception ex)
            {
                Log($"helper: could not start {to}: {ex.Message}; rolling back");
                Rollback(prev, dir, exe, from, to);
                return 1;
            }
            using (np)
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(60))
                {
                    if (File.Exists(marker)) { Log($"helper: {to} started OK (pid {np.Id}); previous version kept in {prev}"); return 0; }
                    if (np.HasExited) break;
                    Thread.Sleep(250);
                }
                Log($"helper: {to} did not start correctly (exited: {np.HasExited}{(np.HasExited ? ", code " + np.ExitCode : "")}); rolling back");
                try { if (!np.HasExited) np.Kill(true); np.WaitForExit(10000); } catch { }
            }
            Rollback(prev, dir, exe, from, to);
            return 1;
        }
        catch (Exception ex)
        {
            Log("helper: update failed: " + ex.Message);
            if (backedUp) Rollback(prev, dir, exe, from, to);
            else TryStart(exe, "--update-failed", to);
            return 1;
        }
    }

    static bool IsX64PeImage(ZipArchiveEntry e)
    {
        try
        {
            byte[] buf = new byte[(int)Math.Min(e.Length, 8192)]; // the first 8 KB, or the whole entry
            int n = 0;
            using (var s = e.Open())
                while (n < buf.Length)
                {
                    int r = s.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
            if (n < 0x40 || buf[0] != (byte)'M' || buf[1] != (byte)'Z') return false;
            int pe = BitConverter.ToInt32(buf, 0x3C); // e_lfanew
            if (pe < 0x40 || pe >= 4096 || pe + 26 > n) return false; // sane and inside what we read
            if (buf[pe] != (byte)'P' || buf[pe + 1] != (byte)'E' || buf[pe + 2] != 0 || buf[pe + 3] != 0) return false;
            if (BitConverter.ToUInt16(buf, pe + 4) != 0x8664) return false; // IMAGE_FILE_MACHINE_AMD64
            return BitConverter.ToUInt16(buf, pe + 24) == 0x20B;            // PE32+ (x64) optional-header magic
        }
        catch { return false; }
    }

    static void Rollback(string prev, string dir, string exe, string from, string to)
    {
        try
        {
            // Remove what the update added, then put the backup back.
            var keep = new HashSet<string>(Directory.EnumerateFiles(prev, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(prev, f)), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
                if (!keep.Contains(Path.GetRelativePath(dir, f))) { try { File.Delete(f); } catch { } }
            CopyDir(prev, dir);
            Log($"helper: rolled back to {from}");
        }
        catch (Exception ex) { Log("helper: rollback failed: " + ex.Message); }
        TryStart(exe, "--update-failed", to);
    }

    static void TryStart(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex) { Log("helper: could not start " + exe + ": " + ex.Message); }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(f, dest, true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
            }
        }
    }
}
