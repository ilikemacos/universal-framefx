// Dominant light = brightest proxy cell (CSLightDir, one group). CSContactShadow marches a few short
// rays toward it. A nearer depth sample inside the thickness band and inside contactRadius darkens the
// pixel; the darkening falls off with distance so only the contact neighbourhood is affected. Two or more
// rays with a small angular jitter soften the penumbra.
//
// CSLightDir cost: 16384 grid loads, ~20 ALU, one group.
// CSContactShadow cost: contactRays * contactSteps depth Loads + 3 grid loads, ~24 ALU per step.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

StructuredBuffer<uint>     gGrid    : register(t0);
Texture2D<float4>          gDepth   : register(t1);
StructuredBuffer<float4>   gLight   : register(t2);   // CSContactShadow: xy = brightest cell, z = brightness
RWStructuredBuffer<float4> gLightOut : register(u0);  // CSLightDir
RWTexture2D<float>         gShadow  : register(u1);  // R32_FLOAT, 0 = none, 1 = full contact

groupshared float gsB[256];
groupshared uint  gsI[256];

[numthreads(256, 1, 1)]
void CSLightDir(uint gi : SV_GroupIndex)
{
    float bestB = -1.0f; uint bestI = 0u;
    [loop] for (uint i = gi; i < (uint)(SSGI_GRID * SSGI_GRID); i += 256u) {
        float b = ssgiUnpackLuma(gGrid[i]);
        if (b > bestB) { bestB = b; bestI = i; }
    }
    gsB[gi] = bestB; gsI[gi] = bestI;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 128u; s > 0u; s >>= 1u) {
        if (gi < s && gsB[gi + s] > gsB[gi]) { gsB[gi] = gsB[gi + s]; gsI[gi] = gsI[gi + s]; }
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi == 0u) {
        uint idx = gsI[0];
        gLightOut[0] = float4((float)(idx % (uint)SSGI_GRID) + 0.5f, (float)(idx / (uint)SSGI_GRID) + 0.5f, gsB[0], 1.0f);
    }
}

[numthreads(8, 8, 1)]
void CSContactShadow(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    float2 uv = ((float2)id.xy + 0.5f) * invLightSize;
    float2 cc = uv * (float)SSGI_GRID;
    float4 L = gLight[0];
    float2 toL = L.xy - cc;
    float distL = length(toL);
    if (distL < 0.75f) { gShadow[id.xy] = 0.0f; return; }
    int2 cell = clamp(int2(cc), int2(0, 0), int2(SSGI_GRID - 1, SSGI_GRID - 1));
    int xm = max(cell.x - 1, 0), xp = min(cell.x + 1, SSGI_GRID - 1);
    int ym = max(cell.y - 1, 0), yp = min(cell.y + 1, SSGI_GRID - 1);
    float gx = ssgiUnpackLuma(gGrid[cell.y * SSGI_GRID + xp]) - ssgiUnpackLuma(gGrid[cell.y * SSGI_GRID + xm]);
    float gy = ssgiUnpackLuma(gGrid[yp * SSGI_GRID + cell.x]) - ssgiUnpackLuma(gGrid[ym * SSGI_GRID + cell.x]);
    float2 dirV = toL / distL + float2(gx, gy) * 0.5f;
    if (dot(dirV, dirV) < 1e-8f) dirV = toL / distL;
    float2 dir = normalize(dirV);
    float aspect = (float)srcSize.x / (float)srcSize.y;
    float4 d0 = gDepth.Load(int3(id.xy, 0));
    if (ssrtProtect(d0.w) > 0.5f) { gShadow[id.xy] = 0.0f; return; }
    float zc = d0.x;
    uint rays = clamp(contactRays, 1u, 4u);
    uint steps = clamp(contactSteps, 1u, 12u);
    float base = atan2(dir.y, dir.x);
    float acc = 0.0f;
    [loop] for (uint r = 0; r < rays; ++r) {
        float ang = base + ((float)r - 0.5f * (float)(rays - 1u)) * 0.18f;
        float2 rd = float2(cos(ang), sin(ang) * aspect);
        float best = 0.0f;
        [loop] for (uint s = 1; s <= steps; ++s) {
            float dist = (float)s * stepCells;
            if (dist > contactRadius) break;
            float2 p = cc + rd * dist;
            if (any(p < 0.0f) || any(p >= (float)SSGI_GRID)) break;
            int2 ip = clamp(int2(p * (1.0f / (float)SSGI_GRID) * (float2)lightSize), int2(0, 0), int2(lightSize) - 1);
            float zS = gDepth.Load(int3(ip, 0)).x;
            float front = zc - zS;
            if (front > 0.012f && front < thickness * 2.0f) {
                float pen = saturate(1.0f - dist / max(contactRadius, 0.5f));
                pen *= saturate(front / max(thickness, 1e-4f));
                best = max(best, pen);
            }
        }
        acc += best;
    }
    gShadow[id.xy] = acc / (float)rays;
}
