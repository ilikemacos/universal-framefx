using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace UniversalFrameFX;

// Third-party upscalers and frame generation, run through their vendors' own runtimes:
//   AMD FSR 2 / FSR 3 upscaling and FSR 3 frame generation: amd_fidelityfx_dx12.dll (FidelityFX SDK 1.1.4, MIT)
//   AMD FSR 4: amd_fidelityfx_loader_dx12.dll + amd_fidelityfx_upscaler_dx12.dll (FidelityFX SDK 2.3, AMD binary licence);
//              offered only when the runtime reports an FSR 4 provider for this GPU.
//   Intel XeSS: libxess.dll (XeSS SDK 3.0, Intel Simplified Software License), D3D12 path (DP4a on non-Intel GPUs).
// Captured windows have no game motion vectors or depth: FrameFX's hardware motion vectors and a flat depth are fed in.

/// <summary>A texture shared between the D3D11 pipeline and the D3D12 vendor runtimes.</summary>
public sealed class SharedTex : IDisposable
{
    public ID3D11Texture2D T11 { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public ID3D11UnorderedAccessView? Uav { get; }
    public ID3D12Resource T12 { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public SharedTex(ID3D11Device dev, ID3D12Device d12, int w, int h, Format f, bool uav)
    {
        Width = w; Height = h; Format = f;
        var bind = BindFlags.ShaderResource | (uav ? BindFlags.UnorderedAccess : 0);
        T11 = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = bind,
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
        });
        Srv = dev.CreateShaderResourceView(T11);
        Uav = uav ? dev.CreateUnorderedAccessView(T11) : null;
        using var r1 = T11.QueryInterface<IDXGIResource1>();
        IntPtr sh = r1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
        try { T12 = d12.OpenSharedHandle<ID3D12Resource>(sh); } finally { CloseHandle(sh); }
    }

    public void Dispose() { T12.Dispose(); Uav?.Dispose(); Srv.Dispose(); T11.Dispose(); }
}

/// <summary>D3D12 device/queue on the same adapter, fence-synchronised with the D3D11 context (GPU-side waits only).</summary>
public sealed class VendorBridge : IDisposable
{
    readonly Gpu _g;
    public ID3D12Device Device { get; }
    public ID3D12CommandQueue Queue { get; }
    public ID3D12GraphicsCommandList List { get; }
    // 1.3.4: 6 allocators (was 3). With FSR 3 upscaling + FSR 3 frame generation there are two submissions per
    // source frame, so a 3-deep ring made Begin() block the CPU (holding Gpu.Lock) on work from ~1.5 frames ago
    // whenever the game kept the GPU busy.
    const int Ring = 6;
    readonly ID3D12CommandAllocator[] _alloc = new ID3D12CommandAllocator[Ring];
    readonly ulong[] _allocFence = new ulong[Ring];
    readonly bool[] _tsValid = new bool[Ring];
    int _ai;
    readonly ID3D11Fence _f11;
    readonly ID3D12Fence _f12;
    readonly ID3D11DeviceContext4 _ctx4;
    readonly AutoResetEvent _evt = new(false);
    ulong _v;
    readonly ID3D12QueryHeap _qh;
    readonly ID3D12Resource _qrb;
    readonly double _tsFreq;
    /// <summary>GPU time of the last completed vendor submission (D3D12 timestamps), ms.</summary>
    public double LastMs { get; private set; }

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public VendorBridge(Gpu g)
    {
        _g = g;
        Device = D3D12.D3D12CreateDevice<ID3D12Device>(g.Adapter, FeatureLevel.Level_11_0)
                 ?? throw new NotSupportedException("no Direct3D 12 device on this adapter");
        // 1.3.4: FSR 3.1 upscaler/FG is compute-only, so a COMPUTE queue at NORMAL priority looks attractive
        // (keeps that work off the game's DIRECT queue). Not switched: this bridge is shared with XeSS and
        // FSR 2/3/4, FidelityFX's DX12 backend records into our list without declaring a queue type, and
        // shared D3D11↔D3D12 resources plus timestamp ResolveQueryData were only validated on DIRECT.
        // A wrong queue type would be silent corruption, not a probe-able fallback.
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct, CommandQueuePriority.Normal));
        for (int i = 0; i < Ring; i++) _alloc[i] = Device.CreateCommandAllocator(CommandListType.Direct);
        List = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _alloc[0], null);
        List.Close();
        using (var d5 = g.Device.QueryInterface<ID3D11Device5>())
        {
            _f11 = d5.CreateFence(0, Vortice.Direct3D11.FenceFlags.Shared);
            IntPtr fh = _f11.CreateSharedHandle(null, null);
            try { _f12 = Device.OpenSharedHandle<ID3D12Fence>(fh); } finally { CloseHandle(fh); }
        }
        _ctx4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        _qh = Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 2 * Ring));
        _qrb = Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer(2 * Ring * 8), ResourceStates.CopyDest);
        Queue.GetTimestampFrequency(out ulong freq); _tsFreq = freq;
    }

    public SharedTex Shared(int w, int h, Format f, bool uav) => new(_g.Device, Device, w, h, f, uav);

    void WaitCpu(ulong v)
    {
        if (v == 0 || _f12.CompletedValue >= v) return;
        _f12.SetEventOnCompletion(v, _evt);
        if (!_evt.WaitOne(5000)) throw new TimeoutException("vendor GPU work timed out");
    }

    public void Flush() { try { WaitCpu(_v); } catch { } }

    /// <summary>Start a D3D12 command list that runs after everything already queued on the D3D11 context.</summary>
    public unsafe ID3D12GraphicsCommandList Begin()
    {
        ulong vIn = ++_v;
        _ctx4.Signal(_f11, vIn);
        _g.Context.Flush();
        _ai = (_ai + 1) % Ring;
        WaitCpu(_allocFence[_ai]);
        if (_tsValid[_ai])
        {
            void* p; var range = new Vortice.Direct3D12.Range((nuint)(_ai * 16), (nuint)(_ai * 16 + 16));
            _qrb.Map(0, range, &p);
            ulong* t = (ulong*)((byte*)p + _ai * 16);
            if (t[1] > t[0]) LastMs = (t[1] - t[0]) * 1000.0 / _tsFreq;
            _qrb.Unmap(0, null);
        }
        _alloc[_ai].Reset();
        List.Reset(_alloc[_ai], null);
        Queue.Wait(_f12, vIn);
        List.EndQuery(_qh, Vortice.Direct3D12.QueryType.Timestamp, (uint)(_ai * 2));
        return List;
    }

    /// <summary>Submit; the D3D11 context waits (on the GPU) for completion before its next commands.</summary>
    public void End()
    {
        List.EndQuery(_qh, Vortice.Direct3D12.QueryType.Timestamp, (uint)(_ai * 2 + 1));
        List.ResolveQueryData(_qh, Vortice.Direct3D12.QueryType.Timestamp, (uint)(_ai * 2), 2, _qrb, (ulong)(_ai * 16));
        List.Close();
        Queue.ExecuteCommandList(List);
        ulong vDone = ++_v;
        Queue.Signal(_f12, vDone);
        _allocFence[_ai] = vDone; _tsValid[_ai] = true;
        _ctx4.Wait(_f11, vDone);
    }

    public void Barrier(ID3D12Resource r, ResourceStates a, ResourceStates b) { if (a != b) List.ResourceBarrierTransition(r, a, b); }

    public void Dispose()
    {
        Flush();
        foreach (var d in new IDisposable?[] { _qrb, _qh, List }.Concat(_alloc).Concat(new IDisposable?[] { Queue, _f12, _f11, _ctx4, Device, _evt })) d?.Dispose();
    }
}

// ───────────────────────────── FidelityFX API (ffx_api.h) ─────────────────────────────

[StructLayout(LayoutKind.Sequential)] public unsafe struct FfxHeader { public ulong Type; public void* Next; }
[StructLayout(LayoutKind.Sequential)]
public struct FfxRes
{
    public IntPtr Resource;
    public uint Type, Format, Width, Height, Depth, Mips, Flags, Usage;
    public uint State;
}
[StructLayout(LayoutKind.Sequential)] struct FfxBackendDX12 { public FfxHeader H; public IntPtr Device; }
[StructLayout(LayoutKind.Sequential)] struct FfxCreateUpscale { public FfxHeader H; public uint Flags, MaxRW, MaxRH, MaxUW, MaxUH; public IntPtr FpMessage; }
[StructLayout(LayoutKind.Sequential)] struct FfxOverrideVersion { public FfxHeader H; public ulong VersionId; }
[StructLayout(LayoutKind.Sequential)] unsafe struct FfxQueryVersions { public FfxHeader H; public ulong CreateDescType; public IntPtr Device; public ulong* OutputCount; public ulong* VersionIds; public IntPtr* VersionNames; }
[StructLayout(LayoutKind.Sequential)]
struct FfxDispatchUpscale
{
    public FfxHeader H; public IntPtr CommandList;
    public FfxRes Color, Depth, Motion, Exposure, Reactive, Tac, Output;
    public float JitterX, JitterY, MvScaleX, MvScaleY;
    public uint RenderW, RenderH, UpW, UpH;
    public byte EnableSharpening; public float Sharpness, FrameTimeDelta, PreExposure;
    public byte Reset; public float CameraNear, CameraFar, Fov, Meters;
    public uint Flags;
}
[StructLayout(LayoutKind.Sequential)] struct FfxCreateFg { public FfxHeader H; public uint Flags, DisplayW, DisplayH, MaxRW, MaxRH, BackBufferFormat; }
[StructLayout(LayoutKind.Sequential)]
struct FfxConfigureFg
{
    public FfxHeader H; public IntPtr SwapChain, PresentCb, PresentCtx, FgCb, FgCtx;
    public byte Enabled, AllowAsync; public FfxRes HudLess; public uint Flags; public byte OnlyGenerated;
    public int RectL, RectT, RectW, RectH; public ulong FrameId;
}
[StructLayout(LayoutKind.Sequential)]
struct FfxPrepareFg
{
    public FfxHeader H; public ulong FrameId; public uint Flags; public IntPtr CommandList;
    public uint RenderW, RenderH; public float JitterX, JitterY, MvScaleX, MvScaleY;
    public float FrameTimeDelta; public byte UnusedReset; public float CameraNear, CameraFar, Fov, Meters;
    public FfxRes Depth, Motion;
}
[StructLayout(LayoutKind.Sequential)]
struct FfxDispatchFg
{
    public FfxHeader H; public IntPtr CommandList; public FfxRes PresentColor, Out0, Out1, Out2, Out3;
    public uint NumGenerated; public byte Reset; public uint TransferFunction; public float MinLum, MaxLum;
    public int RectL, RectT, RectW, RectH; public ulong FrameId;
}

public static class Ffx
{
    public const ulong BackendDX12 = 2, OverrideVersion = 5, QueryGetVersions = 4;
    public const ulong CreateUpscale = 0x10000, DispatchUpscale = 0x10001;
    public const ulong CreateFg = 0x20001, ConfigureFg = 0x20002, DispatchFg = 0x20003, PrepareFg = 0x20004;
    public const uint FmtRGBA8 = 10, FmtRGBA16F = 4, FmtRG16F = 18, FmtR32F = 28, Tex2D = 2;
    public const uint StateComputeRead = 1 << 2, StateUav = 1 << 1;

    public static FfxRes Res(SharedTex t, uint fmt, bool uav) => Res(t.T12, t.Width, t.Height, fmt, uav);
    public static FfxRes Res(ID3D12Resource r, int w, int h, uint fmt, bool uav) => new()
    {
        Resource = r.NativePointer, Type = Tex2D, Format = fmt, Width = (uint)w, Height = (uint)h, Depth = 1, Mips = 1, Flags = 0,
        Usage = uav ? 2u : 0u, State = uav ? StateUav : StateComputeRead,
    };
}

/// <summary>One loaded FidelityFX API runtime DLL (ffxCreateContext/…/ffxDispatch).</summary>
public sealed unsafe class FfxLib
{
    public string File { get; }
    public delegate* unmanaged<IntPtr*, void*, void*, uint> CreateContext;
    public delegate* unmanaged<IntPtr*, void*, uint> DestroyContext;
    public delegate* unmanaged<IntPtr*, void*, uint> Configure;
    public delegate* unmanaged<IntPtr*, void*, uint> Query;
    public delegate* unmanaged<IntPtr*, void*, uint> Dispatch;

    FfxLib(string file, IntPtr m)
    {
        File = file;
        CreateContext = (delegate* unmanaged<IntPtr*, void*, void*, uint>)NativeLibrary.GetExport(m, "ffxCreateContext");
        DestroyContext = (delegate* unmanaged<IntPtr*, void*, uint>)NativeLibrary.GetExport(m, "ffxDestroyContext");
        Configure = (delegate* unmanaged<IntPtr*, void*, uint>)NativeLibrary.GetExport(m, "ffxConfigure");
        Query = (delegate* unmanaged<IntPtr*, void*, uint>)NativeLibrary.GetExport(m, "ffxQuery");
        Dispatch = (delegate* unmanaged<IntPtr*, void*, uint>)NativeLibrary.GetExport(m, "ffxDispatch");
    }

    static readonly Dictionary<string, FfxLib?> Cache = new();
    public static FfxLib? Load(string file, out string why)
    {
        why = "";
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var c)) { if (c == null) why = file + " could not be loaded"; return c; }
            var path = Path.Combine(AppContext.BaseDirectory, file);
            FfxLib? lib = null;
            try
            {
                if (!System.IO.File.Exists(path)) why = file + " is missing next to the program";
                else lib = new FfxLib(file, NativeLibrary.Load(path));
            }
            catch (Exception ex) { why = $"{file}: {ex.Message}"; }
            Cache[file] = lib;
            return lib;
        }
    }

    /// <summary>Upscaler provider versions this runtime offers for <paramref name="device"/> (id, name).</summary>
    public List<(ulong id, string name)> UpscalerVersions(ID3D12Device device)
    {
        const int Cap = 16;
        var ids = stackalloc ulong[Cap];
        var names = stackalloc IntPtr[Cap];
        ulong count = Cap;
        var q = new FfxQueryVersions { H = new FfxHeader { Type = Ffx.QueryGetVersions }, CreateDescType = Ffx.CreateUpscale, Device = device.NativePointer, OutputCount = &count, VersionIds = ids, VersionNames = names };
        uint rc = Query(null, &q);
        var list = new List<(ulong, string)>();
        if (rc != 0) return list;
        for (int i = 0; i < (int)Math.Min(count, (ulong)Cap); i++) list.Add((ids[i], Marshal.PtrToStringAnsi(names[i]) ?? "?"));
        return list;
    }

    public static int Major(string versionName)
    {
        foreach (var ch in versionName) if (char.IsDigit(ch)) return ch - '0';
        return -1;
    }
}

public interface IVendorUpscaler : IDisposable
{
    string Name { get; }
    /// <summary>Record the upscale on the bridge's open command list. Inputs are in COMPUTE_READ, output in UAV state.</summary>
    void Record(VendorBridge br, SharedTex color, SharedTex mv, ID3D12Resource depth, SharedTex output,
                int inW, int inH, float jx, float jy, float sharpness, float dtMs, bool reset);
}

/// <summary>FSR 2 / FSR 3 / FSR 4 upscaling via the FidelityFX API, with an explicit provider version.</summary>
public sealed unsafe class FfxUpscaler : IVendorUpscaler
{
    readonly FfxLib _lib;
    IntPtr _ctx;
    public string Name { get; }
    public static float JitterSign = float.TryParse(Environment.GetEnvironmentVariable("UFX_FSR_JS"), out var s) ? s : -1f;
    public static float MvSign = float.TryParse(Environment.GetEnvironmentVariable("UFX_FSR_MVS"), out var m) ? m : 1f;

    public FfxUpscaler(FfxLib lib, ID3D12Device device, ulong versionId, string label, int maxInW, int maxInH, int outW, int outH)
    {
        _lib = lib; Name = label;
        var backend = new FfxBackendDX12 { H = new FfxHeader { Type = Ffx.BackendDX12 }, Device = device.NativePointer };
        var ov = new FfxOverrideVersion { H = new FfxHeader { Type = Ffx.OverrideVersion, Next = &backend }, VersionId = versionId };
        // Non-linear (sRGB) LDR colour; no exposure texture (FSR uses 1.0).
        var cd = new FfxCreateUpscale { H = new FfxHeader { Type = Ffx.CreateUpscale, Next = &ov }, Flags = 1u << 8, MaxRW = (uint)maxInW, MaxRH = (uint)maxInH, MaxUW = (uint)outW, MaxUH = (uint)outH };
        IntPtr ctx = IntPtr.Zero;
        uint rc = lib.CreateContext(&ctx, &cd, null);
        if (rc != 0 || ctx == IntPtr.Zero) throw new InvalidOperationException($"{label}: ffxCreateContext failed (code {rc})");
        _ctx = ctx;
    }

    public void Record(VendorBridge br, SharedTex color, SharedTex mv, ID3D12Resource depth, SharedTex output,
                       int inW, int inH, float jx, float jy, float sharpness, float dtMs, bool reset)
    {
        var d = new FfxDispatchUpscale
        {
            H = new FfxHeader { Type = Ffx.DispatchUpscale }, CommandList = br.List.NativePointer,
            Color = Ffx.Res(color, Ffx.FmtRGBA8, false), Depth = Ffx.Res(depth, inW, inH, Ffx.FmtR32F, false),
            Motion = Ffx.Res(mv, Ffx.FmtRG16F, false), Output = Ffx.Res(output, Ffx.FmtRGBA16F, true),
            JitterX = JitterSign * jx, JitterY = JitterSign * jy, MvScaleX = MvSign, MvScaleY = MvSign,
            RenderW = (uint)inW, RenderH = (uint)inH, UpW = (uint)output.Width, UpH = (uint)output.Height,
            // Our sharpness is in stops (0 = sharpest); the FSR API takes 0..1 (1 = sharpest).
            EnableSharpening = 1, Sharpness = Math.Clamp(1f - sharpness / 2f, 0f, 1f),
            FrameTimeDelta = Math.Clamp(dtMs, 1f, 100f), PreExposure = 1f, Reset = (byte)(reset ? 1 : 0),
            CameraNear = 0.1f, CameraFar = 1000f, Fov = 1.0f, Meters = 1f, Flags = 1u << 1,   // FFX_UPSCALE_FLAG_NON_LINEAR_COLOR_SRGB
        };
        IntPtr ctx = _ctx;
        uint rc = _lib.Dispatch(&ctx, &d);
        if (rc != 0) throw new InvalidOperationException($"{Name}: ffxDispatch failed (code {rc})");
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        IntPtr ctx = _ctx; _ctx = IntPtr.Zero;
        _lib.DestroyContext(&ctx, null);
    }
}

// ───────────────────────────── Intel XeSS (xess.h / xess_d3d12.h) ─────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct XessInit
{
    public uint OutW, OutH; public int Quality; public uint Flags, CreationNodeMask, VisibleNodeMask;
    public IntPtr TempBufferHeap; public ulong BufferHeapOffset; public IntPtr TempTextureHeap; public ulong TextureHeapOffset; public IntPtr PipelineLibrary;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct XessExec
{
    public IntPtr Color, Velocity, Depth, ExposureScale, ResponsiveMask, Output;
    public float JitterX, JitterY, ExposureScaleValue; public uint ResetHistory, InputW, InputH;
    public uint ColorBaseX, ColorBaseY, MvBaseX, MvBaseY, DepthBaseX, DepthBaseY, RespBaseX, RespBaseY, Res0X, Res0Y, OutBaseX, OutBaseY;
    public IntPtr DescriptorHeap; public uint DescriptorHeapOffset;
}

public sealed unsafe class XessLib
{
    public delegate* unmanaged<ushort*, int> GetVersion;
    public delegate* unmanaged<IntPtr, IntPtr*, int> D3D12CreateContext;
    public delegate* unmanaged<IntPtr, XessInit*, int> D3D12Init;
    public delegate* unmanaged<IntPtr, IntPtr, XessExec*, int> D3D12Execute;
    public delegate* unmanaged<IntPtr, int> DestroyContext;
    public delegate* unmanaged<IntPtr, uint*, int, uint*, uint*, uint*, int> GetOptimalInputResolution;
    public delegate* unmanaged<IntPtr, float, float, int> SetVelocityScale;
    public delegate* unmanaged<IntPtr, float, float, int> SetJitterScale;
    public string VersionName { get; } = "?";

    XessLib(IntPtr m)
    {
        GetVersion = (delegate* unmanaged<ushort*, int>)NativeLibrary.GetExport(m, "xessGetVersion");
        D3D12CreateContext = (delegate* unmanaged<IntPtr, IntPtr*, int>)NativeLibrary.GetExport(m, "xessD3D12CreateContext");
        D3D12Init = (delegate* unmanaged<IntPtr, XessInit*, int>)NativeLibrary.GetExport(m, "xessD3D12Init");
        D3D12Execute = (delegate* unmanaged<IntPtr, IntPtr, XessExec*, int>)NativeLibrary.GetExport(m, "xessD3D12Execute");
        DestroyContext = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(m, "xessDestroyContext");
        GetOptimalInputResolution = (delegate* unmanaged<IntPtr, uint*, int, uint*, uint*, uint*, int>)NativeLibrary.GetExport(m, "xessGetOptimalInputResolution");
        SetVelocityScale = (delegate* unmanaged<IntPtr, float, float, int>)NativeLibrary.GetExport(m, "xessSetVelocityScale");
        SetJitterScale = (delegate* unmanaged<IntPtr, float, float, int>)NativeLibrary.GetExport(m, "xessSetJitterScale");
        var v = stackalloc ushort[4];
        if (GetVersion(v) == 0) VersionName = $"{v[0]}.{v[1]}.{v[2]}";
    }

    static XessLib? _lib; static string _why = ""; static bool _tried;
    public static XessLib? Load(out string why)
    {
        lock (typeof(XessLib))
        {
            if (!_tried)
            {
                _tried = true;
                var path = Path.Combine(AppContext.BaseDirectory, "libxess.dll");
                try
                {
                    if (!File.Exists(path)) _why = "libxess.dll is missing next to the program";
                    else _lib = new XessLib(NativeLibrary.Load(path));
                }
                catch (Exception ex) { _why = "libxess.dll: " + ex.Message; }
            }
            why = _why; return _lib;
        }
    }
}

public sealed unsafe class XessUpscaler : IVendorUpscaler
{
    readonly XessLib _x;
    IntPtr _ctx;
    public string Name { get; }
    public static float JitterSign = float.TryParse(Environment.GetEnvironmentVariable("UFX_XESS_JS"), out var s) ? s : -1f;
    public static float MvSign = float.TryParse(Environment.GetEnvironmentVariable("UFX_XESS_MVS"), out var m) ? m : 1f;
    public string QualityName { get; } = "";

    static readonly (int q, string n)[] Qualities =
    {
        (105, "Ultra Quality Plus"), (104, "Ultra Quality"), (103, "Quality"), (102, "Balanced"), (101, "Performance"), (100, "Ultra Performance"), (106, "Native AA"),
    };

    public XessUpscaler(XessLib x, ID3D12Device device, int inW, int inH, int outW, int outH)
    {
        _x = x;
        IntPtr ctx;
        int rc = x.D3D12CreateContext(device.NativePointer, &ctx);
        if (rc < 0) throw new NotSupportedException($"XeSS is not supported on this GPU/driver (xessD3D12CreateContext {rc})");
        _ctx = ctx;
        // Pick the quality preset whose input-resolution range contains our (fixed) capture size.
        int chosen = -1;
        var o = stackalloc uint[2]; o[0] = (uint)outW; o[1] = (uint)outH;
        var opt = stackalloc uint[2]; var mn = stackalloc uint[2]; var mx = stackalloc uint[2];
        foreach (var (q, n) in Qualities)
        {
            if (x.GetOptimalInputResolution(ctx, o, q, opt, mn, mx) < 0) continue;
            if (inW >= mn[0] && inW <= mx[0] && inH >= mn[1] && inH <= mx[1]) { chosen = q; QualityName = n; break; }
        }
        if (chosen < 0) { Dispose(); throw new NotSupportedException($"XeSS cannot upscale {inW}x{inH} to {outW}x{outH} (ratio outside its 1x-3x range)"); }
        var init = new XessInit { OutW = (uint)outW, OutH = (uint)outH, Quality = chosen, Flags = 1u << 6 };   // LDR input colour
        rc = x.D3D12Init(ctx, &init);
        if (rc < 0) { Dispose(); throw new InvalidOperationException($"xessD3D12Init failed ({rc})"); }
        x.SetVelocityScale(ctx, MvSign, MvSign);
        Name = $"Intel XeSS-SR {x.VersionName} ({QualityName})";
    }

    public void Record(VendorBridge br, SharedTex color, SharedTex mv, ID3D12Resource depth, SharedTex output,
                       int inW, int inH, float jx, float jy, float sharpness, float dtMs, bool reset)
    {
        var e = new XessExec
        {
            Color = color.T12.NativePointer, Velocity = mv.T12.NativePointer, Depth = depth.NativePointer, Output = output.T12.NativePointer,
            JitterX = JitterSign * jx, JitterY = JitterSign * jy, ExposureScaleValue = 1f, ResetHistory = reset ? 1u : 0u,
            InputW = (uint)inW, InputH = (uint)inH,
        };
        int rc = _x.D3D12Execute(_ctx, br.List.NativePointer, &e);
        if (rc < 0) throw new InvalidOperationException($"xessD3D12Execute failed ({rc})");
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        _x.DestroyContext(_ctx); _ctx = IntPtr.Zero;
    }
}

// ───────────────────────────── FSR 3 frame generation (direct dispatch, no proxy swap chain) ─────────────────────────────

public sealed unsafe class FfxFrameGen : IDisposable
{
    readonly FfxLib _lib;
    IntPtr _ctx;
    ulong _frame;
    public int DisplayW { get; }
    public int DisplayH { get; }
    public int RenderW { get; }
    public int RenderH { get; }

    public FfxFrameGen(FfxLib lib, ID3D12Device device, int renderW, int renderH, int displayW, int displayH)
    {
        _lib = lib; DisplayW = displayW; DisplayH = displayH; RenderW = renderW; RenderH = renderH;
        var backend = new FfxBackendDX12 { H = new FfxHeader { Type = Ffx.BackendDX12 }, Device = device.NativePointer };
        var cd = new FfxCreateFg { H = new FfxHeader { Type = Ffx.CreateFg, Next = &backend }, Flags = 0, DisplayW = (uint)displayW, DisplayH = (uint)displayH, MaxRW = (uint)renderW, MaxRH = (uint)renderH, BackBufferFormat = Ffx.FmtRGBA8 };
        IntPtr ctx = IntPtr.Zero;
        uint rc = lib.CreateContext(&ctx, &cd, null);
        if (rc != 0 || ctx == IntPtr.Zero) throw new InvalidOperationException($"FSR 3 frame generation: ffxCreateContext failed (code {rc})");
        _ctx = ctx;
    }

    /// <summary>Configure + prepare (our motion vectors, flat depth) + generate the frame between the previous and this presentColor.</summary>
    public bool Record(VendorBridge br, SharedTex present, SharedTex mv, ID3D12Resource depth, SharedTex output, float jx, float jy, float dtMs, bool reset)
    {
        ulong id = ++_frame;
        IntPtr ctx = _ctx;
        var cfg = new FfxConfigureFg { H = new FfxHeader { Type = Ffx.ConfigureFg }, Enabled = 1, Flags = 1u << 3, FrameId = id };   // NO_SWAPCHAIN_CONTEXT_NOTIFY
        uint rc = _lib.Configure(&ctx, &cfg);
        if (rc != 0) throw new InvalidOperationException($"FSR 3 FG configure failed (code {rc})");
        var prep = new FfxPrepareFg
        {
            H = new FfxHeader { Type = Ffx.PrepareFg }, FrameId = id, CommandList = br.List.NativePointer,
            RenderW = (uint)RenderW, RenderH = (uint)RenderH, JitterX = FfxUpscaler.JitterSign * jx, JitterY = FfxUpscaler.JitterSign * jy,
            MvScaleX = FfxUpscaler.MvSign, MvScaleY = FfxUpscaler.MvSign, FrameTimeDelta = Math.Clamp(dtMs, 1f, 100f),
            CameraNear = 0.1f, CameraFar = 1000f, Fov = 1.0f, Meters = 1f,
            Depth = Ffx.Res(depth, RenderW, RenderH, Ffx.FmtR32F, false), Motion = Ffx.Res(mv, Ffx.FmtRG16F, false),
        };
        rc = _lib.Dispatch(&ctx, &prep);
        if (rc != 0) throw new InvalidOperationException($"FSR 3 FG prepare failed (code {rc})");
        var d = new FfxDispatchFg
        {
            H = new FfxHeader { Type = Ffx.DispatchFg }, CommandList = br.List.NativePointer,
            PresentColor = Ffx.Res(present, Ffx.FmtRGBA8, false), Out0 = Ffx.Res(output, Ffx.FmtRGBA8, true),
            NumGenerated = 1, Reset = (byte)(reset || id == 1 ? 1 : 0), TransferFunction = 0, MinLum = 0, MaxLum = 1,
            RectW = DisplayW, RectH = DisplayH, FrameId = id,
        };
        rc = _lib.Dispatch(&ctx, &d);
        if (rc != 0) throw new InvalidOperationException($"FSR 3 FG dispatch failed (code {rc})");
        return id > 1;
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        IntPtr ctx = _ctx; _ctx = IntPtr.Zero;
        _lib.DestroyContext(&ctx, null);
    }
}
