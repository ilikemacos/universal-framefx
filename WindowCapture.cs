using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace UniversalFrameFX;

public sealed class WindowCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true, PreserveSig = false)]
    static extern IntPtr CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice);

    static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    readonly GraphicsCaptureItem _item;
    readonly IDirect3DDevice _rtDevice;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly Action<Direct3D11CaptureFrame> _onFrame;
    SizeInt32 _size;
    volatile bool _disposed;
    public event Action? Closed;
    public int Width => _size.Width;
    public int Height => _size.Height;

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); } catch (TypeLoadException) { return false; }
    }

    public WindowCapture(ID3D11Device device, IntPtr hwnd, Action<Direct3D11CaptureFrame> onFrame)
    {
        _onFrame = onFrame;
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = CaptureItemIid;
        var ptr = factory.CreateForWindow(hwnd, ref iid);
        if (ptr == IntPtr.Zero) throw new InvalidOperationException("That window cannot be captured.");
        _item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);

        using (var dxgi = device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        {
            var abi = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer);
            _rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi);
            Marshal.Release(abi);
        }
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try { _session.IsBorderRequired = false; } catch { }
        }
        _pool.FrameArrived += OnFrame;
        _item.Closed += (_, _) => Closed?.Invoke();
        _session.StartCapture();
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object? _)
    {
        if (_disposed) return;
        try
        {
            var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            var cs = frame.ContentSize;
            if (cs.Width != _size.Width || cs.Height != _size.Height)
            {
                frame.Dispose();
                _size = cs;
                pool.Recreate(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                return;
            }
            _onFrame(frame);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"capture frame error: {ex.Message}");
        }
    }

    public static ID3D11Texture2D? TextureOf(Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var tiid = typeof(ID3D11Texture2D).GUID;
        var tp = access.GetInterface(ref tiid);
        return tp == IntPtr.Zero ? null : new ID3D11Texture2D(tp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrame;
        _session.Dispose();
        _pool.Dispose();
    }
}
