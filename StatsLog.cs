using System.Globalization;
using System.Text;

namespace UniversalFrameFX;

public static class StatsLog
{
    static readonly object Gate = new();
    static StreamWriter? _file;
    static readonly StringBuilder Buf = new();
    static readonly List<double> Intervals = new();
    static int _real, _gen;
    static double _lastFlushQpc;
    static bool _inited;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_inited) return;
            _inited = true;
            string? path = Environment.GetEnvironmentVariable("UFX_STATS_CSV");
            if (string.IsNullOrWhiteSpace(path)) return;
            string dir = Path.GetDirectoryName(path)!;
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
            if (fresh) _file.WriteLine("qpc_seconds,kind,t,content_cap_s,gpu_ms,present_interval_ms");
        }
    }

    public static void Record(double qpcSeconds, string kind, float t, double contentCapS, double gpuMs, double presentIntervalMs)
    {
        Ensure();
        lock (Gate)
        {
            if (kind == "generated") _gen++;
            else _real++;
            if (presentIntervalMs > 0 && presentIntervalMs < 250) Intervals.Add(presentIntervalMs);
            if (_file != null)
            {
                string cap = double.IsFinite(contentCapS) ? contentCapS.ToString("G17", CultureInfo.InvariantCulture) : "";
                string gpu = double.IsFinite(gpuMs) ? gpuMs.ToString("G17", CultureInfo.InvariantCulture) : "";
                string iv = double.IsFinite(presentIntervalMs) ? presentIntervalMs.ToString("G17", CultureInfo.InvariantCulture) : "";
                Buf.Append(qpcSeconds.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                    .Append(kind).Append(',')
                    .Append(t.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                    .Append(cap).Append(',')
                    .Append(gpu).Append(',')
                    .Append(iv).Append('\n');
                if (_lastFlushQpc == 0) _lastFlushQpc = qpcSeconds;
                if (qpcSeconds - _lastFlushQpc >= 1.0) FlushLocked();
            }
        }
    }

    public static void Flush()
    {
        lock (Gate) FlushLocked();
    }

    static void FlushLocked()
    {
        if (_file == null || Buf.Length == 0) return;
        _file.Write(Buf);
        _file.Flush();
        Buf.Clear();
        _lastFlushQpc = FgV2Pacing.QpcSeconds();
    }

    public static string Summary()
    {
        lock (Gate)
        {
            int n = Intervals.Count;
            int shown = _real + _gen;
            if (n == 0)
                return "Stats: present interval mean=n/a std=n/a generated share=n/a\n";
            double mean = Intervals.Average();
            double var = Intervals.Sum(x => (x - mean) * (x - mean)) / n;
            double std = Math.Sqrt(var);
            string share = shown == 0 ? "n/a" : (_gen / (double)shown).ToString("0.000", CultureInfo.InvariantCulture);
            return $"Stats: present interval mean={mean.ToString("0.000", CultureInfo.InvariantCulture)} ms std={std.ToString("0.000", CultureInfo.InvariantCulture)} ms n={n} generated share={share} ({_gen}/{shown})\n";
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            FlushLocked();
            _file?.Dispose();
            _file = null;
        }
    }
}
