// Depth-aware SSGI march. Same importance-map group early-out as CSMarch. A sample contributes bounce
// only when its depth lies in front of the ray and inside the thickness band (flags bit 0). Occlusion is a
// horizon angle on that depth (flags bit 1) instead of the luminance pseudo-height. Bits that are off fall
// back to the r5 luma horizon, so a GI-only or AO-only dispatch still produces the other channel.
// Existing CSMarch is not this entry point — features-off keeps compiling and running CSMarch.
//
// cost per light pixel (worst case, gating not credited): rays * steps * (1 grid load + 1 depth load)
// and ~52 ALU per step, plus ~30 ALU outside the loop.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

StructuredBuffer<uint> gGrid       : register(t0);
StructuredBuffer<uint> gImportance : register(t1);
Texture2D<float4>      gDepth      : register(t2);
RWTexture2D<uint2>     gLightOut   : register(u0);

groupshared uint gsAny;

uint gridAtD(int2 c) { c = clamp(c, 0, SSGI_GRID - 1); return gGrid[c.y * SSGI_GRID + c.x]; }

float ssrtZ(float2 cell)
{
    int2 p = clamp(int2(cell * (1.0f / (float)SSGI_GRID) * (float2)lightSize), int2(0, 0), int2(lightSize) - 1);
    return gDepth.Load(int3(p, 0)).x;
}

[numthreads(8, 8, 1)]
void CSMarchDepth(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex, uint3 lt : SV_GroupThreadID)
{
    if (gi == 0) gsAny = forceAll;
    GroupMemoryBarrierWithGroupSync();
    uint2 t0 = gid.xy * 8, t1 = min(t0 + 7, lightSize - 1);
    uint2 c0 = (t0 * SSGI_GRID) / lightSize, c1 = (t1 * SSGI_GRID) / lightSize;
    uint2 c = min(c0 + lt.xy, c1);
    uint word = gImportance[c.y * 4 + (c.x >> 5)];
    if ((word >> (c.x & 31)) & 1u) InterlockedOr(gsAny, 1u);
    GroupMemoryBarrierWithGroupSync();

    bool inside = id.x < lightSize.x && id.y < lightSize.y;
    float2 uv = ((float2)id.xy + 0.5f) * invLightSize;
    float2 cc = uv * SSGI_GRID;
    uint centre = gridAtD(int2(cc));
    float lc = ssgiUnpackLuma(centre);
    if (gsAny == 0) {
        if (inside) gLightOut[id.xy] = ssgiPackLight(0.0f, lc, 0.0f);
        return;
    }
    if (!inside) return;

    bool useGi = (flags & 1u) != 0u;
    bool useAo = (flags & 2u) != 0u;
    float4 dtex = gDepth.Load(int3(id.xy, 0));
    float zc = dtex.x;
    float protect = ssrtProtect(dtex.w);
    float aspect = (float)srcSize.x / (float)srcSize.y;
    float rot = frac((float)(frameIndex & 7u) * 0.38196601f);
    float n0 = frac(ssgiInterleave3(id.xy) + rot);
    float jit = frac(ssgiInterleave3(id.yx + 1u) * 0.875f + 0.0625f + rot * 0.5f);
    uint rays = clamp(raysPerTexel, 1u, 8u), steps = clamp(marchSteps, 8u, 32u);
    float hc = heightScale * lc;
    float3 bounce = 0.0f; float aoAcc = 0.0f;
    [loop] for (uint r = 0; r < rays; ++r) {
        float aj = (ssgiIgn((float2)id.xy + 17.0f * (float)r, frameIndex) - 0.5f) * (1.0f / 9.0f);
        float jr = frac(jit + 0.61803399f * (float)r);
        float ang = 6.2831853f * (n0 + aj + (float)r / (float)rays);
        float2 dir = float2(cos(ang), sin(ang) * aspect);
        float sinH = -horizonBias, sinA = 0.0f, d = 0.0f, len = stepCells;
        float3 rayB = 0.0f;
        [loop] for (uint s = 0; s < steps; ++s) {
            d += len * (s == 0u ? (0.5f + jr) : 1.0f);
            len *= stepGrowth;
            float2 p = cc + dir * d;
            if (any(p < 0.0f) || any(p >= (float)SSGI_GRID)) break;
            uint q = gridAtD(int2(p));
            float l = ssgiUnpackLuma(q);
            float dc = d * stepCells;
            float zS = ssrtZ(p);
            float dzA, sinO;
            if (useAo) {
                float dz = (zc - zS) * aoDepthScale;
                sinO = dz * rsqrt(dz * dz + dc * dc + 1e-5f);
                // A gentle slope is not a crevice. Only a real depth step, and only nearby, darkens.
                float solid = saturate((sinO - 0.18f) * 3.5f);
                sinA = max(sinA, solid * saturate(1.0f - dc / aoRadius));
            } else {
                float dz = heightScale * l - hc;
                dzA = min(dz, 0.25f * heightScale);
                sinO = dzA * rsqrt(dzA * dzA + dc * dc) * (1.0f - smoothstep(emitLo, emitHi, l));
                sinA = max(sinA, sinO * saturate(1.0f - dc / aoRadius));
            }
            if (useGi) {
                float travel = saturate(d / 48.0f);
                float zRay = zc + (1.0f - zc) * travel;
                float gap = zRay - zS;
                if (gap > thickness) break;                         // sample is well in front: the ray is blocked
                if (gap >= 0.0f) {
                    float3 col = ssgiToLin(ssgiUnpackRgb(q));
                    float band = 1.0f - gap / max(thickness, 1e-4f);
                    // Steeper than SSGI so a large coloured surface tints its neighbourhood, not the whole frame.
                    rayB += giGain * band * smoothstep(emitLo, emitHi, l) * col / (1.0f + falloff * 3.5f * dc * dc);
                }
            } else {
                float dz = heightScale * l - hc;
                float sinS = dz * rsqrt(dz * dz + dc * dc);
                if (sinS > sinH) {
                    float3 col = ssgiToLin(ssgiUnpackRgb(q));
                    rayB += (sinS - sinH) * smoothstep(emitLo, emitHi, l) * col / (1.0f + falloff * dc * dc);
                    sinH = sinS;
                }
            }
        }
        bounce += rayB; aoAcc += sinA;
    }
    bounce *= 1.0f / ((float)rays * (1.0f + horizonBias));
    bounce *= 1.0f - smoothstep(emitLo, emitHi, lc);
    // Pixels the rays missed still pick up nearby coloured lights (night lamps, foliage under a sky).
    if (dot(bounce, float3(0.2126f, 0.7152f, 0.0722f)) < 0.02f && protect < 0.5f) {
        float3 extra = 0.0f;
        [unroll] for (uint i = 0u; i < 8u; ++i) {
            float ang = (float)i * 0.78539816f;
            float2 dir = float2(cos(ang), sin(ang) * aspect);
            [unroll] for (uint k = 0u; k < 3u; ++k) {
                float rad = (k == 0u) ? 4.0f : ((k == 1u) ? 12.0f : 40.0f);
                float2 p = cc + dir * rad;
                if (all(p >= 0.0f) && all(p < (float)SSGI_GRID)) {
                    uint q = gridAtD(int2(p));
                    float l = ssgiUnpackLuma(q);
                    float w = smoothstep(emitLo, emitHi, l) / (1.0f + 0.0035f * rad * rad);
                    extra += w * giGain * 0.38f * ssgiToLin(ssgiUnpackRgb(q));
                }
            }
        }
        bounce += extra * (1.0f / 8.0f);
    }
    float occ = saturate(aoAcc / (float)rays);
    occ *= 1.0f - smoothstep(emitLo, emitHi, lc);
    if (protect > 0.5f) { bounce = 0.0f; occ = 0.0f; }
    float ao = 1.0f - aoStrength * occ;
    gLightOut[id.xy] = ssgiPackLight(sqrt(saturate(bounce * ao)), lc, occ);
}
