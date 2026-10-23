// AMD FidelityFX Super Resolution 1 (EASU + RCAS), official ffx_a.h / ffx_fsr1.h (MIT), fp32 path.
// Structure follows AMD's FSR_Pass.hlsl sample. Used only for the "AMD FSR 1" option.
cbuffer FsrConst : register(b0) { uint4 Const0; uint4 Const1; uint4 Const2; uint4 Const3; };
#define A_GPU 1
#define A_HLSL 1
SamplerState samLinearClamp : register(s0);
#include "ffx_a.h"
Texture2D InputTexture : register(t0);
RWTexture2D<float4> OutputTexture : register(u0);
#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p) { return InputTexture.GatherRed(samLinearClamp, p, int2(0, 0)); }
AF4 FsrEasuGF(AF2 p) { return InputTexture.GatherGreen(samLinearClamp, p, int2(0, 0)); }
AF4 FsrEasuBF(AF2 p) { return InputTexture.GatherBlue(samLinearClamp, p, int2(0, 0)); }
#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p) { return InputTexture.Load(int3(ASU2(p), 0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}
#include "ffx_fsr1.h"

void EasuPixel(int2 pos)
{
    uint w, h; OutputTexture.GetDimensions(w, h);
    if (pos.x >= (int)w || pos.y >= (int)h) return;
    AF3 c; FsrEasuF(c, pos, Const0, Const1, Const2, Const3);
    OutputTexture[pos] = float4(c, 1);
}
void RcasPixel(int2 pos)
{
    uint w, h; OutputTexture.GetDimensions(w, h);
    if (pos.x >= (int)w || pos.y >= (int)h) return;
    AF3 c; FsrRcasF(c.r, c.g, c.b, pos, Const0);
    OutputTexture[pos] = float4(c, 1);
}

[numthreads(64, 1, 1)]
void EASU(uint3 LocalThreadId : SV_GroupThreadID, uint3 WorkGroupId : SV_GroupID)
{
    AU2 gxy = ARmp8x8(LocalThreadId.x) + AU2(WorkGroupId.x << 4u, WorkGroupId.y << 4u);
    EasuPixel(gxy); gxy.x += 8u; EasuPixel(gxy); gxy.y += 8u; EasuPixel(gxy); gxy.x -= 8u; EasuPixel(gxy);
}

[numthreads(64, 1, 1)]
void RCAS(uint3 LocalThreadId : SV_GroupThreadID, uint3 WorkGroupId : SV_GroupID)
{
    AU2 gxy = ARmp8x8(LocalThreadId.x) + AU2(WorkGroupId.x << 4u, WorkGroupId.y << 4u);
    RcasPixel(gxy); gxy.x += 8u; RcasPixel(gxy); gxy.y += 8u; RcasPixel(gxy); gxy.x -= 8u; RcasPixel(gxy);
}
