// Phase 4 - upscale + composite, one full-screen pixel-shader pass (draw 3 vertices, no vertex buffer).
// Round 2:
//   * the 3 x 3 bilateral now runs at low res (CSFilter in ssgi_march.hlsl); here a 2 x 2 JOINT-bilateral upsample:
//     bilinear weights x exp(-rangeK * (B_game - B_guide)^2), so light still does not bleed across strong edges of the
//     full-res image. 5 loads per pixel (1 game + 4 lighting) instead of 10.
//   * composite in approximate linear light, and the bounce is modulated by the RECEIVER's colour (an albedo estimate
//     from the game pixel's chroma): Final = sqrt(saturate(G^2 + strength * Bounce * AO * albedo)). Multiplying by the
//     receiver is what makes it read as light falling on a surface rather than a glow drawn over it (less bloom-like).
// Cost note: most of this pass is one full-frame read + write. If the app folds it into its existing present / copy
// pass (which already reads the frame and writes the back buffer) that bandwidth disappears; only the 4 lighting loads
// and the maths remain. The output must be a different texture from the game SRV.
#include "ssgi_common.hlsli"

Texture2D<float4> gGame  : register(t0);
Texture2D<uint2>  gLight : register(t1);   // r4: R32G32_UINT (ssgiPackLight)

struct VsOut { float4 pos : SV_Position; };

VsOut VSFullscreen(uint vid : SV_VertexID)
{
    // One triangle covering the screen: (-1,-1) (3,-1) (-1,3). No input layout, no vertex buffer.
    VsOut o;
    float2 p = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(p * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return o;
}

float4 PSComposite(VsOut i) : SV_Target
{
    int2 pix = int2(i.pos.xy);
    float3 game = gGame.Load(int3(pix, 0)).rgb;
    float bg = ssgiBright(game);
    float2 lp = i.pos.xy * (float2)lightSize * invSrcSize - 0.5f;   // continuous low-res texel coordinate
    int2 b0 = int2(floor(lp)), hi = int2(lightSize) - 1;
    float2 f = lp - (float2)b0;
    float4 acc = 0.0f; float wsum = 0.0f;
    [unroll] for (int k = 0; k < 4; ++k) {
        int2 o = int2(k & 1, k >> 1);
        uint2 s = gLight.Load(int3(clamp(b0 + o, 0, hi), 0));
        float wb = (o.x ? f.x : 1.0f - f.x) * (o.y ? f.y : 1.0f - f.y);
        float dl = bg - ssgiLightGuide(s);
        float w = wb * exp(-rangeK * dl * dl);
        float3 q = ssgiLightRgb(s);
        float4 lin = float4(q * q, ssgiLightOcc(s));
        acc += w * lin; wsum += w;
    }
    // r4: no nearest-tap fallback. When the range test rejects most taps (a thin dark detail inside a bright zone, e.g.
    // window mullions, which the low-res guide cannot resolve) the result fades out instead of copying the neighbour's
    // full bounce onto it (r3 lifted such pixels from ~20 to ~66/255).
    float4 lo = acc / max(wsum, 0.25f);
    float3 light = lo.rgb;
    float3 g = ssgiToLin(game);
    float3 albedo = 0.5f * clamp(g / max(ssgiLuma(g), 0.02f), 0.0f, 2.0f);   // receiver chroma at mid albedo
    // r4: visible AO on the game image (aoGame clamped <= 0.6 on the CPU so it cannot crush to black):
    // Final = sqrt(saturate(G^2 * lerp(1, 1 - occ, aoGame) + strength * Bounce * albedo))
    g *= 1.0f - aoGame * lo.a;
    return float4(sqrt(saturate(g + strength * light * albedo)), 1.0f);
}
