using System.Globalization;
using System.Text;

namespace UniversalFrameFX;

internal static class OfflineUpscale
{
    public static int Run(string lowResDir, string nativeDir, string outDir, string[] args)
    {
        Directory.CreateDirectory(outDir);
        string logPath = Path.Combine(outDir, "log.txt");
        var orig = Console.Out;
        using var log = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        Console.SetOut(new ScoreHelpers.TeeWriter(orig, log));
        try
        {
            return RunCore(lowResDir, nativeDir, outDir, args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("offline-upscale: FAIL " + ex);
            return 1;
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    static int RunCore(string lowResDir, string nativeDir, string outDir, string[] args)
    {
        lowResDir = Path.GetFullPath(lowResDir);
        nativeDir = Path.GetFullPath(nativeDir);
        outDir = Path.GetFullPath(outDir);
        var backend = Backend.Temporal;
        int bi = Array.IndexOf(args, "--backend");
        if (bi >= 0 && bi + 1 < args.Length) backend = BackendNames.Parse(args[bi + 1]);
        int preset = 0;
        int pi = Array.IndexOf(args, "--preset");
        if (pi >= 0 && pi + 1 < args.Length)
        {
            string n = args[pi + 1].Trim().ToLowerInvariant();
            preset = n is "quality" ? 1 : n is "competitive" ? 2 : 0;
        }
        preset = GameProfiles.Sanitize(new GameProfile { Preset = preset }).Preset;
        int gi = Array.IndexOf(args, "--gpu");
        if (gi >= 0 && gi + 1 < args.Length)
            Gpu.Choice = args[gi + 1].ToLowerInvariant() switch
            {
                "igpu" or "integrated" => GpuChoice.Integrated,
                "dgpu" or "dedicated" => GpuChoice.Dedicated,
                _ => GpuChoice.Auto,
            };

        var lows = Pngs(lowResDir);
        var natives = Pngs(nativeDir);
        if (lows.Length == 0)
        {
            Console.WriteLine("offline-upscale: no PNGs in " + lowResDir);
            return 2;
        }
        var lowNames = lows.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var natNames = natives.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!lowNames.SetEquals(natNames))
        {
            Console.WriteLine("offline-upscale: low-res and native directories must have the same PNG file names");
            return 2;
        }
        var natByName = natives.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"offline-upscale low={lowResDir} native={nativeDir} out={outDir} backend={BackendNames.Long(backend)} preset={GameProfiles.PresetName(preset)} gpu={Gpu.Choice} frames={lows.Length}");
        var gpuMs = new List<double>();
        var psnrs = new List<double>();
        lock (Gpu.Lock)
        {
            using var gpu = new Gpu();
            gpu.TimingEnabled = true;
            using var pipe = new FramePipeline(gpu)
            {
                Fast = preset != 1,
                SkipUnchanged = false,
                UseV2 = false,
            };
            Console.WriteLine($"gpu: {gpu.AdapterName} ({gpu.KindName})");
            using var gtBmp = new System.Drawing.Bitmap(natives[0]);
            int nw = gtBmp.Width, nh = gtBmp.Height;
            string csvPath = Path.Combine(outDir, "timing.csv");
            using var csv = new StreamWriter(csvPath, false, Encoding.UTF8) { AutoFlush = true };
            csv.WriteLine("index,gpu_ms,psnr,filename");
            for (int i = 0; i < lows.Length; i++)
            {
                string name = Path.GetFileName(lows[i]);
                var src = gpu.LoadPng(lows[i]);
                byte[] gt = ScoreHelpers.LoadPngRgb8(natByName[name]);
                gpu.Sync();
                gpu.ProfileBegin();
                var (outT, _) = pipe.Process(src.Srv, src.Width, src.Height, nw, nh, backend, 0, 0, false);
                var prof = gpu.ProfileEnd();
                double ms = prof.TryGetValue("total", out var t) ? t : 0;
                gpu.SavePng(outT, Path.Combine(outDir, name));
                byte[] got = gpu.ReadRgb8(outT);
                double psnr = ScoreHelpers.PsnrRgb8(got, gt);
                gpuMs.Add(ms);
                psnrs.Add(psnr);
                csv.WriteLine(string.Join(",", i.ToString(CultureInfo.InvariantCulture),
                    ms.ToString("G17", CultureInfo.InvariantCulture),
                    psnr.ToString("G17", CultureInfo.InvariantCulture),
                    name));
                src.Dispose();
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"backend={BackendNames.Long(backend)}"));
        sb.AppendLine(FormattableString.Invariant($"preset={GameProfiles.PresetName(preset)}"));
        sb.AppendLine(FormattableString.Invariant($"frames={lows.Length}"));
        if (gpuMs.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant($"median_gpu_ms={ScoreHelpers.Percentile(gpuMs, 0.5)}"));
            sb.AppendLine(FormattableString.Invariant($"p95_gpu_ms={ScoreHelpers.Percentile(gpuMs, 0.95)}"));
        }
        if (psnrs.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant($"mean_psnr={psnrs.Average()}"));
            sb.AppendLine(FormattableString.Invariant($"median_psnr={ScoreHelpers.Percentile(psnrs, 0.5)}"));
        }
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString());
        Console.WriteLine("offline-upscale: wrote " + Path.Combine(outDir, "summary.txt"));
        return 0;
    }

    static string[] Pngs(string dir) =>
        Directory.GetFiles(dir, "*.png").Concat(Directory.GetFiles(dir, "*.PNG")).Distinct()
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
}
