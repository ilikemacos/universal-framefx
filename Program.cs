using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace UniversalFrameFX;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Native.AttachConsole(-1);
            Console.WriteLine($"Universal-FrameFX {MainForm.DisplayVersion} ({MainForm.Version})");
            return 0;
        }
        int ai = Array.IndexOf(args, "--apply-update");
        if (ai >= 0 && ai + 1 < args.Length)
        {
            string? A(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            return Updater.ApplyUpdate(args[ai + 1], int.TryParse(A("--pid"), out var pid) ? pid : 0, A("--dir") ?? Updater.InstallDir, A("--from") ?? "?", A("--to") ?? "?");
        }
        if (args.Contains("--gamecheck"))
        {
            Native.AttachConsole(-1);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            int goi = Array.IndexOf(args, "--out");
            string? outPath = goi >= 0 && goi + 1 < args.Length ? args[goi + 1] : null;
            var sb = new StringBuilder();
            void Line(string s) { Console.WriteLine(s); sb.AppendLine(s); }

            var det = new GameDetector();
            Line($"gamecheck: Universal-FrameFX {MainForm.Version}  (visible top-level windows)");
            Line("");
            foreach (var w in Native.ListWindows())
            {
                var v = det.Classify(w.Handle);
                string title = Native.GetWindowTextLength(w.Handle) > 0 ? w.Title : "";
                Line($"{(v.IsGame ? "GAME " : "no   ")}  exe={v.Exe,-28} pid={v.Pid,-7} reason: {v.Reason,-40} title: {title}");
            }
            Line("");
            IntPtr fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out uint fgPid);
            var vf = det.Classify(fg);
            Line($"foreground: {(vf.IsGame ? "GAME" : "not a game")}  exe={vf.Exe}  pid={(int)fgPid}  reason: {vf.Reason}");
            Line("");

            const int runs = 400;
            var sw = Stopwatch.StartNew();
            double max = 0;
            for (int i = 0; i < runs; i++)
            {
                det.Classify(Native.GetForegroundWindow());
                max = Math.Max(max, det.LastClassifyMs);
            }
            sw.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            Line($"timing: {runs} Classify(GetForegroundWindow()) calls with warm cache");
            Line($"  avg {sw.Elapsed.TotalMilliseconds / runs:F4} ms/call (running avg {det.AvgClassifyMs:F4} ms), max {max:F4} ms, total {sw.Elapsed.TotalMilliseconds:F1} ms");
            Line($"  process CPU time used: {cpu.TotalMilliseconds:F1} ms");
            if (outPath != null) File.WriteAllText(outPath, sb.ToString());
            return 0;
        }
        Diag.Mark("main");
        {
            int gi = Array.IndexOf(args, "--gpu");
            if (gi >= 0 && gi + 1 < args.Length)
                MainForm.CliGpu = args[gi + 1].ToLowerInvariant() switch { "igpu" or "integrated" => GpuChoice.Integrated, "dgpu" or "dedicated" => GpuChoice.Dedicated, _ => GpuChoice.Auto };
        }
        Diag.InstallCrashHandlers();
        try { CpuBoostWin.StartupRecover(); } catch { }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Diag.WriteCrash("ui thread", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails were saved to " + Diag.CrashLog, "Universal-FrameFX", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        MainForm form;
        try { form = new MainForm(); }
        catch (Exception ex) { Diag.WriteCrash("startup", ex); throw; }
        Diag.Mark("main form constructed");
        int ei = Array.IndexOf(args, "--exit-after");
        int exitAfter = ei >= 0 && ei + 1 < args.Length && int.TryParse(args[ei + 1], out var n) ? n : 0;
        int oi = Array.IndexOf(args, "--out");
        int si = Array.IndexOf(args, "--source");
        int mi = Array.IndexOf(args, "--motion");
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        form.AutoBackend = Arg("--backend");
        form.AutoRes = Arg("--res");
        form.AutoFgKind = Arg("--fgkind");
        form.AutoFgMul = Arg("--fgx");
        form.AutoPreset = Arg("--preset");
        form.CliCompareOff = args.Contains("--compare-off");
        form.UpdateMarker = Arg("--update-marker");
        form.UpdatedFrom = Arg("--updated-from");
        form.UpdateFailed = Arg("--update-failed");
        string? mode = args.Contains("--overlay") ? "overlay" : args.Contains("--fullscreen") ? "fullscreen" : "window";
        form.Automate(args.Contains("--demo"), exitAfter, oi >= 0 && oi + 1 < args.Length ? args[oi + 1] : null,
                      si >= 0 && si + 1 < args.Length ? args[si + 1] : null, mode,
                      mi >= 0 && mi + 1 < args.Length ? args[mi + 1] : null, args.Contains("--fg"));
        Application.Run(form);
        return 0;
    }
}
