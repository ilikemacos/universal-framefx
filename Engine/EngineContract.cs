using Vortice.Direct3D11;

namespace UniversalFrameFX;

// ---------------------------------------------------------------------------------------------------------------
// The image-processing engine (upscaling, frame generation, image effects) is proprietary and NOT part of this
// repository. This file is the boundary between the open app shell and that engine.
//
// What crosses the boundary:
//   app -> engine : the Direct3D 11 texture of the window picture captured by WindowCapture.cs, the user's settings
//                   (SessionSettings below) and the output window handle to draw into.
//   engine -> app : the processed picture (drawn into that output window) and status text / fps numbers for the HUD.
//
// The engine has no network, registry or process access. The only file it writes is the GPU program cache
// handled by ShaderCache in Diag.cs (public). It reads its own runtime files from the program folder.
//
// Building this repository gives the full app shell with the engine replaced by NotIncludedEngine: the window,
// settings, game detection, updater and installer logic all run, and starting an output reports that the engine
// is not included.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Thrown by the placeholder engine in builds made from the public source.</summary>
public sealed class EngineNotIncludedException : Exception
{
    public EngineNotIncludedException()
        : base("The FrameFX image-processing engine is not included in the public source. Download the release build from https://chopstickshq.com/universal-framefx/ to use FrameFX.") { }
}

/// <summary>One running output session of the (closed) engine.</summary>
public interface IFrameEngine : IDisposable
{
    /// <summary>Create the drawing surface for the output window (client size in pixels).</summary>
    void AttachOutput(IntPtr outputWindow, int width, int height, bool overlay);
    /// <summary>The output window changed size.</summary>
    void ResizeOutput(int width, int height);
    /// <summary>A newly captured picture of the source window. Called on the capture thread; the texture is only
    /// valid for the duration of the call. When <see cref="SessionSettings.CompareOff"/> is set, the picture is
    /// shown as captured, with FrameFX processing off.</summary>
    void SubmitFrame(ID3D11Texture2D captured, int contentWidth, int contentHeight);
    /// <summary>Forget previous frames (after a pause, so nothing stale is shown).</summary>
    void Reset();
    long Processed { get; }
    double SourceFps { get; }
    double OutputFps { get; }
    double GpuMs { get; }
    string Status { get; }
    string SwapInfo { get; }
}

/// <summary>Factory for engine sessions. The public build only has the placeholder.</summary>
public static class FrameEngine
{
    public static IFrameEngine Create(Gpu gpu, FramePipeline pipe, SessionSettings settings) => throw new EngineNotIncludedException();
}
