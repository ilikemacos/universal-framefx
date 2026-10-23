using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UniversalFrameFX;

internal static class OfflineFg
{
    static readonly List<double> realMsAll = new();
    static readonly Dictionary<string, double> realBreak = new();
    const int Warmup = 5;

    public static int Run(string inDir, string outDir, string[] args)
    {
        Directory.CreateDirectory(outDir);
        string logPath = Path.Combine(outDir, "log.txt");
        var orig = Console.Out;
        using var log = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        Console.SetOut(new ScoreHelpers.TeeWriter(orig, log));
        try
        {
            return RunCore(inDir, outDir, args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("offline-fg: FAIL " + ex);
            return 1;
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    static int RunCore(string inDir, string outDir, string[] args)
    {
        inDir = Path.GetFullPath(inDir);
        outDir = Path.GetFullPath(outDir);
        int n = 2;
        int fi = Array.IndexOf(args, "--fgx");
        if (fi >= 0 && fi + 1 < args.Length && int.TryParse(args[fi + 1].TrimEnd('x', 'X'), out var nn)) n = nn;
        string mode = "classic";
        int mi = Array.IndexOf(args, "--mode");
        if (mi >= 0 && mi + 1 < args.Length) mode = args[mi + 1].Trim().ToLowerInvariant();
        bool oracle = mode is "oracle";
        bool csrAi = mode is "csrai";
        bool csr2 = oracle || mode is "csr2" or "v2" or "fgv2" or "csrai";
        bool fast = args.Contains("--fast");
        var backend = Backend.Temporal;
        int bi = Array.IndexOf(args, "--backend");
        if (bi >= 0 && bi + 1 < args.Length && Enum.TryParse<Backend>(args[bi + 1], true, out var bk)) backend = bk;
        n = Math.Clamp(n, 2, (csr2 || oracle) ? 20 : 8);
        if (!csr2 && mode != "classic")
        {
            Console.WriteLine("offline-fg: --mode must be classic, csr2, csrai, or oracle");
            return 2;
        }
        string? dumpFlow = null;
        int dfi = Array.IndexOf(args, "--dump-flow");
        if (dfi >= 0 && dfi + 1 < args.Length) dumpFlow = Path.GetFullPath(args[dfi + 1]);
        if (dumpFlow != null && mode is not "csr2" and not "v2" and not "fgv2")
        {
            Console.WriteLine("offline-fg: --dump-flow requires --mode csr2");
            return 2;
        }
        string? dumpFlux = null;
        int dxi = Array.IndexOf(args, "--dump-flux-debug");
        if (dxi >= 0 && dxi + 1 < args.Length) dumpFlux = Path.GetFullPath(args[dxi + 1]);
        int dumpFluxAt = -1;
        int dai = Array.IndexOf(args, "--dump-flux-at");
        if (dai >= 0 && dai + 1 < args.Length && int.TryParse(args[dai + 1], out var dat)) dumpFluxAt = dat;
        if (dumpFlux != null)
        {
            if (!csrAi)
            {
                Console.WriteLine("offline-fg: --dump-flux-debug requires --mode csrai");
                return 2;
            }
            Directory.CreateDirectory(dumpFlux);
        }
        string? mvDir = null;
        int mvi = Array.IndexOf(args, "--mv");
        if (mvi >= 0 && mvi + 1 < args.Length) mvDir = Path.GetFullPath(args[mvi + 1]);
        if (oracle && string.IsNullOrEmpty(mvDir))
        {
            Console.WriteLine("offline-fg: --mode oracle requires --mv DIR");
            return 2;
        }
        bool score = args.Contains("--score");
        int saveEvery = 1;
        int sei = Array.IndexOf(args, "--save-every");
        if (sei >= 0 && sei + 1 < args.Length && int.TryParse(args[sei + 1], out var sm)) saveEvery = Math.Max(0, sm);
        int gi = Array.IndexOf(args, "--gpu");
        if (gi >= 0 && gi + 1 < args.Length)
            Gpu.Choice = args[gi + 1].ToLowerInvariant() switch
            {
                "igpu" or "integrated" => GpuChoice.Integrated,
                "dgpu" or "dedicated" => GpuChoice.Dedicated,
                _ => GpuChoice.Auto,
            };

        var files = Directory.GetFiles(inDir, "*.png").Concat(Directory.GetFiles(inDir, "*.PNG")).Distinct().OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < n + 1)
        {
            Console.WriteLine($"offline-fg: need more than {n} PNGs in {inDir} (got {files.Length})");
            return 2;
        }
        var times = LoadTimes(inDir, files);
        MvMeta? mv = oracle ? LoadMvMeta(mvDir!, n) : null;

        Console.WriteLine($"offline-fg in={inDir} out={outDir} fgx={n} mode={mode} gpu={Gpu.Choice} frames={files.Length} score={score} save-every={saveEvery}");
        var scores = new List<ScoreRow>();
        FgV2? v2 = null;
        string estimatorName = "pyramid";
        bool csraiFallback = false;
        string csraiFailReason = "";
        lock (Gpu.Lock)
        {
            using var gpu = new Gpu();
            gpu.TimingEnabled = true;
            using var pipe = new FramePipeline(gpu)
            {
                FrameGenKind = FgKind.FrameFX,
                UseV2 = csr2,
                UseCsrAiFg = csrAi,
                FgV2VsyncGen = csr2,
                Fast = false,
                SkipUnchanged = false,
                Motion = ParseEstimatorPref(),
                MotionGapN = n,
            };
            Console.WriteLine($"gpu: {gpu.AdapterName} ({gpu.KindName})");
            var loaded = new Tex?[files.Length];
            Tex? Load(int i)
            {
                loaded[i] ??= gpu.LoadPng(files[i]);
                return loaded[i];
            }
            byte[]?[] gt = score ? new byte[]?[files.Length] : Array.Empty<byte[]?>();
            byte[] Gt(int i)
            {
                gt[i] ??= ScoreHelpers.LoadPngRgb8(files[i]);
                return gt[i]!;
            }

            string csvPath = Path.Combine(outDir, "timing.csv");
            using var csv = new StreamWriter(csvPath, false, Encoding.UTF8) { AutoFlush = true };
            csv.WriteLine("index,kind,content_time_s,gpu_ms,filename");
            StreamWriter? scsv = null;
            if (score)
            {
                scsv = new StreamWriter(Path.Combine(outDir, "score.csv"), false, Encoding.UTF8) { AutoFlush = true };
                scsv.WriteLine("index,k,t,psnr,gpu_ms");
            }
            int realCount = 0;
            int genSeq = 0;
            realMsAll.Clear();
            realBreak.Clear();
            Tex? prevOut = null;
            int prevReal = -1;
            Tex? mvB = null, mvF = null;
            int mvAt = -1;
            CsrAiFg? dumpFg = null;
            var dumpPairs = new List<int>();
            if (dumpFlow != null) Directory.CreateDirectory(dumpFlow);
            for (int i = 0; i < files.Length; i += n)
            {
                var src = Load(i)!;
                int w = src.Width, h = src.Height;
                if (mv != null && (mv.Width != w || mv.Height != h))
                    throw new InvalidOperationException($"offline-fg: PNG {w}x{h} != mv json {mv.Width}x{mv.Height}");
                gpu.Sync();
                gpu.ProfileBegin();
                var (curr, _) = pipe.Process(src.Srv, w, h, w, h, backend, 0, 0, true, n);
                var prof = gpu.ProfileEnd();
                if (dumpFlow != null) gpu.SavePng(curr, Path.Combine(dumpFlow, string.Create(CultureInfo.InvariantCulture, $"out_{i:D6}.png")));
                v2 = pipe.V2;
                if (!string.IsNullOrEmpty(pipe.FlowEstimatorName)) estimatorName = pipe.FlowEstimatorName;
                bool record = realCount >= Warmup;
                realCount++;
                double? realMs = SplitReal(prof);
                if (realCount > 1 && ProfileTotal(prof) is double rt)
                {
                    realMsAll.Add(rt);
                    foreach (var kv in prof) if (kv.Key != "total") realBreak[kv.Key] = realBreak.GetValueOrDefault(kv.Key) + kv.Value;
                }
                if (record)
                    WriteRow(csv, i, "real", times[i], realMs, Path.GetFileName(files[i]));

                if (prevOut != null && prevReal >= 0)
                {
                    bool cut = oracle && mv != null && HasCut(mv.Cuts, prevReal, i);
                    if (oracle)
                    {
                        if (!cut && (mvB == null || mvAt != i))
                        {
                            mvB?.Dispose(); mvF?.Dispose();
                            mvB = gpu.UploadRg16(w, h, File.ReadAllBytes(MvPath(mvDir!, "mvb", n, i)));
                            mvF = gpu.UploadRg16(w, h, File.ReadAllBytes(MvPath(mvDir!, "mvf", n, i)));
                            mvAt = i;
                        }
                        for (int k = 1; k < n; k++)
                        {
                            int giOut = prevReal + k;
                            if (giOut >= files.Length) break;
                            float t = k / (float)n;
                            gpu.Sync();
                            gpu.ProfileBegin();
                            Tex? gen;
                            if (cut)
                                gen = pipe.V2Blit(t < 0.5f ? prevOut : curr);
                            else
                                gen = pipe.V2GenerateOracle(prevOut, curr, mvB!, mvF!, t);
                            var gp = gpu.ProfileEnd();
                            EmitGen(gpu, files, outDir, csv, scsv, scores, Gt, giOut, k, t, times, prevReal, i, record, saveEvery, ref genSeq, score, gen, ProfileTotal(gp), Path.GetFileName(files[giOut]));
                        }
                    }
                    else if (csr2)
                    {
                        if (dumpFlow != null && pipe.V2 != null)
                        {
                            dumpFg ??= new CsrAiFg(gpu, true);
                            dumpFg.PackA(pipe.V2, prevOut, curr);
                            WriteDumpFlow(gpu, pipe.V2, dumpFg, dumpFlow, prevReal, i, n, curr.Width, curr.Height);
                            dumpPairs.Add(i);
                        }
                        if (csrAi) pipe.CsrAiFgBeginPair();
                        for (int k = 1; k < n; k++)
                        {
                            int giOut = prevReal + k;
                            if (giOut >= files.Length) break;
                            float t = k / (float)n;
                            gpu.Sync();
                            gpu.ProfileBegin();
                            Tex? gen;
                            if (csrAi) gen = pipe.CsrAiFgGenerateAt(prevOut, curr, t);
                            else gen = fast ? pipe.V2GenerateFastAt(prevOut, curr, t) : pipe.V2GenerateAt(prevOut, curr, t);
                            var gp = gpu.ProfileEnd();
                            if (dumpFlux != null && dumpFluxAt >= 0 && (giOut == dumpFluxAt || i == dumpFluxAt) && pipe.CsrAiFg != null && gen != null)
                                WriteFluxDebug(gpu, pipe.CsrAiFg, dumpFlux, gen, prevReal, i, t, curr.Width, curr.Height);
                            EmitGen(gpu, files, outDir, csv, scsv, scores, Gt, giOut, k, t, times, prevReal, i, record, saveEvery, ref genSeq, score, gen, ProfileTotal(gp), Path.GetFileName(files[giOut]));
                        }
                    }
                    else
                    {
                        double? genMs = SplitGen(prof);
                        if (genMs is double gtot) genMs = gtot / (n - 1);
                        var gens = pipe.Generated;
                        for (int k = 1; k < n; k++)
                        {
                            int giOut = prevReal + k;
                            if (giOut >= files.Length) break;
                            Tex? gen = k - 1 < gens.Count ? gens[k - 1] : null;
                            if (gen == null) continue;
                            float t = k / (float)n;
                            EmitGen(gpu, files, outDir, csv, scsv, scores, Gt, giOut, k, t, times, prevReal, i, record, saveEvery, ref genSeq, score, gen, genMs, Path.GetFileName(files[giOut]));
                        }
                    }
                }
                prevOut = curr;
                prevReal = i;
                for (int j = Math.Max(0, i - 3 * n); j < i; j++)
                {
                    loaded[j]?.Dispose();
                    loaded[j] = null;
                    if (score) gt[j] = null;
                }
            }
            mvB?.Dispose(); mvF?.Dispose();
            dumpFg?.Dispose();
            if (dumpFlow != null)
            {
                int w = 0, h = 0;
                if (files.Length > 0)
                {
                    using var probe = gpu.LoadPng(files[0]);
                    w = probe.Width; h = probe.Height;
                }
                int qw = (w + 3) / 4, qh = (h + 3) / 4;
                var mb = new StringBuilder();
                mb.Append(FormattableString.Invariant($"{{\"w\":{w},\"h\":{h},\"qw\":{qw},\"qh\":{qh},\"k\":{n},\"pairs\":["));
                mb.Append(string.Join(",", dumpPairs));
                mb.Append("]}");
                File.WriteAllText(Path.Combine(dumpFlow, "manifest.json"), mb.ToString());
            }
            foreach (var t in loaded) t?.Dispose();
            scsv?.Dispose();
            csraiFallback = pipe.CsrAiFgFailed;
            csraiFailReason = pipe.CsrAiFgFailReason ?? "";
            Console.WriteLine($"offline-fg: wrote {csvPath}");
        }
        WriteSummary(outDir, n, mode, score, v2, estimatorName, scores, csraiFallback, csraiFailReason);
        return 0;
    }

    static void WriteFluxDebug(Gpu gpu, CsrAiFg fg, string dir, Tex compose, int prev, int curr, float t, int w, int h)
    {
        gpu.Sync();
        int qw = (w + 3) / 4, qh = (h + 3) / 4;
        int hw = (w + 1) / 2, hh = (h + 1) / 2;
        File.WriteAllBytes(Path.Combine(dir, "debug_pack.f16"), fg.ReadPackF16());
        File.WriteAllBytes(Path.Combine(dir, "debug_neta.f16"), fg.ReadNetAF16());
        File.WriteAllBytes(Path.Combine(dir, "debug_packb.f16"), fg.ReadPackBF16());
        File.WriteAllBytes(Path.Combine(dir, "debug_netb.f16"), fg.ReadNetBF16());
        gpu.SavePng(compose, Path.Combine(dir, "debug_compose.png"));
        string ts = t.ToString("G17", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "debug_meta.json"),
            FormattableString.Invariant($"{{\"prev\":{prev},\"curr\":{curr},\"t\":{ts},\"w\":{w},\"h\":{h},\"qw\":{qw},\"qh\":{qh},\"hw\":{hw},\"hh\":{hh}}}"));
    }

    static void WriteDumpFlow(Gpu gpu, FgV2 v2, CsrAiFg dumpFg, string dir, int prev, int curr, int k, int w, int h)
    {
        int qw = (w + 3) / 4, qh = (h + 3) / 4;
        File.WriteAllBytes(Path.Combine(dir, $"pack_{curr:D6}.f16"), dumpFg.ReadPackF16());
        var hud = gpu.ReadHalf4Any(v2.HudTex);
        var hudU8 = new byte[hud.Length];
        for (int i = 0; i < hud.Length; i++) hudU8[i] = hud[i] > 0.5f ? (byte)255 : (byte)0;
        File.WriteAllBytes(Path.Combine(dir, $"hud_{curr:D6}.u8"), hudU8);
        var chg = gpu.ReadR32(v2.ChgTex);
        bool cut = chg.Length > 1 && chg[1] > 0.6f * 4096f;
        File.WriteAllText(Path.Combine(dir, $"meta_{curr:D6}.json"),
            FormattableString.Invariant($"{{\"prev\":{prev},\"curr\":{curr},\"k\":{k},\"w\":{w},\"h\":{h},\"qw\":{qw},\"qh\":{qh},\"cut\":{(cut ? "true" : "false")}}}"));
    }

    static void EmitGen(Gpu gpu, string[] files, string outDir, StreamWriter csv, StreamWriter? scsv, List<ScoreRow> scores,
        Func<int, byte[]> gt, int index, int k, float t, double?[] times, int prev, int curr, bool record, int saveEvery,
        ref int genSeq, bool score, Tex? gen, double? gpuMs, string name)
    {
        if (gen == null) return;
        bool save = record && saveEvery > 0 && genSeq % saveEvery == 0;
        if (record) genSeq++;
        if (save) gpu.SavePng(gen, Path.Combine(outDir, name));
        if (record) WriteRow(csv, index, "generated", LerpTime(times, prev, curr, t), gpuMs, name);
        if (!score) return;
        byte[] a = gpu.ReadRgb8(gen);
        byte[] b = gt(index);
        double psnr = ScoreHelpers.PsnrRgb8(a, b);
        scores.Add(new ScoreRow(index, k, t, psnr, gpuMs));
        string ts = t.ToString("G17", CultureInfo.InvariantCulture);
        string ps = psnr.ToString("G17", CultureInfo.InvariantCulture);
        string gs = gpuMs is double gv ? gv.ToString("G17", CultureInfo.InvariantCulture) : "";
        scsv!.WriteLine($"{index},{k},{ts},{ps},{gs}");
    }

    static void WriteSummary(string outDir, int n, string mode, bool score, FgV2? v2, string estimatorName, List<ScoreRow> scores, bool csraiFallback, string csraiFailReason)
    {
        var sb = new StringBuilder();
        float conf = v2?.ConfThresh ?? 0.35f;
        float st = v2?.StaticThresh ?? 1.5f / 255f;
        float cg = v2?.ColorGate ?? 0.08f;
        float vk = v2?.VecLenK ?? 0.04f;
        float ot = v2?.OcclThresh ?? 1f;
        float rp = v2?.RansacPx ?? 1.75f;
        float fb = v2?.Fallback ?? 0f;
        string est = v2?.EstimatorLabel ?? ParseEstimatorLabel();
        sb.AppendLine(FormattableString.Invariant($"mode={mode} fgx={n}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_CONF_THRESH={conf}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_STATIC_THRESH={st}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_COLOR_GATE={cg}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_VECLEN_K={vk}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_OCCL_THRESH={ot}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_RANSAC_PX={rp}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_FALLBACK={fb}"));
        sb.AppendLine($"UFX_FG2_ESTIMATOR={est}");
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_WIDE_SEARCH={v2?.WideSearchOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_WIDE_SEARCH") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_CLASSIC_CAND={v2?.ClassicCandOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_CLASSIC_CAND") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_EDGE_SMOOTH={v2?.EdgeSmoothOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_EDGE_SMOOTH") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_AMBIGUITY_GATE={v2?.AmbiguityGateOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_AMBIGUITY_GATE") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_TEMPORAL_EXTRAP={v2?.TemporalExtrapOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_TEMPORAL_EXTRAP") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_BICUBIC={v2?.BicubicOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_BICUBIC") == "1" ? 1 : 0)}"));
        sb.AppendLine(FormattableString.Invariant($"UFX_FG2_SHARP_BLEND={v2?.SharpBlendOn ?? (Environment.GetEnvironmentVariable("UFX_FG2_SHARP_BLEND") == "1" ? 1 : 0)}"));
        sb.AppendLine($"estimator={estimatorName}");
        sb.AppendLine(FormattableString.Invariant($"csrai_fallback={(csraiFallback ? "true" : "false")}"));
        sb.AppendLine($"csrai_fail_reason={csraiFailReason}");
        if (score && scores.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant($"mean_psnr={scores.Average(r => r.Psnr)}"));
            foreach (var g in scores.GroupBy(r => r.K).OrderBy(g => g.Key))
                sb.AppendLine(FormattableString.Invariant($"mean_psnr_k{g.Key}={g.Average(r => r.Psnr)}"));
            var ms = scores.Where(r => r.GpuMs is double).Select(r => r.GpuMs!.Value).ToList();
            if (ms.Count > 0)
            {
                sb.AppendLine(FormattableString.Invariant($"median_gpu_ms={ScoreHelpers.Percentile(ms, 0.5)}"));
                sb.AppendLine(FormattableString.Invariant($"p95_gpu_ms={ScoreHelpers.Percentile(ms, 0.95)}"));
            }
        }
        if (realMsAll.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant($"median_real_gpu_ms={ScoreHelpers.Percentile(realMsAll, 0.5)}"));
            foreach (var kv in realBreak.OrderByDescending(k => k.Value).Take(12))
                sb.AppendLine(FormattableString.Invariant($"real_ms_{kv.Key}={kv.Value / realMsAll.Count:0.###}"));
        }
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString());
        Console.WriteLine($"offline-fg: wrote {Path.Combine(outDir, "summary.txt")}");
    }

    static MotionPreference ParseEstimatorPref()
    {
        string est = Environment.GetEnvironmentVariable("UFX_FG2_ESTIMATOR")?.Trim().ToLowerInvariant() ?? "";
        return est switch
        {
            "pyramid" or "sw" or "software" => MotionPreference.Software,
            "nvof" => MotionPreference.Nvof,
            _ => MotionPreference.Auto,
        };
    }

    static string ParseEstimatorLabel()
    {
        string est = Environment.GetEnvironmentVariable("UFX_FG2_ESTIMATOR")?.Trim().ToLowerInvariant() ?? "auto";
        return est is "pyramid" or "sw" or "software" ? "pyramid" : est is "nvof" ? "nvof" : "auto";
    }

    sealed record MvMeta(int Width, int Height, HashSet<int> Cuts);

    static MvMeta LoadMvMeta(string dir, int n)
    {
        string path = Path.Combine(dir, $"mv_k{n}.json");
        if (!File.Exists(path)) throw new FileNotFoundException("offline-fg: missing " + path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        int w = root.GetProperty("width").GetInt32();
        int h = root.GetProperty("height").GetInt32();
        var cuts = new HashSet<int>();
        if (root.TryGetProperty("cuts", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
                cuts.Add(c.GetInt32());
        }
        return new MvMeta(w, h, cuts);
    }

    static bool HasCut(HashSet<int> cuts, int prev, int curr)
    {
        for (int i = prev + 1; i <= curr; i++)
            if (cuts.Contains(i)) return true;
        return false;
    }

    static string MvPath(string dir, string kind, int n, int i) =>
        Path.Combine(dir, $"{kind}_k{n}_{i:D6}.bin");

    static double?[] LoadTimes(string inDir, string[] files)
    {
        var t = new double?[files.Length];
        string csv = Path.Combine(inDir, "frames.csv");
        if (!File.Exists(csv)) return t;
        var byName = files.Select((p, i) => (Path.GetFileName(p), i)).ToDictionary(x => x.Item1, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            if (p.Length < 3) continue;
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tv)) continue;
            string name = Path.GetFileName(p[2].Trim());
            if (byName.TryGetValue(name, out int i)) t[i] = tv;
            else if (int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) && idx >= 0 && idx < t.Length) t[idx] = tv;
        }
        return t;
    }

    static double? LerpTime(double?[] times, int a, int b, double u)
    {
        if (times[a] is double ta && times[b] is double tb) return ta + (tb - ta) * u;
        return null;
    }

    static void WriteRow(StreamWriter csv, int index, string kind, double? t, double? gpuMs, string filename)
    {
        string ts = t is double tv ? tv.ToString("G17", CultureInfo.InvariantCulture) : "";
        string gs = gpuMs is double gv ? gv.ToString("G17", CultureInfo.InvariantCulture) : "";
        csv.WriteLine($"{index},{kind},{ts},{gs},{filename}");
    }

    static double? ProfileTotal(Dictionary<string, double> r) => r.TryGetValue("total", out var t) ? t : null;

    static bool IsGenKernel(string k) =>
        k.Contains("Interp", StringComparison.Ordinal) || k is "FX_FgPrep" or "FG2_Cleanup" or "FG2_Extrap" || k.Contains("framegen", StringComparison.Ordinal);

    static double? SplitReal(Dictionary<string, double> r)
    {
        if (r.Count == 0) return null;
        double gen = r.Where(kv => kv.Key != "total" && IsGenKernel(kv.Key)).Sum(kv => kv.Value);
        if (r.TryGetValue("total", out var tot)) return Math.Max(0, tot - gen);
        return r.Where(kv => kv.Key != "total" && !IsGenKernel(kv.Key)).Sum(kv => kv.Value);
    }

    static double? SplitGen(Dictionary<string, double> r)
    {
        if (r.Count == 0) return null;
        double gen = r.Where(kv => kv.Key != "total" && IsGenKernel(kv.Key)).Sum(kv => kv.Value);
        return gen > 0 ? gen : null;
    }

    readonly record struct ScoreRow(int Index, int K, float T, double Psnr, double? GpuMs);

}
