// Universal-FrameFX SSGI - D3D11 side: resources, the per-frame GPU work and the staging read-back ring.
// EXPERIMENTAL. The C# overlay calls ufx_ssgi_execute_preset / _preset2 when the user turns SSGI on
// (see windows/ssgi/README.md). SsgiSettings::enabled still defaults to false.
//
// Thread safety: every method must be called on the thread that owns the ID3D11DeviceContext (the render thread);
// D3D11 immediate contexts are not thread-safe. The only cross-thread traffic is through SsgiWorker's wait-free
// triple buffers (see ssgi_worker.h). The render loop never waits for the GPU or the worker:
//   * read-back: CopyResource into a 3-deep ring of 64 KB STAGING buffers, Map(READ, DO_NOT_WAIT) on the oldest; if
//     the GPU has not finished it (DXGI_ERROR_WAS_STILL_DRAWING) the frame is skipped, the CPU never blocks.
//   * upload: the 2 KB ImportanceMap goes into a DYNAMIC structured buffer with Map(WRITE_DISCARD) (driver renames the
//     buffer, no stall), only when the worker has published a newer map.
// Zero allocations per frame: every resource / view is created in Init()/Resize(); Execute() only binds and dispatches.
#pragma once
#include <d3d11.h>
#include <cstdint>
#include <memory>

#include "ssgi_worker.h"

namespace ufx::ssgi {

struct SsgiSettings {
    bool     enabled = false;      // OFF by default
    float    strength = 1.5f;      // composite bounce strength (linear light, x receiver albedo; r3 horizon march)
    float    aoStrength = 0.5f;    // pseudo-AO depth (scales the bounce only: Final = Game + strength * Bounce * AO)
    uint32_t marchSteps = 12;      // 8..32
    uint32_t raysPerTexel = 2;     // 1..8
    float    stepCells = 1.5f;
    float    emitLo = 0.35f, emitHi = 0.9f, edgeLuma = 0.12f, falloff = 0.002f, rangeK = 40.0f;
    uint32_t lightDivisor = 4;     // lighting texture = source / divisor (clamped to >= 128 x 128)
    bool     forceAll = false;     // ignore the importance map (debug)
    // r3: horizon march on a luminance pseudo height field + optional temporal accumulation
    float    heightScale = 24.0f;  // cells of height for B = 1 (tuned in the harness)
    float    stepGrowth = 1.12f;   // geometric step growth (1..1.5)
    float    horizonBias = 0.25f;  // initial horizon sin(-angle)
    bool     temporal = false;     // OFF by default: no motion vectors, static-camera assumption (see README)
    float    temporalAlpha = 0.25f;
    uint8_t  reachLog2X = 5, reachLog2Y = 6;   // importance-map emitter dilation (2^n - 1 cells); r4: 5 / 6 everywhere
    // r4
    float    aoRadius = 6.0f;      // near-field AO radius (cells)
    float    aoGame = 0.35f;       // AO on the game image, clamped to <= 0.6 (cannot crush to black)
    bool     temporalPan = true;   // reproject history by the GPU global-shift estimate (only used when temporal)
    float    panMaxSad = 0.06f;    // reject the shift estimate above this mean |dB| (history reset)
    // r6: FrameFX FX_GlobalMotion (backward UV). Used by ufx_ssgi_execute_preset2; the r4 CSShift path stays for the old API.
    bool     temporalExt = false;
    float    panMaxPx = 48.0f;     // reset above this pan length in source pixels (cut / too fast)
    float    lumReset = 0.0502f;   // reset above this pan-aligned mean |dB| (between pan p95 and a cut)
    float    guideReject = 8.0f;   // history weight hits 0 at |dGuide| = 1 / guideReject
    float    clampBox = 1.0f;      // 1 = full 3x3 min/max; smaller pulls the box toward the current texel
};

// r3 presets. Cost numbers are ESTIMATES (no GPU on the build box), see README "Round 3".
enum class SsgiPreset : uint32_t { Default = 0, GTX1050Ti = 1, GTX980Ti = 2 };
inline SsgiSettings MakePreset(SsgiPreset p) {
    SsgiSettings s;
    switch (p) {
    case SsgiPreset::GTX1050Ti:   // target 1080p, ~0.9-1.2 ms est.: 480 x 270 lighting, 4 rays x 16 steps
        s.reachLog2X = 5; s.reachLog2Y = 6;
        s.lightDivisor = 4; s.raysPerTexel = 4; s.marchSteps = 16; s.stepCells = 1.0f; s.stepGrowth = 1.15f; break;
    case SsgiPreset::GTX980Ti:    // target 1440p, ~0.9-1.3 ms est.: 853 x 480 lighting, 6 rays x 20 steps
        s.reachLog2X = 5; s.reachLog2Y = 6;
        s.lightDivisor = 3; s.raysPerTexel = 6; s.marchSteps = 20; s.stepCells = 0.8f; s.stepGrowth = 1.13f; break;
    default: break;
    }
    return s;
}

// Compiled shader bytecode (fxc /T cs_5_0 etc., see tests/fxc-check-ssgi.sh which writes build/cso/*.cso).
struct ShaderBytecode { const void* data; size_t size; };
struct SsgiShaders { ShaderBytecode downsample, march, filter, shift, vsFullscreen, psComposite, panGate; };   // r4: + shift; r6: + panGate (CSPanGate)

// SSRT (screen-space depth / reflections / contact). Separate from SsgiShaders so the C# P/Invoke
// layout of ufx_ssgi_create is unchanged. flags: bit0 GI, bit1 AO, bit2 reflections, bit3 contact shadows.
enum SsrtFlags : uint32_t { SsrtGi = 1u, SsrtAo = 2u, SsrtReflect = 4u, SsrtContact = 8u };
enum class SsrtPresetId : uint32_t { GTX1050Ti_1080p = 1, GTX980Ti_1440p = 2 };

// Tunables mirrored by tests/harness/ssrt_ref.py (SSRT_PRESETS). Cost model must stay <= 2.0 ms — see README.
struct SsrtPreset {
    SsgiSettings gi;
    uint32_t reflSteps = 8, contactRays = 2, contactSteps = 5;
    float reflStrength = 0.45f, contactStrength = 0.55f, thickness = 0.08f, parallaxWeight = 0.40f;
    float horizonY = 0.42f, depthGrad = 600.0f, aoDepthScale = 2.5f, contactRadius = 3.5f;
    float roughBlur = 0.02f, edgeFade = 8.0f, reflReach = 0.80f;
    float wHorizon = 0.62f, wContrast = 0.10f, wDefocus = 0.10f, wLuma = 0.18f, wParallax = 0.40f;
    float skyLuma = 0.62f, glossVarK = 90.0f, glossMin = 0.45f, giGain = 1.55f, panMinPx = 1.5f, glossBright = 2.5f;
};
inline SsrtPreset MakeSsrtPreset(SsrtPresetId id) {
    SsrtPreset p;
    if (id == SsrtPresetId::GTX980Ti_1440p) {
        p.gi = MakePreset(SsgiPreset::GTX980Ti);
        p.gi.raysPerTexel = 4; p.gi.marchSteps = 14; p.gi.stepCells = 0.9f; p.gi.stepGrowth = 1.12f;
        p.reflSteps = 8; p.contactRays = 2; p.contactSteps = 6;
    } else {
        p.gi = MakePreset(SsgiPreset::GTX1050Ti);
        // /5 keeps the temporal pass inside the 2 ms estimate (the /4 composite is already ~0.9 ms).
        p.gi.lightDivisor = 5;
        p.gi.raysPerTexel = 3; p.gi.marchSteps = 10; p.gi.stepCells = 1.0f; p.gi.stepGrowth = 1.12f;
        p.reflSteps = 8; p.contactRays = 2; p.contactSteps = 5;
    }
    return p;
}
struct SsrtShaders {
    ShaderBytecode depth, depthBlur, march, lightDir, reflect, contact, pack, psComposite, cut, accum;
};

class SsgiPipeline {
public:
    SsgiPipeline() = default;
    ~SsgiPipeline() { Shutdown(); }
    SsgiPipeline(const SsgiPipeline&) = delete;
    SsgiPipeline& operator=(const SsgiPipeline&) = delete;

    // Init / Resize / Shutdown: render thread, outside the per-frame path (they allocate).
    HRESULT Init(ID3D11Device* dev, const SsgiShaders& sh, uint32_t srcW, uint32_t srcH, uint32_t lightDivisor = 4);
    HRESULT Resize(uint32_t srcW, uint32_t srcH, uint32_t lightDivisor);
    void Shutdown() noexcept;

    // Per frame, render thread. game: SRV of the captured frame (srcW x srcH). out: RTV of a DIFFERENT texture of the
    // same size that receives Game + strength * Bounce * AO. Does nothing (returns false) if disabled / not initialised.
    // State touched: CS shader/SRV t0-t2/UAV u0/CB b0/sampler s0, VS, PS + SRV t0-t1 + CB b0, IA topology + null layout,
    // OM render target, RS viewport. Hazard slots are unbound right after use; nothing else is saved or restored (the
    // caller's present pass rebinds its own state anyway, which keeps state changes minimal).
    // pan: optional 1x1 R32G32_FLOAT SRV, FX_GlobalMotion's backward UV (curr came from prev at uv+pan).
    // Read on the GPU. Null, or s.temporalExt left false, keeps the previous behaviour (no external reprojection).
    bool Execute(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game, ID3D11RenderTargetView* out, const SsgiSettings& s,
                 ID3D11ShaderResourceView* pan = nullptr) noexcept;

    // Extra shaders + targets. Fails if Init() has not run. Allocates (not a per-frame call).
    HRESULT InitSsrt(const SsrtShaders& sh);
    // preset: 1 = GTX1050Ti_1080p, 2 = GTX980Ti_1440p. flags == 0 runs the existing Execute() with
    // MakePreset (bit-identical to ufx_ssgi_execute_preset). motion / pan may be null (parallax weight 0).
    // motion: per-pixel xy in source pixels (FrameFX _mvLo). pan: 1x1 backward UV (FX_GlobalMotion).
    // temporal != 0 reprojects history. motion (per-pixel, source pixels) wins over pan (1x1 backward UV).
    // Neither bound resets history for that frame. flags == 0 ignores temporal and runs Execute().
    bool ExecuteSsrt(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game, ID3D11RenderTargetView* out,
                     uint32_t preset, uint32_t flags, ID3D11ShaderResourceView* motion,
                     ID3D11ShaderResourceView* pan, int temporal) noexcept;

    SsgiWorker* Worker() noexcept { return worker_.get(); }
    uint64_t SkippedReadbacks() const noexcept { return skipped_; }

private:
    template <class T> static void Release(T*& p) noexcept { if (p) { p->Release(); p = nullptr; } }
    void ReleaseSized() noexcept;

    static constexpr int kRing = 3;
    ID3D11Device* dev_ = nullptr;
    ID3D11ComputeShader* csDown_ = nullptr; ID3D11ComputeShader* csMarch_ = nullptr; ID3D11ComputeShader* csFilter_ = nullptr; ID3D11ComputeShader* csShift_ = nullptr; ID3D11ComputeShader* csPan_ = nullptr;
    ID3D11VertexShader* vs_ = nullptr; ID3D11PixelShader* ps_ = nullptr;
    ID3D11SamplerState* linear_ = nullptr;
    ID3D11Buffer* cb_ = nullptr;                                   // DYNAMIC constant buffer, WRITE_DISCARD per frame
    ID3D11Buffer* grid_ = nullptr;                                 // 64 KB structured buffer (DEFAULT)
    ID3D11UnorderedAccessView* gridUav_ = nullptr; ID3D11ShaderResourceView* gridSrv_ = nullptr;
    ID3D11Buffer* prevGrid_ = nullptr; ID3D11ShaderResourceView* prevGridSrv_ = nullptr;   // r4: last frame's grid
    ID3D11Buffer* shift_ = nullptr;                                // r4: 1 x float4 global shift (GPU only)
    ID3D11UnorderedAccessView* shiftUav_ = nullptr; ID3D11ShaderResourceView* shiftSrv_ = nullptr;
    ID3D11Buffer* staging_[kRing] = {};                            // 64 KB STAGING, CPU_ACCESS_READ
    bool stagingPending_[kRing] = {};
    ID3D11Buffer* importance_ = nullptr;                           // 2 KB DYNAMIC structured buffer
    ID3D11ShaderResourceView* importanceSrv_ = nullptr;
    ID3D11Texture2D* light_ = nullptr;                             // low-res RGBA8 lighting texture (march output)
    ID3D11UnorderedAccessView* lightUav_ = nullptr; ID3D11ShaderResourceView* lightSrv_ = nullptr;
    ID3D11Texture2D* lightB_[2] = {};                              // r2: after CSFilter; r3: ping-pong (history)
    ID3D11UnorderedAccessView* lightBUav_[2] = {}; ID3D11ShaderResourceView* lightBSrv_[2] = {};
    bool historyValid_ = false;
    bool prevValid_ = false;                                       // r6: prevGrid_ holds the previous frame (temporalExt)
    uint32_t cfgFp_ = 0;                                           // r6: rays/steps/divisor; a change drops history
    uint16_t reach_ = 0x0605;                                      // last reach sent to the worker (x | y << 8)
    uint32_t srcW_ = 0, srcH_ = 0, lightW_ = 0, lightH_ = 0;
    uint64_t frame_ = 0, skipped_ = 0;
    std::unique_ptr<SsgiWorker> worker_;

    bool UploadCB(ID3D11DeviceContext* ctx, const SsgiSettings& s, bool blendExt, bool blendOld) noexcept;
    HRESULT CreateSsrtSized() noexcept;
    void ReleaseSsrtSized() noexcept;

    bool ssrt_ = false;
    ID3D11ComputeShader* csDepth_ = nullptr; ID3D11ComputeShader* csDepthBlur_ = nullptr;
    ID3D11ComputeShader* csMarchD_ = nullptr; ID3D11ComputeShader* csLightDir_ = nullptr;
    ID3D11ComputeShader* csReflect_ = nullptr; ID3D11ComputeShader* csContact_ = nullptr;
    ID3D11ComputeShader* csPack_ = nullptr; ID3D11PixelShader* psSsrt_ = nullptr;
    ID3D11ComputeShader* csCut_ = nullptr; ID3D11ComputeShader* csAccum_ = nullptr;
    ID3D11Buffer* cbSsrt_ = nullptr;
    ID3D11Texture2D* raw_ = nullptr; ID3D11UnorderedAccessView* rawUav_ = nullptr; ID3D11ShaderResourceView* rawSrv_ = nullptr;
    ID3D11Texture2D* depth_ = nullptr; ID3D11UnorderedAccessView* depthUav_ = nullptr; ID3D11ShaderResourceView* depthSrv_ = nullptr;
    ID3D11Texture2D* depthHist_ = nullptr; ID3D11ShaderResourceView* depthHistSrv_ = nullptr;   // unused by the blur; kept so older binds stay valid
    ID3D11Texture2D* accGi_[2] = {}; ID3D11UnorderedAccessView* accGiUav_[2] = {}; ID3D11ShaderResourceView* accGiSrv_[2] = {};
    ID3D11Texture2D* accDepth_[2] = {}; ID3D11UnorderedAccessView* accDepthUav_[2] = {}; ID3D11ShaderResourceView* accDepthSrv_[2] = {};
    ID3D11Texture2D* accRefl_[2] = {}; ID3D11UnorderedAccessView* accReflUav_[2] = {}; ID3D11ShaderResourceView* accReflSrv_[2] = {};
    ID3D11Texture2D* accSh_[2] = {}; ID3D11UnorderedAccessView* accShUav_[2] = {}; ID3D11ShaderResourceView* accShSrv_[2] = {};
    ID3D11Texture2D* cut_[2] = {}; ID3D11UnorderedAccessView* cutUav_[2] = {}; ID3D11ShaderResourceView* cutSrv_[2] = {};
    uint32_t accPing_ = 0;
    bool ssrtHist_ = false;
    ID3D11Texture2D* refl_ = nullptr; ID3D11UnorderedAccessView* reflUav_ = nullptr; ID3D11ShaderResourceView* reflSrv_ = nullptr;
    ID3D11Texture2D* shadow_ = nullptr; ID3D11UnorderedAccessView* shadowUav_ = nullptr; ID3D11ShaderResourceView* shadowSrv_ = nullptr;
    ID3D11Texture2D* packed_ = nullptr; ID3D11UnorderedAccessView* packedUav_ = nullptr; ID3D11ShaderResourceView* packedSrv_ = nullptr;
    ID3D11Buffer* lightDir_ = nullptr; ID3D11UnorderedAccessView* lightDirUav_ = nullptr; ID3D11ShaderResourceView* lightDirSrv_ = nullptr;
    ID3D11Texture2D* dummyMotion_ = nullptr; ID3D11ShaderResourceView* dummyMotionSrv_ = nullptr;
    ID3D11Texture2D* dummyPan_ = nullptr; ID3D11ShaderResourceView* dummyPanSrv_ = nullptr;
};

}  // namespace ufx::ssgi
