using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

unsafe sealed class CursorOverlay : IDisposable
{
    readonly Gpu _g;
    ID3D11Texture2D? _tex;
    ID3D11ShaderResourceView? _srv;
    int _w, _h, _hotX, _hotY;
    IntPtr _lastCursor;
    int _lastX, _lastY;
    bool _have;
    public bool Visible { get; private set; }
    public (int x, int y, int w, int h) LastRect { get; private set; }

    public CursorOverlay(Gpu g) => _g = g;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public int CbSize, Flags;
        public IntPtr Cursor;
        public POINT ScreenPos;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool FIcon;
        public int XHotspot, YHotspot;
        public IntPtr HbmMask, HbmColor;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPixel;
        public IntPtr Bits;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    const int CursorShowing = 1;
    const int DiNormal = 3;
    const uint BiRgb = 0;

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int cx, int cy, uint istep, IntPtr flicker, int flags);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, out BITMAP b);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Native.RECT r);

    public static bool TryWindowOrigin(IntPtr hwnd, out int x, out int y)
    {
        x = y = 0;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var r = Native.FrameBounds(hwnd);
            x = r.Left; y = r.Top;
            return true;
        }
        catch
        {
            if (!GetWindowRect(hwnd, out var wr)) return false;
            x = wr.Left; y = wr.Top;
            return true;
        }
    }

    public bool Capture()
    {
        var ci = new CURSORINFO { CbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & CursorShowing) == 0 || ci.Cursor == IntPtr.Zero)
        {
            Visible = false;
            return false;
        }
        Visible = true;
        int sx = ci.ScreenPos.X, sy = ci.ScreenPos.Y;
        bool sameShape = ci.Cursor == _lastCursor && _tex != null;
        _lastX = sx; _lastY = sy;
        if (sameShape) return true;
        if (!GetIconInfo(ci.Cursor, out var ii)) return false;
        try
        {
            int cw = 32, ch = 32;
            IntPtr hb = ii.HbmColor != IntPtr.Zero ? ii.HbmColor : ii.HbmMask;
            if (GetObject(hb, Marshal.SizeOf<BITMAP>(), out var bm) != 0)
            {
                cw = Math.Clamp(bm.Width, 1, 256);
                ch = Math.Clamp(Math.Abs(bm.Height), 1, 256);
                if (ii.HbmColor == IntPtr.Zero) ch = Math.Max(1, ch / 2);
            }
            _hotX = ii.XHotspot; _hotY = ii.YHotspot;
            if (!UploadIcon(ci.Cursor, cw, ch)) return false;
            _lastCursor = ci.Cursor;
            _w = cw; _h = ch;
            _have = true;
            return true;
        }
        finally
        {
            if (ii.HbmColor != IntPtr.Zero) DeleteObject(ii.HbmColor);
            if (ii.HbmMask != IntPtr.Zero) DeleteObject(ii.HbmMask);
        }
    }

    bool UploadIcon(IntPtr hcursor, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER
        {
            Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = w,
            Height = -h,
            Planes = 1,
            BitCount = 32,
            Compression = (int)BiRgb
        };
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        IntPtr dib = CreateDIBSection(hdc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(hdc); return false; }
        IntPtr old = SelectObject(hdc, dib);
        try
        {
            DrawIconEx(hdc, 0, 0, hcursor, w, h, 0, IntPtr.Zero, DiNormal);
            EnsureTex(w, h);
            var m = _g.Context.Map(_tex!, 0, MapMode.WriteDiscard);
            try
            {
                unsafe
                {
                    int pitch = w * 4;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)bits + y * pitch, (byte*)m.DataPointer + y * (long)m.RowPitch, m.RowPitch, pitch);
                }
            }
            finally { _g.Context.Unmap(_tex!, 0); }
            return true;
        }
        finally
        {
            SelectObject(hdc, old);
            DeleteObject(dib);
            DeleteDC(hdc);
        }
    }

    void EnsureTex(int w, int h)
    {
        if (_tex != null && _w == w && _h == h) return;
        _srv?.Dispose(); _tex?.Dispose();
        _tex = _g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write
        });
        _srv = _g.Device.CreateShaderResourceView(_tex);
        _w = w; _h = h;
    }

    public void Draw(ID3D11DeviceContext ctx, int capOriginX, int capOriginY, int capW, int capH, int texW, int texH, int bbW, int bbH, float sx, float sy, float ox, float oy)
    {
        if (!_have || !Visible || _srv == null) return;
        int lx = _lastX - capOriginX - _hotX;
        int ly = _lastY - capOriginY - _hotY;
        float px = lx * (texW / (float)Math.Max(1, capW));
        float py = ly * (texH / (float)Math.Max(1, capH));
        float cw = _w * (texW / (float)Math.Max(1, capW));
        float ch = _h * (texH / (float)Math.Max(1, capH));
        float u0 = px / Math.Max(1, texW);
        float v0 = py / Math.Max(1, texH);
        float u1 = (px + cw) / Math.Max(1, texW);
        float v1 = (py + ch) / Math.Max(1, texH);
        float ndcL = ((u0 * sx + ox) * 2f - 1f);
        float ndcT = (1f - (v0 * sy + oy) * 2f);
        float ndcR = ((u1 * sx + ox) * 2f - 1f);
        float ndcB = (1f - (v1 * sy + oy) * 2f);
        LastRect = ((int)px, (int)py, (int)cw, (int)ch);
        var mapped = ctx.Map(_g.PresentCB, 0, MapMode.WriteDiscard);
        unsafe
        {
            var f = (float*)mapped.DataPointer;
            f[0] = ndcR - ndcL;
            f[1] = ndcB - ndcT;
            f[2] = ndcL;
            f[3] = ndcT;
        }
        ctx.Unmap(_g.PresentCB, 0);
        ctx.OMSetBlendState(_g.AlphaBlend, null, 0xffffffff);
        ctx.VSSetShader(_g.PresentCursorVS);
        ctx.PSSetShader(_g.PresentCursorPS);
        ctx.VSSetConstantBuffer(1, _g.PresentCB);
        ctx.PSSetConstantBuffer(1, _g.PresentCB);
        ctx.PSSetSampler(0, _g.LinearSampler);
        ctx.PSSetShaderResource(0, _srv);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null);
        ctx.OMSetBlendState(null, null, 0xffffffff);
        ctx.VSSetShader(_g.PresentVS);
        ctx.PSSetShader(_g.PresentPS);
    }

    public void Dispose()
    {
        _srv?.Dispose(); _tex?.Dispose();
        _srv = null; _tex = null;
    }
}
