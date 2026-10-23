// Universal-FrameFX SSGI - flat C API so the C# (Vortice) app can P/Invoke a native ssgi.dll later.
// Not called by the app yet (experimental, off by default). All functions: render thread only (see ssgi_pipeline.h).
#include "ssgi_pipeline.h"

#include <new>

#if defined(_WIN32)
#define UFX_SSGI_API extern "C" __declspec(dllexport)
#else
#define UFX_SSGI_API extern "C"
#endif

using namespace ufx::ssgi;

// Create: device + compiled bytecode (fxc .cso). Returns nullptr on failure. Allocates (init time only).
UFX_SSGI_API void* ufx_ssgi_create(ID3D11Device* dev, const SsgiShaders* shaders, uint32_t w, uint32_t h, uint32_t lightDivisor) {
    auto* p = new (std::nothrow) SsgiPipeline();
    if (!p) return nullptr;
    if (FAILED(p->Init(dev, *shaders, w, h, lightDivisor))) { delete p; return nullptr; }
    return p;
}
UFX_SSGI_API int ufx_ssgi_resize(void* h, uint32_t w, uint32_t hgt, uint32_t lightDivisor) {
    return h ? (int)static_cast<SsgiPipeline*>(h)->Resize(w, hgt, lightDivisor) : -1;
}
// Per frame: no allocations, never blocks. Returns 1 if it ran.
UFX_SSGI_API int ufx_ssgi_execute(void* h, ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game,
                                  ID3D11RenderTargetView* out, const SsgiSettings* s) {
    return h && s && static_cast<SsgiPipeline*>(h)->Execute(ctx, game, out, *s) ? 1 : 0;
}
// r3: fill *out with a preset (0 = default, 1 = GTX1050Ti @1080p, 2 = GTX980Ti @1440p). Returns 0, or -1 if out is null.
UFX_SSGI_API int ufx_ssgi_preset(uint32_t preset, SsgiSettings* out) {
    if (!out) return -1;
    *out = MakePreset(static_cast<SsgiPreset>(preset > 2 ? 0 : preset));
    return 0;
}
// r5 (C# app): run with a preset (0 default, 1 GTX1050Ti, 2 GTX980Ti), enabled forced on, so the caller never marshals
// SsgiSettings. The settings object is built on the stack (no allocation). Returns 1 if it ran.
UFX_SSGI_API int ufx_ssgi_execute_preset(void* h, ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game,
                                         ID3D11RenderTargetView* out, uint32_t preset) {
    if (!h) return 0;
    SsgiSettings s = MakePreset(static_cast<SsgiPreset>(preset > 2 ? 0 : preset));
    s.enabled = true;
    return static_cast<SsgiPipeline*>(h)->Execute(ctx, game, out, s) ? 1 : 0;
}
// r6. Tuned by tests/harness/ssgi_harness.py --pan-test (see README Round 6).
// Pan-test (gtx1050ti, 1280x720, 24 frames, 4 rotations): alpha 0.22, guide 8, full 3x3 box.
// Flicker -38.7% vs no temporal, but moving-object ghost p99 is 1.58x, so the feature stays off by default.
static constexpr float kExtAlpha = 0.22f, kExtGuide = 8.0f, kExtLum = 0.0502f, kExtPanPx = 48.0f, kExtClamp = 1.0f;
// pan may be null (1x1 R32G32_FLOAT, FX_GlobalMotion backward UV, sampled on the GPU). temporal==0, or a null pan,
// leaves this frame unaccumulated. The old export above is unchanged (temporal off).
UFX_SSGI_API int ufx_ssgi_execute_preset2(void* h, ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game,
                                          ID3D11RenderTargetView* out, uint32_t preset,
                                          ID3D11ShaderResourceView* pan, int temporal) {
    if (!h) return 0;
    SsgiSettings s = MakePreset(static_cast<SsgiPreset>(preset > 2 ? 0 : preset));
    s.enabled = true;
    s.temporal = temporal != 0;
    s.temporalPan = false;
    s.temporalExt = temporal != 0;
    s.temporalAlpha = kExtAlpha;
    s.guideReject = kExtGuide;
    s.lumReset = kExtLum;
    s.panMaxPx = kExtPanPx;
    s.clampBox = kExtClamp;
    return static_cast<SsgiPipeline*>(h)->Execute(ctx, game, out, s, pan) ? 1 : 0;
}
UFX_SSGI_API void ufx_ssgi_destroy(void* h) { delete static_cast<SsgiPipeline*>(h); }

// SSRT. Shaders are separate from SsgiShaders so ufx_ssgi_create's layout does not change.
// ufx_ssrt_init once, after ufx_ssgi_create. ufx_ssrt_execute preset: 1 = GTX1050Ti_1080p, 2 = GTX980Ti_1440p.
// flags: bit0 depth-aware GI, bit1 depth AO, bit2 reflections, bit3 contact shadows. 0 runs ufx_ssgi_execute_preset's
// path (MakePreset + Execute, temporal off). motion may be null (per-pixel xy in source pixels). pan may be null
// (1x1 backward UV). Either null forces the parallax cue off. temporal != 0 reprojects history when
// motion (per-pixel, source pixels) or, failing that, pan is bound. Neither bound resets that frame.
UFX_SSGI_API int ufx_ssrt_init(void* h, const SsrtShaders* shaders) {
    if (!h || !shaders) return -1;
    return (int)static_cast<SsgiPipeline*>(h)->InitSsrt(*shaders);
}
UFX_SSGI_API int ufx_ssrt_execute(void* h, ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game,
                                  ID3D11RenderTargetView* out, uint32_t preset, uint32_t flags,
                                  ID3D11ShaderResourceView* motion, ID3D11ShaderResourceView* pan, int temporal) {
    if (!h) return 0;
    return static_cast<SsgiPipeline*>(h)->ExecuteSsrt(ctx, game, out, preset, flags, motion, pan, temporal) ? 1 : 0;
}
