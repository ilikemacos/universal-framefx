using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

public sealed class FgV2Async : IDisposable
{
    readonly Gpu _g;
    readonly FgV2 _host;
    readonly ID3D11Device _dev2;
    readonly ID3D11DeviceContext _ctx2;
    readonly ID3D11DeviceContext4 _ctx1_4, _ctx2_4;
    readonly ID3D11Device1 _dev2_1;
    readonly ID3D11Fence _fence1Main, _fence1Worker, _fence2Main, _fence2Worker;
    readonly FgV2 _worker;
    readonly Thread _thread;
    readonly AutoResetEvent _work = new(false);
    volatile bool _run = true;
    volatile Exception? _fail;
    ulong _vIn, _vOut, _waitOut;
    int _inW, _inH, _outW, _outH;
    Format _fmt;
    ID3D11Texture2D? _colMain, _colWork, _mvMain, _mvWork, _ocMain, _ocWork, _hudMain, _hudWork, _chgMain, _chgWork;
    ID3D11ShaderResourceView? _colSrv;
    int _pw, _ph;
    float _jx, _jy;

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public FgV2Async(Gpu g, FgV2 host)
    {
        _g = g; _host = host;
        _dev2 = Gpu.CreateDeviceOn(g.Adapter) ?? throw new InvalidOperationException("second D3D11 device");
        _ctx2 = _dev2.ImmediateContext;
        _dev2_1 = _dev2.QueryInterface<ID3D11Device1>();
        _ctx1_4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        _ctx2_4 = _ctx2.QueryInterface<ID3D11DeviceContext4>();
        using (var d5 = g.Device.QueryInterface<ID3D11Device5>())
        using (var d5b = _dev2.QueryInterface<ID3D11Device5>())
        {
            _fence1Main = d5.CreateFence(0, FenceFlags.Shared);
            IntPtr h1 = _fence1Main.CreateSharedHandle(null, null);
            try { _fence1Worker = OpenFence(d5b, h1); } finally { CloseHandle(h1); }
            _fence2Worker = d5b.CreateFence(0, FenceFlags.Shared);
            IntPtr h2 = _fence2Worker.CreateSharedHandle(null, null);
            try { _fence2Main = OpenFence(d5, h2); } finally { CloseHandle(h2); }
        }
        _worker = new FgV2(_dev2, _ctx2);
        _thread = new Thread(Loop) { IsBackground = true, Name = "FgV2Async" };
        _thread.Start();
    }

    static ID3D11Fence OpenFence(ID3D11Device5 d, IntPtr h) => d.OpenSharedFence<ID3D11Fence>(h);

    (ID3D11Texture2D main, ID3D11Texture2D work) Shared(int w, int h, Format f, BindFlags bind)
    {
        var t = _g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = bind,
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
        });
        using var r1 = t.QueryInterface<IDXGIResource1>();
        IntPtr sh = r1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
        try
        {
            var wtex = _dev2_1.OpenSharedResource1<ID3D11Texture2D>(sh);
            return (t, wtex);
        }
        finally { CloseHandle(sh); }
    }

    void EnsureShared(int inW, int inH, int outW, int outH, Format fmt)
    {
        if (inW == _inW && inH == _inH && outW == _outW && outH == _outH && fmt == _fmt && _colMain != null) return;
        DisposeShared();
        _inW = inW; _inH = inH; _outW = outW; _outH = outH; _fmt = fmt;
        int tx = Math.Max(1, (inW + 7) / 8), ty = Math.Max(1, (inH + 7) / 8);
        (_colMain, _colWork) = Shared(inW, inH, fmt, BindFlags.ShaderResource);
        _colSrv = _dev2.CreateShaderResourceView(_colWork);
        (_mvMain, _mvWork) = Shared(tx, ty, Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        (_ocMain, _ocWork) = Shared(tx, ty, Format.R8_UNorm, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        (_hudMain, _hudWork) = Shared(inW, inH, Format.R16_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        (_chgMain, _chgWork) = Shared(2, 1, Format.R32_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _worker.Ensure(inW, inH, outW, outH);
        _host.Ensure(inW, inH, outW, outH);
    }

    public Tex? Process(ID3D11ShaderResourceView input, int inW, int inH, int outW, int outH, float jx, float jy)
    {
        if (_fail != null) throw _fail;
        using var res = input.Resource.QueryInterface<ID3D11Texture2D>();
        EnsureShared(inW, inH, outW, outH, res.Description.Format);
        if (_waitOut != 0)
        {
            _ctx1_4.Wait(_fence2Main, _waitOut);
            _host.CopyMotionFrom(_mvMain!, _ocMain!, _hudMain!, _chgMain!);
        }
        _g.Context.CopyResource(_colMain!, res);
        _vIn++;
        _pw = inW; _ph = inH; _jx = jx; _jy = jy;
        _ctx1_4.Signal(_fence1Main, _vIn);
        _g.Context.Flush();
        _work.Set();
        var result = _waitOut != 0 ? _host.MotionTex : null;
        _waitOut = _vIn;
        return result;
    }

    void Loop()
    {
        while (_run)
        {
            _work.WaitOne();
            if (!_run) break;
            try
            {
                ulong v = _vIn;
                _ctx2_4.Wait(_fence1Worker, v);
                _worker.Motion = _host.Motion;
                _worker.MvGapN = _host.MvGapN;
                _worker.Process(_colSrv!, _pw, _ph, _jx, _jy);
                _host.EstimatorName = _worker.EstimatorName;
                _ctx2.CopyResource(_mvWork!, _worker.MotionTex.Texture);
                _ctx2.CopyResource(_ocWork!, _worker.OcclTex.Texture);
                _ctx2.CopyResource(_hudWork!, _worker.HudTex.Texture);
                _ctx2.CopyResource(_chgWork!, _worker.ChgTex.Texture);
                _vOut = v;
                _ctx2_4.Signal(_fence2Worker, v);
                _ctx2.Flush();
            }
            catch (Exception ex)
            {
                _fail = ex;
                try { _ctx2_4.Signal(_fence2Worker, _vIn); _ctx2.Flush(); } catch { }
            }
        }
    }

    void DisposeShared()
    {
        _colSrv?.Dispose(); _colSrv = null;
        foreach (var t in new[] { _colMain, _colWork, _mvMain, _mvWork, _ocMain, _ocWork, _hudMain, _hudWork, _chgMain, _chgWork })
            t?.Dispose();
        _colMain = _colWork = _mvMain = _mvWork = _ocMain = _ocWork = _hudMain = _hudWork = _chgMain = _chgWork = null;
    }

    public void Dispose()
    {
        _run = false;
        _work.Set();
        _thread.Join(2000);
        DisposeShared();
        _worker.Dispose();
        foreach (var d in new IDisposable?[] { _fence1Main, _fence1Worker, _fence2Main, _fence2Worker, _ctx1_4, _ctx2_4, _dev2_1, _ctx2, _dev2, _work })
            d?.Dispose();
    }
}
