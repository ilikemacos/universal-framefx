namespace UniversalFrameFX;

/// <summary>1.3.4 base-FPS safeguard for frame generation. Generation must never cost the game real frames: the
/// captured game's fps is measured with generation off (baseline), then watched in 2 s windows while it runs. Two
/// windows in a row more than <see cref="MaxDrop"/> below the baseline lower the multiplier one step
/// (8 → 4 → 3 → 2 → off); higher steps are re-tried after 20 s (doubling up to 160 s after a failed retry).
/// Pure logic (time and frame counts are passed in) so it can be tested without a GPU.</summary>
public sealed class FgGuard
{
    /// <summary>Allowed drop of the base fps (UFX_FG_MAXDROP, default 0.05 = 5%).</summary>
    public static double MaxDrop = double.TryParse(Environment.GetEnvironmentVariable("UFX_FG_MAXDROP"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 && d < 1 ? d : 0.05;
    public const double Window = 2.0, RetryBase = 20.0, RetryMax = 160.0;
    /// <summary>Windows below this source fps (static picture, loading screen, alt-tab) say nothing about cost.</summary>
    public const double MinFps = 10.0;
    public enum Phase { Off, Measuring, Active }

    public Phase State { get; private set; } = Phase.Off;
    /// <summary>Highest multiplier the safeguard allows right now (0 = generation paused by the safeguard).</summary>
    public int MaxCap = 8;
    public int Cap { get; private set; } = 8;
    public double Baseline { get; private set; }
    public double LastFps { get; private set; }
    /// <summary>Last safeguard action, for the HUD ("" when it has not intervened).</summary>
    public string Note { get; private set; } = "";
    public double RetryAt { get; private set; }

    double _w0; long _a0; int _bad, _probeWindows = -1; double _retryDelay = RetryBase;

    /// <summary>Generation may run (baseline known and the cap allows at least ×2).</summary>
    public bool Allows => State == Phase.Active && Cap >= 2;

    /// <summary>Call once per processed source frame.</summary>
    /// <param name="enabled">Frame generation is switched on by the user.</param>
    /// <param name="arrived">Running count of captured (real) frames.</param>
    /// <param name="now">Seconds, monotonic.</param>
    /// <param name="requested">Multiplier picked in the UI (after the refresh clamp).</param>
    /// <param name="running">Multiplier actually used for the last window (0/1 = generation was not running).</param>
    /// <param name="ignore">Window should not count (picture unchanged, budget pause…).</param>
    public void Update(bool enabled, long arrived, double now, int requested, int running, bool ignore = false)
    {
        if (!enabled) { Reset(); return; }
        if (State == Phase.Off) { State = Phase.Measuring; _w0 = now; _a0 = arrived; Note = ""; return; }
        if (now - _w0 < Window) return;
        double fps = (arrived - _a0) / (now - _w0);
        _w0 = now; _a0 = arrived; LastFps = fps;
        bool valid = fps >= MinFps && !ignore;
        if (State == Phase.Measuring)
        {
            if (!valid) return;
            Baseline = fps; State = Phase.Active; Cap = MaxCap;
            return;
        }
        if (!valid) { _bad = 0; return; }
        if (running < 2)
        {
            // Generation not running (paused by the cap, the refresh clamp or the budget): this is the real base fps.
            Baseline = fps; _bad = 0;
        }
        else if (fps < Baseline * (1 - MaxDrop))
        {
            if (++_bad >= 2)
            {
                int from = running;
                Cap = MaxCap > 8 ? FgMul.DownV2(Math.Min(Cap, running)) : FgMul.Down(Math.Min(Cap, running));
                if (_probeWindows >= 0) _retryDelay = Math.Min(_retryDelay * 2, RetryMax);   // a retry failed: back off
                _probeWindows = -1; _bad = 0;
                RetryAt = now + _retryDelay;
                Note = $"×{from} → {(Cap >= 2 ? "×" + Cap : "off")}: base fps fell {(1 - fps / Baseline) * 100:0}% ({Baseline:0.0} → {fps:0.0})";
            }
            return;
        }
        else
        {
            _bad = 0;
            if (_probeWindows >= 0 && ++_probeWindows >= 3) { _probeWindows = -1; _retryDelay = RetryBase; Note = $"×{running} held: base fps {fps:0.0} (baseline {Baseline:0.0})"; }
        }
        if (Cap < MaxCap && Cap < requested && now >= RetryAt && _probeWindows < 0)
        {
            Cap = MaxCap > 8 ? FgMul.UpV2(Cap, MaxCap) : FgMul.Up(Cap, MaxCap);
            _probeWindows = 0;
            Note = $"retrying ×{Math.Min(Cap, requested)} (baseline {Baseline:0.0} fps)";
        }
    }

    public void Reset() { State = Phase.Off; Cap = MaxCap; Baseline = 0; LastFps = 0; Note = ""; _bad = 0; _probeWindows = -1; _retryDelay = RetryBase; RetryAt = 0; }
}
