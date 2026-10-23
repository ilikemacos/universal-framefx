using Vortice.Direct3D11;

namespace UniversalFrameFX;

public readonly record struct FlowHint(ID3D11ShaderResourceView Srv, float Scale, float Grid);

public interface IFlowEstimator : IDisposable
{
    string Name { get; }
    bool HardwareAccelerated { get; }
    Tex EstimateBackward(Tex[] prevLuma, Tex[] currLuma, Tex temporalSeed, FlowHint? seedHint);
    Tex EstimateForward(Tex[] currLuma, Tex[] prevLuma, Tex temporalSeedFwd, FlowHint? seedHint);
    void ResetHistory();
}
