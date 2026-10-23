namespace UniversalFrameFX;

public static class NvofFlowLogic
{
    public const float S105Scale = 32f;

    public static void RawToPixels(int rawX, int rawY, float scale, out float px, out float py)
    {
        float s = scale > 0 ? scale : S105Scale;
        px = rawX / s;
        py = rawY / s;
    }

    public static void TileCentreToGrid(int tileX, int tileY, float tileSize, float grid, int gridW, int gridH, out int gx, out int gy)
    {
        float g = grid < 1f ? 1f : grid;
        gx = (int)((tileX + 0.5f) * tileSize / g);
        gy = (int)((tileY + 0.5f) * tileSize / g);
        gx = Math.Clamp(gx, 0, Math.Max(gridW - 1, 0));
        gy = Math.Clamp(gy, 0, Math.Max(gridH - 1, 0));
    }

    public static void RawToTileVec(int rawX, int rawY, float scale, float tileSize, out float tx, out float ty)
    {
        RawToPixels(rawX, rawY, scale, out float px, out float py);
        float t = tileSize < 1f ? 1f : tileSize;
        tx = px / t;
        ty = py / t;
    }
}

public static class FlowEstimators
{
    public static IFlowEstimator Create(FgV2 host, Gpu? gpu, int w, int h, MotionPreference pref)
    {
        if ((pref is MotionPreference.Auto or MotionPreference.Nvof) && gpu != null && gpu.VendorId == 0x10DE && w > 0 && h > 0)
        {
            try { return new NvofFlowEstimator(host, gpu, w, h); }
            catch { }
        }
        return new PyramidFlowEstimator(host);
    }
}

public sealed class NvofFlowEstimator : IFlowEstimator
{
    readonly FgV2 _host;
    NvofEngine _nvof;
    bool _estimated;

    public NvofFlowEstimator(FgV2 host, Gpu gpu, int w, int h)
    {
        _host = host;
        _nvof = new NvofEngine(gpu, w, h);
    }

    public string Name => _nvof.Name;
    public bool HardwareAccelerated => true;

    public Tex EstimateBackward(Tex[] prevLuma, Tex[] currLuma, Tex temporalSeed, FlowHint? seedHint)
    {
        RunNvof(prevLuma[0], currLuma[0]);
        return _host.RefineNvof(prevLuma, currLuma, temporalSeed, _nvof.Bwd.Srv, _nvof.Scale, _nvof.Grid, _host.MotionTex);
    }

    public Tex EstimateForward(Tex[] currLuma, Tex[] prevLuma, Tex temporalSeedFwd, FlowHint? seedHint)
    {
        if (!_estimated) RunNvof(prevLuma[0], currLuma[0]);
        return _host.RefineNvof(currLuma, prevLuma, temporalSeedFwd, _nvof.Fwd.Srv, _nvof.Scale, _nvof.Grid, _host.FwdTex);
    }

    void RunNvof(Tex prevLuma0, Tex currLuma0)
    {
        _host.FillLuma8(prevLuma0, currLuma0);
        _nvof.Estimate(_host.Luma8Prev, _host.Luma8Curr);
        _estimated = true;
    }

    public void BeginFrame() => _estimated = false;

    public void ResetHistory()
    {
        _nvof.ResetHistory();
        _estimated = false;
    }

    public void Dispose()
    {
        _nvof.Dispose();
    }
}
