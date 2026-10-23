using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Video;
using Vortice.DXGI;

namespace UniversalFrameFX;

public enum MotionPreference { Auto, Nvof, D3D12, Software }

/// <summary>GPU motion hardware. Vectors are block vectors from the input (current) frame to the
/// reference (previous) frame (Bwd) and the reverse (Fwd), as R16G16_SINT textures.</summary>
public interface IMotionEngine : IDisposable
{
    string Name { get; }
    int Grid { get; }        // pixels per vector
    float Scale { get; }     // vector units per pixel
    Tex Bwd { get; }
    Tex Fwd { get; }
    /// <summary>Estimate between two R8_UNORM luma frames. Throws on failure. Caller holds Gpu.Lock.</summary>
    void Estimate(Tex prevL8, Tex currL8);
    /// <summary>Forget temporal state (new scene).</summary>
    void ResetHistory();
}

public static class MotionEngines
{
    public const string SoftwareName = "Built-in (GPU)";

    public static string PrefName(MotionPreference p) => p switch
    {
        MotionPreference.Nvof => "NVIDIA Optical Flow",
        MotionPreference.D3D12 => "D3D12 Video Motion Estimation",
        MotionPreference.Software => "Software only",
        _ => "Auto (hardware when available)",
    };

    /// <summary>Create the best engine allowed by <paramref name="pref"/>; null means software.
    /// <paramref name="log"/> collects why hardware paths were skipped.</summary>
    public static IMotionEngine? Create(Gpu gpu, int w, int h, MotionPreference pref, List<string> log)
    {
        if (pref == MotionPreference.Software) return null;
        if (pref is MotionPreference.Auto or MotionPreference.Nvof)
        {
            if (gpu.VendorId == 0x10DE)
            {
                try { return new NvofEngine(gpu, w, h); }
                catch (Exception ex) { log.Add("NVIDIA Optical Flow: " + ex.Message); }
            }
            else log.Add("NVIDIA Optical Flow: not an NVIDIA GPU");
            if (pref == MotionPreference.Nvof) return null;
        }
        try { return new D3D12MeEngine(gpu, w, h); }
        catch (Exception ex) { log.Add("D3D12 Video Motion Estimation: " + ex.Message); }
        return null;
    }
}

/// <summary>NVIDIA Optical Flow through its Direct3D 11 interface. The runtime (nvofapi64.dll) ships
/// with the NVIDIA driver and is loaded at run time; it is not redistributed. Structures follow the
/// MIT-licensed nvOpticalFlowCommon.h / nvOpticalFlowD3D11.h (API 5.0).</summary>
public sealed unsafe class NvofEngine : IMotionEngine
{
    [StructLayout(LayoutKind.Sequential)]
    struct FnList { public IntPtr Create, Init, FmtCount, Fmt, Reg, Unreg, Exec, Destroy, LastErr, Caps; }

    [StructLayout(LayoutKind.Sequential)]
    struct InitParams
    {
        public uint Width, Height;
        public int OutGrid, HintGrid, Mode, Perf, EnableExternalHints, EnableOutputCost;
        public IntPtr PrivData;
        public int DisparityRange, EnableRoi, PredDirection, EnableGlobalFlow, InputBufferFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExecIn
    {
        public IntPtr Input, Reference, Hints;
        public int DisableTemporalHints;
        public uint Padding;
        public IntPtr PrivData;
        public uint Padding2, NumRois;
        public IntPtr RoiData;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExecOut { public IntPtr Output, Cost, PrivData, BwdOutput, BwdCost, GlobalFlow; }

    const uint ApiVersion = (5 << 4) | 0;
    const int OF_MODE_OPTICALFLOW = 1, PERF_MEDIUM = 10, PRED_FORWARD = 0, PRED_BOTH = 2, FMT_GRAYSCALE8 = 1;
    static IntPtr _lib;

    readonly Gpu _g;
    FnList _fn;
    IntPtr _h;
    readonly ID3D11Texture2D[] _in = new ID3D11Texture2D[2];
    readonly IntPtr[] _inH = new IntPtr[2];
    ID3D11Texture2D? _outB, _outF;
    IntPtr _outBH, _outFH;
    bool _both, _fresh = true;
    public void ResetHistory() => _fresh = true;

    public string Name { get; }
    public int Grid { get; private set; }
    public float Scale => 32f;      // S10.5
    public Tex Bwd { get; private set; } = null!;
    public Tex Fwd { get; private set; } = null!;

    static string Status(int s) => s switch
    {
        0 => "success", 1 => "optical flow not available", 2 => "unsupported device", 3 => "device does not exist",
        4 => "invalid pointer", 5 => "invalid parameter", 6 => "invalid call", 7 => "invalid version (driver too old?)",
        8 => "out of memory", 9 => "not initialized", 10 => "unsupported feature", _ => "generic error " + s,
    };

    void Check(int s, string what)
    {
        if (s == 0) return;
        string detail = "";
        if (_h != IntPtr.Zero && _fn.LastErr != IntPtr.Zero)
        {
            var buf = stackalloc byte[256]; uint size = 256;
            if (((delegate* unmanaged<IntPtr, byte*, uint*, int>)_fn.LastErr)(_h, buf, &size) == 0)
                detail = Marshal.PtrToStringAnsi((IntPtr)buf) ?? "";
        }
        throw new InvalidOperationException($"{what}: {Status(s)}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
    }

    public NvofEngine(Gpu g, int w, int h)
    {
        _g = g;
        if (_lib == IntPtr.Zero && !NativeLibrary.TryLoad("nvofapi64.dll", out _lib))
            throw new NotSupportedException("nvofapi64.dll not found (needs an NVIDIA driver with Optical Flow)");
        if (!NativeLibrary.TryGetExport(_lib, "NvOFAPICreateInstanceD3D11", out var createInst))
            throw new NotSupportedException("driver has no D3D11 optical flow entry point");
        FnList fn;
        int st = ((delegate* unmanaged<uint, FnList*, int>)createInst)(ApiVersion, &fn);
        if (st != 0) throw new NotSupportedException("NvOFAPICreateInstanceD3D11: " + Status(st));
        _fn = fn;

        Exception? last = null;
        // Prefer 2x2 grid (good edges at modest cost), then 4x4 (Turing), then 1x1; bidirectional
        // (Ampere+) first, else two forward passes.
        // 4x4 grid by default: on an RTX 3060 Laptop a 1483x762 window costs ~9.5 ms/frame at 4x4
        // versus ~26 ms at 2x2 (which caps output near 40 fps). UFX_NVOF_GRID=2 selects the finer grid.
        var grids = int.TryParse(Environment.GetEnvironmentVariable("UFX_NVOF_GRID"), out var eg) ? new[] { eg, 4, 2, 1 } : new[] { 4, 2, 1 };
        foreach (int grid in grids)
        foreach (bool both in new[] { true, false })
        {
            try { Open(w, h, grid, both); Name = "NVIDIA Optical Flow"; return; }
            catch (Exception ex) { last = ex; CloseHandle(); }
        }
        throw last ?? new InvalidOperationException("init failed");
    }

    void Open(int w, int h, int grid, bool both)
    {
        IntPtr hh;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)_fn.Create)(_g.Device.NativePointer, _g.Context.NativePointer, &hh), "nvCreateOpticalFlowD3D11");
        _h = hh;
        var ip = new InitParams
        {
            Width = (uint)w, Height = (uint)h, OutGrid = grid, HintGrid = 0, Mode = OF_MODE_OPTICALFLOW,
            Perf = int.TryParse(Environment.GetEnvironmentVariable("UFX_NVOF_PERF"), out var pf) ? pf : PERF_MEDIUM,
            PredDirection = both ? PRED_BOTH : PRED_FORWARD, InputBufferFormat = FMT_GRAYSCALE8,
        };
        Check(((delegate* unmanaged<IntPtr, InitParams*, int>)_fn.Init)(_h, &ip), $"nvOFInit (grid {grid})");
        Grid = grid; _both = both;
        int ow = (w + grid - 1) / grid, oh = (h + grid - 1) / grid;
        for (int i = 0; i < 2; i++) { _in[i] = MakeTex(w, h, Format.R8_UNorm); _inH[i] = Register(_in[i]); }
        _outB = MakeTex(ow, oh, Format.R16G16_SInt); _outBH = Register(_outB);
        _outF = MakeTex(ow, oh, Format.R16G16_SInt); _outFH = Register(_outF);
        Bwd = new Tex(_g.Device, ow, oh, Format.R16G16_SInt, uav: false);
        Fwd = new Tex(_g.Device, ow, oh, Format.R16G16_SInt, uav: false);
    }

    ID3D11Texture2D MakeTex(int w, int h, Format f) => _g.Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.None,
    });

    IntPtr Register(ID3D11Texture2D t)
    {
        IntPtr hb;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)_fn.Reg)(_h, t.NativePointer, &hb), "nvOFRegisterResourceD3D11");
        return hb;
    }

    public void Estimate(Tex prevL8, Tex currL8)
    {
        var ctx = _g.Context;
        ctx.CopyResource(_in[0], prevL8.Texture);
        ctx.CopyResource(_in[1], currL8.Texture);
        var exec = (delegate* unmanaged<IntPtr, ExecIn*, ExecOut*, int>)_fn.Exec;
        if (_both)
        {
            var i = new ExecIn { Input = _inH[1], Reference = _inH[0], DisableTemporalHints = _fresh ? 1 : 0 };
            var o = new ExecOut { Output = _outBH, BwdOutput = _outFH };
            Check(exec(_h, &i, &o), "nvOFExecute");
        }
        else
        {
            var i = new ExecIn { Input = _inH[1], Reference = _inH[0], DisableTemporalHints = 1 };
            var o = new ExecOut { Output = _outBH };
            Check(exec(_h, &i, &o), "nvOFExecute");
            var i2 = new ExecIn { Input = _inH[0], Reference = _inH[1], DisableTemporalHints = 1 };
            var o2 = new ExecOut { Output = _outFH };
            Check(exec(_h, &i2, &o2), "nvOFExecute");
        }
        _fresh = false;
        ctx.CopyResource(Bwd.Texture, _outB!);
        ctx.CopyResource(Fwd.Texture, _outF!);
    }

    void CloseHandle()
    {
        var unreg = (delegate* unmanaged<IntPtr, int>)_fn.Unreg;
        foreach (var hb in new[] { _inH[0], _inH[1], _outBH, _outFH }) if (hb != IntPtr.Zero && unreg != null) unreg(hb);
        _inH[0] = _inH[1] = _outBH = _outFH = IntPtr.Zero;
        if (_h != IntPtr.Zero && _fn.Destroy != IntPtr.Zero) ((delegate* unmanaged<IntPtr, int>)_fn.Destroy)(_h);
        _h = IntPtr.Zero;
        _in[0]?.Dispose(); _in[1]?.Dispose(); _outB?.Dispose(); _outF?.Dispose();
        _in[0] = _in[1] = null!; _outB = _outF = null;
        Bwd?.Dispose(); Fwd?.Dispose(); Bwd = null!; Fwd = null!;
    }

    public void Dispose() => CloseHandle();
}

/// <summary>D3D12 Video Motion Estimation (ID3D12VideoMotionEstimator) on the same adapter, fed
/// through D3D11/D3D12 shared textures and a shared fence.</summary>
public sealed class D3D12MeEngine : IMotionEngine
{
    readonly Gpu _g;
    readonly ID3D12Device _dev;
    readonly ID3D12VideoDevice1 _vdev;
    readonly ID3D12CommandQueue _qDirect, _qEnc;
    readonly ID3D12CommandAllocator _aDirect, _aEnc;
    readonly ID3D12GraphicsCommandList _clDirect;
    readonly ID3D12VideoEncodeCommandList _clEnc;
    readonly ID3D12VideoMotionEstimator _me;
    readonly ID3D12VideoMotionVectorHeap _heapB, _heapF;
    readonly ID3D12Resource _nvPrev, _nvCurr, _stage;
    readonly ID3D11Texture2D _l8Prev11, _l8Curr11, _mvB11, _mvF11;
    readonly ID3D12Resource _l8Prev12, _l8Curr12, _mvB12, _mvF12;
    readonly ID3D11Fence _f11;
    readonly ID3D12Fence _f12;
    readonly ID3D11DeviceContext4 _ctx4;
    readonly AutoResetEvent _evt = new(false);
    readonly int _w, _h, _aw, _ah, _block;
    readonly uint _pitch;
    ulong _v;

    public string Name { get; }
    public int Grid => _block;
    public float Scale => 4f;       // quarter-pel
    public Tex Bwd { get; }
    public Tex Fwd { get; }
    public void ResetHistory() { }

    string _step = "";
    T Step<T>(string name, Func<T> f) { _step = name; try { return f(); } catch (Exception ex) { throw new InvalidOperationException($"{name}: {ex.Message.Split('\n')[0].Trim()}", ex); } }

    public D3D12MeEngine(Gpu g, int w, int h)
    {
        _g = g; _w = w; _h = h;
        _aw = (w + 15) & ~15; _ah = (h + 15) & ~15;
        _dev = Step("D3D12CreateDevice", () => D3D12.D3D12CreateDevice<ID3D12Device>(g.Adapter, FeatureLevel.Level_11_0))
               ?? throw new NotSupportedException("no Direct3D 12 device on this adapter");
        _vdev = _dev.QueryInterfaceOrNull<ID3D12VideoDevice1>() ?? throw new NotSupportedException("driver has no ID3D12VideoDevice1");
        var fd = new FeatureDataVideoMotionEstimator { NodeIndex = 0, InputFormat = Format.NV12 };
        if (!_vdev.CheckFeatureSupport(Vortice.Direct3D12.Video.FeatureVideo.MotionEstimator, ref fd) || fd.BlockSizeFlags == VideoMotionEstimatorSearchBlockSizeFlags.None)
            throw new NotSupportedException("motion estimation not supported by this GPU/driver");
        if ((fd.PrecisionFlags & VideoMotionEstimatorVectorPrecisionFlags.QuarterPel) == 0)
            throw new NotSupportedException("quarter-pel motion estimation not supported");
        if (_aw > fd.SizeRange.MaxWidth || _ah > fd.SizeRange.MaxHeight || _aw < fd.SizeRange.MinWidth || _ah < fd.SizeRange.MinHeight)
            throw new NotSupportedException($"{_aw}x{_ah} outside supported {fd.SizeRange.MinWidth}x{fd.SizeRange.MinHeight}..{fd.SizeRange.MaxWidth}x{fd.SizeRange.MaxHeight}");
        bool b8 = (fd.BlockSizeFlags & VideoMotionEstimatorSearchBlockSizeFlags.Size8x8) != 0;
        _block = b8 ? 8 : 16;
        var size = new VideoSizeRange { MaxWidth = (uint)_aw, MaxHeight = (uint)_ah, MinWidth = (uint)_aw, MinHeight = (uint)_ah };
        var bs = b8 ? VideoMotionEstimatorSearchBlockSize.Size8x8 : VideoMotionEstimatorSearchBlockSize.Size16x16;
        _me = Step("CreateVideoMotionEstimator", () => _vdev.CreateVideoMotionEstimator(new VideoMotionEstimatorDescription
        { NodeMask = 0, InputFormat = Format.NV12, BlockSize = bs, Precision = VideoMotionEstimatorVectorPrecision.QuarterPel, SizeRange = size }, null));
        var hd = new VideoMotionVectorHeapDescription { NodeMask = 0, InputFormat = Format.NV12, BlockSize = bs, Precision = VideoMotionEstimatorVectorPrecision.QuarterPel, SizeRange = size };
        _heapB = Step("CreateVideoMotionVectorHeap", () => _vdev.CreateVideoMotionVectorHeap(hd, null));
        _heapF = _vdev.CreateVideoMotionVectorHeap(hd, null);

        _qDirect = Step("direct queue", () => _dev.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)));
        _qEnc = Step("video encode queue", () => _dev.CreateCommandQueue(new CommandQueueDescription(CommandListType.VideoEncode)));
        _aDirect = _dev.CreateCommandAllocator(CommandListType.Direct);
        _aEnc = Step("encode allocator", () => _dev.CreateCommandAllocator(CommandListType.VideoEncode));
        _clDirect = _dev.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _aDirect, null);
        _clEnc = Step("encode command list", () => _dev.CreateCommandList<ID3D12VideoEncodeCommandList>(0, CommandListType.VideoEncode, _aEnc, null));
        _clEnc.Close();
        _clDirect.Close();

        var nvDesc = ResourceDescription.Texture2D(Format.NV12, (uint)_aw, (uint)_ah, 1, 1);
        _nvPrev = Step("NV12 texture", () => _dev.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, nvDesc, ResourceStates.Common));
        _nvCurr = _dev.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, nvDesc, ResourceStates.Common);
        _pitch = (uint)((_aw + 255) & ~255);
        _stage = _dev.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                    ResourceDescription.Buffer((ulong)_pitch * (ulong)_ah), ResourceStates.Common);

        // Shared D3D11 <-> D3D12 textures and fence.
        (_l8Prev11, _l8Prev12) = Step("shared luma texture", () => Shared(w, h, Format.R8_UNorm));
        (_l8Curr11, _l8Curr12) = Shared(w, h, Format.R8_UNorm);
        int bw = (_aw + _block - 1) / _block, bh = (_ah + _block - 1) / _block;
        (_mvB11, _mvB12) = Step("shared vector texture", () => Shared(bw, bh, Format.R16G16_SInt));
        (_mvF11, _mvF12) = Shared(bw, bh, Format.R16G16_SInt);
        using (var d5 = g.Device.QueryInterface<ID3D11Device5>())
        {
            _f11 = Step("shared fence", () => d5.CreateFence(0, Vortice.Direct3D11.FenceFlags.Shared));
            IntPtr fh = _f11.CreateSharedHandle(null, null);
            try { _f12 = _dev.OpenSharedHandle<ID3D12Fence>(fh); } finally { CloseHandle(fh); }
        }
        _ctx4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        Bwd = new Tex(g.Device, bw, bh, Format.R16G16_SInt, uav: false);
        Fwd = new Tex(g.Device, bw, bh, Format.R16G16_SInt, uav: false);

        // Chroma planes: constant 128 (motion search uses luma).
        Step("chroma init", () => { InitChroma(_nvPrev); InitChroma(_nvCurr); return 0; });
        Name = $"D3D12 Video Motion Estimation ({_block}x{_block} blocks)";
    }

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    (ID3D11Texture2D, ID3D12Resource) Shared(int w, int h, Format f)
    {
        var t = _g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
        });
        using var r1 = t.QueryInterface<IDXGIResource1>();
        IntPtr sh = r1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
        try { return (t, _dev.OpenSharedHandle<ID3D12Resource>(sh)); }
        finally { CloseHandle(sh); }
    }

    void InitChroma(ID3D12Resource nv)
    {
        uint cw = (uint)(_aw / 2), ch = (uint)(_ah / 2), pitch = (uint)((cw * 2 + 255) & ~255);
        using var up = _dev.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                          ResourceDescription.Buffer((ulong)pitch * ch), ResourceStates.GenericRead);
        unsafe
        {
            void* p; up.Map(0, null, &p);
            new Span<byte>(p, (int)(pitch * ch)).Fill(128);
            up.Unmap(0);
        }
        _aDirect.Reset(); _clDirect.Reset(_aDirect, null);
        _clDirect.ResourceBarrierTransition(nv, ResourceStates.Common, ResourceStates.CopyDest, 1);
        var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Format.R8G8_UNorm, cw, ch, 1, pitch) };
        _clDirect.CopyTextureRegion(new TextureCopyLocation(nv, 1), 0, 0, 0, new TextureCopyLocation(up, fp), null);
        _clDirect.ResourceBarrierTransition(nv, ResourceStates.CopyDest, ResourceStates.Common, 1);
        _clDirect.Close();
        _qDirect.ExecuteCommandList(_clDirect);
        _v++; _qDirect.Signal(_f12 ?? throw new InvalidOperationException(), _v);
        WaitCpu(_v);
    }

    void WaitCpu(ulong v)
    {
        if (_f12.CompletedValue >= v) return;
        _f12.SetEventOnCompletion(v, _evt);
        if (!_evt.WaitOne(2000)) throw new TimeoutException("D3D12 motion estimation timed out");
    }

    public void Estimate(Tex prevL8, Tex currL8)
    {
        try { EstimateCore(prevL8, currL8); }
        catch (Exception ex) { throw new InvalidOperationException($"{_step}: {ex.Message.Split('\n')[0].Trim()}", ex); }
    }

    void EstimateCore(Tex prevL8, Tex currL8)
    {
        var ctx = _g.Context;
        _step = "wait previous";
        WaitCpu(_v);                         // previous frame's D3D12 work done: allocators reusable
        ctx.CopyResource(_l8Prev11, prevL8.Texture);
        ctx.CopyResource(_l8Curr11, currL8.Texture);
        ulong vIn = ++_v;
        _ctx4.Signal(_f11, vIn);
        ctx.Flush();

        // Direct queue: luma -> NV12 plane 0 (through a buffer footprint).
        _step = "direct allocator reset"; _aDirect.Reset();
        _step = "direct list reset"; _clDirect.Reset(_aDirect, null);
        _step = "record copies";
        var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Format.R8_UNorm, (uint)_w, (uint)_h, 1, _pitch) };
        foreach (var (src, dst) in new[] { (_l8Prev12, _nvPrev), (_l8Curr12, _nvCurr) })
        {
            _clDirect.ResourceBarrierTransition(src, ResourceStates.Common, ResourceStates.CopySource, 0);
            _clDirect.ResourceBarrierTransition(_stage, ResourceStates.Common, ResourceStates.CopyDest, 0);
            _clDirect.CopyTextureRegion(new TextureCopyLocation(_stage, fp), 0, 0, 0, new TextureCopyLocation(src, 0), null);
            _clDirect.ResourceBarrierTransition(_stage, ResourceStates.CopyDest, ResourceStates.CopySource, 0);
            _clDirect.ResourceBarrierTransition(dst, ResourceStates.Common, ResourceStates.CopyDest, 0);
            _clDirect.CopyTextureRegion(new TextureCopyLocation(dst, 0), 0, 0, 0, new TextureCopyLocation(_stage, fp), null);
            _clDirect.ResourceBarrierTransition(dst, ResourceStates.CopyDest, ResourceStates.Common, 0);
            _clDirect.ResourceBarrierTransition(_stage, ResourceStates.CopySource, ResourceStates.Common, 0);
            _clDirect.ResourceBarrierTransition(src, ResourceStates.CopySource, ResourceStates.Common, 0);
        }
        _step = "close direct list"; _clDirect.Close();
        _step = "submit copies";
        _qDirect.Wait(_f12, vIn);
        _qDirect.ExecuteCommandList(_clDirect);
        ulong vCopy = ++_v;
        _qDirect.Signal(_f12, vCopy);

        // Video encode queue: motion both ways, resolve into the shared textures.
        _step = "encode allocator reset"; _aEnc.Reset();
        _step = "encode list reset"; _clEnc.Reset(_aEnc);
        _step = "record motion estimation";
        const uint All = 0xFFFFFFFF;
        _clEnc.ResourceBarrierTransition(_nvPrev, ResourceStates.Common, ResourceStates.VideoEncodeRead, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_nvCurr, ResourceStates.Common, ResourceStates.VideoEncodeRead, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_mvB12, ResourceStates.Common, ResourceStates.VideoEncodeWrite, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_mvF12, ResourceStates.Common, ResourceStates.VideoEncodeWrite, All, ResourceBarrierFlags.None);
        var outB = new VideoMotionEstimatorOutput { MotionVectorHeap = _heapB };
        var inB = new VideoMotionEstimatorInput { InputTexture2D = _nvCurr, InputSubresourceIndex = 0, ReferenceTexture2D = _nvPrev, ReferenceSubresourceIndex = 0 };
        _step = "estimate backward"; _clEnc.EstimateMotion(_me, ref outB, ref inB);
        var outF = new VideoMotionEstimatorOutput { MotionVectorHeap = _heapF };
        var inF = new VideoMotionEstimatorInput { InputTexture2D = _nvPrev, InputSubresourceIndex = 0, ReferenceTexture2D = _nvCurr, ReferenceSubresourceIndex = 0 };
        _step = "estimate forward"; _clEnc.EstimateMotion(_me, ref outF, ref inF);
        var rOutB = new ResolveVideoMotionVectorHeapOutput { MotionVectorTexture2D = _mvB12 };
        var rInB = new ResolveVideoMotionVectorHeapInput { MotionVectorHeap = _heapB, PixelWidth = (uint)_aw, PixelHeight = (uint)_ah };
        _step = "resolve backward"; _clEnc.ResolveMotionVectorHeap(ref rOutB, ref rInB);
        var rOutF = new ResolveVideoMotionVectorHeapOutput { MotionVectorTexture2D = _mvF12 };
        var rInF = new ResolveVideoMotionVectorHeapInput { MotionVectorHeap = _heapF, PixelWidth = (uint)_aw, PixelHeight = (uint)_ah };
        _step = "resolve forward"; _clEnc.ResolveMotionVectorHeap(ref rOutF, ref rInF);
        _step = "barriers out";
        _clEnc.ResourceBarrierTransition(_nvPrev, ResourceStates.VideoEncodeRead, ResourceStates.Common, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_nvCurr, ResourceStates.VideoEncodeRead, ResourceStates.Common, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_mvB12, ResourceStates.VideoEncodeWrite, ResourceStates.Common, All, ResourceBarrierFlags.None);
        _clEnc.ResourceBarrierTransition(_mvF12, ResourceStates.VideoEncodeWrite, ResourceStates.Common, All, ResourceBarrierFlags.None);
        _step = "close encode list"; _clEnc.Close();
        _step = "submit motion estimation";
        _qEnc.Wait(_f12, vCopy);
        _qEnc.ExecuteCommandList(_clEnc);
        ulong vDone = ++_v;
        _qEnc.Signal(_f12, vDone);

        _ctx4.Wait(_f11, vDone);             // GPU-side wait, no CPU stall
        ctx.CopyResource(Bwd.Texture, _mvB11);
        ctx.CopyResource(Fwd.Texture, _mvF11);
    }

    public void Dispose()
    {
        try { WaitCpu(_v); } catch { }
        Bwd.Dispose(); Fwd.Dispose();
        foreach (var d in new IDisposable?[] { _mvB11, _mvF11, _l8Prev11, _l8Curr11, _mvB12, _mvF12, _l8Prev12, _l8Curr12,
                     _nvPrev, _nvCurr, _stage, _heapB, _heapF, _me, _clEnc, _clDirect, _aEnc, _aDirect, _qEnc, _qDirect,
                     _f12, _f11, _ctx4, _vdev, _dev, _evt })
            d?.Dispose();
    }
}
