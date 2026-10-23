// Phase 1 - downsample the captured frame to the 128 x 128 proxy grid (average colour + chroma-aware brightness), packed one uint per
// cell (RGB8 + luma8, see ssgi_common.hlsli) into a 64 KB RWStructuredBuffer. The C++ side then CopyResource()s that
// buffer into a ring of D3D11_USAGE_STAGING buffers and maps the oldest with D3D11_MAP_FLAG_DO_NOT_WAIT (never stalls),
// so the CPU only ever sees this 64 KB proxy, never a full-resolution texture. The same buffer feeds the Phase 3 march.
//
// Cost is fixed regardless of the capture size: 16 bilinear taps per cell. Each tap sits exactly on a texel corner, so
// the bilinear filter returns the mean of a 2 x 2 texel block: 64 texels averaged per cell (at 1080p a cell spans about
// 15 x 8 pixels, so roughly half of them; enough for a low-frequency lighting proxy and much cheaper than all of them).
// Dispatch: (128 / 8) x (128 / 8) = 16 x 16 groups of 8 x 8 threads, one thread per cell.

#include "ssgi_common.hlsli"

Texture2D<float4>        gGame    : register(t0);   // captured window / game image (any size)
SamplerState             gLinear  : register(s0);   // linear, clamp
RWStructuredBuffer<uint> gGridOut : register(u0);   // 128 * 128 packed cells

[numthreads(8, 8, 1)]
void CSDownsample(uint3 id : SV_DispatchThreadID)
{
    // 16 x 16 groups x 8 x 8 threads = exactly 128 x 128 threads: no bounds check needed.
    float2 cellPx = (float2)srcSize * (1.0f / SSGI_GRID);   // cell size in pixels (fractional is fine)
    float2 org = (float2)id.xy * cellPx;
    float3 sum = 0.0f;
    [unroll] for (uint i = 0; i < 16; ++i) {
        float2 sub = (float2(i & 3, i >> 2) + 0.5f) * 0.25f;          // 4 x 4 sub-positions inside the cell
        float2 corner = max(round(org + sub * cellPx), 1.0f);          // snap to a texel corner -> 2 x 2 texel mean
        corner = min(corner, (float2)srcSize - 1.0f);
        sum += gGame.SampleLevel(gLinear, corner * invSrcSize, 0).rgb;
    }
    float3 avg = sum * (1.0f / 16.0f);
    gGridOut[id.y * SSGI_GRID + id.x] = ssgiPack(avg, ssgiBright(avg));
}
