using System.Text;

namespace UniversalFrameFX;

internal static class ScoreHelpers
{
    public static byte[] LoadPngRgb8(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var outp = new byte[w * h * 3];
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* src = (byte*)bd.Scan0 + y * (long)bd.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 3;
                        outp[o] = src[x * 4 + 2];
                        outp[o + 1] = src[x * 4 + 1];
                        outp[o + 2] = src[x * 4];
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return outp;
    }

    public static double PsnrRgb8(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        int parts = Math.Max(1, Environment.ProcessorCount);
        var acc = new double[parts];
        Parallel.For(0, parts, c =>
        {
            int start = (int)((long)n * c / parts);
            int end = (int)((long)n * (c + 1) / parts);
            double s = 0;
            for (int i = start; i < end; i++)
            {
                int d = a[i] - b[i];
                s += d * (double)d;
            }
            acc[c] = s;
        });
        double mse = acc.Sum() / n;
        if (mse <= 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    public static double Percentile(List<double> xs, double p)
    {
        var a = xs.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        if (a.Length == 1) return a[0];
        double x = p * (a.Length - 1);
        int lo = (int)Math.Floor(x);
        int hi = Math.Min(lo + 1, a.Length - 1);
        double f = x - lo;
        return a[lo] * (1 - f) + a[hi] * f;
    }

    public sealed class TeeWriter : TextWriter
    {
        readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }
}
