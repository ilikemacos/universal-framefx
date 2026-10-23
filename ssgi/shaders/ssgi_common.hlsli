// Universal-FrameFX SSGI (experimental, off by default) - shared declarations for all SSGI shaders.
// Target: Shader Model 5.0 (fxc cs_5_0 / vs_5_0 / ps_5_0) so Kepler / Maxwell / Pascal GTX and GT cards run it.
//
// What this is (and is not): screen-space "GI" from the CAPTURED window image only. There is no depth buffer, no normals
// and no geometry, so "bounce light" here is a heuristic: bright regions of the 2D picture are treated as emitters and
// their colour is spread to nearby pixels, attenuated across strong luminance edges, and a luminance-contrast pseudo-AO
// scales it. It is an approximation of indirect lighting, not ray tracing and not physically based.
//
// Buffer layout shared with the C++ side (ssgi_pipeline.cpp / ssgi_importance.cpp) - keep in sync:
//   proxy grid: 128 x 128 uint, row-major, cell (x, y) at y * 128 + x.
//               bits  0- 7 R, 8-15 G, 16-23 B (8-bit, same encoding as the captured texture: display/gamma space)
//               bits 24-31 brightness B = max(luma709, 0.75 * max(r, g, b)) of those encoded values (round 2: chroma-aware,
//               so a saturated red / blue surface counts as bright as its strongest channel, not only by its luma)
//   importance: 512 uint = 128 rows x 4 words; cell (x, y) is bit (x & 31) of word y * 4 + (x >> 5) (LSB = smaller x).

#ifndef UFX_SSGI_COMMON_HLSLI
#define UFX_SSGI_COMMON_HLSLI

#define SSGI_GRID 128

cbuffer SsgiConstants : register(b0)
{
    uint2  srcSize;        // captured / game texture size in pixels
    uint2  lightSize;      // low-res lighting texture size (>= 128 x 128; default srcSize / 4)
    float2 invSrcSize;
    float2 invLightSize;
    uint   frameIndex;     // for the per-frame noise rotation
    uint   marchSteps;     // 8..32 (clamped on the CPU)
    uint   raysPerTexel;   // 1..8
    float  stepCells;      // march step length in proxy cells (along x; y is aspect-corrected)
    float  strength;       // composite: bounce strength
    float  aoStrength;     // march: pseudo-AO depth (0 = no AO)
    float  emitLo;         // march: luma (0..1) where a sample starts to count as emitting
    float  emitHi;         // march: luma (0..1) of a full emitter
    float  edgeLuma;       // march: luma drop (0..1) between steps treated as an occluding edge ("shadow trap")
    float  falloff;        // march: distance falloff k in 1 / (1 + k d^2), d in cells
    float  rangeK;         // composite: bilateral range weight k in exp(-k dL^2)
    uint   forceAll;       // 1 = ignore the importance map (debug / first frames)
    float  heightScale;    // r3: pseudo-depth height (in proxy cells) of a fully bright cell: h = heightScale * B
    float  stepGrowth;     // r3: geometric step growth (1 = uniform steps; >1 reaches further with the same steps)
    float  temporalAlpha;  // r3: 0 = temporal accumulation off; else history blend weight of the NEW frame (e.g. 0.25)
    float  horizonBias;    // r3: starting horizon sin(-angle) so near-flat neighbours still contribute a little
    float  aoRadius;       // r4: near-field AO radius in cells (contact occlusion only; far horizon feeds the bounce)
    float  aoGame;         // r4: AO applied to the GAME image, clamped <= 0.6 on the CPU: G * lerp(1, AO, aoGame)
    uint   temporalPan;    // r4: 1 = reproject history by the GPU global-shift estimate (CSShift)
    float  panMaxSad;      // r4: mean |dB| above which the shift estimate is rejected (history reset)
    // r6: reprojection from FrameFX FX_GlobalMotion (backward UV in gPan). 0 = the r4 path above.
    float  panMaxPx;       // reset when length(pan * srcSize) exceeds this (source pixels)
    float  lumReset;       // reset when the pan-aligned proxy |dB| exceeds this
    float  guideReject;    // history weight falls to 0 at |dGuide| = 1 / guideReject
    uint   temporalExt;    // 1 = sample history at uv + pan (CSPanGate wrote the pan into gShiftIn)
    float  clampBox;       // 1 = clip history to the current 3x3 min/max; smaller pulls that box toward this texel
    float  _padClamp0, _padClamp1, _padClamp2;
};

float  ssgiLuma(float3 c) { return dot(c, float3(0.2126f, 0.7152f, 0.0722f)); }
// Round 2: chroma-aware brightness used for emission, AO and the bilateral guide (CPU importance reads it too).
float  ssgiBright(float3 c) { return max(ssgiLuma(c), 0.75f * max(c.r, max(c.g, c.b))); }
// Approximate linear light: display values are treated as gamma 2.0 (x^2 to linear, sqrt back). Cheap, close to sRGB.
float3 ssgiToLin(float3 c) { return c * c; }
// Round 2: 3 x 3 interleaved pattern (0..8)/9: neighbouring texels take different ray rotations, so the 3 x 3
// filter after the march sees 9 rotations x raysPerTexel directions = one full period (much less grain than pure noise).
float  ssgiInterleave3(uint2 p) { return (float)((p.x % 3u) + 3u * (p.y % 3u)) * (1.0f / 9.0f); }

uint ssgiPack(float3 c, float l)
{
    uint4 q = (uint4)(saturate(float4(c, l)) * 255.0f + 0.5f);
    return q.x | (q.y << 8) | (q.z << 16) | (q.w << 24);
}
float3 ssgiUnpackRgb(uint p)  { return float3(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF) * (1.0f / 255.0f); }
float  ssgiUnpackLuma(uint p) { return (float)(p >> 24) * (1.0f / 255.0f); }

// Interleaved gradient noise (Jimenez 2014): cheap, no texture, and its error is spread at high spatial frequency like a
// blue-noise mask (it is not true blue noise). The frame index slides the pattern so a temporal filter / the eye averages it.
float ssgiIgn(float2 px, uint frame)
{
    px += 5.588238f * (float)(frame & 63);
    return frac(52.9829189f * frac(dot(px, float2(0.06711056f, 0.00583715f))));
}

// r4 lighting texel, R32G32_UINT: x = sqrt(bounce) r11 g11 b10, y = guide B (8) | occlusion (8) << 8.
// Carrying AO separately from the bounce lets the composite darken the game image itself.
uint2 ssgiPackLight(float3 sq, float guide, float occ)
{
    uint3 q = (uint3)(saturate(sq) * float3(2047.0f, 2047.0f, 1023.0f) + 0.5f);
    uint2 g = (uint2)(saturate(float2(guide, occ)) * 255.0f + 0.5f);
    return uint2(q.x | (q.y << 11) | (q.z << 22), g.x | (g.y << 8));
}
float3 ssgiLightRgb(uint2 p) { return float3(p.x & 0x7FF, (p.x >> 11) & 0x7FF, p.x >> 22) * float3(1.0f / 2047.0f, 1.0f / 2047.0f, 1.0f / 1023.0f); }
float  ssgiLightGuide(uint2 p) { return (float)(p.y & 0xFF) * (1.0f / 255.0f); }
float  ssgiLightOcc(uint2 p) { return (float)((p.y >> 8) & 0xFF) * (1.0f / 255.0f); }

#endif
