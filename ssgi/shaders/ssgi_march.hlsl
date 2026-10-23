// Phase 3 - importance-gated coarse screen-space march over the 128 x 128 proxy grid.
// Output: low-res lighting texture (default srcSize / 4), RGBA8_UNORM:
//   rgb = sqrt(bounce * pseudoAO) (bounce accumulated in approx. LINEAR light, stored gamma-2 encoded for 8-bit
//   precision in the darks), a = brightness of this texel's cell (guide for the edge-preserving filters).
// Round 2: chroma-aware emission (brightness B, see ssgi_common.hlsli), linear-light accumulation, 3 x 3 interleaved ray
// rotations (+ per-frame rotation) instead of per-texel noise, and a separate low-res 3 x 3 bilateral pass (CSFilter
// below) so the full-res composite only needs a 2 x 2 upsample.
//
// Early-out, honestly: on D3D11 / SM5 a compute thread cannot be "discarded" and a partly idle wave still costs a full
// wave. What this shader does is a WHOLE-GROUP early return: the 8 x 8 group looks up every proxy cell its 8 x 8 texels
// cover in the ImportanceMap (CPU-built, one frame or more old) and, if none is set, all 64 threads write a neutral texel
// (no bounce) and return together before the march loop. The saving is the loop, not the launch; a group with even one
// important cell marches all 64 texels. The CPU dilates the map by one cell so moving content is rarely clipped.
//
// The march (per texel, raysPerTexel rays x marchSteps steps, default 2 x 12):
//   * ray angles from interleaved gradient noise (blue-noise-like, rotated per frame), evenly spread around the circle;
//     a per-texel jitter offsets the first step. Steps are isotropic in screen pixels (y corrected for aspect).
//   * each step reads one packed proxy cell (StructuredBuffer, 64 KB, stays in L2) - no texture filtering needed.
//   * bounce: colour of the sample x emission weight (smoothstep(emitLo, emitHi, luma): "light emitting zones") x
//     distance falloff 1 / (1 + falloff d^2) x visibility. Visibility halves each time luma drops by more than edgeLuma
//     between consecutive samples ("shadow trap": a strong dark edge is assumed to occlude what lies beyond it).
//   * the receiver's own emission weight is removed (1 - smoothstep(emitLo, emitHi, own luma)) so bright zones do not
//     brighten themselves.
//   * pseudo-AO: over the first 4 steps of every ray, how much brighter the surroundings are than this cell. A cell
//     darker than its neighbourhood is assumed to be a crevice: AO = 1 - aoStrength * saturate(4 * mean excess).
// None of this knows real depth or normals: it is a luminance heuristic (see ssgi_common.hlsli).
// Dispatch: ceil(lightSize / 8) groups of 8 x 8 threads.

#include "ssgi_common.hlsli"

StructuredBuffer<uint>        gGrid       : register(t0);   // Phase 1 output (this frame, GPU-only path)
StructuredBuffer<uint>        gImportance : register(t1);   // 512 words, uploaded by the render thread (DYNAMIC buffer)
RWTexture2D<uint2>            gLightOut   : register(u0);   // r4: R32G32_UINT, see ssgiPackLight
Texture2D<uint2>              gLightIn    : register(t2);   // CSFilter input (march output)
Texture2D<uint2>              gHistory    : register(t3);   // CSFilter: previous filtered frame (temporal, r3)
StructuredBuffer<float4>      gShiftIn    : register(t4);   // CSFilter: xy = global shift (cells), z = 1 valid (r4)
StructuredBuffer<uint>        gPrevGrid   : register(t5);   // CSShift / CSPanGate: last frame's proxy grid
Texture2D<float2>             gPan        : register(t6);   // CSPanGate: FX_GlobalMotion backward UV, 1x1 R32G32_FLOAT
RWStructuredBuffer<float4>    gShiftOut   : register(u1);   // CSShift / CSPanGate output

groupshared uint gsAny;

uint gridAt(int2 c) { c = clamp(c, 0, SSGI_GRID - 1); return gGrid[c.y * SSGI_GRID + c.x]; }

[numthreads(8, 8, 1)]
void CSMarch(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex, uint3 lt : SV_GroupThreadID)
{
    // ---- whole-group importance test (uniform per group) -------------------------------------------------------------
    // Cells covered by this group's texels. lightSize >= 128 on both axes (enforced on the CPU), so a group of 8 texels
    // spans at most 8 + 1 cells per axis; the 64 threads test up to 8 x 8 of them, which covers it except the rare 9th
    // column/row, folded in by clamping (a cell on the group edge is also covered by the dilation on the CPU side).
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
    float2 cc = uv * SSGI_GRID;                                       // continuous cell coordinates of this texel
    uint centre = gridAt(int2(cc));
    float lc = ssgiUnpackLuma(centre);
    if (gsAny == 0) {                                                 // uniform: the whole group leaves here
        if (inside) gLightOut[id.xy] = ssgiPackLight(0.0f, lc, 0.0f);
        return;
    }
    if (!inside) return;                                              // after the last barrier: safe

    // ---- march ------------------------------------------------------------------------------------------------------
    float aspect = (float)srcSize.x / (float)srcSize.y;              // cells are W/128 x H/128 px: make steps isotropic
    float rot = frac((float)(frameIndex & 7u) * 0.38196601f);         // golden-ratio rotation per frame (for a temporal filter)
    float n0 = frac(ssgiInterleave3(id.xy) + rot);
    float jit = frac(ssgiInterleave3(id.yx + 1u) * 0.875f + 0.0625f + rot * 0.5f);
    uint rays = clamp(raysPerTexel, 1u, 8u), steps = clamp(marchSteps, 8u, 32u);
    // r3: horizon-based march on a luminance-derived pseudo height field h = heightScale * B (cells). Per ray the
    // running horizon sin(angle) only rises; a sample that raises it is VISIBLE and adds its emitted colour weighted by
    // the solid-angle slice it uncovers (sinS - sinH), GTAO-style. Samples hidden behind a nearer raised cell add nothing,
    // which replaces r1/r2's "shadow trap" heuristic with a geometric one. AO = 1 - mean final horizon sine.
    // Steps grow geometrically (stepGrowth), so 16-24 steps reach ~half the screen while staying dense near the texel
    // (near-field contact shading + far-field bounce in one march; a cheap substitute for marching a mip chain).
    float hc = heightScale * lc;
    float3 bounce = 0.0f; float aoAcc = 0.0f;
    [loop] for (uint r = 0; r < rays; ++r) {
        // r4: per-ray sub-bin angle jitter (IGN, +-half of a 1/9 interleave bin) + per-ray step offset (golden ratio):
        // the 3 x 3 interleave alone gives 9 x rays discrete directions per neighbourhood, which shows as radial
        // streaks around small bright sources; the jitter makes the angular coverage continuous and CSFilter averages it.
        float aj = (ssgiIgn((float2)id.xy + 17.0f * (float)r, frameIndex) - 0.5f) * (1.0f / 9.0f);
        float jr = frac(jit + 0.61803399f * (float)r);
        float ang = 6.2831853f * (n0 + aj + (float)r / (float)rays);
        float2 dir = float2(cos(ang), sin(ang) * aspect);
        float sinH = -horizonBias, sinA = 0.0f, d = 0.0f, len = stepCells;
        float3 rayB = 0.0f;
        [loop] for (uint s = 0; s < steps; ++s) {
            d += len * (s == 0 ? (0.5f + jr) : 1.0f);                // first step jittered, then geometric
            len *= stepGrowth;
            float2 p = cc + dir * d;
            if (any(p < 0.0f) || any(p >= (float)SSGI_GRID)) break;   // left the screen: no information beyond
            uint q = gridAt(int2(p));
            float l = ssgiUnpackLuma(q);
            float dz = heightScale * l - hc;
            float dc = d * stepCells;
            float sinS = dz * rsqrt(dz * dz + dc * dc);
            // r5: AO occluders must look like geometry, not light: an emitter-bright sample is a light source (its
            // light is in the bounce), so it does not occlude (kills the dark ring on the dark side of bright objects),
            // and the height step is clamped to aoMaxDz = 0.25 * heightScale (local-contrast clamp: one strong edge
            // cannot fully occlude; mortar-like crevices with moderate contrast still do).
            float dzA = min(dz, 0.25f * heightScale);
            float sinO = dzA * rsqrt(dzA * dzA + dc * dc) * (1.0f - smoothstep(emitLo, emitHi, l));
            sinA = max(sinA, sinO * saturate(1.0f - dc / aoRadius));
            if (sinS > sinH) {
                float3 col = ssgiToLin(ssgiUnpackRgb(q));
                rayB += (sinS - sinH) * smoothstep(emitLo, emitHi, l) * col / (1.0f + falloff * dc * dc);
                sinH = sinS;
            }
        }
        bounce += rayB; aoAcc += sinA;
    }
    bounce *= 1.0f / ((float)rays * (1.0f + horizonBias));
    bounce *= 1.0f - smoothstep(emitLo, emitHi, lc);                  // emitters do not light themselves (no wash-out)
    float occ = saturate(aoAcc / (float)rays);                        // 0 = open, 1 = fully occluded (near field)
    occ *= 1.0f - smoothstep(emitLo, emitHi, lc);                     // r5: bright (emitting) receivers are not darkened
    float ao = 1.0f - aoStrength * occ;                               // bounce AO (as before)
    gLightOut[id.xy] = ssgiPackLight(sqrt(saturate(bounce * ao)), lc, occ);
}

// Round 2 - low-res 3 x 3 edge-preserving (bilateral) filter, lighting A -> lighting B (same size, RGBA8). Moving the
// 3 x 3 here (lightSize texels, ~130k at 1080p / 4) lets the full-res composite use a 2 x 2 upsample: 9 -> 4 lighting
// loads per output pixel. Range weight on the stored guide brightness (alpha), spatial weight 1-2-1 binomial; it also
// averages exactly one period of the 3 x 3 interleaved ray rotations, which removes most of the grain.
[numthreads(8, 8, 1)]
void CSFilter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    uint2 c = gLightIn.Load(int3(id.xy, 0));
    float cg = ssgiLightGuide(c);
    int2 hi = int2(lightSize) - 1;
    float4 acc = 0.0f; float ws = 0.0f;
    float3 cmin = 1.0f, cmax = 0.0f; float omin = 1.0f, omax = 0.0f;
    [unroll] for (int k = 0; k < 9; ++k) {
        int2 o = int2(k % 3 - 1, k / 3 - 1);
        uint2 s = gLightIn.Load(int3(clamp(int2(id.xy) + o, 0, hi), 0));
        float dl = ssgiLightGuide(s) - cg;
        float w = (float)((2 - abs(o.x)) * (2 - abs(o.y))) * exp(-rangeK * dl * dl);
        float3 q = ssgiLightRgb(s);
        float3 ql = q * q; float oc = ssgiLightOcc(s);
        cmin = min(cmin, ql); cmax = max(cmax, ql); omin = min(omin, oc); omax = max(omax, oc);
        acc += w * float4(ql, oc);                                    // filter in linear light; occlusion linear
        ws += w;
    }
    float4 cur = acc / ws;
    if (temporalAlpha > 0.0f) {
        if (temporalExt != 0u) {
            // r6: FrameFX FX_GlobalMotion is a BACKWARD uv vector (curr(uv) came from prev(uv + pan);
            // see FX_GlobalMotion / FX_MvBorder). CSPanGate copied that pan into gShiftIn and cleared z
            // on a long pan or a pan-aligned brightness break. Off-screen history is a disocclusion.
            // Neighbourhood clamp is the current 3x3 min/max in linear light, then the tuned blend.
            float4 sh = gShiftIn[0];
            float2 hp = (float2)id.xy + sh.xy * (float2)lightSize;
            int2 h0 = int2(floor(hp)), h1 = h0 + 1;
            if (sh.z > 0.5f && all(h0 >= 0) && all(h1 <= hi)) {
                float2 fr = hp - float2(h0);
                uint2 s00 = gHistory.Load(int3(h0, 0));
                uint2 s10 = gHistory.Load(int3(h1.x, h0.y, 0));
                uint2 s01 = gHistory.Load(int3(h0.x, h1.y, 0));
                uint2 s11 = gHistory.Load(int3(h1, 0));
                float3 q00 = ssgiLightRgb(s00), q10 = ssgiLightRgb(s10), q01 = ssgiLightRgb(s01), q11 = ssgiLightRgb(s11);
                float3 rgb = lerp(lerp(q00, q10, fr.x), lerp(q01, q11, fr.x), fr.y);
                float hg = lerp(lerp(ssgiLightGuide(s00), ssgiLightGuide(s10), fr.x), lerp(ssgiLightGuide(s01), ssgiLightGuide(s11), fr.x), fr.y);
                float ho = lerp(lerp(ssgiLightOcc(s00), ssgiLightOcc(s10), fr.x), lerp(ssgiLightOcc(s01), ssgiLightOcc(s11), fr.x), fr.y);
                float3 hlin = clamp(rgb * rgb, lerp(cur.rgb, cmin, clampBox), lerp(cur.rgb, cmax, clampBox));
                ho = clamp(ho, lerp(cur.a, omin, clampBox), lerp(cur.a, omax, clampBox));
                float keep = saturate(1.0f - abs(hg - cg) * guideReject) * (1.0f - temporalAlpha);
                cur = lerp(cur, float4(hlin, ho), keep);
            }
        } else {
            // r3/r4, behind a flag: exponential history. r4 reprojects by the global shift CSShift estimated on the proxy
            // grid this frame (camera pan / scroll, no motion vectors, no read-back); history is dropped when the estimate
            // is rejected, when the reprojected texel is off-screen, or where the guide brightness changed (disocclusion).
            float4 sh = temporalPan ? gShiftIn[0] : float4(0.0f, 0.0f, 1.0f, 0.0f);
            int2 hp = int2(floor((float2)id.xy + 0.5f - sh.xy * (float2)lightSize * (1.0f / SSGI_GRID)));
            if (sh.z > 0.5f && all(hp >= 0) && all(hp <= hi)) {
                uint2 h = gHistory.Load(int3(hp, 0));
                float3 hq = ssgiLightRgb(h);
                float keep = saturate(1.0f - abs(ssgiLightGuide(h) - cg) * 10.0f) * (1.0f - temporalAlpha);
                cur = lerp(cur, float4(hq * hq, ssgiLightOcc(h)), keep);
            }
        }
    }
    gLightOut[id.xy] = ssgiPackLight(sqrt(cur.rgb), cg, cur.a);
}

// r4 - global shift estimate for pan-tolerant temporal reprojection. ONE group of 81 threads: thread t tests the
// integer shift (t % 9 - 4, t / 9 - 4) cells (+-4 cells = +-1/32 of the screen per frame) by the mean |dB| between this
// frame's proxy grid and last frame's (gPrevGrid) over 28 x 28 sample cells; thread 0 takes the minimum, refines it to
// sub-cell with a parabola through the neighbours and writes (shift, valid). ~22k grid loads, L2-resident: ~5 us.
groupshared float gsSad[81];
[numthreads(81, 1, 1)]
void CSShift(uint gi : SV_GroupIndex)
{
    int2 sft = int2((int)(gi % 9u) - 4, (int)(gi / 9u) - 4);
    float sad = 0.0f;
    [loop] for (int j = 0; j < 28; ++j) {
        [loop] for (int i = 0; i < 28; ++i) {
            int2 cc = int2(8 + 4 * i, 8 + 4 * j);
            float a = ssgiUnpackLuma(gGrid[cc.y * SSGI_GRID + cc.x]);
            int2 pc = cc - sft;                                       // content moved by +sft: cur(c) = prev(c - sft)
            float b = ssgiUnpackLuma(gPrevGrid[pc.y * SSGI_GRID + pc.x]);
            sad += abs(a - b);
        }
    }
    gsSad[gi] = sad * (1.0f / 784.0f);
    GroupMemoryBarrierWithGroupSync();
    if (gi == 0) {
        uint best = 40u;                                              // ties prefer zero shift
        [loop] for (uint t = 0; t < 81u; ++t) if (gsSad[t] < gsSad[best] - 1e-4f) best = t;
        int bx = (int)(best % 9u), by = (int)(best / 9u);
        float2 f = float2(bx - 4, by - 4);
        if (bx > 0 && bx < 8) { float l = gsSad[best - 1], r = gsSad[best + 1], m = gsSad[best];
            float den = l - 2.0f * m + r; if (den > 1e-5f) f.x += clamp(0.5f * (l - r) / den, -0.5f, 0.5f); }
        if (by > 0 && by < 8) { float u = gsSad[best - 9], d = gsSad[best + 9], m = gsSad[best];
            float den = u - 2.0f * m + d; if (den > 1e-5f) f.y += clamp(0.5f * (u - d) / den, -0.5f, 0.5f); }
        gShiftOut[0] = float4(f, gsSad[best] <= panMaxSad ? 1.0f : 0.0f, gsSad[best]);
    }
}

// r6 - accept or reject FrameFX's camera pan for this frame. ONE group. The pan SRV is the backward UV from
// FX_GlobalMotion (no CPU readback). Samples 28 x 28 proxy cells, the same lattice as CSShift, and writes
// (pan.xy, 1 if the pan is short enough and the aligned brightness still matches, mean |dB|).
groupshared float2 gsPanAcc[64];
[numthreads(64, 1, 1)]
void CSPanGate(uint gi : SV_GroupIndex)
{
    float2 pan = gPan.Load(int3(0, 0, 0));
    float2 pc = pan * (float)SSGI_GRID;                               // curr(c) matches prev(c + pc)
    float sad = 0.0f, n = 0.0f;
    [loop] for (uint k = gi; k < 784u; k += 64u) {
        int x = 8 + 4 * (int)(k % 28u);
        int y = 8 + 4 * (int)(k / 28u);
        float2 q = float2((float)x, (float)y) + pc;
        int2 q0 = int2(floor(q));
        if (all(q0 >= 0) && all(q0 < SSGI_GRID - 1)) {
            float2 fr = q - float2(q0);
            float l00 = ssgiUnpackLuma(gPrevGrid[q0.y * SSGI_GRID + q0.x]);
            float l10 = ssgiUnpackLuma(gPrevGrid[q0.y * SSGI_GRID + q0.x + 1]);
            float l01 = ssgiUnpackLuma(gPrevGrid[(q0.y + 1) * SSGI_GRID + q0.x]);
            float l11 = ssgiUnpackLuma(gPrevGrid[(q0.y + 1) * SSGI_GRID + q0.x + 1]);
            float prev = lerp(lerp(l00, l10, fr.x), lerp(l01, l11, fr.x), fr.y);
            sad += abs(ssgiUnpackLuma(gGrid[y * SSGI_GRID + x]) - prev);
            n += 1.0f;
        }
    }
    gsPanAcc[gi] = float2(sad, n);
    GroupMemoryBarrierWithGroupSync();
    if (gi == 0) {
        float2 a = float2(0.0f, 0.0f);
        [loop] for (uint i = 0; i < 64u; ++i) a += gsPanAcc[i];
        float mean = a.y >= 200.0f ? a.x / a.y : 1.0f;
        float mag = length(pan * float2(srcSize));
        gShiftOut[0] = float4(pan.x, pan.y, (mag <= panMaxPx && mean <= lumReset) ? 1.0f : 0.0f, mean);
    }
}
