// Pseudo-depth from the captured image alone (no depth buffer, no monocular network — see README).
// CSDepth  : per light-res pixel, 3x3 point taps of the game image. Cues: horizon prior, local contrast,
//            defocus (high-frequency energy ratio), luma/sky, and (only when parallaxWeight > 0 and the
//            global pan is longer than panMinPx) temporal parallax from FrameFX motion.
//            Output R16G16: x = raw depth (0 near, 1 far), y = brightness.
//            cost: 9 game Loads + 4 silhouette taps + 1 column-sky tap (+1 motion + 1 pan when
//            parallax is on), ~70 ALU, write 8 B/px. The full-frame read still dominates.
// CSDepthBlur: 3x3 edge-aware bilateral (range on brightness, same rangeK as SSGI), screen-space normal
//            from the raw gradients, smoothness for the reflection gate. Optional history blend when
//            ssrtTemporal > 0 (off in this task).
//            cost: 9 raw Loads (+1 history Load if temporal), ~48 ALU, write 8 B/px.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

Texture2D<float4>  gGame   : register(t0);
Texture2D<float4>  gMotion : register(t1);   // FrameFX _mvLo: xy = motion in source pixels (may be unbound)
Texture2D<float2>  gPan    : register(t2);   // FrameFX _gmv: 1x1 backward UV (may be unbound)
RWTexture2D<float4> gRaw   : register(u0);   // x depth, y brightness, z protect

Texture2D<float4>  gRawIn  : register(t3);   // CSDepthBlur
Texture2D<float4>  gHist   : register(t4);   // previous blurred depth (hook; not read when ssrtTemporal == 0)
RWTexture2D<float4> gDepth : register(u1);

[numthreads(8, 8, 1)]
void CSDepth(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    uint2 stepPx = max(srcSize / max(lightSize, uint2(1, 1)), uint2(1, 1));
    int2 center = int2(((float2)id.xy + 0.5f) * (float2)srcSize * invLightSize);
    int2 hi = int2(srcSize) - 1;
    center = clamp(center, int2(0, 0), hi);
    float Ls[9];
    float sumL = 0.0f;
    float3 rgbC = 0.0f;
    [unroll] for (int k = 0; k < 9; ++k) {
        int2 o = int2(k % 3 - 1, k / 3 - 1);
        int2 p = clamp(center + o * stepPx, int2(0, 0), hi);
        float3 rgb = gGame.Load(int3(p, 0)).rgb;
        float L = ssgiBright(rgb);
        Ls[k] = L; sumL += L;
        if (k == 4) rgbC = rgb;
    }
    // Wider ring so a character / bush interior (dark against a brighter backdrop) reads as near.
    float ring = 0.0f;
    [unroll] for (int s = 0; s < 4; ++s) {
        int2 o = int2(s < 2 ? (s == 0 ? -8 : 8) : 0, s >= 2 ? (s == 2 ? -8 : 8) : 0);
        float L = ssgiBright(gGame.Load(int3(clamp(center + o * stepPx, int2(0, 0), hi), 0)).rgb);
        ring = max(ring, L);
    }
    float Lc = Ls[4];
    float sumAbs = 0.0f;
    [unroll] for (int k2 = 0; k2 < 9; ++k2) sumAbs += abs(Ls[k2] - Lc);
    float mean = sumL * (1.0f / 9.0f);
    float contrast = sumAbs * (1.0f / 9.0f);
    float hf = abs(Lc - mean) / (mean + 0.08f);
    float mx = max(rgbC.r, max(rgbC.g, rgbC.b));
    float mn = min(rgbC.r, min(rgbC.g, rgbC.b));
    float satC = (mx - mn) / max(mx, 1e-3f);
    float2 uv = ((float2)id.xy + 0.5f) * invLightSize;
    float below = saturate((uv.y - horizonY) / max(1.0f - horizonY, 0.001f));
    float above = saturate((horizonY - uv.y) / max(horizonY, 0.001f));
    float skyLike = saturate((Lc - 0.48f) * 3.5f) * saturate((0.28f - satC) * 4.0f);
    float blue = saturate((rgbC.b - max(rgbC.r, rgbC.g)) * 4.0f);
    skyLike = max(skyLike, blue * saturate((Lc - 0.32f) * 3.0f));
    float dHorizon = lerp(1.0f, 0.16f, below);
    dHorizon = max(dHorizon, above);                                   // above the nominal horizon stays far
    float dContrast = lerp(0.82f, 0.20f, saturate(contrast * 10.0f)); // sharp local contrast reads as nearer
    float dDefocus = lerp(0.90f, 0.18f, saturate(hf * 5.0f));          // blur (low HF ratio) reads as farther
    float sky = saturate((Lc - skyLuma) * 5.0f) * above;
    float dLuma = lerp(0.55f, 1.0f, sky);
    float sil = saturate((ring - Lc - 0.06f) * 4.5f);
    float fol = saturate((satC - 0.15f) * 4.0f) * saturate(hf * 3.0f);
    float hazeAmt = saturate((0.22f - satC) * 5.0f) * saturate(1.2f - hf * 3.0f) * (1.0f - below);
    float dPar = 0.55f, wP = 0.0f;
    if (parallaxWeight > 0.0f) {
        uint mw, mh;
        gMotion.GetDimensions(mw, mh);
        int2 mp = int2(uv * float2(mw, mh));
        mp = clamp(mp, int2(0, 0), int2(mw, mh) - 1);
        float2 mv = gMotion.Load(int3(mp, 0)).xy;
        float2 panPx = gPan.Load(int3(0, 0, 0)).xy * (float2)srcSize;
        float panLen = length(panPx);
        if (panLen >= panMinPx) {
            float rel = length(mv) / panLen;                           // ~1 on the background, >1 on nearer parallax
            dPar = saturate(1.15f - 0.55f * rel);
            wP = wParallax;
        }
    }
    // Extra cues only under an outdoor sky. One sample at 2% from the top of this column:
    // a blue or pale sky enables them; an interior wall or a night sky leaves the old prior.
    int topY = (int)(0.02f * (float)srcSize.y);
    float3 topRgb = gGame.Load(int3(clamp(int2(center.x, topY), int2(0, 0), hi), 0)).rgb;
    float topL = ssgiBright(topRgb);
    float topMx = max(topRgb.r, max(topRgb.g, topRgb.b));
    float topMn = min(topRgb.r, min(topRgb.g, topRgb.b));
    float topSat = (topMx - topMn) / max(topMx, 1e-3f);
    float outdoor = 0.0f;
    if ((topL > 0.36f && topRgb.b > topRgb.r + 0.05f && topRgb.b > topRgb.g - 0.04f)
        || (topSat < 0.30f && topL > 0.50f && topRgb.b > topRgb.r - 0.02f))
        outdoor = 1.0f;
    float wSky = 0.70f * skyLike * above * outdoor;
    float wSil = 0.34f * sil * outdoor;
    float wTex = 0.22f * fol * outdoor;
    float wHz = 0.28f * hazeAmt * outdoor;
    float wSum = wCue.x + wCue.y + wCue.z + wCue.w + wP + wSky + wSil + wTex + wHz;
    float depth = (wCue.x * dHorizon + wCue.y * dContrast + wCue.z * dDefocus + wCue.w * dLuma + wP * dPar
                   + wSky * 1.0f + wSil * 0.14f + wTex * 0.22f + wHz * 0.95f) / max(wSum, 1e-4f);
    // Sky only here. Flat UI is decided at full res, where a saturated leaf is not a health bar.
    float skyP = (skyLike > 0.45f && uv.y < 0.72f && satC < 0.35f) ? 1.0f : 0.0f;
    gRaw[id.xy] = float4(saturate(depth), Lc, skyP, 0.0f);
}

[numthreads(8, 8, 1)]
void CSDepthBlur(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    int2 hi = int2(lightSize) - 1;
    float d[9], lum[9];
    [unroll] for (int k = 0; k < 9; ++k) {
        int2 o = int2(k % 3 - 1, k / 3 - 1);
        float4 s = gRawIn.Load(int3(clamp(int2(id.xy) + o, int2(0, 0), hi), 0));
        d[k] = s.x; lum[k] = s.y;
    }
    float acc = 0.0f, ws = 0.0f, sumL = 0.0f, sumL2 = 0.0f;
    float lc = lum[4];
    [unroll] for (int k3 = 0; k3 < 9; ++k3) {
        int2 o = int2(k3 % 3 - 1, k3 / 3 - 1);
        float w = (float)((2 - abs(o.x)) * (2 - abs(o.y))) * exp(-rangeK * (lum[k3] - lc) * (lum[k3] - lc));
        acc += w * d[k3]; ws += w;
        sumL += lum[k3]; sumL2 += lum[k3] * lum[k3];
    }
    float depth = acc / max(ws, 1e-4f);
    // Hook only. ssrtTemporal == 0 skips the history read (the later task owns the toggle).
    if (ssrtTemporal > 0.0f) {
        float h = gHist.Load(int3(id.xy, 0)).x;
        float keep = exp(-abs(h - depth) * 12.0f);
        depth = lerp(depth, h, keep * (1.0f - saturate(ssrtTemporal)));
    }
    // Column / row means of the 3x3, so a one-pixel spike does not flip the normal.
    float dzdx = (d[2] + d[5] + d[8]) * (1.0f / 3.0f) - (d[0] + d[3] + d[6]) * (1.0f / 3.0f);
    float dzdy = (d[6] + d[7] + d[8]) * (1.0f / 3.0f) - (d[0] + d[1] + d[2]) * (1.0f / 3.0f);
    float3 n = normalize(float3(dzdx * depthGrad, dzdy * depthGrad, 1.0f));
    float mean = sumL * (1.0f / 9.0f);
    float var = max(sumL2 * (1.0f / 9.0f) - mean * mean, 0.0f);
    // Near-horizontal floor: normal points clearly up the image (n.y < -0.45 when +y is down)
    // and is not a front-facing wall (n.z stays below ~0.55). A mild slope does not pass.
    float horizontal = saturate((-n.y - 0.40f) * 5.0f) * saturate((0.72f - n.z) * 4.0f);
    float brightG = saturate((lc - 0.12f) * glossBright);
    float smooth = saturate(1.0f - var * glossVarK) * horizontal * brightG;
    float protect = gRawIn.Load(int3(id.xy, 0)).z;
    if (protect > 0.5f) smooth = 0.0f;
    gDepth[id.xy] = float4(saturate(depth), n.x, n.y, ssrtPackSmooth(smooth, protect));
}
