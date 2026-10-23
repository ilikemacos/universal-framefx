// FrameFX.hlsl — Universal-FrameFX 1.2 GPU passes (Direct3D 11, cs_5_0).
// Each entry point is compiled separately at runtime with d3dcompiler_47.
//
// Temporal passes (FX_Dilate .. FX_Accumulate) adapt the algorithm of
// AMD FidelityFX Super Resolution 2.2; EASU / RCAS adapt AMD FidelityFX Super
// Resolution 1. Both are MIT licensed:
//
// FidelityFX Super Resolution 2.2
// =================================
// Copyright (c) 2022-2023 Advanced Micro Devices, Inc. All rights reserved.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// FSR 1: Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
// (same MIT permission text as above).
//
// Modifications Copyright (c) 2026 Chopsticks HQ. Not an AMD product.
//
// Honest adaptation notes: captured desktop frames have no depth buffer, no
// engine motion vectors and (except the built-in test scene) no jitter.
//  - Motion is estimated by hierarchical SAD block matching + parabolic
//    sub-pixel fit (FX_Motion*).
//  - Dilation uses "largest confident motion wins" instead of nearest depth.
//  - Disocclusion compares luma after reprojection instead of depth.
//  - Reactive mask is generated automatically (big luma change, ~no motion).

cbuffer Params : register(b0)
{
    float2 inSize;        // input (low-res) size, pixels
    float2 outSize;       // output size, pixels
    float2 jitter;        // this frame's jitter, input pixels
    float2 prevJitter;
    float2 jitterDelta;   // jitter - prevJitter
    float2 pan;           // test scene pan (uv)
    float  historyValid;
    float  sharpness;     // RCAS stops (0 = sharpest)
    float  maxAccum;
    float  jitterActive;
    int    searchRadius;
    int    applyJitterCancel;
    float  interpT;
    float  sensitivity;
    float  sceneTime;
    float  levelScale;
    float2 _pad;
    float  testHud;       // test scene: draw a static HUD overlay
    float  hwScale;       // hardware motion units per pixel (32 NVOF S10.5, 4 quarter-pel)
    float  hwGrid;        // pixels per hardware vector
    float  staticThresh;  // max luma change for "unchanged"
    float  tileSize;      // static tile size, input pixels
    float  staticOn;      // 1 = static tiles use the spatial path
    float2 _pad2;
    float4 fastP;         // Performance preset: x = sharpen amount, y = 1 force all tiles, z = final motion level scale (2^L), w = unused
    float4 fastQ;         // x = confidence SAD scale, y = history weight floor, z = static-prior SAD margin, w = max accumulated frames
    float4 fastR;         // frame generation: x = colour-consistency slope, y = weight of motion confidence, z = blend gate, w = unused
};

SamplerState linClamp : register(s0);
SamplerState pntClamp : register(s1);

static const float3 REC709 = float3(0.2126f, 0.7152f, 0.0722f);
float luma709(float3 c) { return dot(c, REC709); }
float safeRcp(float x) { return x == 0.0f ? 0.0f : 1.0f / x; }

float2 texSize(Texture2D<float4> t) { uint w, h; t.GetDimensions(w, h); return float2(w, h); }
float2 texSize1(Texture2D<float> t) { uint w, h; t.GetDimensions(w, h); return float2(w, h); }

//===================================================================== luma
Texture2D<float4>   gColor   : register(t0);
Texture2D<float>    gLumaIn  : register(t1);
RWTexture2D<float>  gLumaOut : register(u0);

[numthreads(8, 8, 1)]
void FX_LumaExtract(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    gLumaOut[id.xy] = luma709(gColor.Load(int3(id.xy, 0)).rgb);
}

[numthreads(8, 8, 1)]
void FX_LumaHalve(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    gLumaOut[id.xy] = gLumaIn.SampleLevel(linClamp, uv, 0);
}

//=================================================================== motion
// prev luma t2, curr luma t3, mv in t4 (rg16f, UV units), out u1
Texture2D<float>    gPrevL  : register(t2);
Texture2D<float>    gCurrL  : register(t3);
Texture2D<float4>   gMvIn   : register(t4);
RWTexture2D<float2> gMvOut  : register(u1);
RWTexture2D<float4> gMvPx   : register(u2);

float patchSAD(float2 uv, float2 mvUV, float2 texel)
{
    float sad = 0.0f;
    [unroll] for (int dy = -2; dy <= 2; ++dy)
    [unroll] for (int dx = -2; dx <= 2; ++dx) {
        float2 o = float2(dx, dy) * texel;
        float a = gPrevL.SampleLevel(linClamp, uv + o + mvUV, 0);
        float b = gCurrL.SampleLevel(linClamp, uv + o, 0);
        sad += abs(a - b);
    }
    return sad;
}

[numthreads(8, 8, 1)]
void FX_MotionInit(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float2 best = 0; float bestCost = patchSAD(uv, 0, texel);
    int r = searchRadius;
    for (int dy = -r; dy <= r; ++dy)
    for (int dx = -r; dx <= r; ++dx) {
        if (dx == 0 && dy == 0) continue;
        float2 mv = float2(dx, dy);
        float cost = patchSAD(uv, mv * texel, texel) + 0.002f * length(mv);
        if (cost < bestCost) { bestCost = cost; best = mv; }
    }
    gMvOut[id.xy] = best * texel;
}

[numthreads(8, 8, 1)]
void FX_MotionRefine(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float2 seed = gMvIn.SampleLevel(linClamp, uv, 0).xy;
    float2 best = seed; float bestCost = patchSAD(uv, seed, texel);
    float zc = patchSAD(uv, 0, texel);
    if (zc < bestCost) { bestCost = zc; best = 0; }
    int r = searchRadius;
    for (int dy = -r; dy <= r; ++dy)
    for (int dx = -r; dx <= r; ++dx) {
        if (dx == 0 && dy == 0) continue;
        float2 mv = seed + float2(dx, dy) * texel;
        // Deviation-from-seed penalty: periodic textures have many equally good
        // matches; stay with the coarse-level (unaliased) estimate on ties.
        float cost = patchSAD(uv, mv, texel) + 0.02f * length(float2(dx, dy));
        if (cost < bestCost) { bestCost = cost; best = mv; }
    }
    gMvOut[id.xy] = best;
}

[numthreads(8, 8, 1)]
void FX_MotionSmooth2(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float2 s = 0; float ws = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        float w = (dx == 0 && dy == 0) ? 4.0f : ((dx == 0 || dy == 0) ? 2.0f : 1.0f);
        s += gMvIn.SampleLevel(pntClamp, uv + float2(dx, dy) * texel, 0).xy * w; ws += w;
    }
    gMvOut[id.xy] = s / ws;
}

// UV motion -> input pixels, parabolic sub-pixel fit, jitter cancel, confidence.
[numthreads(8, 8, 1)]
void FX_MotionSubpixel(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvPx.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 size = float2(W, H);
    float2 texel = 1.0f / size;
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float4 seed = gMvIn.SampleLevel(pntClamp, uv, 0);
    float2 mvUV = seed.xy;
    float2 mvPx = mvUV * size;
    float c0  = patchSAD(uv, mvUV, texel);
    // Sub-pixel fit on half-pixel probes (SAD is V-shaped, so +-1 px probes on
    // asymmetric content invent offsets); keep it only if it really lowers cost.
    float2 hs = 0.5f * texel;
    float cxL = patchSAD(uv, mvUV + float2(-hs.x, 0), texel);
    float cxR = patchSAD(uv, mvUV + float2( hs.x, 0), texel);
    float cyU = patchSAD(uv, mvUV + float2(0, -hs.y), texel);
    float cyD = patchSAD(uv, mvUV + float2(0,  hs.y), texel);
    float denX = cxL - 2.0f * c0 + cxR, denY = cyU - 2.0f * c0 + cyD;
    float dx = denX > 1e-6f ? clamp(0.25f * (cxL - cxR) / denX, -0.5f, 0.5f) : 0.0f;
    float dy = denY > 1e-6f ? clamp(0.25f * (cyU - cyD) / denY, -0.5f, 0.5f) : 0.0f;
    float cSub = patchSAD(uv, mvUV + float2(dx, dy) * texel, texel);
    if (cSub < c0 - 0.002f) { mvPx += float2(dx, dy); c0 = cSub; }
    // Static prior: content that only moved by the camera jitter (or not at
    // all) snaps to exactly that vector - desktops and HUDs are mostly static.
    float2 staticPx = applyJitterCancel != 0 ? jitterDelta : float2(0, 0);
    float cs = patchSAD(uv, staticPx * texel, texel);
    if (cs <= c0 + 0.01f) { mvPx = staticPx; c0 = min(c0, cs); }
    // Pixel p samples the scene at p + 0.5 + jitter, so static content appears
    // to move by -(jitterDelta); the backward vector of a static feature is
    // +jitterDelta. Remove it (jitter-cancelled vectors, as the vendor upscalers expect).
    if (applyJitterCancel != 0) mvPx -= jitterDelta;
    // seed.w: forward/backward consistency from hardware motion (1 for the
    // software estimator, whose RG16F seed reads w = 1).
    float conf = saturate(1.0f - c0 / 8.0f) * saturate(seed.w);
    if (_pad.x > 0.5f) { mvPx = 0; conf = 1.0f; }   // diagnostic oracle: known-static scene
    gMvPx[id.xy] = float4(mvPx, conf, 1.0f);
}

[numthreads(8, 8, 1)]
void FX_MotionSmooth4(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvPx.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float3 s = 0; float ws = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        float w = (dx == 0 && dy == 0) ? 4.0f : ((dx == 0 || dy == 0) ? 2.0f : 1.0f);
        s += gMvIn.SampleLevel(pntClamp, uv + float2(dx, dy) * texel, 0).xyz * w; ws += w;
    }
    gMvPx[id.xy] = float4(s / ws, 1.0f);
}


//========================================================= hardware motion
// Import block vectors from NVIDIA Optical Flow / D3D12 Video Motion
// Estimation. t20 = primary grid (input frame -> reference frame), t21 = the
// opposite direction, t2 = reference luma, t3 = input luma. For each pixel the
// best of its own and neighbouring block vectors (by patch SAD) is kept, which
// sharpens block edges; w = forward/backward consistency.
Texture2D<int2>     tHwA   : register(t20);
Texture2D<int2>     tHwB   : register(t21);
RWTexture2D<float4> uSeed  : register(u6);

float2 hwVec(Texture2D<int2> t, int2 blk)
{
    uint w, h; t.GetDimensions(w, h);
    blk = clamp(blk, int2(0, 0), int2(w - 1, h - 1));
    return float2(t.Load(int3(blk, 0))) / hwScale;
}

[numthreads(8, 8, 1)]
void FX_HwImport(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uSeed.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 size = float2(W, H), texel = 1.0f / size;
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    int g = max(1, (int)hwGrid);
    int2 blk = int2(id.xy) / g;
    // Candidates: own block, 8 neighbouring blocks, zero, and the coarse
    // software prior (t4, UV units; _pad2.x = sign for the direction). A small
    // magnitude penalty rejects periodic-texture aliases the hardware can lock onto.
    const float lam = 0.001f;
    float2 best = hwVec(tHwA, blk);
    float bestCost = patchSAD(uv, best * texel, texel) + lam * length(best);
    const int2 nb[12] = { int2(-1,0), int2(1,0), int2(0,-1), int2(0,1), int2(-1,-1), int2(1,-1), int2(-1,1), int2(1,1),
                          int2(-3,0), int2(3,0), int2(0,-3), int2(0,3) };
    [unroll] for (int i = 0; i < 12; ++i) {
        float2 v = hwVec(tHwA, blk + nb[i]);
        float c = patchSAD(uv, v * texel, texel) + 0.004f + lam * length(v);
        if (c < bestCost) { bestCost = c; best = v; }
    }
    float2 coarse = _pad2.x * gMvIn.SampleLevel(linClamp, uv, 0).xy * size;
    float cc = patchSAD(uv, coarse * texel, texel) + 0.004f + lam * length(coarse);
    if (cc < bestCost) { bestCost = cc; best = coarse; }
    float cz = patchSAD(uv, 0, texel) + 0.004f;
    if (cz < bestCost) { bestCost = cz; best = 0; }
    // Consistency: the opposite-direction vector at the matched position
    // should point back here.
    float2 q = float2(id.xy) + 0.5f + best;
    float2 back = hwVec(tHwB, int2(floor(q)) / g);
    float err = length(best + back);
    float cons = 1.0f / (1.0f + 0.5f * err * err);
    uSeed[id.xy] = float4(best * texel, 0.0f, cons);
}

// Software path: forward field (prev frame -> curr frame, UV units) from the
// final backward field by fixed-point inversion. t4 = backward (px, conf).
[numthreads(8, 8, 1)]
void FX_FwdFromBwd(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uSeed.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float4 m = gMvIn.SampleLevel(linClamp, uv, 0);
    float2 f = -m.xy * texel;
    [unroll] for (int i = 0; i < 3; ++i) { m = gMvIn.SampleLevel(linClamp, uv + f, 0); f = -m.xy * texel; }
    float err = length((gMvIn.SampleLevel(linClamp, uv + f, 0).xy * texel + f) * float2(W, H));
    uSeed[id.xy] = float4(f, 0.0f, saturate(m.z) / (1.0f + 0.5f * err * err));
}

// 8-bit luma for the hardware engines. t1 = luma (R16F), u0 = R8_UNORM.
[numthreads(8, 8, 1)]
void FX_Luma8(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    gLumaOut[id.xy] = saturate(gLumaIn.Load(int3(id.xy, 0)));
}

//============================================================ temporal (FSR2)
Texture2D<float4>   tMotion   : register(t4);   // input res, rg = px (backward), b = conf
Texture2D<float>    tDisoccl  : register(t5);
Texture2D<float>    tReactive : register(t6);
Texture2D<float4>   tLockIn   : register(t7);
Texture2D<float4>   tNewLocks : register(t8);
Texture2D<float4>   tHistory  : register(t9);
RWTexture2D<float4> uOut4     : register(u3);
RWTexture2D<float>  uOut1     : register(u4);
RWTexture2D<float4> uOut4b    : register(u5);

[numthreads(8, 8, 1)]
void FX_Dilate(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    float bestScore = -1.0f; float4 best = tMotion.Load(int3(p, 0));
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        float4 s = tMotion.Load(int3(clamp(p + int2(dx, dy), 0, hi), 0));
        float score = length(s.xy) * (0.25f + 0.75f * saturate(s.z));
        if (score > bestScore) { bestScore = score; best = s; }
    }
    uOut4[id.xy] = best;
}

[numthreads(8, 8, 1)]
void FX_Disocclusion(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut1.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float4 mv = tMotion.SampleLevel(pntClamp, uv, 0);
    float2 prevUV = uv + mv.xy / inSize;
    float curr = gCurrL.SampleLevel(pntClamp, uv, 0);
    float prev = gPrevL.SampleLevel(linClamp, prevUV, 0);
    float lo = curr, hi = curr;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        float n = gCurrL.SampleLevel(pntClamp, uv + float2(dx, dy) * texel, 0);
        lo = min(lo, n); hi = max(hi, n);
    }
    float range = max(hi - lo, 1e-4f);
    float mask = saturate((abs(curr - prev) - range) / 0.18f);
    mask = max(mask, saturate(1.0f - mv.z) * 0.65f);
    float nLen = length(mv.xy);
    if (nLen > 0.35f) {
        float minConv = 1.0f;
        [unroll] for (int dy2 = -1; dy2 <= 1; ++dy2)
        [unroll] for (int dx2 = -1; dx2 <= 1; ++dx2) {
            if (dx2 == 0 && dy2 == 0) continue;
            float2 nb = tMotion.SampleLevel(pntClamp, uv + float2(dx2, dy2) * texel, 0).xy;
            minConv = min(minConv, dot(nb / max(length(nb), 1e-4f), mv.xy / nLen));
        }
        mask = max(mask, saturate(1.0f - minConv) * saturate(nLen / 4.0f));
    }
    if (any(prevUV < 0.0f) || any(prevUV > 1.0f)) mask = 1.0f;
    uOut1[id.xy] = mask;
}

[numthreads(8, 8, 1)]
void FX_Reactive(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut1.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float4 mv = tMotion.SampleLevel(pntClamp, uv, 0);
    float curr = gCurrL.SampleLevel(pntClamp, uv, 0);
    float prev = gPrevL.SampleLevel(linClamp, uv + mv.xy / inSize, 0);
    float speed = length(mv.xy);
    uOut1[id.xy] = saturate((abs(curr - prev) - 0.07f) / 0.18f) * saturate(1.0f - speed / 0.45f);
}

// Thin-feature (1 px ridge) detection on current luma — simplified ffx_fsr2_lock.
[numthreads(8, 8, 1)]
void FX_LockDetect(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    float nucleus = gCurrL.Load(int3(p, 0));
    float dMin = 1e20f, dMax = 0.0f;
    uint mask = 1u << 4;
    int idx = 0;
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x) {
        if (!(x == 0 && y == 0)) {
            float s = gCurrL.Load(int3(clamp(p + int2(x, y), 0, hi), 0));
            float ratio = max(s, nucleus) / max(min(s, nucleus), 1e-6f);
            if (ratio < 1.05f) mask |= (1u << idx);
            else { dMin = min(dMin, s); dMax = max(dMax, s); }
        }
        idx++;
    }
    bool ridge = (nucleus > dMax) || (nucleus < dMin);
    const uint r0 = (1u<<0)|(1u<<1)|(1u<<3)|(1u<<4);
    const uint r1 = (1u<<1)|(1u<<2)|(1u<<4)|(1u<<5);
    const uint r2 = (1u<<3)|(1u<<4)|(1u<<6)|(1u<<7);
    const uint r3 = (1u<<4)|(1u<<5)|(1u<<7)|(1u<<8);
    if (((mask & r0) == r0) || ((mask & r1) == r1) || ((mask & r2) == r2) || ((mask & r3) == r3)) ridge = false;
    uOut4[id.xy] = ridge ? float4(1.0f, nucleus, 0, 1) : float4(0, 0, 0, 0);
}

// Output-res lock history: reproject, refresh, decay, kill on disocclusion/reactive.
[numthreads(8, 8, 1)]
void FX_LockUpdate(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / outSize;
    float4 mv = tMotion.SampleLevel(linClamp, uv, 0);
    float2 prevUV = uv + mv.xy / inSize;
    float2 lk = 0;
    if (historyValid > 0.5f && all(prevUV >= 0.0f) && all(prevUV <= 1.0f))
        lk = tLockIn.SampleLevel(linClamp, prevUV, 0).rg;
    float4 nl = tNewLocks.SampleLevel(linClamp, uv, 0);
    if (nl.r > 0.5f) { lk.x = lk.x > 0.0f ? 2.0f : 1.0f; lk.y = nl.g; }
    lk.x *= 1.0f - saturate(tDisoccl.SampleLevel(linClamp, uv, 0));
    lk.x *= 1.0f - saturate(tReactive.SampleLevel(linClamp, uv, 0));
    lk.x = max(lk.x - 0.12f, 0.0f);
    if (lk.x <= 0.0f) lk = 0;
    uOut4[id.xy] = float4(lk, 0, 1);
}

float3 rgbToYCoCg(float3 c) {
    return float3(0.25f*c.r + 0.5f*c.g + 0.25f*c.b, 0.5f*c.r - 0.5f*c.b, -0.25f*c.r + 0.5f*c.g - 0.25f*c.b);
}
float3 yCoCgToRGB(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }
float3 tonemapF(float3 c) { return c / (max(max(c.r, c.g), max(c.b, 0.0f)) + 1.0f); }
float3 inverseTonemapF(float3 c) { return c / max(1.0f - max(max(c.r, c.g), c.b), 1.0f / 65504.0f); }
float lanczos2ApproxSq(float x2) {
    x2 = min(x2, 4.0f);
    float a = (2.0f / 5.0f) * x2 - 1.0f;
    float b = (1.0f / 4.0f) * x2 - 1.0f;
    return ((25.0f / 16.0f) * a * a - (25.0f / 16.0f - 1.0f)) * (b * b);
}
float catmullRom(float x) {
    x = abs(x);
    if (x <= 1.0f) return (1.5f * x - 2.5f) * x * x + 1.0f;
    if (x <= 2.0f) return ((-0.5f * x + 2.5f) * x - 4.0f) * x + 2.0f;
    return 0.0f;
}
float3 clipToBox(float3 target, float3 bMin, float3 bMax) {
    float3 c = 0.5f * (bMax + bMin), e = 0.5f * (bMax - bMin) + 1e-6f;
    float3 o = target - c;
    float3 u = abs(o / e);
    float m = max(u.x, max(u.y, u.z));
    return m > 1.0f ? c + o / m : target;
}
float4 historyCatmullRom(float2 uv) {
    float2 size = outSize;
    float2 pos = uv * size - 0.5f;
    float2 f = frac(pos), i = floor(pos);
    float wx[4], wy[4];
    [unroll] for (int k = 0; k < 4; ++k) { wx[k] = catmullRom(float(k - 1) - f.x); wy[k] = catmullRom(float(k - 1) - f.y); }
    float4 acc = 0; float ws = 0;
    int2 hi = int2(size) - 1;
    [unroll] for (int y = 0; y < 4; ++y)
    [unroll] for (int x = 0; x < 4; ++x) {
        float w = wx[x] * wy[y];
        int2 tp = clamp(int2(i) + int2(x - 1, y - 1), 0, hi);
        acc += tHistory.Load(int3(tp, 0)) * w; ws += w;
    }
    return acc / max(ws, 1e-6f);
}

// Accumulate: jitter-aware Lanczos-2 upsample + Catmull-Rom history
// reprojection + YCoCg variance rectification + lock-softened clip + weighted
// blend. t0 = current colour (input res). Writes history (u3) and colour (u5).
Texture2D<float>  tStaticTiles : register(t11);
Texture2D<float4> tSpatial     : register(t12);

[numthreads(8, 8, 1)]
void FX_Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)outSize.x || id.y >= (uint)outSize.y) return;
    float2 hrUV = (float2(id.xy) + 0.5f) / outSize;
    // Static tiles (unchanged text/UI): take the crisp spatial result and keep
    // it as history; temporal accumulation only runs where things move.
    float stat = staticOn > 0.5f ? saturate(tStaticTiles.SampleLevel(linClamp, hrUV, 0)) : 0.0f;
    float3 spatialRGB = stat > 0.0f ? tSpatial.Load(int3(id.xy, 0)).rgb : float3(0, 0, 0);
    if (stat >= 0.999f) {
        uOut4[id.xy] = float4(spatialRGB, 1.0f);
        uOut4b[id.xy] = float4(spatialRGB, 1.0f);
        return;
    }
    int2 inI = int2(inSize);
    float4 mv = tMotion.SampleLevel(linClamp, hrUV, 0);
    float dclip = tDisoccl.SampleLevel(linClamp, hrUV, 0);
    float react = tReactive.SampleLevel(linClamp, hrUV, 0);
    float lockS = saturate(tLockIn.SampleLevel(linClamp, hrUV, 0).r);
    float conf = saturate(mv.z);
    float speed = length(mv.xy);

    float2 srcPos = (float2(id.xy) + 0.5f) * inSize / outSize;
    int2 srcPx = int2(floor(srcPos));
    float kernelReactive = max(max(react, dclip), historyValid < 0.5f ? 1.0f : 0.0f);
    float down = max(inSize.x / outSize.x, 1e-4f);
    float kbMax = min(1.99f, 1.0f + (1.0f / down - 1.0f));
    float kbMin = max(1.0f, (1.0f + kbMax) * 0.3f);
    float kernelBias = lerp(kbMax, kbMin, saturate(max(0.25f * dclip, kernelReactive)));
    // No jitter (captured frames): samples never change phase, so use a plain
    // Lanczos-2 footprint at input scale instead of the narrowed jitter kernel.
    if (jitterActive < 0.5f) kernelBias = _pad.y > 0.0f ? _pad.y : 1.0f;

    float3 accCol = 0, m1 = 0, m2 = 0;
    float accW = 0, maxW = 0, boxW = 0;
    float3 aMin = 1e20f, aMax = -1e20f;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        int2 ip = srcPx + int2(dx, dy);
        bool on = ip.x >= 0 && ip.y >= 0 && ip.x < inI.x && ip.y < inI.y;
        int2 ic = clamp(ip, 0, inI - 1);
        float3 ycc = rgbToYCoCg(tonemapF(gColor.Load(int3(ic, 0)).rgb));
        // Source pixel ip was sampled at scene position ip + 0.5 + jitter.
        float2 off = (float2(ip) + 0.5f + jitter) - srcPos;
        float2 ob = off * kernelBias;
        float w = on ? lanczos2ApproxSq(dot(ob, ob)) : 0.0f;
        accCol += ycc * w; accW += w; maxW = max(maxW, w);
        if (on) {
            float bw = exp(-2.0f * dot(off, off));
            aMin = min(aMin, ycc); aMax = max(aMax, ycc);
            m1 += ycc * bw; m2 += ycc * ycc * bw; boxW += bw;
        }
    }
    float3 upY;
    float cover;
    if (accW > 1e-3f) {
        upY = clamp(accCol / accW, aMin, aMax);   // dering
        cover = clamp(maxW, 0.05f, 1.0f);           // new-sample weight
    } else {
        upY = rgbToYCoCg(tonemapF(gColor.SampleLevel(linClamp, hrUV - jitter / inSize, 0).rgb));
        cover = 0.05f;
    }
    boxW = max(boxW, 1e-3f);
    float3 mean = m1 / boxW;
    float3 sigma = sqrt(max(m2 / boxW - mean * mean, 0.0f));
    float3 bMin = max(aMin, mean - 1.25f * sigma);
    float3 bMax = min(aMax, mean + 1.25f * sigma);

    float2 histUV = hrUV + mv.xy / inSize;
    bool offscreen = any(histUV < 0.0f) || any(histUV > 1.0f);
    bool fresh = historyValid < 0.5f || offscreen;
    float3 histRGB = 0; float histW = 0;
    if (!fresh) { float4 h = historyCatmullRom(histUV); histRGB = h.rgb; histW = max(h.a, 0.0f); }
    float3 histY = rgbToYCoCg(tonemapF(saturate(histRGB)));
    histY = lerp(clipToBox(histY, bMin, bMax), histY, lockS * 0.85f);

    float w = histW;
    w *= 1.0f - saturate(dclip);
    w *= 1.0f - saturate(react);
    w *= lerp(0.15f, 1.0f, conf);
    w *= exp2(-0.12f * speed);
    if (fresh) w = 0.0f;
    w = min(w, maxAccum);
    float acc = min(maxAccum, w + cover);
    float alpha = fresh ? 1.0f : cover / max(acc, 1e-5f);
    float3 outRGB = saturate(inverseTonemapF(yCoCgToRGB(lerp(histY, upY, alpha))));
    if (fresh) acc = cover;
    uOut4[id.xy] = float4(outRGB, acc);
    uOut4b[id.xy] = float4(lerp(outRGB, spatialRGB, stat), 1.0f);
}

//=========================================================== static areas
// Per-pixel "unchanged" counter (u7, R32F, in place) and static-HUD mask (u4):
// a pixel is HUD/overlay if it has not changed for 3+ frames and has detail.
RWTexture2D<float> uCount : register(u7);
Texture2D<float>   tCount : register(t10);

[numthreads(8, 8, 1)]
void FX_StaticCount(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uCount.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy);
    float d = abs(gCurrL.Load(int3(p, 0)) - gPrevL.Load(int3(p, 0)));
    float n = uCount[id.xy];
    uCount[id.xy] = (historyValid > 0.5f && d <= staticThresh) ? min(n + 1.0f, 64.0f) : 0.0f;
}

// Static HUD/overlay mask (u4): the whole 5x5 neighbourhood unchanged for 3+
// frames (so flat pixels next to moving edges do not qualify) and real detail.

[numthreads(8, 8, 1)]
void FX_HudMask(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut1.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    float mn = 64.0f, lo = 1e9f, hiL = -1e9f;
    [unroll] for (int dy = -2; dy <= 2; ++dy)
    [unroll] for (int dx = -2; dx <= 2; ++dx) {
        int2 q = clamp(p + int2(dx, dy), 0, hi);
        mn = min(mn, tCount.Load(int3(q, 0)));
        if (abs(dx) <= 1 && abs(dy) <= 1) { float s = gCurrL.Load(int3(q, 0)); lo = min(lo, s); hiL = max(hiL, s); }
    }
    uOut1[id.xy] = (mn >= 3.0f && hiL - lo > 0.06f) ? 1.0f : 0.0f;
}

// One thread per tile: 1 if every pixel of the tile is unchanged for 2+ frames.

[numthreads(8, 8, 1)]
void FX_StaticTiles(uint3 id : SV_DispatchThreadID)
{
    uint TW, TH; uOut1.GetDimensions(TW, TH);
    if (id.x >= TW || id.y >= TH) return;
    uint W, H; tCount.GetDimensions(W, H);
    int ts = max(4, (int)tileSize);
    int2 o = int2(id.xy) * ts;
    float mn = 64.0f;
    for (int y = 0; y < ts; ++y)
    for (int x = 0; x < ts; ++x) {
        int2 q = o + int2(x, y);
        if (q.x < (int)W && q.y < (int)H) mn = min(mn, tCount.Load(int3(q, 0)));
    }
    uOut1[id.xy] = mn >= 2.0f ? 1.0f : 0.0f;
}


//================================================================= spatial
// EASU (FSR 1) with 12 taps; RCAS; bilinear. Input t0, output u3.
float edgeLuma(float3 c) { return c.g + 0.5f * (c.r + c.b); }

void easuSet(inout float2 dir, inout float len, float2 pp, int corner,
             float lA, float lB, float lC, float lD, float lE)
{
    float w = 0.0f;
    if (corner == 0) w = (1.0f - pp.x) * (1.0f - pp.y);
    else if (corner == 1) w = pp.x * (1.0f - pp.y);
    else if (corner == 2) w = (1.0f - pp.x) * pp.y;
    else w = pp.x * pp.y;
    float dc = lD - lC, cb = lC - lB;
    float lenX = max(abs(dc), abs(cb)); lenX = safeRcp(lenX);
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(abs(dirX) * lenX); lenX *= lenX;
    len += lenX * w;
    float ec = lE - lC, ca = lC - lA;
    float lenY = max(abs(ec), abs(ca)); lenY = safeRcp(lenY);
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(abs(dirY) * lenY); lenY *= lenY;
    len += lenY * w;
}

void easuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len2, float lob, float clp, float3 c)
{
    float2 v = float2(off.x * dir.x + off.y * dir.y, off.x * (-dir.y) + off.y * dir.x);
    v *= len2;
    float d2 = min(dot(v, v), clp);
    float wB = (2.0f / 5.0f) * d2 - 1.0f;
    float wA = lob * d2 - 1.0f;
    wB *= wB; wA *= wA;
    wB = (25.0f / 16.0f) * wB - (25.0f / 16.0f - 1.0f);
    float w = wB * wA;
    aC += c * w; aW += w;
}

float3 tapIn(int2 p) { int2 hi = int2(inSize) - 1; return gColor.Load(int3(clamp(p, 0, hi), 0)).rgb; }

[numthreads(8, 8, 1)]
void FX_EASU(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)outSize.x || id.y >= (uint)outSize.y) return;
    float2 pp = (float2(id.xy) + 0.5f) * (inSize / outSize) - 0.5f;
    float2 fp = floor(pp); pp -= fp;
    int2 f0 = int2(fp);
    float3 bC = tapIn(f0 + int2(0,-1)), cC = tapIn(f0 + int2(1,-1));
    float3 eC = tapIn(f0 + int2(-1,0)), fC = tapIn(f0), gC = tapIn(f0 + int2(1,0)), hC = tapIn(f0 + int2(2,0));
    float3 iC = tapIn(f0 + int2(-1,1)), jC = tapIn(f0 + int2(0,1)), kC = tapIn(f0 + int2(1,1)), lC = tapIn(f0 + int2(2,1));
    float3 nC = tapIn(f0 + int2(0,2)), oC = tapIn(f0 + int2(1,2));
    float bL = edgeLuma(bC), cL = edgeLuma(cC), eL = edgeLuma(eC), fL = edgeLuma(fC), gL = edgeLuma(gC), hL = edgeLuma(hC);
    float iL = edgeLuma(iC), jL = edgeLuma(jC), kL = edgeLuma(kC), lL = edgeLuma(lC), nL = edgeLuma(nC), oL = edgeLuma(oC);
    float2 dir = 0; float len = 0;
    easuSet(dir, len, pp, 0, bL, eL, fL, gL, jL);
    easuSet(dir, len, pp, 1, cL, fL, gL, hL, kL);
    easuSet(dir, len, pp, 2, fL, iL, jL, kL, nL);
    easuSet(dir, len, pp, 3, gL, jL, kL, lL, oL);
    float dirR = dot(dir, dir);
    bool zro = dirR < (1.0f / 32768.0f);
    dirR = zro ? 1.0f : rsqrt(dirR);
    dir.x = zro ? 1.0f : dir.x;
    dir *= dirR;
    len = len * 0.5f; len *= len;
    float stretch = dot(dir, dir) * safeRcp(max(abs(dir.x), abs(dir.y)));
    float2 len2 = float2(1.0f + (stretch - 1.0f) * len, 1.0f - 0.5f * len);
    float lob = 0.5f + ((1.0f / 4.0f - 0.04f) - 0.5f) * len;
    float clp = safeRcp(lob);
    float3 aC = 0; float aW = 0;
    easuTap(aC, aW, float2( 0,-1) - pp, dir, len2, lob, clp, bC);
    easuTap(aC, aW, float2( 1,-1) - pp, dir, len2, lob, clp, cC);
    easuTap(aC, aW, float2(-1, 1) - pp, dir, len2, lob, clp, iC);
    easuTap(aC, aW, float2( 0, 1) - pp, dir, len2, lob, clp, jC);
    easuTap(aC, aW, float2( 0, 0) - pp, dir, len2, lob, clp, fC);
    easuTap(aC, aW, float2(-1, 0) - pp, dir, len2, lob, clp, eC);
    easuTap(aC, aW, float2( 1, 1) - pp, dir, len2, lob, clp, kC);
    easuTap(aC, aW, float2( 2, 1) - pp, dir, len2, lob, clp, lC);
    easuTap(aC, aW, float2( 2, 0) - pp, dir, len2, lob, clp, hC);
    easuTap(aC, aW, float2( 1, 0) - pp, dir, len2, lob, clp, gC);
    easuTap(aC, aW, float2( 1, 2) - pp, dir, len2, lob, clp, oC);
    easuTap(aC, aW, float2( 0, 2) - pp, dir, len2, lob, clp, nC);
    float3 mn4 = min(min(fC, gC), min(jC, kC));
    float3 mx4 = max(max(fC, gC), max(jC, kC));
    uOut4[id.xy] = float4(clamp(aC * safeRcp(aW), mn4, mx4), 1.0f);
}

[numthreads(8, 8, 1)]
void FX_RCAS(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut4.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    int2 p = int2(id.xy), hi = int2(w - 1, h - 1);
    float3 b = gColor.Load(int3(clamp(p + int2(0,-1), 0, hi), 0)).rgb;
    float3 d = gColor.Load(int3(clamp(p + int2(-1,0), 0, hi), 0)).rgb;
    float3 e = gColor.Load(int3(p, 0)).rgb;
    float3 f = gColor.Load(int3(clamp(p + int2(1,0), 0, hi), 0)).rgb;
    float3 hh = gColor.Load(int3(clamp(p + int2(0,1), 0, hi), 0)).rgb;
    float bL = b.g, dL = d.g, eL = e.g, fL = f.g, hL = hh.g;
    float nz = 0.25f * (bL + dL + fL + hL) - eL;
    float range = max(max(max(bL, dL), max(eL, fL)), hL) - min(min(min(bL, dL), min(eL, fL)), hL);
    nz = saturate(abs(nz) * safeRcp(range));
    nz = 1.0f - 0.5f * nz;
    float3 mn4 = min(min(b, d), min(f, hh));
    float3 mx4 = max(max(b, d), max(f, hh));
    float3 hitMin = mn4 / max(4.0f * mx4, 1e-6f);
    float3 hitMax = (1.0f - mx4) / min(4.0f * mn4 - 4.0f, -1e-6f);
    float3 lobeRGB = max(-hitMin, hitMax);
    const float kLimit = 0.25f - (1.0f / 16.0f);
    float lobe = max(-kLimit, min(max(max(lobeRGB.r, lobeRGB.g), lobeRGB.b), 0.0f));
    lobe *= exp2(-sharpness) * nz;
    float3 o = (lobe * (b + d + f + hh) + e) * safeRcp(4.0f * lobe + 1.0f);
    uOut4[id.xy] = float4(saturate(o), 1.0f);
}

[numthreads(8, 8, 1)]
void FX_Bilinear(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut4.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    uOut4[id.xy] = float4(gColor.SampleLevel(linClamp, uv, 0).rgb, 1.0f);
}

//========================================================== vendor-upscaler inputs
// Captured colour -> RGBA8 at render size (shared with the D3D12 vendor runtimes).
[numthreads(8, 8, 1)]
void FX_CopyIn(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut4.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    uOut4[id.xy] = float4(gColor.Load(int3(id.xy, 0)).rgb, 1.0f);
}

// FrameFX motion (pixels, current -> previous) -> RG16F motion-vector texture.
[numthreads(8, 8, 1)]
void FX_PackMv(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gMvOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    gMvOut[id.xy] = gMvIn.SampleLevel(linClamp, (float2(id.xy) + 0.5f) / float2(w, h), 0).xy;
}

//========================================================== frame generation
// FrameFX's own optical-flow interpolation (NOT AMD FSR 3 code).
// t0 = previous output, t9 = current output (bound as tHistory slot), t4 = motion.
[numthreads(8, 8, 1)]
void FX_Interpolate(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float2 v = tMotion.SampleLevel(linClamp, uv, 0).xy / inSize;   // backward, uv
    float3 a = gColor.SampleLevel(linClamp, uv + v * interpT, 0).rgb;
    float3 b = tHistory.SampleLevel(linClamp, uv - v * (1.0f - interpT), 0).rgb;
    float conf = saturate(1.0f - length(a - b) * lerp(2.0f, 8.0f, sensitivity));
    float3 warped = lerp(a, b, interpT);
    float3 plain = lerp(gColor.SampleLevel(linClamp, uv, 0).rgb, tHistory.SampleLevel(linClamp, uv, 0).rgb, interpT);
    uOut4[id.xy] = float4(lerp(plain, warped, conf), 1.0f);
}


// 1.2 frame generation. t0 = previous output (A), t9 = current output (B),
// t4 = backward motion at B (input px, z = confidence), t13 = forward motion
// at A (UV, w = confidence), t14 = static-HUD mask (input res).
// Two candidates (warp along B's backward field, warp along A's forward
// field). A candidate whose far end disagrees with the opposite field is an
// occlusion: only its near-side colour is used. Low confidence falls back to
// a plain blend; static HUD/overlay pixels are never warped.
Texture2D<float4> tFwd : register(t13);
Texture2D<float>  tHud : register(t14);

bool inside(float2 p) { return all(p >= 0.0f) && all(p <= 1.0f); }
Texture2D<float> tChg : register(t16);   // FX_ChangeDetect result: [0] changed samples, [1] strongly changed samples
bool sceneCut() { return tChg.Load(int3(1, 0, 0)) > 0.6f * 4096.0f; }

[numthreads(8, 8, 1)]
void FX_Interpolate2(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float t = interpT;
    float3 a0 = gColor.SampleLevel(linClamp, uv, 0).rgb;
    float3 b0 = tHistory.SampleLevel(linClamp, uv, 0).rgb;
    float3 plain = lerp(a0, b0, t);

    float2 it = 1.0f / inSize;
    float hud = tHud.SampleLevel(linClamp, uv, 0);
    hud = max(hud, max(tHud.SampleLevel(linClamp, uv + float2(it.x, 0), 0), tHud.SampleLevel(linClamp, uv - float2(it.x, 0), 0)));
    hud = max(hud, max(tHud.SampleLevel(linClamp, uv + float2(0, it.y), 0), tHud.SampleLevel(linClamp, uv - float2(0, it.y), 0)));
    if (hud > 0.25f && _pad2.y < 0.5f) { uOut4[id.xy] = float4(b0, 1.0f); return; }
    if (sceneCut() && _pad2.y < 0.5f) { uOut4[id.xy] = float4(t < 0.5f ? a0 : b0, 1.0f); return; }   // 1.3.4: scene cut -> nearest real frame

    // Candidate B: backward field, fixed-point lookup of the source pixel.
    float4 mb = tMotion.SampleLevel(linClamp, uv, 0);
    float2 vb = mb.xy * it;
    [unroll] for (int i = 0; i < 2; ++i) { mb = tMotion.SampleLevel(linClamp, uv - (1.0f - t) * vb, 0); vb = mb.xy * it; }
    float2 pB = uv - (1.0f - t) * vb, pAb = pB + vb;
    float3 bB = tHistory.SampleLevel(linClamp, pB, 0).rgb;
    float3 bA = gColor.SampleLevel(linClamp, pAb, 0).rgb;
    float errB = length((tFwd.SampleLevel(linClamp, pAb, 0).xy + vb) * inSize);
    bool consB = errB < 1.0f && inside(pAb) && tHud.SampleLevel(linClamp, pAb, 0) < 0.5f;

    // Candidate A: forward field.
    float4 mf = tFwd.SampleLevel(linClamp, uv, 0);
    float2 vf = mf.xy;
    [unroll] for (int j = 0; j < 2; ++j) { mf = tFwd.SampleLevel(linClamp, uv - t * vf, 0); vf = mf.xy; }
    float2 pA = uv - t * vf, pBa = pA + vf;
    float3 aA = gColor.SampleLevel(linClamp, pA, 0).rgb;
    float3 aB = tHistory.SampleLevel(linClamp, pBa, 0).rgb;
    float errA = length((tMotion.SampleLevel(linClamp, pBa, 0).xy * it + vf) * inSize);
    bool consA = errA < 1.0f && inside(pBa) && tHud.SampleLevel(linClamp, pBa, 0) < 0.5f;

    float k = lerp(2.0f, 8.0f, sensitivity);
    float3 colB = consB ? lerp(bA, bB, t) : bB;
    float3 colA = consA ? lerp(aA, aB, t) : aA;
    float wB = saturate(mb.z) * (consB ? saturate(1.0f - length(bA - bB) * k) : 0.15f) * (inside(pB) ? 1.0f : 0.0f);
    float wA = saturate(mf.w) * (consA ? saturate(1.0f - length(aA - aB) * k) : 0.15f) * (inside(pA) ? 1.0f : 0.0f);
    float ws = wA + wB;
    float3 warped = ws > 1e-4f ? (colA * wA + colB * wB) / ws : plain;
    float conf = max(wA, wB);
    if (_pad2.y > 0.5f) { uOut4[id.xy] = float4(saturate(hud), consB ? 1.0f : 0.0f, consA ? 1.0f : 0.0f, 1.0f); return; }
    uOut4[id.xy] = float4(lerp(plain, warped, saturate(conf / 0.3f)), 1.0f);
}


//==================================================== Performance preset (1.3.1)
// Low-cost path: motion is searched on a 1/8-res luma pyramid (groupshared full
// search), refined at 1/4 res and given a sub-pixel fit at 1/2 res; reprojection,
// clipping, accumulation and sharpening run in ONE output pass; tiles that have not
// changed for 3 frames are skipped entirely (their output and history from two
// frames ago are still in the ping-pong textures).

// Input colour -> half-resolution luma (one bilinear tap = 2x2 average).
[numthreads(8, 8, 1)]
void FX_FastLuma(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    gLumaOut[id.xy] = luma709(gColor.SampleLevel(linClamp, uv, 0).rgb);
}

// 1.3.4 quality: global camera-pan estimate for flicks beyond the local search range (+-32 input px at 1/8).
// One group: the previous and current 1/8 luma are resampled to a fixed 64x36 grid (4 bilinear taps each), every
// shift within +-12 x +-6 grid texels (+-19% of the width, +-17% of the height per frame) is scored by the mean
// absolute difference of the overlap, and the best gets a parabolic sub-texel fit. Output (u1, 1x1): the backward
// vector in UV units. The 1/8 search and the 1/4 refine test it as an extra candidate (harness: 44 px/frame flick
// 14 -> 26 dB, 64 px 13 -> 23 dB; all scenes +0.5 dB).
#define GM_W 64
#define GM_H 36
#define GM_RX 12
#define GM_RY 6
#define GM_NC ((2 * GM_RX + 1) * (2 * GM_RY + 1))
groupshared float gmA[GM_H * GM_W];
groupshared float gmB[GM_H * GM_W];
groupshared float gmCost[GM_NC];
Texture2D<float2> gGlob : register(t17);
[numthreads(256, 1, 1)]
void FX_GlobalMotion(uint3 lt : SV_GroupThreadID)
{
    uint li = lt.x;
    uint sw, sh; gCurrL.GetDimensions(sw, sh);
    float2 q = 0.25f / float2(GM_W, GM_H);
    for (uint k = li; k < GM_W * GM_H; k += 256) {
        float2 uv = (float2(k % GM_W, k / GM_W) + 0.5f) / float2(GM_W, GM_H);
        float a = 0, b = 0;
        [unroll] for (int t = 0; t < 4; ++t) {
            float2 o = float2((t & 1) ? q.x : -q.x, (t & 2) ? q.y : -q.y);
            a += gPrevL.SampleLevel(linClamp, uv + o, 0); b += gCurrL.SampleLevel(linClamp, uv + o, 0);
        }
        gmA[k] = 0.25f * a; gmB[k] = 0.25f * b;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint c = li; c < GM_NC; c += 256) {
        int dx = (int)(c % (2 * GM_RX + 1)) - GM_RX, dy = (int)(c / (2 * GM_RX + 1)) - GM_RY;
        int x0 = max(0, -dx), x1 = min(GM_W, GM_W - dx), y0 = max(0, -dy), y1 = min(GM_H, GM_H - dy);
        float sum = 0, n = 0;
        for (int y = y0; y < y1; ++y)
            for (int x = x0; x < x1; ++x) { sum += abs(gmA[(y + dy) * GM_W + x + dx] - gmB[y * GM_W + x]); n += 1.0f; }
        gmCost[c] = (n >= 400.0f ? sum / n : 1e9f) + 0.0005f * float(abs(dx) + abs(dy));
    }
    GroupMemoryBarrierWithGroupSync();
    if (li != 0) return;
    uint bi = 0; float bc = 1e20f;
    for (uint c2 = 0; c2 < GM_NC; ++c2) if (gmCost[c2] < bc) { bc = gmCost[c2]; bi = c2; }
    int ix = (int)(bi % (2 * GM_RX + 1)), iy = (int)(bi / (2 * GM_RX + 1));
    float2 g = float2(ix - GM_RX, iy - GM_RY);
    if (ix > 0 && ix < 2 * GM_RX) {
        float cl = gmCost[bi - 1], cr = gmCost[bi + 1], d = cl + cr - 2.0f * bc;
        if (d > 1e-9f) g.x += clamp(0.5f * (cl - cr) / d, -0.5f, 0.5f);
    }
    if (iy > 0 && iy < 2 * GM_RY) {
        float cu = gmCost[bi - (2 * GM_RX + 1)], cd = gmCost[bi + (2 * GM_RX + 1)], d = cu + cd - 2.0f * bc;
        if (d > 1e-9f) g.y += clamp(0.5f * (cu - cd) / d, -0.5f, 0.5f);
    }
    gMvOut[uint2(0, 0)] = g / float2(GM_W, GM_H);   // UV units
}

// Full search at the coarsest level (radius 4, 3x3 patch) from groupshared memory.
#define FS_R 4
#define FS_N (8 + 2 * FS_R + 2)
groupshared float gsPrev[FS_N][FS_N];
groupshared float gsCurr[10][10];
#define FS_C ((2 * FS_R + 1) * (2 * FS_R + 1))
groupshared float gsCost[64][FS_C];
groupshared float gsBlock[FS_C];

[numthreads(8, 8, 1)]
void FX_FastSearch(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    int2 base = int2(gid.xy) * 8;
    int2 hi = int2(W - 1, H - 1);
    uint li = lt.y * 8 + lt.x;
    for (uint k = li; k < FS_N * FS_N; k += 64) {
        int2 q = clamp(base + int2(k % FS_N, k / FS_N) - (FS_R + 1), 0, hi);
        gsPrev[k / FS_N][k % FS_N] = gPrevL.Load(int3(q, 0));
    }
    for (uint k2 = li; k2 < 100; k2 += 64) {
        int2 q = clamp(base + int2(k2 % 10, k2 / 10) - 1, 0, hi);
        gsCurr[k2 / 10][k2 % 10] = gCurrL.Load(int3(q, 0));
    }
    GroupMemoryBarrierWithGroupSync();
    bool live = id.x < W && id.y < H;
    float c[9];
    [unroll] for (int j = 0; j < 9; ++j) c[j] = gsCurr[lt.y + j / 3][lt.x + j % 3];
    float bestCost = 1e20f; int2 best = 0;
    for (int ci = 0; ci < FS_C; ++ci) {
        int dx = ci % (2 * FS_R + 1) - FS_R, dy = ci / (2 * FS_R + 1) - FS_R;
        float sad = 0;
        [unroll] for (int j2 = 0; j2 < 9; ++j2)
            sad += abs(gsPrev[lt.y + FS_R + dy + j2 / 3][lt.x + FS_R + dx + j2 % 3] - c[j2]);
        gsCost[li][ci] = live ? sad : 0.0f;
        sad += 0.004f * (abs(dx) + abs(dy));
        if (sad < bestCost) { bestCost = sad; best = int2(dx, dy); }
    }
    GroupMemoryBarrierWithGroupSync();
    // Block consensus (64x64 input px): the summed cost of every candidate over the
    // block resolves repetitive patterns (text, grids) that fool a 3x3 patch.
    for (uint cj = li; cj < FS_C; cj += 64) {
        float sum = 0;
        for (uint t = 0; t < 64; ++t) sum += gsCost[t][cj];
        int ddx = (int)(cj % (2 * FS_R + 1)) - FS_R, ddy = (int)(cj / (2 * FS_R + 1)) - FS_R;
        gsBlock[cj] = sum + 0.05f * (abs(ddx) + abs(ddy));
    }
    GroupMemoryBarrierWithGroupSync();
    if (!live) return;
    int bi = 0; float bc = 1e20f;
    for (int cb = 0; cb < FS_C; ++cb) if (gsBlock[cb] < bc) { bc = gsBlock[cb]; bi = cb; }
    int2 blockV = int2(bi % (2 * FS_R + 1) - FS_R, bi / (2 * FS_R + 1) - FS_R);
    float cBlock = gsCost[li][bi];
    if (cBlock <= bestCost + 0.06f) best = blockV;
    // 1.3.4: global-pan candidates (FX_GlobalMotion, t17; zero/unbound = off) for motion beyond +-FS_R.
    float2 gG = gGlob.Load(int3(0, 0, 0)) * float2(W, H);
    if (any(abs(gG) > 1.0f)) {
        float bcost = gsCost[li][(best.y + FS_R) * (2 * FS_R + 1) + best.x + FS_R] + 0.004f * (abs(best.x) + abs(best.y));
        int2 g0 = int2(round(gG)), hiL = int2(W - 1, H - 1);
        [unroll] for (int gc = 0; gc < 9; ++gc) {
            int2 d = g0 + int2(gc % 3 - 1, gc / 3 - 1);
            float sad = 0;
            [unroll] for (int j3 = 0; j3 < 9; ++j3)
                sad += abs(gPrevL.Load(int3(clamp(int2(id.xy) + int2(j3 % 3 - 1, j3 / 3 - 1) + d, 0, hiL), 0)) - c[j3]);
            sad += 0.004f * (abs(d.x) + abs(d.y));
            if (sad < bcost) { bcost = sad; best = d; }
        }
    }
    gMvOut[id.xy] = float2(best) / float2(W, H);   // UV units
}

float patchSAD3(float2 uv, float2 mvUV, float2 texel)
{
    float sad = 0.0f;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx) {
        float2 o = float2(dx, dy) * texel;
        sad += abs(gPrevL.SampleLevel(linClamp, uv + o + mvUV, 0) - gCurrL.SampleLevel(pntClamp, uv + o, 0));
    }
    return sad;
}

// Performance preset: has the input tile under this motion-level pixel been unchanged
// for 3+ frames (so everything there is skipped)? ls = input px per level texel.
bool fastStaticAt(uint2 pix, float ls)
{
    if (fastP.y > 0.5f || historyValid < 0.5f) return false;
    int2 t = int2((float2(pix) + 0.5f) * ls / tileSize);
    return tCount.Load(int3(t, 0)) >= 3.0f;
}

// Wider sparse patch (3x3 at spacing 1 + ring at spacing 2): a 5x5 footprint for
// 17 taps, which keeps repetitive detail (text, grids) from aliasing.
Texture2D<float4> gMvPrev : register(t15);
float patchSADw(float2 uv, float2 mvUV, float2 texel)
{
    float sad = patchSAD3(uv, mvUV, texel);
    [unroll] for (int k = 0; k < 8; ++k) {
        int2 o = int2(k < 3 ? k - 1 : (k < 5 ? (k == 3 ? -1 : 1) : k - 6), k < 3 ? -1 : (k < 5 ? 0 : 1)) * 2;
        float2 of = float2(o) * texel;
        sad += abs(gPrevL.SampleLevel(linClamp, uv + of + mvUV, 0) - gCurrL.SampleLevel(pntClamp, uv + of, 0));
    }
    return sad;
}

// 1.3.4 round 4: the current-frame half of patchSADw/patchSAD3 does not depend on the vector; fetch it once per texel
// and reuse it for every candidate (same terms, same summation order).
int2 sadwOff(int k)   // k < 9: 3x3 at spacing 1 (dy-major), k >= 9: ring at spacing 2 (patchSADw order)
{
    if (k < 9) return int2(k % 3 - 1, k / 3 - 1);
    int r = k - 9;
    return int2(r < 3 ? r - 1 : (r < 5 ? (r == 3 ? -1 : 1) : r - 6), r < 3 ? -1 : (r < 5 ? 0 : 1)) * 2;
}
void currPatchW(float2 uv, float2 texel, out float c[17])
{
    [unroll] for (int k = 0; k < 17; ++k) c[k] = gCurrL.SampleLevel(pntClamp, uv + float2(sadwOff(k)) * texel, 0);
}
float patchSAD3c(float c[17], float2 uv, float2 mvUV, float2 texel)
{
    float sad = 0.0f;
    [unroll] for (int k = 0; k < 9; ++k) sad += abs(gPrevL.SampleLevel(linClamp, uv + float2(sadwOff(k)) * texel + mvUV, 0) - c[k]);
    return sad;
}
float patchSADwc(float c[17], float2 uv, float2 mvUV, float2 texel)
{
    float sad = patchSAD3c(c, uv, mvUV, texel);
    [unroll] for (int k = 9; k < 17; ++k) sad += abs(gPrevL.SampleLevel(linClamp, uv + float2(sadwOff(k)) * texel + mvUV, 0) - c[k]);
    return sad;
}

// Lucas-Kanade refinement of a backward vector (UV units) on a 5x5 window of the
// current level (gradients computed once), iterated on the previous level.
// 1.3.4 round 4: split into prep (current-frame window + gradients + normal matrix, independent of the start vector)
// and solve (iterations on the previous frame), so FX_FastRefine preps once for all its starts and FX_FastFinal reads
// the window from groupshared memory. Same arithmetic as before, fewer texture loads.
void lkAccum(int k, float cc, float gx, float gy, inout float cv[25], inout float2 gr[25], inout float3 a)
{
    float wk = exp(-0.25f * float((k % 5 - 2) * (k % 5 - 2) + (k / 5 - 2) * (k / 5 - 2)));
    cv[k] = cc; gr[k] = float2(gx, gy) * wk;
    a += float3(gx * gx, gx * gy, gy * gy) * wk;
}
void lkPrepTex(int2 pix, int2 hiL, out float cv[25], out float2 gr[25], out float3 a)
{
    a = 0;
    [unroll] for (int k = 0; k < 25; ++k) {
        int2 q = pix + int2(k % 5 - 2, k / 5 - 2);
        float cc = gCurrL.Load(int3(clamp(q, 0, hiL), 0));
        float gx = 0.5f * (gCurrL.Load(int3(clamp(q + int2(1, 0), 0, hiL), 0)) - gCurrL.Load(int3(clamp(q - int2(1, 0), 0, hiL), 0)));
        float gy = 0.5f * (gCurrL.Load(int3(clamp(q + int2(0, 1), 0, hiL), 0)) - gCurrL.Load(int3(clamp(q - int2(0, 1), 0, hiL), 0)));
        lkAccum(k, cc, gx, gy, cv, gr, a);
    }
}
float2 lkSolve(float cv[25], float2 gr[25], float3 a, float2 uv, float2 mvUV, float2 texel, float maxStep, int iters)
{
    // Small Tikhonov term: flat or 1-D patches keep their vector instead of exploding.
    float a11 = a.x + 1e-4f, a12 = a.y, a22 = a.z + 1e-4f;
    float det = a11 * a22 - a12 * a12;
    float2 d = 0;
    [unroll] for (int itr = 0; itr < 2; ++itr) {
        if (itr < iters) {
        float b1 = 0, b2 = 0;
        [unroll] for (int k2 = 0; k2 < 25; ++k2) {
            float2 o = float2(k2 % 5 - 2, k2 / 5 - 2);
            float e = gPrevL.SampleLevel(linClamp, uv + (o + d) * texel + mvUV, 0) - cv[k2];
            b1 += gr[k2].x * e; b2 += gr[k2].y * e;
        }
        float2 st = -float2(a22 * b1 - a12 * b2, a11 * b2 - a12 * b1) / det;
        d = clamp(d + clamp(st, -0.75f, 0.75f), -maxStep, maxStep);
        }
    }
    return mvUV + d * texel;
}
float2 lkRefine(int2 pix, float2 uv, float2 mvUV, float2 texel, int2 hiL, float maxStep)
{
    float cv[25]; float2 gr[25]; float3 a;
    lkPrepTex(pix, hiL, cv, gr, a);
    return lkSolve(cv, gr, a, uv, mvUV, texel, maxStep, 2);
}
// Groupshared current-level window for an 8x8 group: 14x14 texels (window +-2, gradient +-1), clamped like the loads.
groupshared float gsLk[196];
void lkLoadGs(uint2 gid, uint gi, int2 hiL)
{
    int2 org = int2(gid) * 8 - 3;
    for (uint s = gi; s < 196; s += 64) gsLk[s] = gCurrL.Load(int3(clamp(org + int2(s % 14, s / 14), 0, hiL), 0));
}
void lkPrepGs(int2 lp, out float cv[25], out float2 gr[25], out float3 a)   // lp = pixel - group origin (3..10)
{
    a = 0;
    [unroll] for (int k = 0; k < 25; ++k) {
        int i = (lp.y + k / 5 - 2) * 14 + lp.x + k % 5 - 2;
        lkAccum(k, gsLk[i], 0.5f * (gsLk[i + 1] - gsLk[i - 1]), 0.5f * (gsLk[i + 14] - gsLk[i - 14]), cv, gr, a);
    }
}

// 1/4 res: pick the best of three starting vectors - the 1/8-res search result,
// zero, and last frame's vector here (motion is temporally coherent) - then refine
// it continuously with Lucas-Kanade. No integer jumps, so repetitive detail (text,
// grids) cannot pull the vector onto a false match one period away.
[numthreads(8, 8, 1)]
void FX_FastRefine(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    if (fastStaticAt(id.xy, inSize.x / W)) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float2 cands[4];
    cands[0] = gMvIn.SampleLevel(pntClamp, uv, 0).xy;
    cands[1] = 0;
    cands[2] = historyValid > 0.5f ? gMvPrev.SampleLevel(pntClamp, uv, 0).xy / inSize : 0;
    cands[3] = gGlob.Load(int3(0, 0, 0));   // 1.3.4: global pan (0 when off)
    // Refine every start, then compare: a false match one text period away can look
    // as good as the truth, so ties go to the shorter vector.
    float2 best = 0; float bestCost = 1e20f;
    float lcv[25]; float2 lgr[25]; float3 la3;
    lkPrepTex(int2(id.xy), int2(W - 1, H - 1), lcv, lgr, la3);   // round 4: once for all starts (was per start)
    float lcp[17]; currPatchW(uv, texel, lcp);
    // 1.3.4 round 3: during a detected pan the global vector replaces the zero start (one LK refine fewer;
    // harness all scenes +0.05 dB, occluder +0.3, flicks 32-64 px +0.2..+0.5, 22 px pan -0.27).
    bool panOn = any(abs(cands[3]) > 0.3f * texel);
    [unroll] for (int c = 0; c < 4; ++c) {
        if (c == 1 && panOn) continue;
        if (c >= 1 && all(abs(cands[c] - cands[0]) < 0.3f * texel)) continue;   // duplicate start
        if (c >= 2 && all(abs(cands[c] - cands[1]) < 0.3f * texel)) continue;
        if (c == 3 && all(abs(cands[3] - cands[2]) < 0.3f * texel)) continue;
        float2 r = lkSolve(lcv, lgr, la3, uv, cands[c], texel, 1.5f, 2);
        float cost = patchSADwc(lcp, uv, r, texel) + 0.004f * length(r / texel);
        if (cost < bestCost) { bestCost = cost; best = r; }
    }
    gMvOut[id.xy] = best;
}

// 1.3.4 quality (frame gen on): the same filter at 1/2 res, straight from the input colour, into a separate
// motion-only luma (the box half-res luma stays for tile change detection). Harness +0.30 dB.
[numthreads(8, 8, 1)]
void FX_FastLumaAA(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 st = 1.0f / inSize;
    float2 c = (float2(id.xy) + 0.5f) / float2(w, h);
    float acc = 0, ws = 0;
    [unroll] for (int j = -1; j <= 1; ++j)
    [unroll] for (int i = -1; i <= 1; ++i) {
        float wt = exp(-2.0f * float(i * i + j * j));
        acc += luma709(gColor.SampleLevel(linClamp, c + float2(i, j) * 2.0f * st, 0).rgb) * wt; ws += wt;
    }
    gLumaOut[id.xy] = acc / ws;
}

// 1.3.4 quality (frame gen on): 1/4-res luma as the 2x2 box plus a sparse 3x3 Gaussian of neighbouring boxes (sigma ~1
// source texel). Fine texture no longer aliases at 1/4 res, so Lucas-Kanade there converges (harness +0.35 dB).
[numthreads(8, 8, 1)]
void FX_LumaHalveAA(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    uint sw, sh; gLumaIn.GetDimensions(sw, sh);
    float2 st = 1.0f / float2(sw, sh);
    float2 c = (float2(id.xy) + 0.5f) / float2(w, h);
    float acc = 0, ws = 0;
    [unroll] for (int j = -1; j <= 1; ++j)
    [unroll] for (int i = -1; i <= 1; ++i) {
        float wt = exp(-2.0f * float(i * i + j * j));
        acc += gLumaIn.SampleLevel(linClamp, c + float2(i, j) * 2.0f * st, 0) * wt; ws += wt;
    }
    gLumaOut[id.xy] = acc / ws;
}

// Anti-aliased 1/8-res luma from 1/4 res (Gaussian, sigma 1.5 texels of the source):
// the plain 2x2 box chain aliases fine repetitive detail (text) into false matches.
[numthreads(8, 8, 1)]
void FX_FastAA(uint3 id : SV_DispatchThreadID)
{
    uint w, h; gLumaOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    uint sw, sh; gLumaIn.GetDimensions(sw, sh);
    float2 st = 1.0f / float2(sw, sh);
    float2 c = (float2(id.xy) + 0.5f) / float2(w, h);
    float acc = 0, ws = 0;
    [unroll] for (int j = 0; j < 4; ++j)
    [unroll] for (int i = 0; i < 4; ++i) {
        float2 o = float2(i * 2 - 3, j * 2 - 3);
        float wt = exp(-dot(o, o) / (2.0f * 2.25f));
        acc += gLumaIn.SampleLevel(linClamp, c + o * st, 0) * wt; ws += wt;
    }
    gLumaOut[id.xy] = acc / ws;
}

// Spatial propagation at 1/4 res: every pixel re-tests its 8 neighbours' vectors
// (wide patch) and keeps the best, which removes isolated wrong matches.
[numthreads(8, 8, 1)]
void FX_FastClean(uint3 id : SV_DispatchThreadID)
{
    uint W, H; gMvOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    if (fastStaticAt(id.xy, inSize.x / W)) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    int2 hi = int2(W - 1, H - 1);
    float2 best = gMvIn.Load(int3(id.xy, 0)).xy;
    // 1.3.4 round 3: fast camera pan (global vector > 1.5 texels at 1/8 res, t17): the propagation pulls edge/outlier
    // vectors the wrong way there and costs 8 wide-patch tests per texel - skip it (harness all scenes +0.12 dB,
    // fast pan +0.58, flicks +0.2..+0.7; slower motion keeps it, where it helps zoom/occluder).
    if (length(gGlob.Load(int3(0, 0, 0)) * float2(W, H) * 0.5f) > 1.5f) { gMvOut[id.xy] = best; return; }
    float lcp[17]; currPatchW(uv, texel, lcp);   // round 4: current-frame taps once for all 9 candidates
    float bestCost = patchSADwc(lcp, uv, best, texel);
    [unroll] for (int k = 0; k < 9; ++k) {
        if (k == 4) continue;
        float2 mv = gMvIn.Load(int3(clamp(int2(id.xy) + int2(k % 3 - 1, k / 3 - 1), 0, hi), 0)).xy;
        float cost = patchSADwc(lcp, uv, mv, texel) + 0.002f;
        if (cost < bestCost) { bestCost = cost; best = mv; }
    }
    gMvOut[id.xy] = best;
}

// Sub-pixel fit + static prior + jitter cancel + confidence at the final level.
// Output (u2): rg = backward motion in INPUT pixels, b = confidence.
float ls0() { return max(fastP.z, 1.0f); }
[numthreads(8, 8, 1)]
void FX_FastFinal(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
{
    uint W, H; gMvPx.GetDimensions(W, H);
    lkLoadGs(gid.xy, gi, int2(W - 1, H - 1));   // round 4: LK window from groupshared (196 loads per group, not 125 per texel)
    GroupMemoryBarrierWithGroupSync();
    if (id.x >= W || id.y >= H) return;
    if (fastStaticAt(id.xy, inSize.x / W)) { gMvPx[id.xy] = float4(0, 0, 1, 1); return; }   // unchanged tile: static
    float2 size = float2(W, H), texel = 1.0f / size;
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float2 start = gMvIn.SampleLevel(linClamp, uv, 0).xy;
    // round 4: current-frame 3x3 fetched once for the pick, the fit cost and the static-prior cost
    float lcp[17]; [unroll] for (int q3 = 0; q3 < 9; ++q3) lcp[q3] = gCurrL.SampleLevel(pntClamp, uv + float2(sadwOff(q3)) * texel, 0);
    [unroll] for (int q4 = 9; q4 < 17; ++q4) lcp[q4] = 0;
    // 1.3.4 quality (frame gen on, _pad2.x): the bilinear upsample blends two objects' vectors at motion edges;
    // also try the vector of this pixel's own coarse texel and its 4 neighbours (harness: +0.58 dB, SSIM +0.004).
    if (_pad2.x > 0.5f && ls0() < 3.0f) {
        uint cw, ch; gMvIn.GetDimensions(cw, ch);
        int2 cp = int2(uv * float2(cw, ch)), chi = int2(cw - 1, ch - 1);
        float bc = 1e20f; float2 cand0 = start;
        // 1.3.4 round 3: 7th candidate = global camera pan (t17; 0 / unbound when off). Fixes interior outliers and
        // edge-band vectors on fast pans (harness: 22 px pan +1.7 dB, all scenes +0.44 dB).
        float2 gG = gGlob.Load(int3(0, 0, 0));
        bool useG = any(abs(gG) > 0.25f * texel);
        // Fast pan (> 1.5 texels at 1/8 res): the 4 neighbour vectors only add wrong edge picks; skip them (harness
        // all scenes +0.05 dB, fast pan +0.28; 36 taps fewer per texel on exactly the most expensive frames).
        bool fastPan = length(gG * inSize * 0.125f) > 1.5f;
        [unroll] for (int k = 0; k < 7; ++k) {
            if (k == 6 && !useG) continue;
            if (k >= 2 && k <= 5 && (fastPan || (((int)fastR.w) & 4) != 0)) continue;   // + Competitive: 3-candidate pick
            int2 o = k == 1 ? int2(0, 0) : (k == 2 ? int2(1, 0) : (k == 3 ? int2(-1, 0) : (k == 4 ? int2(0, 1) : int2(0, -1))));
            float2 vc = k == 0 ? cand0 : (k == 6 ? gG : gMvIn.Load(int3(clamp(cp + o, 0, chi), 0)).xy);
            float cc = 0;
            [unroll] for (int q2 = 0; q2 < 9; ++q2) cc += abs(gPrevL.SampleLevel(linClamp, uv + float2(q2 % 3 - 1, q2 / 3 - 1) * texel + vc, 0) - lcp[q2]);
            if (cc < bc) { bc = cc; start = vc; }
        }
    }
    float lcv[25]; float2 lgr[25]; float3 la3;
    lkPrepGs(int2(id.xy) - (int2(gid.xy) * 8 - 3), lcv, lgr, la3);
    // round 4: one LK iteration at the final level (harness +0.08 dB, cheaper); fastR.w bit 1 = two (UFX_FG_LK2=1)
    int fcode = (int)fastR.w;
    float2 mvUV = lkSolve(lcv, lgr, la3, uv, start, texel, 1.0f, (fcode & 3) == 2 ? 2 : 1);
    float c0 = patchSAD3c(lcp, uv, mvUV, texel);
    float ls = max(fastP.z, 1.0f);                 // input pixels per level pixel
    float2 mvPx = mvUV * size * ls;                // input pixels
    float2 staticPx = applyJitterCancel != 0 ? jitterDelta : float2(0, 0);
    float cs = patchSAD3c(lcp, uv, staticPx / ls * texel, texel);
    if (cs <= c0 + fastQ.z || length(mvPx - staticPx) < 0.06f) { mvPx = staticPx; c0 = min(c0, cs); }
    if (applyJitterCancel != 0) mvPx -= jitterDelta;
    float conf = saturate(1.0f - c0 / fastQ.x);
    if (_pad.x > 0.5f) { mvPx = 0; conf = 1.0f; }
    if (fastP.w > 0.5f) { mvPx = pan; conf = 1.0f; }   // diagnostic: known constant motion
    gMvPx[id.xy] = float4(mvPx, conf, 1.0f);
}

// One thread per 16x16 input tile: frames unchanged (u7, in place), from half-res luma,
// measured against the luma the tile was last RENDERED with (u4, updated only when the
// tile renders), so slow drifts below the threshold per frame still add up and re-render.
[numthreads(8, 8, 1)]
void FX_FastTiles(uint3 id : SV_DispatchThreadID)
{
    uint TW, TH; uCount.GetDimensions(TW, TH);
    if (id.x >= TW || id.y >= TH) return;
    uint W, H; gCurrL.GetDimensions(W, H);
    int ts = max(2, (int)tileSize / 2);
    int2 o = int2(id.xy) * ts;
    float md = 0;
    for (int y = 0; y < ts; ++y)
    for (int x = 0; x < ts; ++x) {
        int2 q = min(o + int2(x, y), int2(W - 1, H - 1));
        md = max(md, abs(gCurrL.Load(int3(q, 0)) - uOut1[q]));
    }
    float n = uCount[id.xy];
    float nn = (historyValid > 0.5f && md <= staticThresh) ? min(n + 1.0f, 64.0f) : 0.0f;
    uCount[id.xy] = nn;
    if (nn < 3.0f) {
        for (int y2 = 0; y2 < ts; ++y2)
        for (int x2 = 0; x2 < ts; ++x2) {
            int2 q = o + int2(x2, y2);
            if (q.x < (int)W && q.y < (int)H) uOut1[q] = gCurrL.Load(int3(q, 0));
        }
    }
}

// Output-pixel range covered by input tile t (one thread group per tile).
bool fastTile(uint2 gid, out int2 o0, out int2 o1)
{
    float2 sc = outSize / inSize;
    float ts = tileSize;
    o0 = int2(floor(float2(gid) * ts * sc));
    o1 = min(int2(floor(float2(gid + 1) * ts * sc)), int2(outSize));
    bool skip = fastP.y < 0.5f && historyValid > 0.5f && tCount.Load(int3(gid, 0)) >= 3.0f;
    return !skip;
}

float3 sampleHistory5(float2 uv)
{
    // Bilinear-optimised Catmull-Rom (5 taps, corners dropped).
    float2 pos = uv * outSize, tc = floor(pos - 0.5f) + 0.5f, f = pos - tc;
    float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 w3 = f * f * (-0.5f + 0.5f * f);
    float2 w12 = w1 + w2, t12 = (tc + w2 / w12) / outSize;
    float2 t0 = (tc - 1.0f) / outSize, t3 = (tc + 2.0f) / outSize;
    float4 r = tHistory.SampleLevel(linClamp, float2(t12.x, t0.y), 0) * (w12.x * w0.y)
             + tHistory.SampleLevel(linClamp, float2(t0.x, t12.y), 0) * (w0.x * w12.y)
             + tHistory.SampleLevel(linClamp, t12, 0) * (w12.x * w12.y)
             + tHistory.SampleLevel(linClamp, float2(t3.x, t12.y), 0) * (w3.x * w12.y)
             + tHistory.SampleLevel(linClamp, float2(t12.x, t3.y), 0) * (w12.x * w3.y);
    float ws = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return r.rgb / max(ws, 1e-4f);
}

// Fused reproject + clip + accumulate + sharpen. t0 = input colour, t4 = motion
// (any resolution, input px), t9 = history in, t10 = tile counters; u3 = history
// out (rgb, a = accumulated weight), u5 = output (RGBA8).
// One group per 16x16 input tile: the 18x18 input footprint is converted to YCoCg
// once into groupshared memory; tap offsets and the (separable) box weights are
// computed once per output column and row, the radial Lanczos-2 weight per tap.
#define FA_T 16
#define FA_N (FA_T + 2)
#define FA_MAXO 128
groupshared float3 gaIn[FA_N][FA_N];
groupshared float4 gaWx[FA_MAXO]; groupshared float4 gaWy[FA_MAXO];   // xyz = squared scaled offsets of taps -1..1, w = source index
groupshared float3 gaBx[FA_MAXO]; groupshared float3 gaBy[FA_MAXO];   // box (moments) taps

[numthreads(8, 8, 1)]
void FX_FastAccum(uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    int2 o0, o1;
    if (!fastTile(gid.xy, o0, o1)) return;
    int2 inI = int2(inSize);
    int2 tb = int2(gid.xy) * FA_T - 1;                  // input origin of the groupshared tile
    uint li = lt.y * 8 + lt.x;
    for (uint k = li; k < FA_N * FA_N; k += 64) {
        int2 q = clamp(tb + int2(k % FA_N, k / FA_N), 0, inI - 1);
        gaIn[k / FA_N][k % FA_N] = rgbToYCoCg(gColor.Load(int3(q, 0)).rgb);
    }
    float down = max(inSize.x / outSize.x, 1e-4f);
    float kbMax = min(1.99f, 1.0f + (1.0f / down - 1.0f));
    float kb = jitterActive < 0.5f ? (_pad.y > 0.0f ? _pad.y : 1.0f) : kbMax;
    int2 cnt = min(o1 - o0, FA_MAXO);
    // Per-column and per-row weights.
    for (int ci = (int)li; ci < cnt.x; ci += 64) {
        int x = o0.x + ci;
        float sp = (x + 0.5f) * inSize.x / outSize.x;
        int si = (int)floor(sp);
        float3 wl, wb;
        [unroll] for (int d = -1; d <= 1; ++d) {
            float off = (si + d + 0.5f + jitter.x) - sp;
            wl[d + 1] = off * off * kb * kb;           // squared, scaled offset (radial Lanczos below)
            wb[d + 1] = exp(-2.0f * off * off);
        }
        gaWx[ci] = float4(wl, si - tb.x); gaBx[ci] = wb;
    }
    for (int ri = (int)li; ri < cnt.y; ri += 64) {
        int y = o0.y + ri;
        float sp = (y + 0.5f) * inSize.y / outSize.y;
        int si = (int)floor(sp);
        float3 wl, wb;
        [unroll] for (int d = -1; d <= 1; ++d) {
            float off = (si + d + 0.5f + jitter.y) - sp;
            wl[d + 1] = off * off * kb * kb;           // squared, scaled offset (radial Lanczos below)
            wb[d + 1] = exp(-2.0f * off * off);
        }
        gaWy[ri] = float4(wl, si - tb.y); gaBy[ri] = wb;
    }
    GroupMemoryBarrierWithGroupSync();
    for (int yy = (int)lt.y; yy < cnt.y; yy += 8)
    for (int xx = (int)lt.x; xx < cnt.x; xx += 8) {
        int2 p = o0 + int2(xx, yy);
        float2 hrUV = (float2(p) + 0.5f) / outSize;
        float4 wx = gaWx[xx], wy = gaWy[yy];
        float3 bx = gaBx[xx], by = gaBy[yy];
        int cx = clamp((int)wx.w, 1, FA_N - 2), cy = clamp((int)wy.w, 1, FA_N - 2);
        float3 accCol = 0, m1 = 0, m2 = 0, aMin = 1e20f, aMax = -1e20f;
        float boxW = 0, accW = 0, maxW = 0;
        [unroll] for (int dy = -1; dy <= 1; ++dy)
        [unroll] for (int dx = -1; dx <= 1; ++dx) {
            float3 ycc = gaIn[cy + dy][cx + dx];
            float w = lanczos2ApproxSq(wx[dx + 1] + wy[dy + 1]);
            float bw = bx[dx + 1] * by[dy + 1];
            accCol += ycc * w; accW += w; maxW = max(maxW, w);
            aMin = min(aMin, ycc); aMax = max(aMax, ycc);
            m1 += ycc * bw; m2 += ycc * ycc * bw; boxW += bw;
        }
        float3 mean = m1 / max(boxW, 1e-3f);
        float3 upY = accW > 1e-3f ? clamp(accCol / accW, aMin, aMax) : mean;
        float cover = clamp(maxW, 0.05f, 1.0f);
        float3 sigma = sqrt(max(m2 / max(boxW, 1e-3f) - mean * mean, 0.0f));
        float3 bMin = max(aMin, mean - 1.25f * sigma), bMax = min(aMax, mean + 1.25f * sigma);
        float4 mv = tMotion.SampleLevel(linClamp, hrUV, 0);
        float2 histUV = hrUV + mv.xy / inSize;
        bool fresh = historyValid < 0.5f || any(histUV < 0.0f) || any(histUV > 1.0f);
        float3 outY = upY; float acc = cover;
        if (!fresh) {
            float histW = tHistory.SampleLevel(linClamp, histUV, 0).a;
            float3 histY = clipToBox(rgbToYCoCg(saturate(sampleHistory5(histUV))), bMin, bMax);
            float w = histW * lerp(fastQ.y, 1.0f, saturate(mv.z)) * exp2(-0.12f * length(mv.xy));
            w = min(w, fastQ.w);
            acc = min(fastQ.w, w + cover);
            outY = lerp(histY, upY, cover / max(acc, 1e-5f));
        }
        uOut4[p] = float4(saturate(yCoCgToRGB(outY)), acc);
        // Sharpen luma against the local input mean, limited to the local range (no halos).
        float3 sy = outY;
        sy.x = clamp(outY.x + fastP.x * (outY.x - mean.x), aMin.x, aMax.x);
        uOut4b[p] = float4(saturate(yCoCgToRGB(sy)), 1.0f);
    }
}

// 1.3.4 quality (tuned with the CPU harness, see windows/tests/fg-quality): a warp end landing on a STATIC pixel
// (identical in both real frames: HUD, crosshair, overlay text) while the destination moves would drag the HUD into
// the scene, so that end is dropped; an end outside the frame is dropped; both gone -> plain blend.
float max3(float3 v) { return max(v.x, max(v.y, v.z)); }

// 1.3.4 quality: at motion edges the bilinear motion lookup mixes two objects' vectors (tears / halos). Pick, among
// the resolved vector and the four diagonal neighbouring motion texels, the one whose t = 0.5 symmetric warp agrees
// best between the two real frames (harness: +0.46 dB, SSIM +0.018). Enabled by _pad2.x (latency budget < level 4).
float symCost(float2 uv, float2 v) { return dot(abs(gColor.SampleLevel(linClamp, uv + 0.5f * v, 0).rgb - tHistory.SampleLevel(linClamp, uv - 0.5f * v, 0).rgb), 1.0f); }
float2 pickVector(float2 uv, float2 it, float2 v0)
{
    uint mw, mh; tMotion.GetDimensions(mw, mh);
    float2 os = 1.0f / float2(mw, mh);
    float2 best = v0; float bc = symCost(uv, v0);
    [unroll] for (int k = 0; k < 4; ++k) {
        float2 o = float2((k & 1) ? 1.0f : -1.0f, (k & 2) ? 1.0f : -1.0f);
        float2 vc = tMotion.SampleLevel(pntClamp, uv + 1.5f * o * os, 0).xy * it;   // 1.5 texels: harness +0.12 dB vs 1
        float c = symCost(uv, vc) + 0.02f;
        if (c < bc) { bc = c; best = vc; }
    }
    return best;
}

float4 fgStep(float2 uv, float2 vb, float mconf, float3 a0, float3 b0, float t, bool pre = false)
{
    float2 pB = uv - (1.0f - t) * vb, pA = pB + vb;
    float4 bB4 = tHistory.SampleLevel(linClamp, pB, 0), bA4 = gColor.SampleLevel(linClamp, pA, 0);
    float3 bB = bB4.rgb, bA = bA4.rgb;
    bool sA, sB;
    // Round 4: pre = the frames come from FX_FgPrep with the static mask |A - B| < thresh in alpha (both copies), so the
    // static test at each warp end rides on the colour tap (2 taps per step instead of 4; same test as the harness).
    [branch] if (pre) { sA = bA4.a > 0.5f; sB = bB4.a > 0.5f; }
    else {
        sA = max3(abs(bA - tHistory.SampleLevel(linClamp, pA, 0).rgb)) < staticThresh;
        sB = max3(abs(bB - gColor.SampleLevel(linClamp, pB, 0).rgb)) < staticThresh;
    }
    // Both ends on static overlay pixels while this pixel moves: the content is hidden under the HUD in both real
    // frames, so warping would paint HUD colour here; blend the two real frames instead (harness +0.53 dB).
    if (sA && sB) return float4(lerp(a0, b0, t), 1.0f);
    float wA = (1.0f - t) * (inside(pA) && !(sA && !sB) ? 1.0f : 0.0f);
    float wB = t * (inside(pB) && !(sB && !sA) ? 1.0f : 0.0f);
    float ws = wA + wB;
    return float4(ws > 1e-4f ? (bA * wA + bB * wB) / ws : lerp(a0, b0, t), 1.0f);
}

// Fused frame generation from the low-res backward field. t0 = previous output (A),
// t9 = current output (B), t4 = motion (input px, b = confidence), t10 = tile counters.
[numthreads(8, 8, 1)]
void FX_FastInterp(uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    int2 o0, o1;
    if (!fastTile(gid.xy, o0, o1)) return;
    float t = interpT;
    float2 it = 1.0f / inSize;
    for (int y = o0.y + (int)lt.y; y < o1.y; y += 8)
    for (int x = o0.x + (int)lt.x; x < o1.x; x += 8) {
        int2 p = int2(x, y);
        float2 uv = (float2(p) + 0.5f) / outSize;
        float3 a0 = gColor.SampleLevel(linClamp, uv, 0).rgb, b0 = tHistory.SampleLevel(linClamp, uv, 0).rgb;
        float3 plain = lerp(a0, b0, t);
        float4 mb = tMotion.SampleLevel(linClamp, uv, 0);
        float2 vb = mb.xy * it;
        [unroll] for (int i = 0; i < 2; ++i) { mb = tMotion.SampleLevel(linClamp, uv - (1.0f - t) * vb, 0); vb = mb.xy * it; }
        float2 pB = uv - (1.0f - t) * vb, pA = pB + vb;
        float3 bB = tHistory.SampleLevel(linClamp, pB, 0).rgb, bA = gColor.SampleLevel(linClamp, pA, 0).rgb;
        float conf = lerp(1.0f, saturate(mb.z), fastR.y) * saturate(1.0f - length(bA - bB) * fastR.x) * (inside(pA) && inside(pB) ? 1.0f : 0.0f);
        if (fastR.w < -0.5f) { uOut4[p] = float4(lerp(plain, lerp(bA, bB, t), saturate(conf / fastR.z)), 1.0f); continue; }   // UFX_FG_LEGACY=1
        // 1.3.4 quality: static/HUD pixels unwarped, scene cut -> nearest real frame, static or off-frame warp ends dropped.
        if (sceneCut()) { uOut4[p] = float4(t < 0.5f ? a0 : b0, 1.0f); continue; }
        if (max3(abs(a0 - b0)) < staticThresh) { uOut4[p] = float4(b0, 1.0f); continue; }
        if (_pad2.x > 0.5f) vb = pickVector(uv, it, vb);
        uOut4[p] = fgStep(uv, vb, saturate(mb.z), a0, b0, t);
    }
}

// 1.3.4: Quality-preset parity. The same tuned generator as the Performance path (scene cut -> nearest real frame,
// static pixels unwarped, static/off-frame/hidden-under-HUD warp ends handled by fgStep), one pass per generated
// frame over every pixel (no tile skipping: the Quality preset has no tile counters). UFX_FG_Q2=1 = FX_Interpolate2.
[numthreads(8, 8, 1)]
void FX_Interp1(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float t = interpT;
    float2 it = 1.0f / inSize;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float3 a0 = gColor.SampleLevel(linClamp, uv, 0).rgb, b0 = tHistory.SampleLevel(linClamp, uv, 0).rgb;
    if (sceneCut()) { uOut4[id.xy] = float4(t < 0.5f ? a0 : b0, 1.0f); return; }
    if (max3(abs(a0 - b0)) < staticThresh) { uOut4[id.xy] = float4(b0, 1.0f); return; }
    float4 mb = tMotion.SampleLevel(linClamp, uv, 0);
    float2 vb = mb.xy * it;
    mb = tMotion.SampleLevel(linClamp, uv - 0.5f * vb, 0); vb = mb.xy * it;   // fixed-point step at t = 0.5 (as FX_FastInterpN)
    if (_pad2.x > 0.5f) vb = pickVector(uv, it, vb);
    uOut4[id.xy] = fgStep(uv, vb, saturate(mb.z), a0, b0, t);
}

//================================================================ test scene
float hash21(float2 p) { return frac(sin(dot(p, float2(127.1f, 311.7f))) * 43758.5453f); }

float3 testSceneColor(float2 uv, float time)
{
    uv = saturate(uv);
    float2 tUV = uv + float2(time * 0.035f, 0.0f);
    float2 grid = tUV * float2(96.0f, 54.0f);
    float2 cell = floor(grid), f = frac(grid);
    float rnd = hash21(cell);
    float bar = step(0.18f, f.x) * step(f.x, 0.82f) * step(0.22f, f.y) * step(f.y, 0.78f);
    float strokes = step(0.5f, abs(sin(f.x * 9.0f + rnd * 9.0f))) * step(0.45f, abs(sin(f.y * 7.0f + rnd * 5.0f)));
    float glyph = step(0.38f, rnd) * bar * strokes;
    float2 c = uv - 0.5f;
    float ang = time * 0.37f, ca = cos(ang), sa = sin(ang);
    float2 rc = float2(c.x * ca - c.y * sa, c.x * sa + c.y * ca);
    float zoom = 9.0f + 3.5f * sin(time * 0.28f);
    float2 zc = rc * zoom + 16.0f;
    float checker = ((int(floor(zc.x)) + int(floor(zc.y))) & 1) ? 1.0f : 0.0f;
    float yLine = 0.28f + 0.10f * sin(time * 0.9f);
    float xLine = 0.71f + 0.06f * cos(time * 1.15f);
    float dLine = uv.x + uv.y - (1.0f + 0.08f * sin(time * 0.65f));
    const float lw = 1.2f / 720.0f;
    float lines = max(max(1.0f - saturate(abs(uv.y - yLine) / lw), 1.0f - saturate(abs(uv.x - xLine) / lw)),
                      1.0f - saturate(abs(dLine) / (lw * 1.4f)));
    float2 occC = float2(0.50f + 0.28f * sin(time * 0.85f), 0.52f + 0.22f * cos(time * 0.67f));
    float2 occ = abs(uv - occC);
    float occluder = (occ.x < 0.075f && occ.y < 0.075f) ? 1.0f : 0.0f;
    float3 bg = lerp(float3(0.10f, 0.12f, 0.16f), float3(0.82f, 0.80f, 0.74f), checker);
    bg = lerp(bg, float3(0.05f, 0.05f, 0.06f), glyph * 0.85f);
    bg = lerp(bg, float3(1.0f, 0.92f, 0.18f), lines);
    bg = lerp(bg, float3(0.86f, 0.16f, 0.12f), occluder);
    return bg;
}

// Static HUD in screen space (not panned): a text panel and a status bar.
float4 hudColor(float2 s)
{
    float4 o = 0;
    if (s.x > 0.03f && s.x < 0.33f && s.y > 0.05f && s.y < 0.20f) {
        float2 g = (s - float2(0.03f, 0.05f)) * float2(160.0f, 90.0f);
        float2 c = floor(g), f = frac(g);
        float r = hash21(c + 7.0f);
        float on = step(0.3f, r) * step(0.15f, f.x) * step(f.x, 0.85f) * step(0.2f, f.y) * step(f.y, 0.8f)
                 * step(0.5f, abs(sin(f.x * 11.0f + r * 7.0f)));
        o = float4(lerp(float3(0.04f, 0.05f, 0.08f), float3(0.95f, 0.97f, 1.0f), on), 1.0f);
    }
    if (s.y > 0.90f && s.y < 0.95f && s.x > 0.25f && s.x < 0.75f) {
        float fill = step(s.x, 0.25f + 0.5f * 0.62f);
        o = float4(lerp(float3(0.15f, 0.15f, 0.15f), float3(0.20f, 0.85f, 0.35f), fill), 1.0f);
    }
    return o;
}
float3 sceneWithHud(float2 s, float2 p, float time)
{
    float3 c = testSceneColor(s + p, time);
    if (testHud > 0.5f) { float4 h = hudColor(s); c = lerp(c, h.rgb, h.a); }
    return c;
}

// Point-sample the analytic scene at pixel centre + jitter: a genuinely
// jittered low-res render (and, with jitter 0 at output res, ground truth).
[numthreads(8, 8, 1)]
void FX_TestScene(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut4.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f + jitter) / float2(w, h);
    uOut4[id.xy] = float4(sceneWithHud(uv, pan, sceneTime), 1.0f);
}

// Ground truth: 4x4 supersampled (box-filtered) scene at output resolution.
[numthreads(8, 8, 1)]
void FX_TestSceneSS(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut4.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float3 acc = 0;
    [unroll] for (int j = 0; j < 4; j++)
    [unroll] for (int i = 0; i < 4; i++)
    {
        float2 o = (float2(i, j) + 0.5f) / 4.0f;
        acc += sceneWithHud((float2(id.xy) + o) / float2(w, h), pan, sceneTime);
    }
    uOut4[id.xy] = float4(acc / 16.0f, 1.0f);
}

//=================================================================== present
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
cbuffer PresentParams : register(b1) { float2 pScale; float2 pOffset; };
VSOut PresentVS(uint vid : SV_VertexID)
{
    float2 p[3] = { float2(-1, 1), float2(3, 1), float2(-1, -3) };
    float2 t[3] = { float2(0, 0), float2(2, 0), float2(0, 2) };
    VSOut o; o.pos = float4(p[vid], 0, 1); o.uv = t[vid]; return o;
}
float4 PresentPS(VSOut i) : SV_Target
{
    float2 uv = i.uv * pScale + pOffset;
    if (any(uv < 0.0f) || any(uv > 1.0f)) return float4(0, 0, 0, 1);
    return float4(gColor.SampleLevel(linClamp, uv, 0).rgb, 1.0f);
}
VSOut PresentCursorVS(uint vid : SV_VertexID)
{
    float2 t[3] = { float2(0, 0), float2(2, 0), float2(0, 2) };
    float2 uv = t[vid];
    VSOut o;
    o.pos = float4(uv * pScale + pOffset, 0, 1);
    o.uv = uv;
    return o;
}
float4 PresentCursorPS(VSOut i) : SV_Target
{
    if (any(i.uv < 0.0f) || any(i.uv > 1.0f)) return float4(0, 0, 0, 0);
    return gColor.SampleLevel(linClamp, i.uv, 0);
}

//============================================== 1.3.4 frame-change detector
// Frame generation is skipped while the captured picture does not change. One 16x16 group samples a 64x64
// grid of the input, compares luma with the previous grid (u4, R32F 64x64) and writes the number of changed
// samples to u0[0,0] (R32F 1x1). The CPU reads it back two frames later from a staging ring (never stalls).
groupshared float gsChg[256];
groupshared float gsCut[256];
groupshared uint gsHa[32];
groupshared uint gsHb[32];
[numthreads(16, 16, 1)]
void FX_ChangeDetect(uint3 lt : SV_GroupThreadID)
{
    float n = 0.0f, nc = 0.0f;
    uint i = lt.y * 16 + lt.x;
    if (i < 32) { gsHa[i] = 0; gsHb[i] = 0; }
    GroupMemoryBarrierWithGroupSync();
    [loop] for (uint j = 0; j < 16; ++j) {
        uint2 q = lt.xy * 4 + uint2(j & 3, j >> 2);
        float2 uv = (float2(q) + 0.5f) / 64.0f;
        float l = luma709(gColor.SampleLevel(pntClamp, uv, 0).rgb);
        float dl = abs(l - uOut1[q]);
        if (historyValid < 0.5f || dl > staticThresh) n += 1.0f;
        if (historyValid > 0.5f && dl > 0.15f) nc += 1.0f;   // 1.3.4: strong change (scene-cut vote)
        uint dummy;
        InterlockedAdd(gsHa[min((uint)(saturate(uOut1[q]) * 32.0f), 31u)], 1u, dummy);
        InterlockedAdd(gsHb[min((uint)(saturate(l) * 32.0f), 31u)], 1u, dummy);
        uOut1[q] = l;
    }
    gsChg[i] = n; gsCut[i] = nc;
    GroupMemoryBarrierWithGroupSync();
    // 1.3.4: a fast pan also changes most pixels strongly, but keeps the luma histogram; a cut changes both.
    // Cut votes count only when the 32-bin histogram distance exceeds 0.35 (harness: pans <= 0.23, cuts ~0.8).
    float hd = 0.0f;
    if (i == 0) { [loop] for (uint b = 0; b < 32; ++b) hd += abs((float)gsHa[b] - (float)gsHb[b]); hd *= 0.5f / 4096.0f; }
    [unroll] for (uint s = 128; s > 0; s >>= 1) { if (i < s) { gsChg[i] += gsChg[i + s]; gsCut[i] += gsCut[i + s]; } GroupMemoryBarrierWithGroupSync(); }
    if (i == 0) { gLumaOut[uint2(0, 0)] = gsChg[0]; gLumaOut[uint2(1, 0)] = hd > 0.35f ? gsCut[0] : 0.0f; }
}

//=========================================== 1.3.4 fused multi-frame generation
// All N-1 frames of ×N in ONE pass (latency budget): the motion field is resolved once per pixel (fixed-point
// lookup at t = 0.5) and reused for every step, the plain-blend taps are shared, and each step costs two colour
// taps and one write. fastR.w = number of generated frames (1..7), written to u0..u6 in order (t = k/(n+1)).
// Same inputs and tile skipping as FX_FastInterp: t0 = previous output (A), t9 = current output (B), t4 = motion,
// t10 = tile counters.
RWTexture2D<float4> uG1 : register(u0);
RWTexture2D<float4> uG2 : register(u1);
RWTexture2D<float4> uG3 : register(u2);
RWTexture2D<float4> uG4 : register(u3);
RWTexture2D<float4> uG5 : register(u4);
RWTexture2D<float4> uG6 : register(u5);
RWTexture2D<float4> uG7 : register(u6);

// Round 4: generation prep for the fused pass: copies of A (t0) and B (t9) with the per-pixel static mask
// (max |A - B| < staticThresh) in alpha, written to u0 / u1 over the whole frame (no tile skipping, so warp ends that
// land in skipped tiles read current data). FX_FastInterpN then reads them in place of A and B (_pad2.y + 8).
[numthreads(8, 8, 1)]
void FX_FgPrep(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uG1.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float3 a = gColor.Load(int3(id.xy, 0)).rgb, b = tHistory.Load(int3(id.xy, 0)).rgb;
    float s = max3(abs(a - b)) < staticThresh ? 1.0f : 0.0f;
    uG1[id.xy] = float4(a, s);
    uG2[id.xy] = float4(b, s);
}

// Round 4: per-step vector for the fused pass. vh = field resolved at t = 0.5, v1 = field at the pixel itself.
// mode (_pad2.y): 0 = vh for every step (1.3.4 fused); 1 = one fixed-point motion tap at t for steps t <= 0.5
// (Competitive: harness 8x 32.53 -> 32.67 dB, flicks 44 px 32.0 -> 32.5, 64 px 27.8 -> 28.6); 2 = that tap for every
// step; 3 = two fixed-point taps from v1 at t, i.e. exactly the per-frame FX_FastInterp vector (Performance: same
// quality as the per-frame passes at ~70% of their generation cost); 4 = mode 1 during fast camera pans only, else 0
// (Competitive default: same flick quality as mode 1, -0.08 dB overall, no extra taps in slow scenes).
float2 fgVecT(float2 uv, float2 it, float2 vh, float2 v1, float t, float mode)
{
    float2 v = vh;
    [branch] if (mode > 2.5f) {
        v = tMotion.SampleLevel(linClamp, uv - (1.0f - t) * v1, 0).xy * it;
        v = tMotion.SampleLevel(linClamp, uv - (1.0f - t) * v, 0).xy * it;
    } else if (mode > 1.5f || (mode > 0.5f && t <= 0.5f)) {
        v = tMotion.SampleLevel(linClamp, uv - (1.0f - t) * vh, 0).xy * it;
    }
    return v;
}

[numthreads(8, 8, 1)]
void FX_FastInterpN(uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    int2 o0, o1;
    if (!fastTile(gid.xy, o0, o1)) return;
    int n = (int)fastR.w;
    float inv = 1.0f / (float)(n + 1);
    float2 it = 1.0f / inSize;
    for (int y = o0.y + (int)lt.y; y < o1.y; y += 8)
    for (int x = o0.x + (int)lt.x; x < o1.x; x += 8) {
        int2 p = int2(x, y);
        float2 uv = (float2(p) + 0.5f) / outSize;
        float3 a0 = gColor.SampleLevel(linClamp, uv, 0).rgb, b0 = tHistory.SampleLevel(linClamp, uv, 0).rgb;
        float4 mb = tMotion.SampleLevel(linClamp, uv, 0);
        float2 vb = mb.xy * it, v1 = vb;
        mb = tMotion.SampleLevel(linClamp, uv - 0.5f * vb, 0); vb = mb.xy * it;   // one fixed-point step at t = 0.5
        float mc = saturate(mb.z);
        // 1.3.4: static pixel (HUD) -> latest real frame, unwarped; scene cut -> nearest real frame (no cross-fade).
        bool st = max3(abs(a0 - b0)) < staticThresh, cut = sceneCut();
        if (st || cut) {
            float4 b4 = float4(b0, 1.0f), a4 = float4(a0, 1.0f), nb = cut ? a4 : b4;
            uG1[p] = (cut && 1.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 1) uG2[p] = (cut && 2.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 2) uG3[p] = (cut && 3.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 3) uG4[p] = (cut && 4.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 4) uG5[p] = (cut && 5.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 5) uG6[p] = (cut && 6.0f * inv >= 0.5f) || st ? b4 : nb;
            if (n > 6) uG7[p] = (cut && 7.0f * inv >= 0.5f) || st ? b4 : nb;
            continue;
        }
        if (_pad2.x > 0.5f) vb = pickVector(uv, it, vb);
        bool pre = _pad2.y > 7.5f;
        float vm = _pad2.y - (pre ? 8.0f : 0.0f);
        // mode 4 (Competitive): mode 1 during fast camera pans only (t17; same test as the FastFinal pick), else mode 0.
        if (vm > 3.5f) vm = length(gGlob.Load(int3(0, 0, 0)) * inSize * 0.125f) > 1.5f ? 1.0f : 0.0f;
        uG1[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 1.0f * inv, vm), mc, a0, b0, 1.0f * inv, pre);
        if (n > 1) uG2[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 2.0f * inv, vm), mc, a0, b0, 2.0f * inv, pre);
        if (n > 2) uG3[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 3.0f * inv, vm), mc, a0, b0, 3.0f * inv, pre);
        if (n > 3) uG4[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 4.0f * inv, vm), mc, a0, b0, 4.0f * inv, pre);
        if (n > 4) uG5[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 5.0f * inv, vm), mc, a0, b0, 5.0f * inv, pre);
        if (n > 5) uG6[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 6.0f * inv, vm), mc, a0, b0, 6.0f * inv, pre);
        if (n > 6) uG7[p] = fgStep(uv, fgVecT(uv, it, vb, v1, 7.0f * inv, vm), mc, a0, b0, 7.0f * inv, pre);
    }
}

//====================================== 1.3.4 generation motion filters (frame generation only)
// 1.3.4 quality: spatial propagation on the FINAL motion field (frame gen only, budget level < 4): each texel re-tests its 8
// neighbours' vectors against the two real output frames (t0 = A, t9 = B; B(x) ~ A(x + v)) on a 3x3 patch at motion-texel
// spacing and keeps the best (GPU-structured harness: +0.22 dB on top of the final-level pick).
float cleanCost(float2 uv, float2 vUV, float2 st)
{
    float c = 0;
    [unroll] for (int k = 0; k < 9; ++k) {
        float2 o = float2(k % 3 - 1, k / 3 - 1) * st;
        c += abs(luma709(tHistory.SampleLevel(linClamp, uv + o, 0).rgb) - luma709(gColor.SampleLevel(linClamp, uv + o + vUV, 0).rgb));
    }
    return c;
}
[numthreads(8, 8, 1)]
void FX_MvClean(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 hi = int2(W - 1, H - 1);
    float2 st = 1.0f / float2(W, H), uv = (float2(id.xy) + 0.5f) * st, it = 1.0f / inSize;
    float4 c = tMotion.Load(int3(id.xy, 0));
    float2 bv = c.xy; float bc = cleanCost(uv, c.xy * it, st);
    [unroll] for (int k = 0; k < 9; ++k) {
        if (k == 4) continue;
        float2 v = tMotion.Load(int3(clamp(int2(id.xy) + int2(k % 3 - 1, k / 3 - 1), 0, hi), 0)).xy;
        float cc = cleanCost(uv, v * it, st) + 0.02f;
        if (cc < bc) { bc = cc; bv = v; }
    }
    uOut4[id.xy] = float4(bv, c.z, c.w);
}

// 3x3 vector median (the neighbour vector with the smallest summed distance to the others) of the final low-res
// field: removes isolated wrong vectors that turn into tears in generated frames, keeps motion edges (a vector
// median never invents a vector). Upscalers keep the unfiltered field. t4 = motion (rg px, b conf), u3 = out.
[numthreads(8, 8, 1)]
void FX_MvMedian(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 hi = int2(W - 1, H - 1);
    float2 v[9]; float4 c = tMotion.Load(int3(id.xy, 0));
    [unroll] for (int k = 0; k < 9; ++k) v[k] = tMotion.Load(int3(clamp(int2(id.xy) + int2(k % 3 - 1, k / 3 - 1), 0, hi), 0)).xy;
    float best = 1e20f; float2 bv = c.xy;
    [unroll] for (int i = 0; i < 9; ++i) {
        float s = 0;
        [unroll] for (int j = 0; j < 9; ++j) s += length(v[i] - v[j]);
        if (s < best) { best = s; bv = v[i]; }
    }
    uOut4[id.xy] = float4(bv, c.z, c.w);
}

// 1.3.4 quality: frame borders. Where a vector's source lies off-screen (content entering during a pan) its patch cost
// is meaningless and the field there is mostly wrong. Within 24 texels of an edge, re-test the vectors 4, 8, 16 and 24
// texels further inside, with a cost over ON-SCREEN taps only (harness: +0.9 dB overall, fast pan +2.1 dB).
float borderCost(float2 uv, float2 vUV, float2 st)
{
    float c = 0, n = 0;
    [unroll] for (int k = 0; k < 9; ++k) {
        float2 q = uv + float2(k % 3 - 1, k / 3 - 1) * st, a = q + vUV;
        if (all(a >= 0.0f) && all(a <= 1.0f)) {
            c += abs(luma709(tHistory.SampleLevel(linClamp, q, 0).rgb) - luma709(gColor.SampleLevel(linClamp, a, 0).rgb)); n += 1.0f;
        }
    }
    // Sources off-screen: no evidence either way -> a neutral cost (~4/255 luma), so a vector carried in from further
    // inside beats a wrong on-screen match (harness: fast pan +1.4 dB vs refusing off-screen vectors).
    return n >= 3.0f ? c / n : 0.015f;
}
// round 4: same cost with the vector-independent tHistory lumas fetched once per texel
void histLuma9(float2 uv, float2 st, out float h[9])
{
    [unroll] for (int k = 0; k < 9; ++k) h[k] = luma709(tHistory.SampleLevel(linClamp, uv + float2(k % 3 - 1, k / 3 - 1) * st, 0).rgb);
}
float borderCostC(float h[9], float2 uv, float2 vUV, float2 st)
{
    float c = 0, n = 0;
    [unroll] for (int k = 0; k < 9; ++k) {
        float2 q = uv + float2(k % 3 - 1, k / 3 - 1) * st, a = q + vUV;
        if (all(a >= 0.0f) && all(a <= 1.0f)) { c += abs(h[k] - luma709(gColor.SampleLevel(linClamp, a, 0).rgb)); n += 1.0f; }
    }
    return n >= 3.0f ? c / n : 0.015f;
}
[numthreads(8, 8, 1)]
void FX_MvBorder(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float4 c = tMotion.Load(int3(id.xy, 0));
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    int2 dir = int2(p.x < 24 ? 1 : (p.x > hi.x - 24 ? -1 : 0), p.y < 24 ? 1 : (p.y > hi.y - 24 ? -1 : 0));
    if (all(dir == 0)) { uOut4[id.xy] = c; return; }
    float2 st = 1.0f / float2(W, H), uv = (float2(p) + 0.5f) * st, it = 1.0f / inSize;
    float hl[9]; histLuma9(uv, st, hl);
    float2 bv = c.xy; float bc = borderCostC(hl, uv, c.xy * it, st);
    [unroll] for (int k = 0; k < 4; ++k) {
        int o = k == 0 ? 4 : (k == 1 ? 8 : (k == 2 ? 16 : 24));
        float2 v = tMotion.Load(int3(clamp(p + dir * o, 0, hi), 0)).xy;
        float cc = borderCostC(hl, uv, v * it, st) + 0.002f;
        if (cc < bc) { bc = cc; bv = v; }
    }
    // 1.3.4 round 3: the winner's source or the camera pan's source is off-screen (content entering: an on-screen
    // match there is spurious) and the field 24 texels inside agrees with the global camera pan (within 1.5 texels)
    // -> take the pan (harness: fast pan +2.4 dB, zoom/occluder unchanged).
    float2 gPx = gGlob.Load(int3(0, 0, 0)) * inSize;
    if (any(abs(gPx) > 0.5f)) {
        float2 a = uv + bv * it, ag = uv + gPx * it;
        float2 vin = tMotion.Load(int3(clamp(p + dir * 24, 0, hi), 0)).xy;
        bool off = any(a < 0.0f) || any(a > 1.0f) || any(ag < 0.0f) || any(ag > 1.0f);
        if (off && length(vin - gPx) < 1.5f * inSize.x / float(W)) bv = gPx;
    }
    uOut4[id.xy] = float4(bv, c.z, c.w);
}

// 1.3.4 quality: next to a static overlay (HUD, crosshair, text) the static pixels in a patch pull the vector toward
// zero. A moving texel with a static pixel within 3 texels (ring test) re-tests the vectors 2, 4 and 8 texels away in
// the 4 directions with a cost over moving, on-screen taps only (harness: +0.42 dB after the border pass).
bool staticAt(float2 q) { return max3(abs(tHistory.SampleLevel(linClamp, q, 0).rgb - gColor.SampleLevel(linClamp, q, 0).rgb)) < staticThresh; }
float movingCost(float2 uv, float2 vUV, float2 st)
{
    float c = 0, n = 0;
    [unroll] for (int k = 0; k < 9; ++k) {
        float2 q = uv + float2(k % 3 - 1, k / 3 - 1) * st, a = q + vUV;
        if (all(a >= 0.0f) && all(a <= 1.0f) && !staticAt(q)) {
            c += abs(luma709(tHistory.SampleLevel(linClamp, q, 0).rgb) - luma709(gColor.SampleLevel(linClamp, a, 0).rgb)); n += 1.0f;
        }
    }
    return n >= 3.0f ? c / n : 1e3f;
}
float movingCostC(float h[9], bool mv[9], float2 uv, float2 vUV, float2 st)
{
    float c = 0, n = 0;
    [unroll] for (int k = 0; k < 9; ++k) {
        float2 q = uv + float2(k % 3 - 1, k / 3 - 1) * st, a = q + vUV;
        if (all(a >= 0.0f) && all(a <= 1.0f) && mv[k]) { c += abs(h[k] - luma709(gColor.SampleLevel(linClamp, a, 0).rgb)); n += 1.0f; }
    }
    return n >= 3.0f ? c / n : 1e3f;
}
groupshared uint gsHudSt[196];   // round 4: static flags of the group's 14x14 neighbourhood (texel centres, +-3)
[numthreads(8, 8, 1)]
void FX_MvHud(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
{
    uint W, H; uOut4.GetDimensions(W, H);
    float2 st = 1.0f / float2(W, H);
    // Round 4: skipped during fast camera pans (t17 bound; same test as the FastFinal pick): the re-test there picked
    // worse vectors for big flicks (harness 64 px/frame flick Performance 28.3 -> 29.2 dB, Competitive 28.6 -> 29.5;
    // overall +0.04 / +0.00 dB) and the fast-pan frames are the most expensive ones. UFX_FG_HUDPAN=0 keeps it.
    bool fastPan = length(gGlob.Load(int3(0, 0, 0)) * inSize * 0.125f) > 1.5f;
    if (!fastPan) {
        int2 org = int2(gid.xy) * 8 - 3;
        for (uint sI = gi; sI < 196; sI += 64) gsHudSt[sI] = staticAt((float2(org + int2(sI % 14, sI / 14)) + 0.5f) * st) ? 1u : 0u;
    }
    GroupMemoryBarrierWithGroupSync();
    if (id.x >= W || id.y >= H) return;
    float4 c = tMotion.Load(int3(id.xy, 0));
    if (fastPan) { uOut4[id.xy] = c; return; }
    float2 uv = (float2(id.xy) + 0.5f) * st, it = 1.0f / inSize;
    int li = (int(id.y) - (int(gid.y) * 8 - 3)) * 14 + (int(id.x) - (int(gid.x) * 8 - 3));   // this texel in gsHudSt
    bool near = false;
    if (gsHudSt[li] == 0u) {
        [unroll] for (int r = 0; r < 9; ++r) { if (r != 4 && gsHudSt[li + (r / 3 - 1) * 42 + (r % 3 - 1) * 3] != 0u) near = true; }
    }
    if (!near) { uOut4[id.xy] = c; return; }
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    // round 4: tHistory lumas and static flags of the 3x3 fetched once for all 13 candidates
    float hl[9]; bool mvk[9];
    [unroll] for (int q = 0; q < 9; ++q) {
        float2 qq = uv + float2(q % 3 - 1, q / 3 - 1) * st;
        hl[q] = luma709(tHistory.SampleLevel(linClamp, qq, 0).rgb); mvk[q] = gsHudSt[li + (q / 3 - 1) * 14 + (q % 3 - 1)] == 0u;
    }
    float2 bv = c.xy; float bc = movingCostC(hl, mvk, uv, c.xy * it, st);
    [loop] for (int k = 0; k < 12; ++k) {
        int o = k < 4 ? 2 : (k < 8 ? 4 : 8);
        int d = k & 3;
        int2 dir = d == 0 ? int2(1, 0) : (d == 1 ? int2(-1, 0) : (d == 2 ? int2(0, 1) : int2(0, -1)));
        float2 v = tMotion.Load(int3(clamp(p + dir * o, 0, hi), 0)).xy;
        float cc = movingCostC(hl, mvk, uv, v * it, st) + 0.002f;
        if (cc < bc) { bc = cc; bv = v; }
    }
    uOut4[id.xy] = float4(bv, c.z, c.w);
}

// 1.3.4 quality: motion failure guard. Beyond the search range (a flick faster than ~32 input px per frame at the
// 1/8 level) the field is garbage and warped frames score below simply repeating a frame (harness: 14 dB vs 18).
// On a 64x64 grid, count moving on-screen samples whose t = 0.5 warp ends disagree strongly; if more than 25% do
// (normal motion <= 4%, out-of-range pans 46-61%), vote a scene cut so generation shows the nearest real frame.
// Opt-in (UFX_FG_MOTIONFAIL=1): by PSNR/SSIM the repeat scores no better than the bad warp.
// t0 = A, t9 = B, t4 = generation motion (px), u0 = the change detector's 2x1 result ([1] = cut votes).
groupshared float gsBad[256];
groupshared float gsVal[256];
[numthreads(16, 16, 1)]
void FX_MotionFail(uint3 lt : SV_GroupThreadID)
{
    float bad = 0.0f, val = 0.0f;
    float2 it = 1.0f / inSize;
    [loop] for (uint j = 0; j < 16; ++j) {
        uint2 q = lt.xy * 4 + uint2(j & 3, j >> 2);
        float2 uv = (float2(q) + 0.5f) / 64.0f;
        float3 a0 = gColor.SampleLevel(linClamp, uv, 0).rgb, b0 = tHistory.SampleLevel(linClamp, uv, 0).rgb;
        if (max3(abs(a0 - b0)) < staticThresh) continue;
        float2 v = tMotion.SampleLevel(linClamp, uv, 0).xy * it;
        float2 pa = uv + 0.5f * v, pb = uv - 0.5f * v;
        if (!inside(pa) || !inside(pb)) continue;
        val += 1.0f;
        if (max3(abs(gColor.SampleLevel(linClamp, pa, 0).rgb - tHistory.SampleLevel(linClamp, pb, 0).rgb)) > 0.15f) bad += 1.0f;
    }
    uint i = lt.y * 16 + lt.x;
    gsBad[i] = bad; gsVal[i] = val;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 128; s > 0; s >>= 1) { if (i < s) { gsBad[i] += gsBad[i + s]; gsVal[i] += gsVal[i + s]; } GroupMemoryBarrierWithGroupSync(); }
    if (i == 0 && gsVal[0] >= 256.0f && gsBad[0] > 0.25f * gsVal[0]) gLumaOut[uint2(1, 0)] = 4096.0f;
}
