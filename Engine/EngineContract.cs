using Vortice.Direct3D11;

namespace UniversalFrameFX;

public sealed class EngineNotIncludedException : Exception
{
    public EngineNotIncludedException()
        : base("The FrameFX image-processing engine is not included in the public source. Download the release build from https://chopstickshq.com/universal-framefx/ to use FrameFX.") { }
}

public interface IFrameEngine : IDisposable
{
    void AttachOutput(IntPtr outputWindow, int width, int height, bool overlay);
    void ResizeOutput(int width, int height);
    void SubmitFrame(ID3D11Texture2D captured, int contentWidth, int contentHeight);
    void Reset();
    long Processed { get; }
    double SourceFps { get; }
    double OutputFps { get; }
    double GpuMs { get; }
    string Status { get; }
    string SwapInfo { get; }
}

public static class FrameEngine
{
    public static IFrameEngine Create(Gpu gpu, FramePipeline pipe, SessionSettings settings) => throw new EngineNotIncludedException();
}
