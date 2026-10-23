namespace UniversalFrameFX;

/// <summary>1.3.4 latency budget: keeps FrameFX's added GPU time per captured frame (processing + generated frames +
/// present draw) under <see cref="BudgetMs"/> (default 1.5 ms, UFX_LATENCY_BUDGET) by stepping through cheaper paths,
/// cheapest-quality-loss first. Over budget (median of 15 frames) → one level up; well under budget for a while → one
/// level back, with a doubling hold if that goes over again. Pure logic: timings are passed in.</summary>
public sealed class LatencyBudget
{
    public static double DefaultMs = double.TryParse(Environment.GetEnvironmentVariable("UFX_LATENCY_BUDGET"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1.5;
    public double BudgetMs = DefaultMs;
    public const int MaxLevel = 9;
    public static readonly string[] LevelNames =
    {
        "as selected",
        "FrameFX generation instead of AMD FSR 3 FG",
        "+ Performance preset, fused generation pass",
        "+ CSR 1.3 instead of the vendor upscaler",
        "+ motion at 1/4 resolution, lighter generation motion (no AA motion luma, neighbour pick or 2nd median)",
        "+ lighter upscale (1:1 passthrough / CSR 1.2)",
        "+ multiplier capped at ×4",
        "+ multiplier capped at ×3",
        "+ multiplier capped at ×2",
        "+ frame generation off",
    };
    public int Level { get; private set; }
    public double MedianMs { get; private set; }
    readonly Queue<double> _win = new();
    double _underSince = -1, _hold = 3, _lastDownAt = -1e9;

    /// <summary>Multiplier cap of the current level (8 = none, 0 = generation off).</summary>
    public int MulCap => Level switch { >= 9 => 0, 8 => 2, 7 => 3, 6 => 4, _ => 8 };

    /// <summary>Feed one frame's measured GPU ms (0 or less = no measurement yet).</summary>
    public void Update(double ms, double now)
    {
        if (ms <= 0) return;
        _win.Enqueue(ms); while (_win.Count > 15) _win.Dequeue();
        if (_win.Count < 15) return;
        var sorted = _win.OrderBy(x => x).ToArray();
        MedianMs = sorted[sorted.Length / 2];
        if (MedianMs > BudgetMs)
        {
            _underSince = -1;
            if (Level < MaxLevel)
            {
                if (now - _lastDownAt < _hold * 2) _hold = Math.Min(_hold * 2, 48);   // the cheaper-level retry failed
                Level++; _win.Clear();
            }
        }
        else if (MedianMs < BudgetMs * 0.6 && Level > 0)
        {
            if (_underSince < 0) _underSince = now;
            else if (now - _underSince >= _hold) { Level--; _lastDownAt = now; _underSince = -1; _win.Clear(); }
        }
        else _underSince = -1;
    }

    public void Reset() { Level = 0; _win.Clear(); _underSince = -1; _hold = 3; MedianMs = 0; }
}
