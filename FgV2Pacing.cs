using System.Diagnostics;

namespace UniversalFrameFX;

public static class FgV2Pacing
{
    public static double QpcSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static double CaptureSeconds(TimeSpan systemRelativeTime) => systemRelativeTime.TotalSeconds;

    public static double HoldDelay(double sourceInterval, double processMarginS)
    {
        double h = sourceInterval + processMarginS;
        double lo = Math.Max(0, sourceInterval * 0.5);
        double hi = Math.Max(lo, sourceInterval * 2.0 + 0.008);
        return Math.Clamp(h, lo, hi);
    }

    public static double PlaybackTime(double vsyncTime, double holdDelay, bool extrapolate) =>
        extrapolate ? vsyncTime : vsyncTime - holdDelay;

    public static float InterpT(double playbackTime, double prevCap, double currCap, out bool showCurr)
    {
        double span = currCap - prevCap;
        if (span <= 1e-6) { showCurr = true; return 1f; }
        if (playbackTime >= currCap - 1e-6) { showCurr = true; return 1f; }
        double t = (playbackTime - prevCap) / span;
        t = Math.Clamp(t, 0, 1);
        showCurr = t >= 1.0 - 1e-3;
        return (float)t;
    }

    public static bool ShouldGenerate(double nowS, double lastGenAtS, bool haveLast, double sourceInterval, int cap)
    {
        if (!haveLast) return true;
        int n = Math.Max(2, cap);
        double minSpacing = sourceInterval / n;
        return nowS - lastGenAtS >= minSpacing - 1e-7;
    }

    public static int RateCapDispatches(int vsyncs, double sourceInterval, double refreshInterval, int cap)
    {
        int n = 0;
        double last = double.NaN;
        for (int i = 0; i < vsyncs; i++)
        {
            double now = i * refreshInterval;
            if (ShouldGenerate(now, last, !double.IsNaN(last), sourceInterval, cap))
            {
                n++;
                last = now;
            }
        }
        return n;
    }
}
