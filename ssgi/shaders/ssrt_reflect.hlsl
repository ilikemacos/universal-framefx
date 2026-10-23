// Screen-space reflection. View ray (+y down the image, +z into the screen) is reflected about the
// depth-gradient normal. Only pixels whose smoothness is above glossMin march: low local variance,
// enough brightness, and a near-horizontal normal (floor-like, n.y < 0 and n.z not dominant).
// The march is coarse-to-fine (first half of the steps are 4x the fine stride, total length reflReach
// in units of screen height) — a short hierarchical stand-in for a depth mip chain. Misses and screen
// edges fade to zero. Roughness jitters a second colour tap.
//
// cost: 1 depth Load + reflSteps depth Loads + up to 2 game Loads, ~40 ALU per step.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

Texture2D<float4>   gDepth : register(t0);
Texture2D<float4>   gGame  : register(t1);
RWTexture2D<float4> gRefl  : register(u0);   // RGBA8: rgb colour, a confidence

[numthreads(8, 8, 1)]
void CSReflect(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    float4 d0 = gDepth.Load(int3(id.xy, 0));
    float sm = ssrtSmooth(d0.a);
    if (ssrtProtect(d0.a) > 0.5f || sm < glossMin) { gRefl[id.xy] = float4(0, 0, 0, 0); return; }
    float nz = sqrt(saturate(1.0f - d0.g * d0.g - d0.b * d0.b));
    float3 N = normalize(float3(d0.g, d0.b, nz));
    float2 uv = ((float2)id.xy + 0.5f) * invLightSize;
    float aspect = (float)srcSize.x / (float)srcSize.y;
    float3 V = normalize(float3((uv.x * 2.0f - 1.0f) * aspect, uv.y * 2.0f - 1.0f, 1.0f));
    float3 R = reflect(V, N);
    if (R.z < 0.05f) { gRefl[id.xy] = float4(0, 0, 0, 0); return; }
    float2 uvDir = float2(R.x / aspect, R.y);
    float dl = length(uvDir);
    if (dl < 1e-4f) { gRefl[id.xy] = float4(0, 0, 0, 0); return; }
    uvDir /= dl;
    uint steps = clamp(reflSteps, 1u, 16u);
    uint nC = steps >> 1;
    uint nF = steps - nC;
    float sF = reflReach / max(4.0f * (float)nC + (float)nF, 1.0f);
    float sC = 4.0f * sF;
    float zc = d0.x;
    float traveled = 0.0f;
    bool hit = false;
    float2 hitUv = uv;
    float gapH = thickness;
    [loop] for (uint s = 0; s < steps; ++s) {
        traveled += (s < nC) ? sC : sF;
        float2 u = uv + uvDir * traveled;
        if (any(u < 0.0f) || any(u > 1.0f)) break;
        int2 ip = clamp(int2(u * (float2)lightSize), int2(0, 0), int2(lightSize) - 1);
        float zS = gDepth.Load(int3(ip, 0)).x;
        float zRay = lerp(zc, 1.0f, saturate(traveled / max(reflReach, 1e-3f)));
        float gap = zRay - zS;
        if (gap > thickness) break;
        if (gap >= 0.0f) { hit = true; hitUv = u; gapH = gap; break; }
    }
    if (!hit) { gRefl[id.xy] = float4(0, 0, 0, 0); return; }
    int2 hi = int2(srcSize) - 1;
    int2 gp = clamp(int2(hitUv * (float2)srcSize), int2(0, 0), hi);
    float3 col = gGame.Load(int3(gp, 0)).rgb;
    float rough = saturate(1.0f - sm);
    if (rough > 0.02f) {
        float2 perp = float2(-uvDir.y, uvDir.x);
        float2 u2 = saturate(hitUv + perp * rough * roughBlur);
        int2 gp2 = clamp(int2(u2 * (float2)srcSize), int2(0, 0), hi);
        col = 0.5f * (col + gGame.Load(int3(gp2, 0)).rgb);
    }
    float edge = min(min(hitUv.x, hitUv.y), min(1.0f - hitUv.x, 1.0f - hitUv.y));
    float edge0 = min(min(uv.x, uv.y), min(1.0f - uv.x, 1.0f - uv.y));
    float conf = saturate(edge * edgeFade) * saturate(edge0 * edgeFade);
    conf *= saturate(1.0f - gapH / max(thickness, 1e-4f));
    conf *= lerp(1.0f, 0.35f, rough);
    conf *= sm;
    gRefl[id.xy] = float4(col, saturate(conf));
}
