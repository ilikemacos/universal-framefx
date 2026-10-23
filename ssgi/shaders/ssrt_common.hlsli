// SSRT constants (register b1). The SSGI cbuffer at b0 is unchanged — existing entry points never read b1,
// so ufx_ssgi_execute_preset / _preset2 stay on the old layout (see ssgi_common.hlsli).
//
// flags bits: 1 = depth-aware GI march, 2 = depth horizon AO, 4 = reflections, 8 = contact shadows.
// ssrtTemporal stays 0 for CSDepthBlur (that hook does not blend). Accumulation is CSSsrtAccum:
// temporalMode 0 = reset, 1 = per-pixel motion, 2 = global pan. accumAlpha is the new-frame weight.
#ifndef UFX_SSRT_COMMON_HLSLI
#define UFX_SSRT_COMMON_HLSLI

cbuffer SsrtCB : register(b1)
{
    uint  flags;
    uint  reflSteps;
    uint  contactRays;
    uint  contactSteps;
    float reflStrength;
    float contactStrength;
    float thickness;
    float parallaxWeight;     // 0 when motion or pan is missing; the shader also rejects a trivial pan
    float horizonY;
    float depthGrad;          // multiplies depth gradients before the normal is normalised
    float aoDepthScale;
    float contactRadius;      // proxy cells
    float roughBlur;          // UV-ish blur of the reflection hit, scaled by roughness
    float edgeFade;
    float ssrtTemporal;       // 0 = do not read depth history
    float reflReach;          // march length in UV-height units (0.5 = half the screen)
    float4 wCue;              // horizon, contrast, defocus, luma
    float  wParallax;
    float  skyLuma;
    float  glossVarK;
    float  glossMin;
    float  giGain;
    float  panMinPx;
    float  glossBright;
    float  _padSsrt;          // unused; kept so CSSsrtAccum fields stay at the same offsets
    // CSSsrtAccum / CSSsrtCut. Appended so the fields above stay at the offsets the other entry points were compiled with.
    float  accumAlpha;        // weight of the current frame (1 = history ignored)
    float  depthReject;       // drop history when |depth - reprojected depth| exceeds this
    float  mvRejectPx;        // drop history when per-pixel motion disagrees by more than this (source pixels)
    float  lumCut;            // |mean luma - previous mean| above this resets the whole frame
    float  clipBox;           // 1 = clip history to the current 3x3; smaller pulls that box toward this texel
    float  forceReset;        // > 0.5 : no history this frame (first frame, resize, or no vectors)
    uint   temporalMode;      // 0 reset, 1 per-pixel motion SRV, 2 global pan only
    float  _padTemporal;
};

// Light-res smoothness lives in depth.w. Values >= 1.5 mean "leave this pixel alone"
// (sky or flat UI); the smoothness is w - 2. Consumers must unpack before using w.
float ssrtSmooth(float w) { return w >= 1.5f ? (w - 2.0f) : w; }
float ssrtProtect(float w) { return w >= 1.5f ? 1.0f : 0.0f; }
float ssrtPackSmooth(float smooth, float protect) { return protect > 0.5f ? (smooth + 2.0f) : smooth; }

#endif
