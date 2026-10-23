using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

public struct AffineModel
{
    public float A00, A01, A02, A10, A11, A12, Conf, SnapOn;
}

public sealed class GlobalAffineEstimator : IDisposable
{
    public const int RansacIters = 96;
    public const float RansacThresh = 1.75f;
    public static float InlierPx = RansacThresh;
    public const float SnapMedian = 12f;
    public const int RansacSeed = 1;
    const int Ring = 3;
    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly bool _on;
    ID3D11Texture2D[] _stage = new ID3D11Texture2D[Ring];
    int _w, _h, _slot, _issued;
    AffineModel _model = Identity();
    public AffineModel Last => _model;
    public bool Enabled => _on;

    public GlobalAffineEstimator(ID3D11Device dev, ID3D11DeviceContext ctx)
    {
        _dev = dev; _ctx = ctx;
        _on = Environment.GetEnvironmentVariable("UFX_FG_V2_GRANSAC") != "0";
    }

    public static AffineModel Identity() => new() { A00 = 1, A11 = 1, Conf = 0, SnapOn = 0 };

    static ID3D11Texture2D Staging(ID3D11Device d, int w, int h) =>
        d.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });

    public void Ensure(int w, int h)
    {
        if (!_on) return;
        if (w == _w && h == _h && _stage[0] != null) return;
        foreach (var t in _stage) t?.Dispose();
        _w = w; _h = h;
        for (int i = 0; i < Ring; i++) _stage[i] = Staging(_dev, w, h);
        _slot = 0; _issued = 0;
        _model = Identity();
    }

    public void Push(Tex mv)
    {
        if (!_on || mv == null) return;
        Ensure(mv.Width, mv.Height);
        int s = _slot;
        _ctx.CopyResource(_stage[s], mv.Texture);
        _slot = (_slot + 1) % Ring;
        _issued++;
        int r = (_slot + 1) % Ring;
        if (_issued < 3) return;
        if (_ctx.Map(_stage[r], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var m).Failure) return;
        try
        {
            int n = _w * _h;
            var vx = new float[n];
            var vy = new float[n];
            var cf = new float[n];
            unsafe
            {
                for (int y = 0; y < _h; y++)
                {
                    byte* row = (byte*)m.DataPointer + y * (long)m.RowPitch;
                    for (int x = 0; x < _w; x++)
                    {
                        int i = y * _w + x;
                        var h4 = *(ulong*)(row + x * 8);
                        vx[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(h4 & 0xffff));
                        vy[i] = (float)BitConverter.UInt16BitsToHalf((ushort)((h4 >> 16) & 0xffff));
                        cf[i] = (float)BitConverter.UInt16BitsToHalf((ushort)((h4 >> 32) & 0xffff));
                    }
                }
            }
            _model = FitRansac(vx, vy, cf, _w, _h, RansacIters, RansacSeed);
        }
        finally { _ctx.Unmap(_stage[r], 0); }
    }

    public static float MedianVecLen(float[] vx, float[] vy, int n)
    {
        if (n <= 0) return 0;
        var xs = new float[n];
        var ys = new float[n];
        Array.Copy(vx, xs, n);
        Array.Copy(vy, ys, n);
        Array.Sort(xs);
        Array.Sort(ys);
        int m = n / 2;
        float mx = (n & 1) == 0 ? 0.5f * (xs[m - 1] + xs[m]) : xs[m];
        float my = (n & 1) == 0 ? 0.5f * (ys[m - 1] + ys[m]) : ys[m];
        return MathF.Sqrt(mx * mx + my * my);
    }

    public static bool SnapTile(float vx, float vy, float fitX, float fitY, float snapOn, float thresh = RansacThresh) =>
        snapOn >= 0.5f && MathF.Sqrt((vx - fitX) * (vx - fitX) + (vy - fitY) * (vy - fitY)) > thresh;

    public static void FittedAt(in AffineModel m, float cx, float cy, out float vx, out float vy)
    {
        vx = m.A00 * cx + m.A01 * cy + m.A02 - cx;
        vy = m.A10 * cx + m.A11 * cy + m.A12 - cy;
    }

    public static AffineModel FitRansac(float[] vx, float[] vy, float[] conf, int w, int h, int iters, int seed = RansacSeed)
    {
        int n = w * h;
        var rng = new Random(seed);
        AffineModel best = Identity();
        float bestIn = -1;
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) if (conf[i] > 0.05f) idx.Add(i);
        if (idx.Count < 6) { best.SnapOn = MedianVecLen(vx, vy, n) > SnapMedian ? 1f : 0f; return best; }
        Span<int> pick = stackalloc int[3];
        for (int it = 0; it < iters; it++)
        {
            for (int k = 0; k < 3; k++) pick[k] = idx[rng.Next(idx.Count)];
            if (!Solve3(vx, vy, w, pick, out var m)) continue;
            float inn = 0, tot = 0;
            int nin = 0;
            for (int k = 0; k < idx.Count; k++)
            {
                int i = idx[k];
                int x = i % w, y = i / w;
                float cx = (x + 0.5f) * 8f, cy = (y + 0.5f) * 8f;
                float px = m.A00 * cx + m.A01 * cy + m.A02;
                float py = m.A10 * cx + m.A11 * cy + m.A12;
                float dx = px - (cx + vx[i]), dy = py - (cy + vy[i]);
                float e = MathF.Sqrt(dx * dx + dy * dy);
                float wt = conf[i];
                tot += wt;
                if (e < InlierPx) { inn += wt; nin++; }
            }
            float frac = tot > 1e-4f ? inn / tot : 0;
            if (nin >= 12 && frac > bestIn) { bestIn = frac; m.Conf = frac; best = m; }
        }
        best.SnapOn = MedianVecLen(vx, vy, n) > SnapMedian ? 1f : 0f;
        return best;
    }

    public static bool Solve3(float[] vx, float[] vy, int w, ReadOnlySpan<int> pick, out AffineModel m)
    {
        m = Identity();
        Span<float> a = stackalloc float[36];
        Span<float> b = stackalloc float[6];
        for (int k = 0; k < 3; k++)
        {
            int i = pick[k];
            int x = i % w, y = i / w;
            float cx = (x + 0.5f) * 8f, cy = (y + 0.5f) * 8f;
            float px = cx + vx[i], py = cy + vy[i];
            int r0 = k * 2, r1 = r0 + 1;
            a[r0 * 6 + 0] = cx; a[r0 * 6 + 1] = cy; a[r0 * 6 + 2] = 1;
            a[r1 * 6 + 3] = cx; a[r1 * 6 + 4] = cy; a[r1 * 6 + 5] = 1;
            b[r0] = px; b[r1] = py;
        }
        Span<float> xsol = stackalloc float[6];
        if (!Solve6(a, b, xsol)) return false;
        m = new AffineModel
        {
            A00 = xsol[0], A01 = xsol[1], A02 = xsol[2],
            A10 = xsol[3], A11 = xsol[4], A12 = xsol[5], Conf = 1
        };
        return true;
    }

    public static bool SolveHomography4(ReadOnlySpan<(float x, float y, float xp, float yp)> pts, Span<float> h)
    {
        if (pts.Length < 4) return false;
        Span<float> a = stackalloc float[72];
        Span<float> b = stackalloc float[8];
        for (int i = 0; i < 4; i++)
        {
            var p = pts[i];
            int r0 = i * 2, r1 = r0 + 1;
            a[r0 * 8 + 0] = p.x; a[r0 * 8 + 1] = p.y; a[r0 * 8 + 2] = 1; a[r0 * 8 + 6] = -p.xp * p.x; a[r0 * 8 + 7] = -p.xp * p.y;
            a[r1 * 8 + 3] = p.x; a[r1 * 8 + 4] = p.y; a[r1 * 8 + 5] = 1; a[r1 * 8 + 6] = -p.yp * p.x; a[r1 * 8 + 7] = -p.yp * p.y;
            b[r0] = p.xp; b[r1] = p.yp;
        }
        return SolveN(a, b, h, 8);
    }

    static bool Solve6(Span<float> a, Span<float> b, Span<float> x) => SolveN(a, b, x, 6);

    static bool SolveN(Span<float> a, Span<float> b, Span<float> x, int n)
    {
        Span<float> m = stackalloc float[n * n];
        a.CopyTo(m);
        b.CopyTo(x);
        for (int i = 0; i < n; i++)
        {
            int piv = i;
            float best = Math.Abs(m[i * n + i]);
            for (int r = i + 1; r < n; r++)
            {
                float v = Math.Abs(m[r * n + i]);
                if (v > best) { best = v; piv = r; }
            }
            if (best < 1e-8f) return false;
            if (piv != i)
            {
                for (int c = 0; c < n; c++) (m[i * n + c], m[piv * n + c]) = (m[piv * n + c], m[i * n + c]);
                (x[i], x[piv]) = (x[piv], x[i]);
            }
            float d = m[i * n + i];
            for (int c = i; c < n; c++) m[i * n + c] /= d;
            x[i] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == i) continue;
                float f = m[r * n + i];
                for (int c = i; c < n; c++) m[r * n + c] -= f * m[i * n + c];
                x[r] -= f * x[i];
            }
        }
        return true;
    }

    public static float CandidateCost(float localSad, float affSad, float localConf, float affConf) =>
        localConf < 0.35f && affConf > 0.4f && affSad <= localSad + 0.05f ? affSad : localSad;

    public void Dispose()
    {
        foreach (var t in _stage) t?.Dispose();
        Array.Clear(_stage);
    }
}
