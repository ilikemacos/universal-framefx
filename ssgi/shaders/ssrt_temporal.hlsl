// Temporal accumulation for SSRT. SM5.0 only.
// CSSsrtCut  : one group. Mean proxy brightness versus last frame. A large jump sets reset.
// CSSsrtAccum: reproject GI/AO, reflections, contact and pseudo-depth.
//   Motion SRV (temporalMode 1): FrameFX backward vector, xy in source pixels, z = confidence.
//   Pan only (temporalMode 2): 1x1 backward UV. Neither (mode 0) or forceReset writes the current frame.
// Rejection: 3x3 neighbourhood clamp, hard depth test, motion disagreement, confidence weight.
//
// Accum cost (light-res pixel): 3x3 of GI, depth, reflection and contact (36 Loads),
// bilinear history of the same four (16), two motion Loads, one pan Load. ~80 ALU.
// Writes one GI (8 B), depth (8 B), reflection (4 B) and contact (4 B) texel.
#include "ssgi_common.hlsli"
#include "ssrt_common.hlsli"

StructuredBuffer<uint> gGrid : register(t0);
Texture2D<float4> gCutPrev : register(t1);
RWTexture2D<float4> gCutOut : register(u0);

Texture2D<uint2>  gGi      : register(t2);
Texture2D<float4> gDepth   : register(t3);
Texture2D<float4> gRefl    : register(t4);
Texture2D<float>  gShadow  : register(t5);
Texture2D<uint2>  gGiH     : register(t6);
Texture2D<float4> gDepthH  : register(t7);
Texture2D<float4> gReflH   : register(t8);
Texture2D<float>  gShadowH : register(t9);
Texture2D<float4> gMotion  : register(t10);   // xy source pixels, z confidence
Texture2D<float2> gPan     : register(t11);   // 1x1 backward UV

RWTexture2D<uint2>  gGiO     : register(u1);
RWTexture2D<float4> gDepthO  : register(u2);
RWTexture2D<float4> gReflO   : register(u3);
RWTexture2D<float>  gShadowO : register(u4);

groupshared float gsCut[64];

[numthreads(64, 1, 1)]
void CSSsrtCut(uint gi : SV_GroupIndex)
{
    float acc = 0.0f;
    [loop] for (uint k = gi; k < (uint)(SSGI_GRID * SSGI_GRID); k += 64u)
        acc += (float)(gGrid[k] >> 24) * (1.0f / 255.0f);
    gsCut[gi] = acc;
    GroupMemoryBarrierWithGroupSync();
    if (gi == 0) {
        float sum = 0.0f;
        [unroll] for (uint i = 0; i < 64u; ++i) sum += gsCut[i];
        float4 prevC = gCutPrev.Load(int3(0, 0, 0));
        float mean = sum * (1.0f / (float)(SSGI_GRID * SSGI_GRID));
        float jump = abs(mean - prevC.y);
        // A real cut resets this frame and the next one, so the old picture cannot blend back in.
        // forceReset (first frame, resize, no vectors) does not arm that extra frame.
        float reset = 0.0f, cool = 0.0f;
        if (forceReset > 0.5f) reset = 1.0f;
        else if (jump > lumCut) { reset = 1.0f; cool = 1.0f; }
        else if (prevC.w > 0.5f) reset = 1.0f;
        gCutOut[uint2(0, 0)] = float4(reset, mean, jump, cool);
    }
}

float3 LinOf(uint2 p) { float3 q = ssgiLightRgb(p); return q * q; }

void Box3(int2 id, int2 hi, out float3 cmin, out float3 cmax, out float omin, out float omax,
          out float dmin, out float dmax, out float3 rmin, out float3 rmax, out float smin, out float smax)
{
    cmin = 1e9; cmax = -1e9; omin = 1e9; omax = -1e9;
    dmin = 1e9; dmax = -1e9; rmin = 1e9; rmax = -1e9; smin = 1e9; smax = -1e9;
    [unroll] for (int k = 0; k < 9; ++k) {
        int2 o = int2(k % 3 - 1, k / 3 - 1);
        int2 p = clamp(id + o, int2(0, 0), hi);
        uint2 g = gGi.Load(int3(p, 0));
        float3 lin = LinOf(g);
        float oc = ssgiLightOcc(g);
        cmin = min(cmin, lin); cmax = max(cmax, lin);
        omin = min(omin, oc); omax = max(omax, oc);
        float d = gDepth.Load(int3(p, 0)).x;
        dmin = min(dmin, d); dmax = max(dmax, d);
        float4 r = gRefl.Load(int3(p, 0));
        rmin = min(rmin, r.rgb); rmax = max(rmax, r.rgb);
        float s = gShadow.Load(int3(p, 0));
        smin = min(smin, s); smax = max(smax, s);
    }
}

float4 Bil4(Texture2D<float4> t, int2 h0, int2 h1, float2 fr)
{
    float4 s00 = t.Load(int3(h0, 0));
    float4 s10 = t.Load(int3(h1.x, h0.y, 0));
    float4 s01 = t.Load(int3(h0.x, h1.y, 0));
    float4 s11 = t.Load(int3(h1, 0));
    return lerp(lerp(s00, s10, fr.x), lerp(s01, s11, fr.x), fr.y);
}

float Bil1(Texture2D<float> t, int2 h0, int2 h1, float2 fr)
{
    float s00 = t.Load(int3(h0, 0));
    float s10 = t.Load(int3(h1.x, h0.y, 0));
    float s01 = t.Load(int3(h0.x, h1.y, 0));
    float s11 = t.Load(int3(h1, 0));
    return lerp(lerp(s00, s10, fr.x), lerp(s01, s11, fr.x), fr.y);
}

float3 BilGi(int2 h0, int2 h1, float2 fr, out float guide, out float occ)
{
    uint2 s00 = gGiH.Load(int3(h0, 0));
    uint2 s10 = gGiH.Load(int3(h1.x, h0.y, 0));
    uint2 s01 = gGiH.Load(int3(h0.x, h1.y, 0));
    uint2 s11 = gGiH.Load(int3(h1, 0));
    float3 l00 = LinOf(s00), l10 = LinOf(s10), l01 = LinOf(s01), l11 = LinOf(s11);
    float3 lin = lerp(lerp(l00, l10, fr.x), lerp(l01, l11, fr.x), fr.y);
    float g00 = ssgiLightGuide(s00), g10 = ssgiLightGuide(s10), g01 = ssgiLightGuide(s01), g11 = ssgiLightGuide(s11);
    guide = lerp(lerp(g00, g10, fr.x), lerp(g01, g11, fr.x), fr.y);
    float o00 = ssgiLightOcc(s00), o10 = ssgiLightOcc(s10), o01 = ssgiLightOcc(s01), o11 = ssgiLightOcc(s11);
    occ = lerp(lerp(o00, o10, fr.x), lerp(o01, o11, fr.x), fr.y);
    return lin;
}

[numthreads(8, 8, 1)]
void CSSsrtAccum(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lightSize.x || id.y >= lightSize.y) return;
    int2 ip = int2(id.xy);
    int2 hi = int2(lightSize) - 1;
    uint2 curG = gGi.Load(int3(ip, 0));
    float4 curD = gDepth.Load(int3(ip, 0));
    float4 curR = gRefl.Load(int3(ip, 0));
    float curS = gShadow.Load(int3(ip, 0));
    float reset = gCutPrev.Load(int3(0, 0, 0)).x;
    // gCutPrev here is the cut buffer just written (bound as t1 after the cut pass — see the pipeline).
    // The accum pass rebinds that SRV on t1. A reset or a missing vector copies the current sample.
    bool use = temporalMode != 0u && forceReset < 0.5f && reset < 0.5f;
    float2 mvPx = 0.0f;
    float conf = 1.0f;
    if (use && temporalMode == 1u) {
        uint mw, mh;
        gMotion.GetDimensions(mw, mh);
        float2 uv = ((float2)id.xy + 0.5f) * invLightSize;
        int2 mp = clamp(int2(uv * float2(mw, mh)), int2(0, 0), int2(mw, mh) - 1);
        float4 m = gMotion.Load(int3(mp, 0));
        mvPx = m.xy;
        conf = saturate(m.z);
    } else if (use && temporalMode == 2u) {
        mvPx = gPan.Load(int3(0, 0, 0)).xy * (float2)srcSize;
        conf = 1.0f;
    } else use = false;

    float2 offset = mvPx * (float2)lightSize / max((float2)srcSize, float2(1.0f, 1.0f));
    float2 hp = (float2)id.xy + offset;
    float2 fl = floor(hp);
    float2 fr = hp - fl;
    int2 h0 = int2(fl);
    int2 h1 = h0 + int2(1, 1);
    bool inside = all(h0 >= 0) && all(h0 <= hi);
    if (fr.x > 1e-6f) inside = inside && h1.x <= hi.x;
    if (fr.y > 1e-6f) inside = inside && h1.y <= hi.y;
    h0 = clamp(h0, int2(0, 0), hi);
    h1 = clamp(h1, int2(0, 0), hi);

    float keep = 0.0f;
    float3 hLin = LinOf(curG);
    float hOcc = ssgiLightOcc(curG);
    float hDepth = curD.x;
    float4 hRefl = curR;
    float hSh = curS;
    if (use && inside && conf > 0.02f) {
        float guideH, occH;
        hLin = BilGi(h0, h1, fr, guideH, occH);
        hOcc = occH;
        float4 hd = Bil4(gDepthH, h0, h1, fr);
        hDepth = hd.x;
        hRefl = Bil4(gReflH, h0, h1, fr);
        hSh = Bil1(gShadowH, h0, h1, fr);
        float3 cmin, cmax, rmin, rmax;
        float omin, omax, dmin, dmax, smin, smax;
        Box3(ip, hi, cmin, cmax, omin, omax, dmin, dmax, rmin, rmax, smin, smax);
        float b = saturate(clipBox);
        float3 clin = LinOf(curG);
        float3 lo = lerp(clin, cmin, b), hiC = lerp(clin, cmax, b);
        hLin = clamp(hLin, min(lo, hiC), max(lo, hiC));
        float olo = lerp(ssgiLightOcc(curG), omin, b), ohi = lerp(ssgiLightOcc(curG), omax, b);
        hOcc = clamp(hOcc, min(olo, ohi), max(olo, ohi));
        float dlo = lerp(curD.x, dmin, b), dhi = lerp(curD.x, dmax, b);
        hDepth = clamp(hDepth, min(dlo, dhi), max(dlo, dhi));
        float3 rlo = lerp(curR.rgb, rmin, b), rhi = lerp(curR.rgb, rmax, b);
        hRefl.rgb = clamp(hRefl.rgb, min(rlo, rhi), max(rlo, rhi));
        float slo = lerp(curS, smin, b), shi = lerp(curS, smax, b);
        hSh = clamp(hSh, min(slo, shi), max(slo, shi));
        keep = (1.0f - accumAlpha) * conf;
        if (abs(hDepth - curD.x) > depthReject) keep = 0.0f;
        if (temporalMode == 1u) {
            uint mw, mh;
            gMotion.GetDimensions(mw, mh);
            float2 huv = saturate((hp + 0.5f) * invLightSize);
            int2 mp = clamp(int2(huv * float2(mw, mh)), int2(0, 0), int2(mw, mh) - 1);
            float2 mvH = gMotion.Load(int3(mp, 0)).xy;
            if (length(mvPx - mvH) > mvRejectPx) keep = 0.0f;
        }
    }
    float3 outLin = lerp(LinOf(curG), hLin, keep);
    float outOcc = lerp(ssgiLightOcc(curG), hOcc, keep);
    float outD = lerp(curD.x, hDepth, keep);
    float4 outR = lerp(curR, hRefl, keep);
    float outS = lerp(curS, hSh, keep);
    gGiO[ip] = ssgiPackLight(sqrt(saturate(outLin)), ssgiLightGuide(curG), saturate(outOcc));
    gDepthO[ip] = float4(saturate(outD), curD.yzw);
    gReflO[ip] = saturate(outR);
    gShadowO[ip] = saturate(outS);
}
