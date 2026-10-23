using System.Diagnostics;
using Vortice.DXGI;

namespace UniversalFrameFX;

/// <summary>Headless GPU self-test: builds every shader, probes the motion hardware, then measures
/// quality against ground truth on the analytic test scene. No capture permission needed.</summary>
public static class SelfTest
{
    static double Psnr(float[] a, float[] b)
    {
        double mse = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; mse += d * d; }
        mse /= a.Length;
        return mse <= 1e-12 ? 99 : 10 * Math.Log10(1.0 / mse);
    }
    static bool HasNaN(float[] a) { foreach (var v in a) if (float.IsNaN(v)) return true; return false; }

    public static string? DumpDir;

    /// <summary>One column of the comparison: an upscaler under a preset.</summary>
    sealed record Col(string Name, Backend B, bool Fast);

    /// <summary>1.3.1 comparison at 1440p and 4K: CSR 1.3 Performance vs CSR 1.3 Quality (the 1.3 pipeline),
    /// the vendor upscalers (real runtimes) and CSR 1.2; GPU time per frame; frame generation.</summary>
    static bool VendorTests(Gpu gpu, Action<string> log)
    {
        bool ok = true;
        log("");
        log("Third-party upscalers (vendor runtimes, fed CSR motion vectors + flat depth):");
        VendorSupport.Probe(gpu);
        var opts = new[] { Backend.Fsr1, Backend.Fsr2, Backend.Fsr3, Backend.Fsr4, Backend.XeSS };
        foreach (var b in opts)
        {
            var i = VendorSupport.Get(b);
            log($"  {BackendNames.Long(b),-12} {(i.Ok ? "available - " + i.Detail : "unavailable - " + i.Why + (i.Detail.Length > 0 ? $" ({i.Detail})" : ""))}");
        }
        var fgi = VendorSupport.FsrFrameGen;
        log($"  {"AMD FSR 3 FG",-12} {(fgi.Ok ? "available - " + fgi.Detail : "unavailable - " + fgi.Why)}");
        var cols = new List<Col> { new("CSR1.3-P", Backend.Temporal, true), new("CSR1.3-Q", Backend.Temporal, false) };
        cols.AddRange(opts.Where(b => VendorSupport.Get(b).Ok).Select(b => new Col(ColName(b), b, true)));
        cols.Add(new("CSR1.2", Backend.Spatial, true));

        var scen = new (string name, bool jitter, bool animate, bool pan, bool hud)[]
        {
            ("static + jitter (test-scene render)", true, false, false, false),
            ("animated + jitter", true, true, false, false),
            ("still, no jitter (desktop capture)", false, false, false, false),
            ("slow pan, no jitter (scrolling capture)", false, false, true, false),
            ("pan + static HUD, no jitter (game capture)", false, false, true, true),
        };
        const int frames = 32, scored = 8;
        lock (Gpu.Lock)
        {
            foreach (var (label, inW, inH, outW, outH) in new[] { ("1440p", 1280, 720, 2560, 1440), ("4K", 1920, 1080, 3840, 2160) })
            {
                using var pipe = new FramePipeline(gpu);
                using var low = new Tex(gpu.Device, inW, inH, Format.R8G8B8A8_UNorm);
                using var truth = new Tex(gpu.Device, outW, outH, Format.R8G8B8A8_UNorm);
                int phases = Jitter.PhaseCount(inW, outW);
                log("");
                log($"{label}: {inW}x{inH} -> {outW}x{outH}, PSNR dB (mean of last {scored} of {frames} frames) vs 4x4-supersampled truth. -P = Performance preset, -Q = Quality preset (1.3 pipeline); vendor columns use the Performance motion path.");
                log($"  {"scenario",-44} {string.Join(" ", cols.Select(c => $"{c.Name,9}"))}");
                var names = new Dictionary<string, string>();
                foreach (var sc in scen)
                {
                    var row = new List<string>();
                    foreach (var c in cols)
                    {
                        pipe.Fast = c.Fast; pipe.TestHud = sc.hud; pipe.Motion = MotionPreference.Auto; pipe.Sharpness = 0.25f;
                        pipe.ClearVendorFailures();
                        pipe.Ensure(inW, inH, outW, outH); pipe.Reset();
                        double sum = 0; Tex? o = null;
                        for (int f = 0; f < frames; f++)
                        {
                            float time = sc.animate ? 2f + f / 60f : 2f;
                            float px = sc.pan ? f * 0.37f / inW : 0, py = sc.pan ? f * 0.11f / inH : 0;
                            var (jx, jy) = sc.jitter && Backends.IsTemporal(c.B) ? Jitter.Offset(f, phases) : (0f, 0f);
                            pipe.EncodeTestScene(low, time, jx, jy, px, py);
                            (o, _) = pipe.Process(low.Srv, inW, inH, outW, outH, c.B, jx, jy, false);
                            if (f >= frames - scored)
                            {
                                pipe.EncodeTestSceneTruth(truth, time, px, py);
                                var t = gpu.ReadRgb(o);
                                if (HasNaN(t)) { log($"FAIL: NaN in {c.Name} output"); ok = false; }
                                sum += Psnr(t, gpu.ReadRgb(truth));
                            }
                        }
                        names[c.Name] = pipe.ActiveUpscaler;
                        if (Environment.GetEnvironmentVariable("UFX_MVDIAG") == "1" && c.B == Backend.Temporal && pipe.LastMotion is { } lm)
                        {
                            var mv = gpu.ReadHalf4(lm); int n = mv.Length / 4;
                            var xs = new float[n]; var ys = new float[n]; double cf = 0;
                            for (int i = 0; i < n; i++) { xs[i] = mv[i * 4]; ys[i] = mv[i * 4 + 1]; cf += mv[i * 4 + 2]; }
                            Array.Sort(xs); Array.Sort(ys);
                            log($"  mvdiag {sc.name} {c.Name}: {lm.Width}x{lm.Height} median ({xs[n / 2]:0.000}, {ys[n / 2]:0.000}) p10/p90 x {xs[n / 10]:0.00}/{xs[n * 9 / 10]:0.00} conf {cf / n:0.00}");
                        }
                        if (pipe.VendorNote is { } note && Backends.IsVendor(c.B)) { row.Add($"{"fail",9}"); log("  note: " + note); continue; }
                        row.Add($"{sum / scored,9:0.00}");
                        if (DumpDir != null && o != null && sc.hud) gpu.SavePng(o, Path.Combine(DumpDir, $"cmp_{label}_{c.Name}.png"));
                    }
                    log($"  {sc.name,-44} {string.Join(" ", row)}");
                }
                // GPU time per frame: timestamps around Process only (the test-scene render is excluded), median of 30
                // frames on a panning scene with a HUD, where every tile changes (worst case).
                var gpuMs = new List<string>(); var wall = new List<string>();
                var br = new Dictionary<string, Dictionary<string, double>>();
                foreach (var c in cols)
                {
                    var (g, w, d) = Bench(gpu, pipe, low, c.B, c.Fast, inW, inH, outW, outH, false, FgKind.FrameFX, true);
                    gpuMs.Add($"{g,9:0.00}"); wall.Add($"{w,9:0.00}"); br[c.Name] = d;
                }
                log($"  {"GPU ms/frame, all tiles changing (median)",-44} {string.Join(" ", gpuMs)}");
                log($"  {"  throughput ms/frame (60 frames, wall)",-44} {string.Join(" ", wall)}");
                var vq = cols.Where(c => Backends.IsVendor(c.B)).Select(c => (c.Name, ms: Bench(gpu, pipe, low, c.B, false, inW, inH, outW, outH, false, FgKind.FrameFX, true).gpu));
                log($"  vendor options with the Quality motion path, GPU ms: {string.Join(", ", vq.Select(v => $"{v.Name} {v.ms:0.00}"))}");
                foreach (var c in cols.Where(c => Backends.IsVendor(c.B)))
                    if (br[c.Name].TryGetValue("vendor", out var vm)) log($"    {c.Name} vendor runtime (D3D12 timestamps) {vm:0.00} ms of that");
                var still = Bench(gpu, pipe, low, Backend.Temporal, true, inW, inH, outW, outH, false, FgKind.FrameFX, false);
                var stillQ = Bench(gpu, pipe, low, Backend.Temporal, false, inW, inH, outW, outH, false, FgKind.FrameFX, false);
                log($"  still desktop (nothing changes; tiles skipped): CSR1.3-P {still.gpu:0.00} ms, CSR1.3-Q {stillQ.gpu:0.00} ms");
                var fgP = Bench(gpu, pipe, low, Backend.Temporal, true, inW, inH, outW, outH, true, FgKind.FrameFX, true);
                var fgQ = Bench(gpu, pipe, low, Backend.Temporal, false, inW, inH, outW, outH, true, FgKind.FrameFX, true);
                string fgA = VendorSupport.FsrFrameGen.Ok ? $"{Bench(gpu, pipe, low, Backend.Temporal, true, inW, inH, outW, outH, true, FgKind.Fsr3, true).gpu:0.00}" : "n/a";
                log($"  CSR 1.3 + frame generation, GPU ms per source frame (upscale + 1 generated frame = 2 output frames): Performance + FrameFX FG {fgP.gpu:0.00} · Quality + FrameFX FG {fgQ.gpu:0.00} · Performance + AMD FSR 3 FG {fgA}");
                // 1.3.4: frame-generation multiplier cost. GPU ms per source frame for ×2/×3/×4/×8 and the cost of each
                // generated frame ((total - upscale only) / (N - 1)), Performance preset, panning scene (no frame skipped).
                var mulRuns = new List<(string lab, Backend b, FgKind k)> { ("CSR 1.3 + FrameFX", Backend.Temporal, FgKind.FrameFX) };
                if (VendorSupport.FsrFrameGen.Ok && VendorSupport.Get(Backend.Fsr3).Ok) mulRuns.Add(("AMD FSR 3 + FSR 3 motion", Backend.Fsr3, FgKind.Fsr3));
                foreach (var (lab, mb, mk) in mulRuns)
                {
                    double up0 = Bench(gpu, pipe, low, mb, true, inW, inH, outW, outH, false, mk, true).gpu;
                    var cells = new List<string>(); var tots = new Dictionary<int, double>();
                    foreach (int n in FgMul.Allowed)
                    {
                        double tot = Bench(gpu, pipe, low, mb, true, inW, inH, outW, outH, true, mk, true, n).gpu;
                        tots[n] = tot;
                        cells.Add($"×{n} {tot:0.00} ms ({(tot - up0) / (n - 1):0.000}/gen)");
                    }
                    log($"  Frame generation multiplier cost, {lab} (upscale only {up0:0.00} ms): {string.Join(" · ", cells)}  [{pipe.ActiveFrameGen}]");
                    foreach (double baseFps in new[] { 30.0, 60.0 })
                    {
                        var caps = new List<string>();
                        foreach (int hz in new[] { 60, 120, 165, 240 })
                        {
                            int best = 0;
                            foreach (int n in FgMul.Allowed)
                                if (baseFps * n <= hz * 1.0 + 1e-6 && tots[n] * baseFps / 1000.0 <= 0.35) best = n;
                            caps.Add($"{hz} Hz ×{(best >= 2 ? best.ToString() : "-")}");
                        }
                        log($"    max multiplier at base {baseFps:0} fps (refresh fit + 35% GPU budget): {string.Join(", ", caps)}");
                    }
                }
                // 1.3.4 latency budget: GPU ms per captured frame by stage for the paths the budget can pick, scaled
                // output and 1:1 (overlay on the game window). Worst case: every tile changes. Present draw not included
                // (no swap chain here; the HUD measures it, ~0.05-0.1 ms).
                var lat = new List<(string lab, Backend b, bool fast, FgKind k, int n, bool fused, bool lowMv)>();
                if (VendorSupport.FsrFrameGen.Ok && VendorSupport.Get(Backend.Fsr3).Ok) lat.Add(("L0 AMD FSR 3 + AMD FSR 3 FG ×2", Backend.Fsr3, true, FgKind.Fsr3, 2, false, false));
                lat.Add(("L0 CSR 1.3-P + FrameFX ×2", Backend.Temporal, true, FgKind.FrameFX, 2, false, false));
                foreach (int n in new[] { 2, 4, 8 }) lat.Add(($"L2 CSR 1.3-P + fused ×{n}", Backend.Temporal, true, FgKind.FrameFX, n, true, false));
                foreach (int n in new[] { 4, 8 }) lat.Add(($"L4 CSR 1.3-P, 1/4 motion + fused ×{n}", Backend.Temporal, true, FgKind.FrameFX, n, true, true));
                foreach (var (lw, lh, tag) in new[] { (outW, outH, $"{inW}x{inH}->{outW}x{outH}"), (inW, inH, $"1:1 {inW}x{inH}") })
                {
                    log($"  Latency budget paths, {tag} (GPU ms per captured frame, budget {LatencyBudget.DefaultMs:0.0} ms):");
                    var rows = new List<(string, Backend, bool, FgKind, int, bool, bool)>(lat);
                    foreach (int n in new[] { 4, 8 }) rows.Add(($"L5 {(lw == inW ? "passthrough" : "CSR 1.2")}, 1/4 motion + fused ×{n}", lw == inW ? Backend.Bilinear : Backend.Spatial, true, FgKind.FrameFX, n, true, true));
                    foreach (var (lab, lb, lf, lk, ln, lfu, llo) in rows)
                    {
                        pipe.FusedFg = lfu; pipe.LowResMotion = llo; pipe.FusedVecMode = 0;
                        var (g, _, d) = Bench(gpu, pipe, low, lb, lf, inW, inH, lw, lh, true, lk, true, ln);
                        pipe.FusedFg = false; pipe.LowResMotion = false;
                        var byStage = d.Where(kv => kv.Key != "total").GroupBy(kv => StageOf(kv.Key)).Select(gr => (gr.Key, ms: gr.Sum(x => x.Value))).OrderByDescending(x => x.ms);
                        log($"    {lab,-44} {g,6:0.00} ms {(g <= LatencyBudget.DefaultMs ? "OK  " : "OVER")}  {string.Join(" · ", byStage.Select(x => $"{x.Key} {x.ms:0.00}"))}");
                    }
                }
                log("  breakdown, Performance (ms): " + string.Join(", ", br["CSR1.3-P"].Where(kv => kv.Key != "total").OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.000}")));
                log("  breakdown, Quality (ms): " + string.Join(", ", br["CSR1.3-Q"].Where(kv => kv.Key != "total").OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value:0.000}")));
                log("  ran as: " + string.Join("; ", cols.Select(c => $"{c.Name} = {names.GetValueOrDefault(c.Name, "?")}")));
            }

            // Jitter / motion-vector sign check (1440p): confirms the conventions we pass to each runtime.
            log("");
            log("Convention check at 1440p (PSNR, dB): jitter sign on 'static + jitter', motion-vector sign on 'slow pan'.");
            foreach (var b in new[] { Backend.Fsr3, Backend.XeSS }.Where(x => VendorSupport.Get(x).Ok))
            {
                var parts = new List<string>();
                foreach (var (js, mvs) in new[] { (-1f, 1f), (1f, 1f), (-1f, -1f) })
                {
                    SetSigns(b, js, mvs);
                    double pj = Score(gpu, b, true, false), pm = Score(gpu, b, false, true);
                    parts.Add($"js {js:+0;-0} mv {mvs:+0;-0}: jitter {pj:0.00} pan {pm:0.00}");
                }
                SetSigns(b, -1f, 1f);
                log($"  {BackendNames.Long(b),-12} " + string.Join(" | ", parts));
            }

            // Frame generation quality at 1440p, middle frame vs truth.
            log("");
            log("Frame generation at 1440p (interpolated middle frame after 8 frames; PSNR vs truth, dB):");
            foreach (var (name, sx, sy, hud) in new[] { ("pure pan 3x2 px", 3f, 2f, false), ("pan 4x1 px + static HUD", 4f, 1f, true) })
            {
                var row = new List<string>();
                foreach (var (kind, fast, lab) in new[] { (FgKind.FrameFX, true, "FrameFX FG (Performance)"), (FgKind.FrameFX, false, "FrameFX FG (Quality)"), (FgKind.Fsr3, true, "FSR 3 FG") })
                {
                    if (kind == FgKind.Fsr3 && !VendorSupport.FsrFrameGen.Ok) { row.Add("FSR 3 FG: n/a"); continue; }
                    const int inW = 1280, inH = 720, outW = 2560, outH = 1440;
                    using var pipe = new FramePipeline(gpu) { FrameGenKind = kind, TestHud = hud, Fast = fast };
                    using var low = new Tex(gpu.Device, inW, inH, Format.R8G8B8A8_UNorm);
                    using var truthM = new Tex(gpu.Device, outW, outH, Format.R8G8B8A8_UNorm);
                    Tex? interp = null;
                    const int N = 8;
                    for (int k = 0; k < N; k++)
                    {
                        pipe.EncodeTestScene(low, 3f, 0, 0, k * sx / inW, k * sy / inH);
                        (_, interp) = pipe.Process(low.Srv, inW, inH, outW, outH, Backend.Temporal, 0, 0, true);
                    }
                    pipe.EncodeTestSceneTruth(truthM, 3f, (N - 1.5f) * sx / inW, (N - 1.5f) * sy / inH);
                    if (interp == null || pipe.VendorNote != null) { row.Add($"{lab}: none ({pipe.VendorNote ?? "no frame"})"); continue; }
                    var g = gpu.ReadRgb(interp);
                    if (HasNaN(g)) { ok = false; log("FAIL: NaN in FG output"); }
                    row.Add($"{lab} {Psnr(g, gpu.ReadRgb(truthM)):0.00} dB");
                    if (DumpDir != null) gpu.SavePng(interp, Path.Combine(DumpDir, $"fgv_{(hud ? "hud" : "pan")}_{kind}_{(fast ? "P" : "Q")}.png"));
                }
                log($"  {name,-26} " + string.Join(" | ", row));
            }
        }
        return ok;
    }

    /// <summary>1.3.4 round 4: real GPU timing of the shipped presets on this PC (timestamps around Process, median of 30
    /// frames, every tile changing, test scene with a HUD). Slow pan = 0.37 input px/frame; fast pan = 24 px/frame (a
    /// camera flick: the global-pan paths are active). Targets are the offline estimates: Performance 1440p x4 1.4 ms,
    /// Competitive 1080p x8 1.0 ms. Ablation rows show what the camera-pan estimate and the round-4 Competitive passes cost.</summary>
    static bool PresetTiming(Gpu gpu, Action<string> log)
    {
        bool ok = true;
        log("");
        log("Preset timing (real GPU timestamps on this PC; GPU ms per captured frame incl. all generated frames, median of 30):");
        var rows = new List<(string lab, int inW, int inH, int outW, int outH, int n, string preset, double target)>
        {
            ("Performance 1280x720->2560x1440 x4 (fused, per-frame vector)", 1280, 720, 2560, 1440, 4, "perf", 1.4),
            ("  ablation: Performance 1440p x4, per-frame passes (round 3 default)", 1280, 720, 2560, 1440, 4, "perf-perframe", 1.4),
            ("  ablation: Performance 1440p x4, fused shared vector (round 3 L2)", 1280, 720, 2560, 1440, 4, "perf-fused0", 1.4),
            ("Competitive 1280x720->1920x1080 x8", 1280, 720, 1920, 1080, 8, "comp", 1.0),
            ("Competitive 1920x1080 1:1 x8", 1920, 1080, 1920, 1080, 8, "comp", 1.0),
            ("  ablation: Competitive 720p->1080p, no camera-pan", 1280, 720, 1920, 1080, 8, "comp-noglob", 1.0),
            ("  ablation: Competitive 720p->1080p, round-3 lean set", 1280, 720, 1920, 1080, 8, "comp-lean", 1.0),
            ("  ablation: Competitive 720p->1080p, shared t=0.5 vector (7f7ec23)", 1280, 720, 1920, 1080, 8, "comp-fused0", 1.0),
            ("  ablation: Performance 1440p x4, final LK 2 iterations", 1280, 720, 2560, 1440, 4, "perf-lk2", 1.4),
            ("  experiment: Competitive 720p->1080p + static-mask prep pass (UFX_FG_PREP=1)", 1280, 720, 1920, 1080, 8, "comp-prep", 1.0),
            ("  experiment: Competitive 720p->1080p + prep pass + motion tap every step (UFX_FG_FUSEDV=2)", 1280, 720, 1920, 1080, 8, "comp-prep2", 1.0),
        };
        bool globSaved = FramePipeline.GlobalEnv;
        foreach (var (lab, inW, inH, outW, outH, n, preset, target) in rows)
        {
            try
            {
                using var pipe = new FramePipeline(gpu) { Fast = true };
                using var low = new Tex(gpu.Device, inW, inH, Format.R8G8B8A8_UNorm);
                bool comp = preset.StartsWith("comp");
                pipe.PickVectors = !comp; pipe.FusedFg = preset != "perf-perframe"; pipe.LowResMotion = false;
                pipe.FusedVecMode = preset is "comp-lean" or "perf-fused0" or "comp-fused0" ? 0 : preset == "comp-prep2" ? 2 : comp ? 4 : 3;
                pipe.Lean = preset == "comp-lean"; pipe.PickLite = comp && preset != "comp-lean"; pipe.SkipClean = comp && preset != "comp-lean";
                pipe.Lk2Override = preset == "perf-lk2";
                pipe.FgPrep = FramePipeline.FgPrepEnv || preset.StartsWith("comp-prep");
                FramePipeline.GlobalEnv = globSaved && preset != "comp-noglob";
                var cells = new List<string>();
                foreach (var (scene, px) in new[] { ("slow pan", 0.37f), ("fast pan 24 px/f", 24f) })
                {
                    var (g, _, d) = Bench(gpu, pipe, low, Backend.Temporal, true, inW, inH, outW, outH, true, FgKind.FrameFX, true, n, px);
                    if (double.IsNaN(g)) { cells.Add($"{scene}: n/a"); continue; }
                    var stages = d.Where(kv => kv.Key != "total").GroupBy(kv => StageOf(kv.Key)).Select(gr => (gr.Key, ms: gr.Sum(x => x.Value))).OrderByDescending(x => x.ms);
                    var top = d.Where(kv => kv.Key != "total").OrderByDescending(kv => kv.Value).Take(6);
                    cells.Add($"{scene} {g:0.00} ms {(g <= target ? "OK" : "OVER")} [{string.Join(" · ", stages.Select(x => $"{x.Key} {x.ms:0.00}"))}] top: {string.Join(", ", top.Select(kv => $"{kv.Key} {kv.Value:0.000}"))}");
                }
                log($"  {lab} (target {target:0.0} ms):");
                foreach (var c in cells) log("      " + c);
            }
            catch (Exception ex) { log($"  {lab}: FAIL {ex.Message}"); ok = false; }
            finally { FramePipeline.GlobalEnv = globSaved; }
        }
        return ok;
    }

    /// <summary>Returns (median GPU ms of Process per frame via timestamps, wall ms/frame of a 60-frame run minus the
    /// test-scene render, median per-pass breakdown). moving = panning scene with HUD; otherwise a still scene.</summary>
    static (double gpu, double wall, Dictionary<string, double> br) Bench(Gpu gpu, FramePipeline pipe, Tex low, Backend b, bool fast,
        int inW, int inH, int outW, int outH, bool fg, FgKind kind, bool moving, int mul = 2, float panPx = 0.37f)
    {
        pipe.ClearVendorFailures(); pipe.TestHud = true; pipe.FrameGenKind = kind; pipe.Fast = fast;
        pipe.Ensure(inW, inH, outW, outH); pipe.Reset();
        int phases = Jitter.PhaseCount(inW, outW);
        void Scene(int f)
        {
            var (jx, jy) = moving && Backends.IsTemporal(b) ? Jitter.Offset(f, phases) : (0f, 0f);
            if (moving) pipe.EncodeTestScene(low, 2f + f / 60f, jx, jy, f * panPx / inW, f * panPx * (0.11f / 0.37f) / inH);
            else if (f < 2) pipe.EncodeTestScene(low, 2f, 0, 0, 0, 0);
        }
        void Proc(int f)
        {
            var (jx, jy) = moving && Backends.IsTemporal(b) ? Jitter.Offset(f, phases) : (0f, 0f);
            pipe.Process(low.Srv, inW, inH, outW, outH, b, jx, jy, fg, mul);
        }
        for (int f = 0; f < 12; f++) { Scene(f); Proc(f); }
        var per = new List<double>(); var parts = new Dictionary<string, List<double>>();
        for (int f = 12; f < 42; f++)
        {
            Scene(f);
            gpu.Sync();
            gpu.ProfileBegin();
            Proc(f);
            var r = gpu.ProfileEnd();
            if (r.TryGetValue("total", out var tot)) per.Add(tot);
            foreach (var kv in r) (parts.TryGetValue(kv.Key, out var l) ? l : parts[kv.Key] = new()).Add(kv.Value);
            if (pipe.VendorMs > 0) (parts.TryGetValue("vendor", out var lv) ? lv : parts["vendor"] = new()).Add(pipe.VendorMs);
        }
        // Throughput: scene-only run vs scene + pipeline run, back to back.
        gpu.Sync();
        var sw = Stopwatch.StartNew();
        for (int f = 42; f < 102; f++) Scene(f);
        gpu.Sync();
        double sceneMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int f = 102; f < 162; f++) { Scene(f); Proc(f); }
        gpu.Sync();
        double wall = (sw.Elapsed.TotalMilliseconds - sceneMs) / 60;
        pipe.TestHud = false; pipe.FrameGenKind = FgKind.FrameFX;
        var br = parts.ToDictionary(kv => kv.Key, kv => Median(kv.Value));
        bool failed = pipe.VendorNote != null && Backends.IsVendor(b);
        return (failed ? double.NaN : Median(per), failed ? double.NaN : wall, br);
    }

    /// <summary>1.3.4: pipeline stage of a profiled kernel (self-test latency rows).</summary>
    static string StageOf(string k) => k switch
    {
        "vendor" => "vendor D3D12",
        "FX_ChangeDetect" => "change",
        "FX_CopyIn" or "FX_PackMv" => "vendor copy",
        "FX_FastAccum" or "FX_Accumulate" or "FX_EASU" or "FX_RCAS" or "FX_Bilinear" or "FX_LockDetect" or "FX_LockUpdate"
            or "FX_Reactive" or "FX_Disocclusion" or "FX_Dilate" or "FX_StaticTiles" or "FX_StaticCount" => "upscale",
        _ when k.Contains("Interp") || k == "FX_FgPrep" => "framegen",
        _ when k.StartsWith("FX_Mv") || k == "FX_GlobalMotion" || k == "FX_MotionFail" => "fg-motion",
        _ when k.StartsWith("FX_") => "motion",
        _ when k.StartsWith("FG2_") && (k.Contains("Interp") || k == "FG2_Cleanup" || k == "FG2_Extrap") => "framegen",
        _ when k.StartsWith("FG2_") => "motion",
        _ => "other",
    };

    static bool V2Timing(Gpu gpu, Action<string> log)
    {
        bool ok = true;
        log("");
        log("CSR 2.0 kernel timing (UFX_FG_V2=1; GPU timestamps around Process, median of 30, panning HUD scene):");
        try
        {
            using var pipe = new FramePipeline(gpu) { Fast = true, UseV2 = true, TestHud = true };
            using var low = new Tex(gpu.Device, 1280, 720, Format.R8G8B8A8_UNorm);
            var (g, _, d) = Bench(gpu, pipe, low, Backend.Temporal, true, 1280, 720, 2560, 1440, true, FgKind.FrameFX, true, 4);
            var fg2 = d.Where(kv => kv.Key.StartsWith("FG2_")).OrderByDescending(kv => kv.Value).ToList();
            log($"  CSR 1.3-P + CSR 2.0 ×4: {g:0.00} ms total; FG2 kernels: {(fg2.Count == 0 ? "none recorded" : string.Join(", ", fg2.Select(kv => $"{kv.Key} {kv.Value:0.000}")))}");
            log($"  VendorMs during CSR 2.0 generate: {pipe.VendorMs:0.00} (expect 0)");
            if (pipe.VendorMs != 0) { log("  FAIL: generated path touched vendor upscaler"); ok = false; }
            if (pipe.FgV2Active) log($"  motion source {pipe.Csr20Label}; CSR 2.0 active");
            var names = d.Keys.ToHashSet();
            foreach (var k in new[] { "FG2_GlobalMotion", "FG2_HudDownsample" })
                log($"  {k}: {(names.Contains(k) ? "timed" : "missing")}");
            gpu.ProfileBegin();
            var (o, _) = pipe.Process(low.Srv, 1280, 720, 2560, 1440, Backend.Temporal, 0, 0, true, 4);
            pipe.V2Extrapolate(o, 1.2f);
            var pe = gpu.ProfileEnd();
            log($"  FG2_Extrap: {(pe.ContainsKey("FG2_Extrap") ? $"{pe["FG2_Extrap"]:0.000} ms" : "not recorded")}");
            log($"  vsync t sample (CPU): check FgLogicTest jitter sequence");
        }
        catch (Exception ex) { log($"  CSR 2.0 timing: FAIL {ex.Message}"); ok = false; }
        return ok;
    }

    static string ColName(Backend b) => b switch { Backend.Temporal => "CSR1.3", Backend.Spatial => "CSR1.2", Backend.Fsr1 => "FSR1", Backend.Fsr2 => "FSR2", Backend.Fsr3 => "FSR3", Backend.Fsr4 => "FSR4", Backend.XeSS => "XeSS", _ => b.ToString() };
    static double Median(List<double> l) { var a = l.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }

    static void SetSigns(Backend b, float js, float mvs)
    {
        if (b == Backend.XeSS) { XessUpscaler.JitterSign = js; XessUpscaler.MvSign = mvs; }
        else { FfxUpscaler.JitterSign = js; FfxUpscaler.MvSign = mvs; }
    }

    static double Score(Gpu gpu, Backend b, bool jitter, bool pan)
    {
        const int inW = 1280, inH = 720, outW = 2560, outH = 1440, frames = 24, scored = 6;
        using var pipe = new FramePipeline(gpu);
        using var low = new Tex(gpu.Device, inW, inH, Format.R8G8B8A8_UNorm);
        using var truth = new Tex(gpu.Device, outW, outH, Format.R8G8B8A8_UNorm);
        int phases = Jitter.PhaseCount(inW, outW);
        double sum = 0;
        for (int f = 0; f < frames; f++)
        {
            float px = pan ? f * 0.37f / inW : 0, py = pan ? f * 0.11f / inH : 0;
            var (jx, jy) = jitter ? Jitter.Offset(f, phases) : (0f, 0f);
            pipe.EncodeTestScene(low, 2f, jx, jy, px, py);
            var (o, _) = pipe.Process(low.Srv, inW, inH, outW, outH, b, jx, jy, false);
            if (f >= frames - scored) { pipe.EncodeTestSceneTruth(truth, 2f, px, py); sum += Psnr(gpu.ReadRgb(o), gpu.ReadRgb(truth)); }
        }
        return pipe.VendorNote != null ? double.NaN : sum / scored;
    }

    sealed record Cfg(string Name, MotionPreference Motion, bool Static, bool NewFg);

    /// <summary>Developer tuning (env UFX_TUNE="conf=0.6,minw=0.15;conf=0.3"): 1440p PSNR of CSR 1.3 Performance per variant.</summary>
    static void Tune(Gpu gpu, string spec, Action<string> log)
    {
        var scen = new (string name, bool jitter, bool animate, bool pan, bool hud)[]
        {
            ("static+jit", true, false, false, false), ("anim+jit", true, true, false, false), ("still", false, false, false, false),
            ("pan", false, false, true, false), ("pan+hud", false, false, true, true),
        };
        const int frames = 32, scored = 8, inW = 1280, inH = 720, outW = 2560, outH = 1440;
        var f0 = (FramePipeline.FastFgK, FramePipeline.FastFgMvW, FramePipeline.FastFgGate);
        var d0 = (FramePipeline.FastConfK, FramePipeline.FastMinW, FramePipeline.FastStatMargin, FramePipeline.FastAccumMax, FramePipeline.FastSharpBase);
        log($"  {"variant",-40} {string.Join(" ", scen.Select(x => $"{x.name,10}"))}");
        lock (Gpu.Lock)
        {
            using var low = new Tex(gpu.Device, inW, inH, Format.R8G8B8A8_UNorm);
            using var truth = new Tex(gpu.Device, outW, outH, Format.R8G8B8A8_UNorm);
            int phases = Jitter.PhaseCount(inW, outW);
            foreach (var v in ("Q;" + spec).Split(';'))
            {
                (FramePipeline.FastConfK, FramePipeline.FastMinW, FramePipeline.FastStatMargin, FramePipeline.FastAccumMax, FramePipeline.FastSharpBase) = d0;
                (FramePipeline.FastFgK, FramePipeline.FastFgMvW, FramePipeline.FastFgGate) = f0;
                foreach (var kv in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = kv.Split('='); if (t.Length != 2) continue;
                    float f = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    switch (t[0].Trim()) { case "conf": FramePipeline.FastConfK = f; break; case "minw": FramePipeline.FastMinW = f; break;
                        case "stat": FramePipeline.FastStatMargin = f; break; case "acc": FramePipeline.FastAccumMax = f; break; case "sharp": FramePipeline.FastSharpBase = f; break;
                        case "fgk": FramePipeline.FastFgK = f; break; case "fgmv": FramePipeline.FastFgMvW = f; break; case "fggate": FramePipeline.FastFgGate = f; break; }
                }
                using var pipe = new FramePipeline(gpu) { Fast = v != "Q", Sharpness = 0.25f };
                var row = new List<string>();
                foreach (var sc in scen)
                {
                    pipe.TestHud = sc.hud; pipe.Ensure(inW, inH, outW, outH); pipe.Reset();
                    double sum = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        float time = sc.animate ? 2f + f / 60f : 2f;
                        float px = sc.pan ? f * 0.37f / inW : 0, py = sc.pan ? f * 0.11f / inH : 0;
                        var (jx, jy) = sc.jitter ? Jitter.Offset(f, phases) : (0f, 0f);
                        pipe.EncodeTestScene(low, time, jx, jy, px, py);
                        var (o, _) = pipe.Process(low.Srv, inW, inH, outW, outH, Backend.Temporal, jx, jy, false);
                        if (f >= frames - scored) { pipe.EncodeTestSceneTruth(truth, time, px, py); sum += Psnr(gpu.ReadRgb(o), gpu.ReadRgb(truth)); }
                    }
                    row.Add($"{sum / scored,10:0.00}");
                }
                // Frame generation: pure pan 3x2 px/frame; PSNR of the last upscaled frame and of the generated middle frame.
                using (var truthM = new Tex(gpu.Device, outW, outH, Format.R8G8B8A8_UNorm))
                {
                    pipe.TestHud = false; pipe.Reset();
                    Tex? interp = null, outp = null;
                    for (int k = 0; k < 8; k++)
                    {
                        pipe.EncodeTestScene(low, 3f, 0, 0, k * 3f / inW, k * 2f / inH);
                        (outp, interp) = pipe.Process(low.Srv, inW, inH, outW, outH, Backend.Temporal, 0, 0, true);
                    }
                    pipe.EncodeTestSceneTruth(truthM, 3f, 6.5f * 3f / inW, 6.5f * 2f / inH);
                    double pi = interp != null ? Psnr(gpu.ReadRgb(interp), gpu.ReadRgb(truthM)) : 0;
                    if (DumpDir != null && interp != null)
                    {
                        string tag = v == "Q" ? "Q" : "P";
                        gpu.SavePng(interp, Path.Combine(DumpDir, $"tune_fg_{tag}.png"));
                        gpu.SavePng(truthM, Path.Combine(DumpDir, "tune_fg_truth.png"));
                        if (pipe.LastMotion is { } lmv)
                        {
                            var mv = gpu.ReadHalf4(lmv);
                            var sbm = new System.Text.StringBuilder();
                            for (int y = 0; y < lmv.Height; y += 4) { for (int x = 0; x < lmv.Width; x += 4) { int i = (y * lmv.Width + x) * 4; sbm.Append($"{mv[i]:0.0},{mv[i + 1]:0.0},{mv[i + 2]:0.00} "); } sbm.AppendLine(); }
                            File.WriteAllText(Path.Combine(DumpDir, $"tune_mv_{tag}.txt"), sbm.ToString());
                        }
                    }
                    pipe.EncodeTestSceneTruth(truthM, 3f, 7f * 3f / inW, 7f * 2f / inH);
                    double po = Psnr(gpu.ReadRgb(outp!), gpu.ReadRgb(truthM));
                    string md = "";
                    if (pipe.LastMotion is { } lm)
                    {
                        var mv = gpu.ReadHalf4(lm); int n = mv.Length / 4;
                        var xs = new float[n]; var ys = new float[n]; double cf = 0;
                        for (int i = 0; i < n; i++) { xs[i] = mv[i * 4]; ys[i] = mv[i * 4 + 1]; cf += mv[i * 4 + 2]; }
                        Array.Sort(xs); Array.Sort(ys);
                        md = $" mv {lm.Width}x{lm.Height} med ({xs[n / 2]:0.00},{ys[n / 2]:0.00}) p10 {xs[n / 10]:0.00} p90 {xs[n * 9 / 10]:0.00} conf {cf / n:0.00}";
                    }
                    foreach (int L in new[] { 3, 2 })
                    {
                        var t = pipe.DebugMvLevels[L]; var a = gpu.ReadHalfN(t, 2); int n = a.Length / 2;
                        var xs = new float[n]; var ys = new float[n];
                        for (int i = 0; i < n; i++) { xs[i] = a[i * 2] * inW; ys[i] = a[i * 2 + 1] * inH; }
                        Array.Sort(xs); Array.Sort(ys);
                        md += $" | L{L} {t.Width}x{t.Height} med ({xs[n / 2]:0.00},{ys[n / 2]:0.00}) p10 {xs[n / 10]:0.00} p90 {xs[n * 9 / 10]:0.00}";
                    }
                    {
                        var lc = gpu.ReadHalfN(pipe.DebugLuma(false)[3], 1); var lp = gpu.ReadHalfN(pipe.DebugLuma(true)[3], 1);
                        double dd = 0, mc = 0; for (int i = 0; i < lc.Length; i++) { dd += Math.Abs(lc[i] - lp[i]); mc += lc[i]; }
                        md += $" | L3 luma mean {mc / lc.Length:0.000} |prev-curr| {dd / lc.Length:0.0000}";
                    }
                    row.Add($"fg {pi:0.00} base {po:0.00}{md}");
                }
                log($"  {v,-40} {string.Join(" ", row)}");
            }
        }
        (FramePipeline.FastConfK, FramePipeline.FastMinW, FramePipeline.FastStatMargin, FramePipeline.FastAccumMax, FramePipeline.FastSharpBase) = d0;
    }

    public static int Run(Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        log($"Universal-FrameFX {MainForm.Version} - self-test");
        Gpu gpu;
        try { gpu = new Gpu(); }
        catch (Exception ex) { log("FAIL: GPU/shader setup: " + ex.Message); return 1; }
        using var _g = gpu;
        log($"GPU: {gpu.AdapterName}");
        log($"  compiled {gpu.Report.Count} shader stages (cs_5_0 ×{Gpu.Kernels.Length}, vs/ps 5_0): OK");
        if (Environment.GetEnvironmentVariable("UFX_TUNE") is { Length: > 0 } tspec) { Tune(gpu, tspec, log); log("SELFTEST PASS"); return 0; }
        // UFX_SELFTEST_TIMING=1: only the preset timing section (about a minute).
        if (Environment.GetEnvironmentVariable("UFX_SELFTEST_TIMING") == "1")
        {
            bool okT = PresetTiming(gpu, log);
            log($"Elapsed {sw.Elapsed.TotalSeconds:0.0} s"); log(okT ? "SELFTEST PASS" : "SELFTEST FAIL"); return okT ? 0 : 1;
        }
        bool ok = true;
        const int inW = 640, inH = 360, outW = 1280, outH = 720, frames = 48, scored = 16;

        // Motion hardware probe.
        log("");
        log("Motion sources:");
        string? hwName = null;
        var hwCfgs = new List<Cfg>();
        lock (Gpu.Lock)
        {
            foreach (var pref in new[] { MotionPreference.Nvof, MotionPreference.D3D12 })
            {
                var why = new List<string>();
                IMotionEngine? e = null;
                try { e = MotionEngines.Create(gpu, inW, inH, pref, why); } catch (Exception ex) { why.Add(ex.Message); }
                bool match = e != null && (pref == MotionPreference.Nvof ? e is NvofEngine : e is D3D12MeEngine);
                if (match) log($"  {MotionEngines.PrefName(pref)}: available - {e!.Name}");
                else log($"  {MotionEngines.PrefName(pref)}: not available - {string.Join("; ", why.Where(w => w.StartsWith(pref == MotionPreference.Nvof ? "NVIDIA" : "D3D12")).DefaultIfEmpty("unsupported"))}");
                if (match && hwName == null) hwName = e!.Name;
                if (match) hwCfgs.Add(new(pref == MotionPreference.Nvof ? "1.2 nvof" : "1.2 d3d12", pref, true, true));
                e?.Dispose();
            }
        }
        log($"  Auto selects: {hwName ?? MotionEngines.SoftwareName} (software estimator is the fallback)");

        using var pipe = new FramePipeline(gpu) { Fast = false };   // 1.1/1.2 comparisons use the Quality pipeline
        var dev = gpu.Device;
        using var low = new Tex(dev, inW, inH, Format.R8G8B8A8_UNorm);
        using var truth = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
        using var oT = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
        using var oS = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
        using var oB = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
        int phases = Jitter.PhaseCount(inW, outW);

        var cfgs = new List<Cfg> { new("1.1", MotionPreference.Software, false, false), new("1.2 sw", MotionPreference.Software, true, true) };
        cfgs.AddRange(hwCfgs);

        var scenarios = new (string name, bool jitter, bool animate, bool pan, bool hud)[]
        {
            ("DIAG static+jitter, oracle zero motion", true, false, false, false),
            ("static scene + jitter (ideal accumulation)", true, false, false, false),
            ("animated, jittered (test-scene source)", true, true, false, false),
            ("animated, no jitter (like a capture)", false, true, false, false),
            ("static, no jitter (still desktop)", false, false, false, false),
            ("slow pan, no jitter (scrolling capture)", false, false, true, false),
            ("slow pan + static HUD, no jitter (game-like)", false, false, true, true),
        };
        log("");
        log($"Upscale {inW}x{inH} -> {outW}x{outH}. PSNR vs 4x4-supersampled ground truth, mean of last {scored} of {frames} frames (dB, higher is better).");
        log("Temporal columns: 1.1 = software motion, no static tiles (the 1.1 algorithm); 1.2 sw = static tiles + software motion; 1.2 nvof / 1.2 d3d12 = static tiles + that hardware motion source.");
        log($"  {"scenario",-46} {string.Join(" ", cfgs.Select(c => $"{c.Name,7}"))}  spatial bilinear");
        lock (Gpu.Lock)
        {
            foreach (var sc in scenarios)
            {
                var res = new List<double>();
                double pS = 0, pB = 0;
                for (int ci = 0; ci < cfgs.Count; ci++)
                {
                    var cfg = cfgs[ci];
                    pipe.Motion = cfg.Motion; pipe.StaticTiles = cfg.Static; pipe.NewFrameGen = cfg.NewFg;
                    pipe.TestHud = sc.hud;
                    pipe.Ensure(inW, inH, outW, outH);
                    pipe.Reset();
                    pipe.Sharpness = 0.25f;
                    pipe.OracleStatic = sc.name.StartsWith("DIAG");
                    double sT = 0, sS = 0, sB = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        float time = sc.animate ? 2f + f / 60f : 2f;
                        float px = sc.pan ? f * 0.37f / inW : 0, py = sc.pan ? f * 0.11f / inH : 0;
                        var (jx, jy) = sc.jitter ? Jitter.Offset(f, phases) : (0f, 0f);
                        pipe.EncodeTestScene(low, time, jx, jy, px, py);
                        pipe.EncodeTemporal(low.Srv, jx, jy, oT);
                        if (ci == 0) { pipe.EncodeSpatial(low.Srv, oS); pipe.EncodeBilinear(low.Srv, oB); }
                        if (f >= frames - scored)
                        {
                            pipe.EncodeTestSceneTruth(truth, time, px, py);
                            var gt = gpu.ReadRgb(truth);
                            var t = gpu.ReadRgb(oT);
                            if (HasNaN(t)) { log("FAIL: NaN in temporal output"); ok = false; }
                            sT += Psnr(t, gt);
                            if (ci == 0) { sS += Psnr(gpu.ReadRgb(oS), gt); sB += Psnr(gpu.ReadRgb(oB), gt); }
                        }
                    }
                    if (DumpDir != null)
                    {
                        string tag = new string(sc.name.Where(char.IsLetterOrDigit).Take(12).ToArray()) + "_" + cfg.Name.Replace(" ", "").Replace(".", "");
                        gpu.SavePng(oT, Path.Combine(DumpDir, tag + "_temporal.png"));
                    }
                    pipe.OracleStatic = false;
                    double pT = sT / scored;
                    if (double.IsNaN(pT) || double.IsInfinity(pT)) ok = false;
                    res.Add(pT);
                    if (ci == 0) { pS = sS / scored; pB = sB / scored; }
                }
                string d = res.Count > 1 ? $"  (1.2 vs 1.1: {res.Skip(1).Max() - res[0]:+0.00;-0.00} dB)" : "";
                log($"  {sc.name,-46} {string.Join(" ", res.Select(v => $"{v,7:0.00}"))}  {pS,7:0.00} {pB,7:0.00}{d}");
            }
            pipe.TestHud = false;

            // Motion accuracy: pure 3x2 px pan, per source (also validates the vector sign).
            log("");
            log("Motion accuracy on a pure 3x2 px pan, textured pixels only (backward should be (+3,+2), forward (-3,-2)):");
            using var lowB = new Tex(dev, inW, inH, Format.R8G8B8A8_UNorm);
            foreach (var cfg in cfgs.Where(c => c.Name != "1.1"))
            {
                pipe.Motion = cfg.Motion;
                pipe.Ensure(inW, inH, outW, outH); pipe.Reset();
                for (int k = 0; k < 4; k++)
                {
                    pipe.EncodeTestScene(low, 3f, 0, 0, k * 3f / inW, k * 2f / inH);
                    pipe.EncodeMotion(low.Srv, 0, 0);
                }
                var lum = gpu.ReadRgb(low);
                var textured = new bool[inW * inH];
                for (int y = 1; y < inH - 1; y++) for (int x = 1; x < inW - 1; x++)
                {
                    float lo = 9, hi = -9;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { float v = lum[3 * ((y + dy) * inW + x + dx) + 1]; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                    textured[y * inW + x] = hi - lo > 0.1f && x > 8 && y > 8 && x < inW - 8 && y < inH - 8;
                }
                var bw = gpu.ReadHalf4(pipe.LastMotion!);
                var fw = gpu.ReadHalf4(pipe.Forward!);
                int n = bw.Length / 4, gb = 0, gf = 0; double sx = 0, sy = 0, fx = 0, fy = 0, cf = 0;
                int nt = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!textured[i]) continue;
                    nt++;
                    sx += bw[i * 4]; sy += bw[i * 4 + 1]; cf += bw[i * 4 + 2];
                    double fxx = fw[i * 4] * inW, fyy = fw[i * 4 + 1] * inH; fx += fxx; fy += fyy;
                    if (Math.Abs(bw[i * 4] - 3) < 0.5 && Math.Abs(bw[i * 4 + 1] - 2) < 0.5) gb++;
                    if (Math.Abs(fxx + 3) < 0.5 && Math.Abs(fyy + 2) < 0.5) gf++;
                }
                if (DumpDir != null && pipe.Engine is { } eng && pipe.MotionSource == eng.Name)
                {
                    var raw = gpu.ReadShort2(eng.Bwd); var rawF = gpu.ReadShort2(eng.Fwd);
                    var hist = raw.Chunk(2).GroupBy(v => (v[0] / eng.Scale, v[1] / eng.Scale)).OrderByDescending(gq => gq.Count()).Take(4)
                        .Select(gq => $"({gq.Key.Item1:0.##},{gq.Key.Item2:0.##})x{100.0 * gq.Count() / (raw.Length / 2):0}%");
                    var histF = rawF.Chunk(2).GroupBy(v => (v[0] / eng.Scale, v[1] / eng.Scale)).OrderByDescending(gq => gq.Count()).Take(3)
                        .Select(gq => $"({gq.Key.Item1:0.##},{gq.Key.Item2:0.##})x{100.0 * gq.Count() / (rawF.Length / 2):0}%");
                    int rn = raw.Length / 2, rg = 0; double rx = 0, ry = 0;
                    for (int i = 0; i < rn; i++) { double vx = raw[2 * i] / eng.Scale, vy = raw[2 * i + 1] / eng.Scale; rx += vx; ry += vy; if (Math.Abs(vx - 3) < 0.5 && Math.Abs(vy - 2) < 0.5) rg++; }
                    log($"    raw mean ({rx / rn:0.00},{ry / rn:0.00}) {100.0 * rg / rn:0.0}% within 0.5 px");
                    var sbq = new System.Text.StringBuilder();
                    for (int gy = 0; gy < 6; gy++) { sbq.Append("    raw x row: "); for (int gx = 0; gx < 10; gx++) { int bx = gx * eng.Bwd.Width / 10 + 3, by = gy * eng.Bwd.Height / 6 + 3; sbq.Append($"{raw[2 * (by * eng.Bwd.Width + bx)] / eng.Scale,6:0.0}"); } sbq.Append("   final x: "); for (int gx = 0; gx < 10; gx++) { int px = gx * inW / 10 + 6, py = gy * inH / 6 + 6; sbq.Append($"{bw[4 * (py * inW + px)],6:0.0}"); } sbq.AppendLine(); }
                    log(sbq.ToString().TrimEnd());
                    log($"    raw {eng.Bwd.Width}x{eng.Bwd.Height} bwd top: {string.Join(" ", hist)}; fwd top: {string.Join(" ", histF)}");
                }
                n = Math.Max(1, nt);
                log($"  {pipe.MotionSource,-52} textured px: bwd mean ({sx / n:0.00},{sy / n:0.00}) {100.0 * gb / n:0.0}% within 0.5 px; fwd mean ({fx / n:0.00},{fy / n:0.00}) {100.0 * gf / n:0.0}%; conf {cf / n:0.00}");
            }

            // Frame generation: 5 source frames (so HUD/static detection has history), interpolate the
            // middle of the last pair; compare with the ground-truth middle frame.
            log("");
            log("Frame generation (middle frame between the last two of 5 frames; PSNR vs ground truth, dB):");
            log($"  {"scenario",-34}   blend  {string.Join(" ", cfgs.Select(c => $"{c.Name,7}"))}");
            using var hA = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
            using var hB = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
            using var hM = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
            using var fg = new Tex(dev, outW, outH, Format.R8G8B8A8_UNorm);
            var fgScen = new (string name, float sx, float sy, float dt, bool hud)[]
            {
                ("pure pan 3x2 px", 3, 2, 0, false),
                ("pan 3x2 px + animation", 3, 2, 2f / 60f, false),
                ("pan 4x1 px + static HUD", 4, 1, 1f / 60f, true),
                ("fast animation (occlusion)", 0, 0, 6f / 60f, false),
            };
            foreach (var s in fgScen)
            {
                var row = new List<double>(); double pb = 0;
                for (int ci = 0; ci < cfgs.Count; ci++)
                {
                    var cfg = cfgs[ci];
                    pipe.Motion = cfg.Motion; pipe.StaticTiles = cfg.Static; pipe.NewFrameGen = cfg.NewFg; pipe.TestHud = s.hud;
                    pipe.Ensure(inW, inH, outW, outH); pipe.Reset();
                    const int N = 5; float t0 = 3f;
                    for (int k = 0; k < N; k++)
                    {
                        pipe.EncodeTestScene(low, t0 + k * s.dt, 0, 0, k * s.sx / inW, k * s.sy / inH);
                        pipe.EncodeMotion(low.Srv, 0, 0);
                    }
                    if (DumpDir != null && ci == 1)
                    {
                        var l4 = gpu.ReadRgb(low);
                        pipe.EncodeTestScene(lowB, t0 + (N - 2) * s.dt, 0, 0, (N - 2) * s.sx / inW, (N - 2) * s.sy / inH);
                        var l3 = gpu.ReadRgb(lowB);
                        int same = 0; for (int i = 0; i < l4.Length / 3; i++) if (Math.Abs(l4[3 * i + 1] - l3[3 * i + 1]) < 0.002f) same++;
                        var hm = gpu.ReadHalf4Any(pipe.HudMask!);
                        log($"    [diag] {s.name}: identical input pixels last pair {100.0 * same / (l4.Length / 3):0.0}%, HUD mask {100.0 * hm.Count(v => v > 0.5f) / hm.Length:0.0}%");
                    }
                    int a = N - 2, b = N - 1;
                    pipe.EncodeTestSceneTruth(hA, t0 + a * s.dt, a * s.sx / inW, a * s.sy / inH);
                    pipe.EncodeTestSceneTruth(hB, t0 + b * s.dt, b * s.sx / inW, b * s.sy / inH);
                    pipe.EncodeTestSceneTruth(hM, t0 + (a + 0.5f) * s.dt, (a + 0.5f) * s.sx / inW, (a + 0.5f) * s.sy / inH);
                    pipe.EncodeInterpolate(hA, hB, pipe.LastMotion!, fg);
                    var ga = gpu.ReadRgb(hA); var gb = gpu.ReadRgb(hB); var m = gpu.ReadRgb(hM); var g = gpu.ReadRgb(fg);
                    if (HasNaN(g)) { log("FAIL: NaN in interpolated frame"); ok = false; }
                    if (ci == 0)
                    {
                        var blend = new float[ga.Length];
                        for (int i = 0; i < ga.Length; i++) blend[i] = 0.5f * (ga[i] + gb[i]);
                        pb = Psnr(blend, m);
                    }
                    row.Add(Psnr(g, m));
                    if (DumpDir != null) gpu.SavePng(fg, Path.Combine(DumpDir, $"fg_{new string(s.name.Where(char.IsLetterOrDigit).Take(10).ToArray())}_{cfg.Name.Replace(" ", "").Replace(".", "")}.png"));
                }
                string d = row.Count > 1 ? $"  (1.2 vs 1.1: {row.Skip(1).Max() - row[0]:+0.00;-0.00} dB, best vs blend: {row.Max() - pb:+0.00;-0.00} dB)" : "";
                log($"  {s.name,-34} {pb,7:0.00}  {string.Join(" ", row.Select(v => $"{v,7:0.00}"))}{d}");
            }
            pipe.TestHud = false; pipe.Motion = MotionPreference.Auto; pipe.StaticTiles = true; pipe.NewFrameGen = true;
            foreach (var l in pipe.MotionLog) log("  note: " + l);
        }
        if (Environment.GetEnvironmentVariable("UFX_SKIP_VENDOR") != "1") ok &= VendorTests(gpu, log);
        ok &= PresetTiming(gpu, log);
        if (Environment.GetEnvironmentVariable("UFX_FG_V2") == "1") ok &= V2Timing(gpu, log);
        log("");
        log($"Elapsed {sw.Elapsed.TotalSeconds:0.0} s");
        log(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
        return ok ? 0 : 1;
    }
}
