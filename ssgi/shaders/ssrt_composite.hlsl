// Pack reflection (premultiplied by confidence) and contact shadow into one RGBA8 texture, then a
// full-res composite. The GI / AO upsample is the same 2x2 joint bilateral as PSComposite. With both
// strengths at 0 the arithmetic matches PSComposite (features-off stays bit-identical).
//
// When reflections or contact are on, the packed signal is one nearest Load (the field is already
// light-resolution) instead of a second 2x2. Bounce is added in the receiver's colour, then the luma of
// that add is mostly removed so the frame's average brightness stays put. Crevice darkening and contact
// use only a strong, nearby occlusion. Sky, flat saturated UI, and emissive cores are copied through.
//
// CSPack cost: 1 refl Load + 1 shadow Load, ~8 ALU, write 4 B/px.
// PSCompositeSsrt cost: 1 game Load + 4 lighting Loads + 1 packed Load when either strength is > 0.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

Texture2D<float4>   gReflIn  : register(t0);
Texture2D<float>    gShadow  : register(t1);
RWTexture2D<float4> gPacked  : register(u0);

Texture2D<float4>   gGame    : register(t2);
Texture2D<uint2>    gLight   : register(t3);
Texture2D<float4>   gExtra   : register(t4);   // rgb = reflection * confidence, a = contact

[numthreads(8, 8, 1)]
void CSPack(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    float3 refl = 0.0f;
    float sh = 0.0f;
    if (reflStrength > 0.0f) {
        float4 r = gReflIn.Load(int3(id.xy, 0));
        refl = r.rgb * r.a;
    }
    if (contactStrength > 0.0f) sh = saturate(gShadow.Load(int3(id.xy, 0)));
    gPacked[id.xy] = float4(refl, sh);
}

struct VsOut { float4 pos : SV_Position; };

float4 PSCompositeSsrt(VsOut i) : SV_Target
{
    int2 pix = int2(i.pos.xy);
    float3 game = gGame.Load(int3(pix, 0)).rgb;
    float bg = ssgiBright(game);
    float2 lp = i.pos.xy * (float2)lightSize * invSrcSize - 0.5f;
    int2 b0 = int2(floor(lp)), hi = int2(lightSize) - 1;
    float2 f = lp - (float2)b0;
    float4 acc = 0.0f; float wsum = 0.0f;
    bool extra = reflStrength > 0.0f || contactStrength > 0.0f;
    [unroll] for (int k = 0; k < 4; ++k) {
        int2 o = int2(k & 1, k >> 1);
        int2 tp = clamp(b0 + o, int2(0, 0), hi);
        uint2 s = gLight.Load(int3(tp, 0));
        float guide = ssgiLightGuide(s);
        float wb = (o.x ? f.x : 1.0f - f.x) * (o.y ? f.y : 1.0f - f.y);
        float dl = bg - guide;
        float wr = exp(-rangeK * dl * dl);
        // High-frequency receivers (foliage) fail the range test and would lose the bounce entirely.
        // Features-off keeps the hard test so that path stays identical to PSComposite.
        if (extra) wr = max(wr, 0.22f);
        float w = wb * wr;
        float3 q = ssgiLightRgb(s);
        acc += w * float4(q * q, ssgiLightOcc(s));
        wsum += w;
    }
    float4 lo = acc / max(wsum, 0.25f);
    float3 g = ssgiToLin(game);
    float3 albedo = 0.5f * clamp(g / max(ssgiLuma(g), 0.02f), 0.0f, 2.0f);
    if (!extra) {
        g *= 1.0f - aoGame * lo.a;
        return float4(sqrt(saturate(g + strength * lo.rgb * albedo)), 1.0f);
    }
    float4 ex = gExtra.Load(int3(clamp(int2(floor(lp + 0.5f)), int2(0, 0), hi), 0));
    float2 uv = i.pos.xy * invSrcSize;
    float mx = max(game.r, max(game.g, game.b));
    float mn = min(game.r, min(game.g, game.b));
    float satC = (mx - mn) / max(mx, 1e-3f);
    // Pure red fill or a blue panel. Green foliage and a red barn (not this saturated) stay shaded.
    float uiRed = (game.r > 0.85f && game.g < 0.12f && game.b < 0.12f) ? 1.0f : 0.0f;
    float uiBlue = (game.b > 0.80f && game.r < 0.20f && game.g < 0.35f) ? 1.0f : 0.0f;
    float ui = max(uiRed, uiBlue);
    float glow = (bg > 0.80f) ? 1.0f : 0.0f;
    // Upper sky, including a saturated cyan sky. A coloured surface below the horizon is not sky.
    float upper = (uv.y < 0.52f) ? 1.0f : 0.0f;
    float blueSky = (bg > 0.36f && game.b > game.r + 0.05f && game.b > game.g - 0.04f) ? 1.0f : 0.0f;
    float pale = (satC < 0.30f && bg > 0.50f && game.b > game.r - 0.02f) ? 1.0f : 0.0f;
    float sky = upper * max(blueSky, pale);
    float protect = max(ui, max(glow, sky));
    float crevice = saturate((lo.a - 0.45f) / 0.25f);
    float shade = saturate(crevice * aoGame * 0.32f + contactStrength * 0.32f * saturate(ex.a) * step(0.45f, ex.a));
    shade *= (1.0f - protect);
    float3 add = (strength * lo.rgb + reflStrength * ex.rgb) * albedo * (1.0f - protect);
    // A saturated surface is the source of the bleed, not a place to repaint. Duller neighbours keep it.
    float emitter = saturate((satC - 0.40f) * 8.0f) * saturate((bg - 0.26f) * 5.0f);
    add *= (1.0f - emitter);
    float addY = ssgiLuma(add);
    // Colour of the bounce is kept. Luma of the add is small, then most of the remaining luma
    // change (including crevice shade) is pulled back so the frame mean stays near the source.
    float3 lin = g * (1.0f - shade) + (add - addY) * 0.95f + addY * 0.50f;
    float y0 = ssgiLuma(g);
    float y1 = ssgiLuma(lin);
    lin += (y0 - y1) * 0.65f;
    if (protect > 0.5f) lin = g;
    return float4(sqrt(saturate(lin)), 1.0f);
}
