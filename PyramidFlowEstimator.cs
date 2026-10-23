namespace UniversalFrameFX;

public sealed class PyramidFlowEstimator : IFlowEstimator
{
    readonly FgV2 _host;
    public PyramidFlowEstimator(FgV2 host) => _host = host;
    public string Name => "pyramid";
    public bool HardwareAccelerated => false;
    public Tex EstimateBackward(Tex[] prevLuma, Tex[] currLuma, Tex temporalSeed, FlowHint? seedHint) =>
        _host.SearchPyramid(prevLuma, currLuma, temporalSeed, seedHint, _host.MotionTex);
    public Tex EstimateForward(Tex[] currLuma, Tex[] prevLuma, Tex temporalSeedFwd, FlowHint? seedHint) =>
        _host.SearchPyramid(currLuma, prevLuma, temporalSeedFwd, seedHint, _host.FwdTex);
    public void ResetHistory() => _host.Reset();
    public void Dispose() { }
}
