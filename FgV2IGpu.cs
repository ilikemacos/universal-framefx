using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

sealed class FgV2IGpu : IDisposable
{
    const int Ring = 3;
    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11ComputeShader _p8;
    readonly ID3D11Buffer _cb;
    readonly ID3D11SamplerState _lin, _pt;
    Tex? _prev, _curr, _hist, _tv;
    ID3D11Texture2D[] _upPrev = new ID3D11Texture2D[Ring];
    ID3D11Texture2D[] _upCurr = new ID3D11Texture2D[Ring];
    ID3D11Texture2D[] _upHist = new ID3D11Texture2D[Ring];
    ID3D11Texture2D[] _rdTv = new ID3D11Texture2D[Ring];
    ID3D11Texture2D[] _mainPrev = Array.Empty<ID3D11Texture2D>();
    ID3D11Texture2D[] _mainCurr = Array.Empty<ID3D11Texture2D>();
    ID3D11Texture2D[] _mainHist = Array.Empty<ID3D11Texture2D>();
    ID3D11Texture2D[] _mainTv = Array.Empty<ID3D11Texture2D>();
    ID3D11Texture2D? _uploadTv;
    int _w, _h, _slot, _issued;
    bool _have;

    public FgV2IGpu()
    {
        using var ad = Gpu.OpenAdapterFor(GpuChoice.Integrated) ?? throw new InvalidOperationException("no integrated adapter");
        _dev = Gpu.CreateDeviceOn(ad) ?? throw new InvalidOperationException("iGPU device");
        _ctx = _dev.ImmediateContext;
        _lin = _dev.CreateSamplerState(SamplerDescription.LinearClamp);
        _pt = _dev.CreateSamplerState(SamplerDescription.PointClamp);
        string src;
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", "FgV2.hlsl");
        if (File.Exists(loose)) src = File.ReadAllText(loose);
        else
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("FgV2.hlsl") ?? throw new InvalidOperationException("FgV2.hlsl is missing.");
            src = new StreamReader(s).ReadToEnd();
        }
        var blobs = ShaderCache.GetMany(src, new[] { ("FG2_P8Search", "cs_5_0") }, "FgV2.hlsl");
        try { _p8 = _dev.CreateComputeShader(blobs["FG2_P8Search"]); }
        catch
        {
            using var bl = Gpu.CompileSource(src, "FG2_P8Search", "cs_5_0", "FgV2.hlsl");
            _p8 = _dev.CreateComputeShader(bl.AsSpan().ToArray());
        }
        _cb = _dev.CreateBuffer(new BufferDescription
        {
            ByteWidth = 128, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write
        });
    }

    static ID3D11Texture2D Staging(ID3D11Device d, int w, int h, Format f, CpuAccessFlags cpu) =>
        d.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = cpu,
        });

    public void Ensure(int w, int h)
    {
        if (w == _w && h == _h && _tv != null) return;
        DisposeTex();
        _w = w; _h = h;
        _prev = new Tex(_dev, w, h, Format.R16_Float);
        _curr = new Tex(_dev, w, h, Format.R16_Float);
        _hist = new Tex(_dev, w, h, Format.R16G16B16A16_Float);
        _tv = new Tex(_dev, w, h, Format.R16G16B16A16_Float);
        for (int i = 0; i < Ring; i++)
        {
            _upPrev[i] = Staging(_dev, w, h, Format.R16_Float, CpuAccessFlags.Write);
            _upCurr[i] = Staging(_dev, w, h, Format.R16_Float, CpuAccessFlags.Write);
            _upHist[i] = Staging(_dev, w, h, Format.R16G16B16A16_Float, CpuAccessFlags.Write);
            _rdTv[i] = Staging(_dev, w, h, Format.R16G16B16A16_Float, CpuAccessFlags.Read);
        }
        _slot = 0; _issued = 0; _have = false;
    }

    public void BindMain(ID3D11Device main, int w, int h)
    {
        if (_mainPrev.Length == Ring && _mainPrev[0] != null && _mainPrev[0].Description.Width == (uint)w) return;
        foreach (var t in _mainPrev.Concat(_mainCurr).Concat(_mainHist).Concat(_mainTv)) t?.Dispose();
        _uploadTv?.Dispose();
        _mainPrev = new ID3D11Texture2D[Ring];
        _mainCurr = new ID3D11Texture2D[Ring];
        _mainHist = new ID3D11Texture2D[Ring];
        _mainTv = new ID3D11Texture2D[Ring];
        for (int i = 0; i < Ring; i++)
        {
            _mainPrev[i] = Staging(main, w, h, Format.R16_Float, CpuAccessFlags.Read);
            _mainCurr[i] = Staging(main, w, h, Format.R16_Float, CpuAccessFlags.Read);
            _mainHist[i] = Staging(main, w, h, Format.R16G16B16A16_Float, CpuAccessFlags.Read);
            _mainTv[i] = Staging(main, w, h, Format.R16G16B16A16_Float, CpuAccessFlags.Write);
        }
        _uploadTv = Staging(main, w, h, Format.R16G16B16A16_Float, CpuAccessFlags.Write);
    }

    static void CopyMapped(ID3D11DeviceContext srcCtx, ID3D11Texture2D src, ID3D11DeviceContext dstCtx, ID3D11Texture2D dst, int w, int bpp)
    {
        if (srcCtx.Map(src, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var rm).Failure) throw new InvalidOperationException("map");
        try
        {
            var dw = dstCtx.Map(dst, 0, MapMode.Write);
            try
            {
                unsafe
                {
                    int pitch = w * bpp;
                    for (int y = 0; y < src.Description.Height; y++)
                    {
                        Buffer.MemoryCopy((byte*)rm.DataPointer + y * (long)rm.RowPitch, (byte*)dw.DataPointer + y * (long)dw.RowPitch, dw.RowPitch, pitch);
                    }
                }
            }
            finally { dstCtx.Unmap(dst, 0); }
        }
        finally { srcCtx.Unmap(src, 0); }
    }

    public bool TryP8(ID3D11DeviceContext main, Tex prev, Tex curr, Tex hist, Tex dest)
    {
        Ensure(prev.Width, prev.Height);
        BindMain(prev.Texture.Device, prev.Width, prev.Height);
        int s = _slot;
        main.CopyResource(_mainPrev[s], prev.Texture);
        main.CopyResource(_mainCurr[s], curr.Texture);
        main.CopyResource(_mainHist[s], hist.Texture);
        _slot = (_slot + 1) % Ring;
        _issued++;
        int r = (_slot + 1) % Ring;
        if (_issued < 3 || main.Map(_mainPrev[r], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out _).Failure)
        {
            if (_have) { main.CopyResource(dest.Texture, _uploadTv!); return true; }
            return false;
        }
        main.Unmap(_mainPrev[r], 0);
        try
        {
            CopyMapped(main, _mainPrev[r], _ctx, _upPrev[r], _w, 2);
            CopyMapped(main, _mainCurr[r], _ctx, _upCurr[r], _w, 2);
            CopyMapped(main, _mainHist[r], _ctx, _upHist[r], _w, 8);
            _ctx.CopyResource(_prev!.Texture, _upPrev[r]);
            _ctx.CopyResource(_curr!.Texture, _upCurr[r]);
            _ctx.CopyResource(_hist!.Texture, _upHist[r]);
            var p = new FgV2Params
            {
                InW = _w * 8, InH = _h * 8, TileSize = 8, TilesX = _w, TilesY = _h,
                HistoryValid = 1, Lambda = 0.02f, CensusW = 0.3f, SadW = 1f
            };
            var m = _ctx.Map(_cb, 0, MapMode.WriteDiscard);
            try { unsafe { *(FgV2Params*)m.DataPointer = p; } }
            finally { _ctx.Unmap(_cb, 0); }
            _ctx.CSSetShader(_p8);
            _ctx.CSSetConstantBuffer(0, _cb);
            _ctx.CSSetSampler(0, _lin);
            _ctx.CSSetSampler(1, _pt);
            _ctx.CSSetShaderResource(2, _prev.Srv);
            _ctx.CSSetShaderResource(3, _curr.Srv);
            _ctx.CSSetShaderResource(15, _hist.Srv);
            _ctx.CSSetUnorderedAccessView(1, _tv!.Uav);
            _ctx.Dispatch((uint)((_w + 7) / 8), (uint)((_h + 7) / 8), 1);
            _ctx.CSSetShaderResource(2, null);
            _ctx.CSSetShaderResource(3, null);
            _ctx.CSSetShaderResource(15, null);
            _ctx.CSSetUnorderedAccessView(1, null);
            _ctx.CopyResource(_rdTv[r], _tv.Texture);
            if (_ctx.Map(_rdTv[r], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var om).Success)
            {
                try
                {
                    var wm = main.Map(_uploadTv!, 0, MapMode.Write);
                    try
                    {
                        unsafe
                        {
                            int pitch = _w * 8;
                            for (int y = 0; y < _h; y++)
                                Buffer.MemoryCopy((byte*)om.DataPointer + y * (long)om.RowPitch, (byte*)wm.DataPointer + y * (long)wm.RowPitch, wm.RowPitch, pitch);
                        }
                    }
                    finally { main.Unmap(_uploadTv!, 0); }
                }
                finally { _ctx.Unmap(_rdTv[r], 0); }
                main.CopyResource(dest.Texture, _uploadTv!);
                _have = true;
                return true;
            }
        }
        catch { }
        if (_have) { main.CopyResource(dest.Texture, _uploadTv!); return true; }
        return false;
    }

    void DisposeTex()
    {
        _prev?.Dispose(); _curr?.Dispose(); _hist?.Dispose(); _tv?.Dispose();
        _prev = _curr = _hist = _tv = null;
        foreach (var t in _upPrev.Concat(_upCurr).Concat(_upHist).Concat(_rdTv).Concat(_mainPrev).Concat(_mainCurr).Concat(_mainHist).Concat(_mainTv)) t?.Dispose();
        _uploadTv?.Dispose(); _uploadTv = null;
        _upPrev = new ID3D11Texture2D[Ring]; _upCurr = new ID3D11Texture2D[Ring]; _upHist = new ID3D11Texture2D[Ring]; _rdTv = new ID3D11Texture2D[Ring];
        _mainPrev = _mainCurr = _mainHist = _mainTv = Array.Empty<ID3D11Texture2D>();
    }

    public void Dispose()
    {
        DisposeTex();
        _p8.Dispose(); _cb.Dispose(); _lin.Dispose(); _pt.Dispose();
        _ctx.Dispose(); _dev.Dispose();
    }
}
