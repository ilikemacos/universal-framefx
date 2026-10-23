// Universal-FrameFX SSGI - D3D11 pipeline implementation. See ssgi_pipeline.h for the threading / allocation rules.
#include "ssgi_pipeline.h"

#include <algorithm>
#include <cstring>

namespace ufx::ssgi {
namespace {

// Mirrors cbuffer SsgiConstants in shaders/ssgi_common.hlsli (16-byte packing, 144 bytes).
struct alignas(16) Constants {
    uint32_t srcW, srcH, lightW, lightH;
    float invSrcW, invSrcH, invLightW, invLightH;
    uint32_t frameIndex, marchSteps, raysPerTexel; float stepCells;
    float strength, aoStrength, emitLo, emitHi;
    float edgeLuma, falloff, rangeK; uint32_t forceAll;
    float heightScale, stepGrowth, temporalAlpha, horizonBias;
    float aoRadius, aoGame; uint32_t temporalPan; float panMaxSad;
    float panMaxPx, lumReset, guideReject; uint32_t temporalExt;
    float clampBox, padClamp0, padClamp1, padClamp2;
};
static_assert(sizeof(Constants) == 144, "must match ssgi_common.hlsli");

HRESULT MakeBuffer(ID3D11Device* d, UINT bytes, D3D11_USAGE usage, UINT bind, UINT cpu, UINT misc, UINT stride,
                   const void* init, ID3D11Buffer** out) {
    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = bytes; bd.Usage = usage; bd.BindFlags = bind; bd.CPUAccessFlags = cpu; bd.MiscFlags = misc;
    bd.StructureByteStride = stride;
    D3D11_SUBRESOURCE_DATA sd{ init, 0, 0 };
    return d->CreateBuffer(&bd, init ? &sd : nullptr, out);
}

}  // namespace

HRESULT SsgiPipeline::Init(ID3D11Device* dev, const SsgiShaders& sh, uint32_t srcW, uint32_t srcH, uint32_t lightDivisor) {
    Shutdown();
    dev_ = dev; dev_->AddRef();
    HRESULT hr;
    if (FAILED(hr = dev->CreateComputeShader(sh.downsample.data, sh.downsample.size, nullptr, &csDown_))) return hr;
    if (FAILED(hr = dev->CreateComputeShader(sh.march.data, sh.march.size, nullptr, &csMarch_))) return hr;
    if (FAILED(hr = dev->CreateComputeShader(sh.filter.data, sh.filter.size, nullptr, &csFilter_))) return hr;
    if (FAILED(hr = dev->CreateComputeShader(sh.shift.data, sh.shift.size, nullptr, &csShift_))) return hr;
    if (FAILED(hr = dev->CreateComputeShader(sh.panGate.data, sh.panGate.size, nullptr, &csPan_))) return hr;
    if (FAILED(hr = dev->CreateVertexShader(sh.vsFullscreen.data, sh.vsFullscreen.size, nullptr, &vs_))) return hr;
    if (FAILED(hr = dev->CreatePixelShader(sh.psComposite.data, sh.psComposite.size, nullptr, &ps_))) return hr;

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(hr = dev->CreateSamplerState(&sd, &linear_))) return hr;
    if (FAILED(hr = MakeBuffer(dev, sizeof(Constants), D3D11_USAGE_DYNAMIC, D3D11_BIND_CONSTANT_BUFFER,
                               D3D11_CPU_ACCESS_WRITE, 0, 0, nullptr, &cb_))) return hr;

    // Proxy grid (GPU writes in Phase 1, reads in Phase 3) + its read-back ring.
    if (FAILED(hr = MakeBuffer(dev, kCells * 4, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE,
                               0, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, 4, nullptr, &grid_))) return hr;
    if (FAILED(hr = dev->CreateUnorderedAccessView(grid_, nullptr, &gridUav_))) return hr;
    if (FAILED(hr = dev->CreateShaderResourceView(grid_, nullptr, &gridSrv_))) return hr;
    if (FAILED(hr = MakeBuffer(dev, kCells * 4, D3D11_USAGE_DEFAULT, D3D11_BIND_SHADER_RESOURCE,
                               0, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, 4, nullptr, &prevGrid_))) return hr;
    if (FAILED(hr = dev->CreateShaderResourceView(prevGrid_, nullptr, &prevGridSrv_))) return hr;
    if (FAILED(hr = MakeBuffer(dev, 16, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE,
                               0, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, 16, nullptr, &shift_))) return hr;
    if (FAILED(hr = dev->CreateUnorderedAccessView(shift_, nullptr, &shiftUav_))) return hr;
    if (FAILED(hr = dev->CreateShaderResourceView(shift_, nullptr, &shiftSrv_))) return hr;
    for (auto& s : staging_)
        if (FAILED(hr = MakeBuffer(dev, kCells * 4, D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_READ, 0, 0, nullptr, &s))) return hr;

    // ImportanceMap: starts all ones (march everything) until the worker's first map arrives.
    uint32_t ones[kMaskWords]; std::memset(ones, 0xFF, sizeof(ones));
    if (FAILED(hr = MakeBuffer(dev, kMaskWords * 4, D3D11_USAGE_DYNAMIC, D3D11_BIND_SHADER_RESOURCE, D3D11_CPU_ACCESS_WRITE,
                               D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, 4, ones, &importance_))) return hr;
    if (FAILED(hr = dev->CreateShaderResourceView(importance_, nullptr, &importanceSrv_))) return hr;

    worker_ = std::make_unique<SsgiWorker>();                      // starts the jthread (init-time allocation)
    return Resize(srcW, srcH, lightDivisor);
}

HRESULT SsgiPipeline::Resize(uint32_t srcW, uint32_t srcH, uint32_t lightDivisor) {
    if (!dev_) return E_FAIL;
    if (srcW == srcW_ && srcH == srcH_ && light_ && lightW_ == std::max<uint32_t>(kGrid, srcW / std::max(1u, lightDivisor))) return S_OK;
    ReleaseSized();
    srcW_ = srcW; srcH_ = srcH;
    lightDivisor = std::max(1u, lightDivisor);
    lightW_ = std::max<uint32_t>(kGrid, srcW / lightDivisor);     // the march's group test assumes >= 128 per axis
    lightH_ = std::max<uint32_t>(kGrid, srcH / lightDivisor);
    D3D11_TEXTURE2D_DESC td{};
    td.Width = lightW_; td.Height = lightH_; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R32G32_UINT;                           // r4: 64-bit packed (bounce 11/11/10, guide, AO); typed UAV store required at FL11
    td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr;
    if (FAILED(hr = dev_->CreateTexture2D(&td, nullptr, &light_))) return hr;
    if (FAILED(hr = dev_->CreateUnorderedAccessView(light_, nullptr, &lightUav_))) return hr;
    if (FAILED(hr = dev_->CreateShaderResourceView(light_, nullptr, &lightSrv_))) return hr;
    for (int i = 0; i < 2; ++i) {                                  // ping-pong: filtered output + temporal history
        if (FAILED(hr = dev_->CreateTexture2D(&td, nullptr, &lightB_[i]))) return hr;
        if (FAILED(hr = dev_->CreateUnorderedAccessView(lightB_[i], nullptr, &lightBUav_[i]))) return hr;
        if (FAILED(hr = dev_->CreateShaderResourceView(lightB_[i], nullptr, &lightBSrv_[i]))) return hr;
    }
    historyValid_ = false;
    prevValid_ = false;
    return ssrt_ ? CreateSsrtSized() : S_OK;
}

void SsgiPipeline::ReleaseSized() noexcept {
    ReleaseSsrtSized();
    Release(lightSrv_); Release(lightUav_); Release(light_); for (int i = 0; i < 2; ++i) { Release(lightBSrv_[i]); Release(lightBUav_[i]); Release(lightB_[i]); }
    historyValid_ = false;
    prevValid_ = false;
}

void SsgiPipeline::Shutdown() noexcept {
    worker_.reset();                                               // stops + joins the worker before resources go
    ReleaseSized();
    Release(importanceSrv_); Release(importance_);
    for (auto& s : staging_) Release(s);
    for (auto& p : stagingPending_) p = false;
    Release(gridSrv_); Release(gridUav_); Release(grid_);
    Release(prevGridSrv_); Release(prevGrid_); Release(shiftSrv_); Release(shiftUav_); Release(shift_);
    Release(cb_); Release(linear_);
    Release(psSsrt_); Release(csPack_); Release(csContact_); Release(csReflect_); Release(csLightDir_);
    Release(csMarchD_); Release(csDepthBlur_); Release(csDepth_);
    Release(csAccum_); Release(csCut_);
    Release(cbSsrt_);
    Release(lightDirSrv_); Release(lightDirUav_); Release(lightDir_);
    Release(dummyPanSrv_); Release(dummyPan_); Release(dummyMotionSrv_); Release(dummyMotion_);
    Release(ps_); Release(vs_); Release(csPan_); Release(csShift_); Release(csFilter_); Release(csMarch_); Release(csDown_);
    ssrt_ = false;
    Release(dev_);
    srcW_ = srcH_ = lightW_ = lightH_ = 0;
}

bool SsgiPipeline::Execute(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game, ID3D11RenderTargetView* out,
                           const SsgiSettings& s, ID3D11ShaderResourceView* pan) noexcept {
    if (!s.enabled || !light_ || !lightB_[0] || !worker_ || !game || !out) return false;
    // Preset / march-shape change: the accumulated lighting is no longer the same estimator.
    if (const uint32_t fp = s.raysPerTexel | (s.marchSteps << 8) | (s.lightDivisor << 16); fp != cfgFp_) {
        historyValid_ = false; prevValid_ = false; cfgFp_ = fp;
    }
    if (const uint16_t r = (uint16_t)(s.reachLog2X | (s.reachLog2Y << 8)); r != reach_) {   // longer r3 march reach
        ImportanceParams ip; ip.reachLog2X = std::min<uint8_t>(s.reachLog2X, 5); ip.reachLog2Y = std::min<uint8_t>(s.reachLog2Y, 6);
        worker_->SetParams(ip); reach_ = r;                         // atomic store, no allocation, no wait
    }
    const uint32_t slot = (uint32_t)(frame_ % kRing);

    // 1. Read back the OLDEST pending grid (written kRing - 1 frames ago). DO_NOT_WAIT: never blocks; if the GPU is
    //    still busy with it we skip and try again next frame. 64 KB memcpy into the worker's private slot, then unmap.
    {
        const uint32_t old = (uint32_t)((frame_ + 1) % kRing);
        if (stagingPending_[old]) {
            D3D11_MAPPED_SUBRESOURCE m;
            HRESULT hr = ctx->Map(staging_[old], 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
            if (SUCCEEDED(hr)) {
                GridFrame& g = worker_->BeginGridWrite();
                std::memcpy(g.cells, m.pData, sizeof(g.cells));
                ctx->Unmap(staging_[old], 0);
                stagingPending_[old] = false;
                worker_->PublishGrid(frame_ - (kRing - 1));
            } else {
                ++skipped_;                                         // DXGI_ERROR_WAS_STILL_DRAWING (or device loss)
            }
        }
    }
    // 2. Newest ImportanceMap from the worker -> DYNAMIC buffer (WRITE_DISCARD: renamed by the driver, no stall).
    {
        const MaskFrame* mf = nullptr;
        if (worker_->TryAcquireMask(mf)) {
            D3D11_MAPPED_SUBRESOURCE m;
            if (SUCCEEDED(ctx->Map(importance_, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) {
                std::memcpy(m.pData, mf->words, sizeof(mf->words));
                ctx->Unmap(importance_, 0);
            }
        }
    }
    // 3. Constants (one WRITE_DISCARD per frame).
    const bool ext = s.temporal && s.temporalExt;
    const bool blendExt = ext && pan && historyValid_ && prevValid_;
    const bool blendOld = s.temporal && !s.temporalExt && historyValid_;
    if (!UploadCB(ctx, s, blendExt, blendOld)) return false;
    ID3D11ShaderResourceView* const nullSrv[2] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* const nullUav[1] = { nullptr };

    // 4. Phase 1: downsample -> grid (16 x 16 groups), then queue its copy into this frame's staging slot.
    ctx->CSSetConstantBuffers(0, 1, &cb_);                          // shared by both compute passes
    ctx->CSSetSamplers(0, 1, &linear_);
    ctx->CSSetShader(csDown_, nullptr, 0);
    ctx->CSSetShaderResources(0, 1, &game);
    ctx->CSSetUnorderedAccessViews(0, 1, &gridUav_, nullptr);
    ctx->Dispatch(kGrid / 8, kGrid / 8, 1);
    ctx->CopyResource(staging_[slot], grid_);
    stagingPending_[slot] = true;

    // 5. Phase 3: march -> lighting texture. The grid's UAV is replaced by the lighting UAV in the same slot (one call),
    //    so the grid can be bound as an SRV without a read/write hazard.
    ID3D11ShaderResourceView* marchSrv[2] = { gridSrv_, importanceSrv_ };
    ctx->CSSetShader(csMarch_, nullptr, 0);
    ctx->CSSetUnorderedAccessViews(0, 1, &lightUav_, nullptr);
    ctx->CSSetShaderResources(0, 2, marchSrv);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
    // 5a. History reprojection setup. r6 (temporalExt): read FrameFX's pan on the GPU and accept/reject it.
    //     r4 (temporalPan, no external pan): the internal ±4 cell search. Either way the previous proxy grid is kept.
    if (s.temporal && s.temporalExt) {
        if (pan && historyValid_ && prevValid_) {
            ctx->CSSetShader(csPan_, nullptr, 0);
            ctx->CSSetShaderResources(5, 1, &prevGridSrv_);
            ctx->CSSetShaderResources(6, 1, &pan);
            ctx->CSSetUnorderedAccessViews(1, 1, &shiftUav_, nullptr);
            ctx->Dispatch(1, 1, 1);
            ID3D11ShaderResourceView* const n2[2] = { nullptr, nullptr };
            ctx->CSSetShaderResources(5, 2, n2);
            ctx->CSSetUnorderedAccessViews(1, 1, nullUav, nullptr);
        }
        ctx->CopyResource(prevGrid_, grid_);
        prevValid_ = true;
    } else if (s.temporal && s.temporalPan) {
        ctx->CSSetShader(csShift_, nullptr, 0);
        ctx->CSSetShaderResources(5, 1, &prevGridSrv_);
        ctx->CSSetUnorderedAccessViews(1, 1, &shiftUav_, nullptr);
        ctx->Dispatch(1, 1, 1);
        ID3D11ShaderResourceView* const n1[1] = { nullptr };
        ctx->CSSetShaderResources(5, 1, n1);
        ctx->CSSetUnorderedAccessViews(1, 1, nullUav, nullptr);
        ctx->CopyResource(prevGrid_, grid_);
    } else {
        prevValid_ = false;
    }
    // 5b. Round 2: low-res 3 x 3 bilateral, lighting A (t2) -> lighting B (u0). Binding B's UAV in slot 0 unbinds A's.
    ctx->CSSetShader(csFilter_, nullptr, 0);
    const uint32_t cur = (uint32_t)(frame_ & 1), prev = cur ^ 1u;   // write cur, read prev as history (t3)
    ctx->CSSetUnorderedAccessViews(0, 1, &lightBUav_[cur], nullptr);
    ID3D11ShaderResourceView* filtSrv[3] = { lightSrv_, lightBSrv_[prev], shiftSrv_ };
    ctx->CSSetShaderResources(2, 3, filtSrv);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
    ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);        // lighting B is read by the PS next
    ID3D11ShaderResourceView* const nullSrv5[5] = { nullptr, nullptr, nullptr, nullptr, nullptr };
    ctx->CSSetShaderResources(0, 5, nullSrv5);                      // lighting A is written by next frame's march

    // 6. Phase 4: full-screen triangle, bilateral upscale + composite into 'out'.
    ID3D11ShaderResourceView* psSrv[2] = { game, lightBSrv_[cur] };
    historyValid_ = true;
    D3D11_VIEWPORT vp{ 0.0f, 0.0f, (float)srcW_, (float)srcH_, 0.0f, 1.0f };
    ctx->OMSetRenderTargets(1, &out, nullptr);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(vs_, nullptr, 0);
    ctx->PSSetShader(ps_, nullptr, 0);
    ctx->PSSetConstantBuffers(0, 1, &cb_);
    ctx->PSSetShaderResources(0, 2, psSrv);
    ctx->Draw(3, 0);
    ctx->PSSetShaderResources(0, 2, nullSrv);                       // next frame's march writes the lighting texture

    ++frame_;
    return true;
}

bool SsgiPipeline::UploadCB(ID3D11DeviceContext* ctx, const SsgiSettings& s, bool blendExt, bool blendOld) noexcept {
    Constants c{};
    c.srcW = srcW_; c.srcH = srcH_; c.lightW = lightW_; c.lightH = lightH_;
    c.invSrcW = 1.0f / srcW_; c.invSrcH = 1.0f / srcH_; c.invLightW = 1.0f / lightW_; c.invLightH = 1.0f / lightH_;
    c.frameIndex = (uint32_t)frame_;
    c.marchSteps = std::clamp(s.marchSteps, 8u, 32u); c.raysPerTexel = std::clamp(s.raysPerTexel, 1u, 8u);
    c.stepCells = s.stepCells; c.strength = s.strength; c.aoStrength = s.aoStrength;
    c.emitLo = s.emitLo; c.emitHi = s.emitHi; c.edgeLuma = s.edgeLuma; c.falloff = s.falloff; c.rangeK = s.rangeK;
    c.forceAll = s.forceAll ? 1u : 0u;
    c.heightScale = s.heightScale; c.stepGrowth = std::clamp(s.stepGrowth, 1.0f, 1.5f);
    c.temporalAlpha = (blendExt || blendOld) ? std::clamp(s.temporalAlpha, 0.05f, 1.0f) : 0.0f;
    c.horizonBias = std::clamp(s.horizonBias, 0.0f, 0.9f);
    c.aoRadius = std::clamp(s.aoRadius, 1.0f, 32.0f); c.aoGame = std::clamp(s.aoGame, 0.0f, 0.6f);
    c.temporalPan = s.temporalPan ? 1u : 0u; c.panMaxSad = std::max(s.panMaxSad, 0.0f);
    c.panMaxPx = std::max(s.panMaxPx, 0.0f); c.lumReset = std::max(s.lumReset, 0.0f);
    c.guideReject = std::max(s.guideReject, 0.0f); c.temporalExt = blendExt ? 1u : 0u;
    c.clampBox = std::clamp(s.clampBox, 0.0f, 1.0f);
    D3D11_MAPPED_SUBRESOURCE m;
    if (FAILED(ctx->Map(cb_, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) return false;
    std::memcpy(m.pData, &c, sizeof(c));
    ctx->Unmap(cb_, 0);
    return true;
}

namespace {

struct alignas(16) SsrtConstants {
    uint32_t flags, reflSteps, contactRays, contactSteps;
    float reflStrength, contactStrength, thickness, parallaxWeight;
    float horizonY, depthGrad, aoDepthScale, contactRadius;
    float roughBlur, edgeFade, ssrtTemporal, reflReach;
    float wHorizon, wContrast, wDefocus, wLuma;
    float wParallax, skyLuma, glossVarK, glossMin;
    float giGain, panMinPx, glossBright, pad;
    float accumAlpha, depthReject, mvRejectPx, lumCut;
    float clipBox, forceReset; uint32_t temporalMode; float padT;
};
static_assert(sizeof(SsrtConstants) == 144, "must match ssrt_common.hlsli");

// SYNC tests/harness/ssrt_ref.py TEMPORAL. New-frame weight, hard depth reject, motion-disagreement
// in source pixels, mean-luma scene cut, neighbourhood clip (1 = full 3x3 box).
static constexpr float kSsrtAlpha = 0.22f, kSsrtDepth = 0.045f, kSsrtMvPx = 6.0f, kSsrtLum = 0.08f, kSsrtClip = 1.0f;

HRESULT MakeTex(ID3D11Device* d, UINT w, UINT h, DXGI_FORMAT fmt, UINT bind, const void* init, UINT pitch,
                ID3D11Texture2D** tex, ID3D11UnorderedAccessView** uav, ID3D11ShaderResourceView** srv) {
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1; td.Format = fmt;
    td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = bind;
    D3D11_SUBRESOURCE_DATA sd{ init, pitch, 0 };
    HRESULT hr = d->CreateTexture2D(&td, init ? &sd : nullptr, tex);
    if (FAILED(hr)) return hr;
    if (uav && (bind & D3D11_BIND_UNORDERED_ACCESS))
        if (FAILED(hr = d->CreateUnorderedAccessView(*tex, nullptr, uav))) return hr;
    if (srv && (bind & D3D11_BIND_SHADER_RESOURCE))
        if (FAILED(hr = d->CreateShaderResourceView(*tex, nullptr, srv))) return hr;
    return S_OK;
}

}  // namespace

void SsgiPipeline::ReleaseSsrtSized() noexcept {
    for (int i = 0; i < 2; ++i) {
        Release(cutSrv_[i]); Release(cutUav_[i]); Release(cut_[i]);
        Release(accShSrv_[i]); Release(accShUav_[i]); Release(accSh_[i]);
        Release(accReflSrv_[i]); Release(accReflUav_[i]); Release(accRefl_[i]);
        Release(accDepthSrv_[i]); Release(accDepthUav_[i]); Release(accDepth_[i]);
        Release(accGiSrv_[i]); Release(accGiUav_[i]); Release(accGi_[i]);
    }
    Release(packedSrv_); Release(packedUav_); Release(packed_);
    Release(shadowSrv_); Release(shadowUav_); Release(shadow_);
    Release(reflSrv_); Release(reflUav_); Release(refl_);
    Release(depthHistSrv_); Release(depthHist_);
    Release(depthSrv_); Release(depthUav_); Release(depth_);
    Release(rawSrv_); Release(rawUav_); Release(raw_);
    accPing_ = 0;
    ssrtHist_ = false;
}

HRESULT SsgiPipeline::CreateSsrtSized() noexcept {
    ReleaseSsrtSized();
    if (!dev_ || !lightW_ || !lightH_) return E_FAIL;
    const UINT bind = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr;
    // xyz = depth, brightness, protect (sky / flat UI). 8 B/px; the full-frame read still dominates this pass.
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R16G16B16A16_FLOAT, bind, nullptr, 0, &raw_, &rawUav_, &rawSrv_))) return hr;
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R16G16B16A16_FLOAT, bind, nullptr, 0, &depth_, &depthUav_, &depthSrv_))) return hr;
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, nullptr, 0,
                            &depthHist_, nullptr, &depthHistSrv_))) return hr;
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R8G8B8A8_UNORM, bind, nullptr, 0, &refl_, &reflUav_, &reflSrv_))) return hr;
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R32_FLOAT, bind, nullptr, 0, &shadow_, &shadowUav_, &shadowSrv_))) return hr;
    if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R8G8B8A8_UNORM, bind, nullptr, 0, &packed_, &packedUav_, &packedSrv_))) return hr;
    float cut0[4] = {};
    for (int i = 0; i < 2; ++i) {
        if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R32G32_UINT, bind, nullptr, 0, &accGi_[i], &accGiUav_[i], &accGiSrv_[i]))) return hr;
        if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R16G16B16A16_FLOAT, bind, nullptr, 0, &accDepth_[i], &accDepthUav_[i], &accDepthSrv_[i]))) return hr;
        if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R8G8B8A8_UNORM, bind, nullptr, 0, &accRefl_[i], &accReflUav_[i], &accReflSrv_[i]))) return hr;
        if (FAILED(hr = MakeTex(dev_, lightW_, lightH_, DXGI_FORMAT_R32_FLOAT, bind, nullptr, 0, &accSh_[i], &accShUav_[i], &accShSrv_[i]))) return hr;
        if (FAILED(hr = MakeTex(dev_, 1, 1, DXGI_FORMAT_R32G32B32A32_FLOAT, bind, cut0, 16, &cut_[i], &cutUav_[i], &cutSrv_[i]))) return hr;
    }
    return S_OK;
}

HRESULT SsgiPipeline::InitSsrt(const SsrtShaders& sh) {
    if (!dev_) return E_FAIL;
    Release(psSsrt_); Release(csPack_); Release(csContact_); Release(csReflect_); Release(csLightDir_);
    Release(csMarchD_); Release(csDepthBlur_); Release(csDepth_);
    Release(csAccum_); Release(csCut_);
    Release(cbSsrt_);
    Release(lightDirSrv_); Release(lightDirUav_); Release(lightDir_);
    Release(dummyPanSrv_); Release(dummyPan_); Release(dummyMotionSrv_); Release(dummyMotion_);
    ssrt_ = false;
    HRESULT hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.depth.data, sh.depth.size, nullptr, &csDepth_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.depthBlur.data, sh.depthBlur.size, nullptr, &csDepthBlur_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.march.data, sh.march.size, nullptr, &csMarchD_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.lightDir.data, sh.lightDir.size, nullptr, &csLightDir_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.reflect.data, sh.reflect.size, nullptr, &csReflect_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.contact.data, sh.contact.size, nullptr, &csContact_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.pack.data, sh.pack.size, nullptr, &csPack_))) return hr;
    if (FAILED(hr = dev_->CreatePixelShader(sh.psComposite.data, sh.psComposite.size, nullptr, &psSsrt_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.cut.data, sh.cut.size, nullptr, &csCut_))) return hr;
    if (FAILED(hr = dev_->CreateComputeShader(sh.accum.data, sh.accum.size, nullptr, &csAccum_))) return hr;
    if (FAILED(hr = MakeBuffer(dev_, sizeof(SsrtConstants), D3D11_USAGE_DYNAMIC, D3D11_BIND_CONSTANT_BUFFER,
                               D3D11_CPU_ACCESS_WRITE, 0, 0, nullptr, &cbSsrt_))) return hr;
    if (FAILED(hr = MakeBuffer(dev_, 16, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE,
                               0, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, 16, nullptr, &lightDir_))) return hr;
    if (FAILED(hr = dev_->CreateUnorderedAccessView(lightDir_, nullptr, &lightDirUav_))) return hr;
    if (FAILED(hr = dev_->CreateShaderResourceView(lightDir_, nullptr, &lightDirSrv_))) return hr;
    float zero4[4] = {};
    if (FAILED(hr = MakeTex(dev_, 1, 1, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, zero4, 8,
                            &dummyMotion_, nullptr, &dummyMotionSrv_))) return hr;
    float zero2[2] = {};
    if (FAILED(hr = MakeTex(dev_, 1, 1, DXGI_FORMAT_R32G32_FLOAT, D3D11_BIND_SHADER_RESOURCE, zero2, 8,
                            &dummyPan_, nullptr, &dummyPanSrv_))) return hr;
    ssrt_ = true;
    return lightW_ ? CreateSsrtSized() : S_OK;
}

bool SsgiPipeline::ExecuteSsrt(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* game, ID3D11RenderTargetView* out,
                               uint32_t preset, uint32_t flags, ID3D11ShaderResourceView* motion,
                               ID3D11ShaderResourceView* pan, int temporal) noexcept {
    if (!light_ || !worker_ || !game || !out) return false;
    const bool off = (flags & (SsrtGi | SsrtAo | SsrtReflect | SsrtContact)) == 0;
    const SsgiPreset oldId = preset == 2u ? SsgiPreset::GTX980Ti : SsgiPreset::GTX1050Ti;
    if (off) {
        SsgiSettings s = MakePreset(oldId);
        s.enabled = true;
        const uint32_t div = std::max(1u, s.lightDivisor);
        const uint32_t wantW = std::max<uint32_t>(kGrid, srcW_ / div);
        const uint32_t wantH = std::max<uint32_t>(kGrid, srcH_ / div);
        if (lightW_ != wantW || lightH_ != wantH) {
            if (FAILED(Resize(srcW_, srcH_, div))) return false;
        }
        return Execute(ctx, game, out, s, nullptr);
    }
    if (!ssrt_ || !csDepth_ || !psSsrt_) return false;
    SsrtPreset pr = MakeSsrtPreset(preset == 2u ? SsrtPresetId::GTX980Ti_1440p : SsrtPresetId::GTX1050Ti_1080p);
    SsgiSettings s = pr.gi;
    s.enabled = true;
    s.temporal = false;
    s.temporalExt = false;
    const bool tempOn = temporal != 0 && csAccum_ && csCut_;
    const uint32_t tempMode = !tempOn ? 0u : (motion ? 1u : (pan ? 2u : 0u));
    const uint32_t div = std::max(1u, s.lightDivisor);
    const uint32_t wantW = std::max<uint32_t>(kGrid, srcW_ / div);
    const uint32_t wantH = std::max<uint32_t>(kGrid, srcH_ / div);
        if (lightW_ != wantW || lightH_ != wantH) {
        if (FAILED(Resize(srcW_, srcH_, div))) return false;
    } else if (!raw_ || !accGi_[0]) {
        if (FAILED(CreateSsrtSized())) return false;
    }
    if (const uint32_t fp = s.raysPerTexel | (s.marchSteps << 8) | (s.lightDivisor << 16); fp != cfgFp_) {
        historyValid_ = false; prevValid_ = false; ssrtHist_ = false; cfgFp_ = fp;
    }
    if (const uint16_t r = (uint16_t)(s.reachLog2X | (s.reachLog2Y << 8)); r != reach_) {
        ImportanceParams ip; ip.reachLog2X = std::min<uint8_t>(s.reachLog2X, 5); ip.reachLog2Y = std::min<uint8_t>(s.reachLog2Y, 6);
        worker_->SetParams(ip); reach_ = r;
    }
    const uint32_t slot = (uint32_t)(frame_ % kRing);
    {
        const uint32_t old = (uint32_t)((frame_ + 1) % kRing);
        if (stagingPending_[old]) {
            D3D11_MAPPED_SUBRESOURCE m;
            HRESULT hr = ctx->Map(staging_[old], 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
            if (SUCCEEDED(hr)) {
                GridFrame& g = worker_->BeginGridWrite();
                std::memcpy(g.cells, m.pData, sizeof(g.cells));
                ctx->Unmap(staging_[old], 0);
                stagingPending_[old] = false;
                worker_->PublishGrid(frame_ - (kRing - 1));
            } else {
                ++skipped_;
            }
        }
    }
    {
        const MaskFrame* mf = nullptr;
        if (worker_->TryAcquireMask(mf)) {
            D3D11_MAPPED_SUBRESOURCE m;
            if (SUCCEEDED(ctx->Map(importance_, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) {
                std::memcpy(m.pData, mf->words, sizeof(mf->words));
                ctx->Unmap(importance_, 0);
            }
        }
    }
    if (!UploadCB(ctx, s, false, false)) return false;
    {
        SsrtConstants c{};
        const bool wantRefl = (flags & SsrtReflect) != 0;
        const bool wantContact = (flags & SsrtContact) != 0;
        const bool parallax = motion && pan;
        c.flags = flags;
        c.reflSteps = std::clamp(pr.reflSteps, 1u, 16u);
        c.contactRays = std::clamp(pr.contactRays, 1u, 4u);
        c.contactSteps = std::clamp(pr.contactSteps, 1u, 12u);
        c.reflStrength = wantRefl ? pr.reflStrength : 0.0f;
        c.contactStrength = wantContact ? pr.contactStrength : 0.0f;
        c.thickness = std::max(pr.thickness, 0.001f);
        c.parallaxWeight = parallax ? pr.parallaxWeight : 0.0f;
        c.horizonY = pr.horizonY; c.depthGrad = pr.depthGrad; c.aoDepthScale = pr.aoDepthScale;
        c.contactRadius = pr.contactRadius; c.roughBlur = pr.roughBlur; c.edgeFade = pr.edgeFade;
        c.ssrtTemporal = 0.0f;   // history hook stays off
        c.reflReach = pr.reflReach;
        c.wHorizon = pr.wHorizon; c.wContrast = pr.wContrast; c.wDefocus = pr.wDefocus; c.wLuma = pr.wLuma;
        c.wParallax = pr.wParallax; c.skyLuma = pr.skyLuma; c.glossVarK = pr.glossVarK; c.glossMin = pr.glossMin;
        c.giGain = pr.giGain; c.panMinPx = pr.panMinPx; c.glossBright = pr.glossBright;
        c.accumAlpha = kSsrtAlpha;
        c.depthReject = kSsrtDepth;
        c.mvRejectPx = kSsrtMvPx;
        c.lumCut = kSsrtLum;
        c.clipBox = kSsrtClip;
        c.forceReset = (!ssrtHist_ || tempMode == 0u) ? 1.0f : 0.0f;
        c.temporalMode = tempMode;
        D3D11_MAPPED_SUBRESOURCE m;
        if (FAILED(ctx->Map(cbSsrt_, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) return false;
        std::memcpy(m.pData, &c, sizeof(c));
        ctx->Unmap(cbSsrt_, 0);
    }
    ID3D11Buffer* cbs[2] = { cb_, cbSsrt_ };
    ID3D11UnorderedAccessView* const nullUav[2] = { nullptr, nullptr };
    ID3D11ShaderResourceView* const nullSrv8[8] = {};
    ctx->CSSetConstantBuffers(0, 2, cbs);
    ctx->CSSetSamplers(0, 1, &linear_);

    ctx->CSSetShader(csDown_, nullptr, 0);
    ctx->CSSetShaderResources(0, 1, &game);
    ctx->CSSetUnorderedAccessViews(0, 1, &gridUav_, nullptr);
    ctx->Dispatch(kGrid / 8, kGrid / 8, 1);
    ctx->CopyResource(staging_[slot], grid_);
    stagingPending_[slot] = true;

    ID3D11ShaderResourceView* depthSrv[3] = { game, motion ? motion : dummyMotionSrv_, pan ? pan : dummyPanSrv_ };
    ctx->CSSetShader(csDepth_, nullptr, 0);
    ctx->CSSetShaderResources(0, 3, depthSrv);
    ctx->CSSetUnorderedAccessViews(0, 1, &rawUav_, nullptr);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);

    ctx->CSSetShader(csDepthBlur_, nullptr, 0);
    ID3D11ShaderResourceView* blurSrv[2] = { rawSrv_, depthHistSrv_ };
    ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
    ctx->CSSetShaderResources(3, 2, blurSrv);
    ctx->CSSetUnorderedAccessViews(1, 1, &depthUav_, nullptr);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
    ctx->CSSetUnorderedAccessViews(1, 1, nullUav, nullptr);
    ctx->CSSetShaderResources(0, 6, nullSrv8);

    const bool depthMarch = (flags & (SsrtGi | SsrtAo)) != 0;
    ID3D11ShaderResourceView* marchSrv[3] = { gridSrv_, importanceSrv_, depthSrv_ };
    ctx->CSSetShader(depthMarch ? csMarchD_ : csMarch_, nullptr, 0);
    ctx->CSSetUnorderedAccessViews(0, 1, &lightUav_, nullptr);
    ctx->CSSetShaderResources(0, depthMarch ? 3u : 2u, marchSrv);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);

    ctx->CSSetShader(csLightDir_, nullptr, 0);
    ctx->CSSetUnorderedAccessViews(0, 1, &lightDirUav_, nullptr);
    ctx->CSSetShaderResources(0, 1, &gridSrv_);
    ctx->Dispatch(1, 1, 1);
    ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);

    ctx->CSSetShader(csFilter_, nullptr, 0);
    const uint32_t cur = (uint32_t)(frame_ & 1), prev = cur ^ 1u;
    ctx->CSSetUnorderedAccessViews(0, 1, &lightBUav_[cur], nullptr);
    ID3D11ShaderResourceView* filtSrv[3] = { lightSrv_, lightBSrv_[prev], shiftSrv_ };
    ctx->CSSetShaderResources(2, 3, filtSrv);
    ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
    ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
    ctx->CSSetShaderResources(0, 6, nullSrv8);

    const bool wantRefl = (flags & SsrtReflect) != 0;
    const bool wantContact = (flags & SsrtContact) != 0;
    if (wantRefl) {
        ctx->CSSetShader(csReflect_, nullptr, 0);
        ID3D11ShaderResourceView* rs[2] = { depthSrv_, game };
        ctx->CSSetShaderResources(0, 2, rs);
        ctx->CSSetUnorderedAccessViews(0, 1, &reflUav_, nullptr);
        ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
        ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
        ctx->CSSetShaderResources(0, 2, nullSrv8);
    }
    if (wantContact) {
        ctx->CSSetShader(csContact_, nullptr, 0);
        ID3D11ShaderResourceView* cs[3] = { gridSrv_, depthSrv_, lightDirSrv_ };
        ctx->CSSetShaderResources(0, 3, cs);
        ctx->CSSetUnorderedAccessViews(1, 1, &shadowUav_, nullptr);
        ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
        ctx->CSSetUnorderedAccessViews(1, 1, nullUav, nullptr);
        ctx->CSSetShaderResources(0, 3, nullSrv8);
    }
    ID3D11ShaderResourceView* lightForPs = lightBSrv_[cur];
    ID3D11ShaderResourceView* reflForPack = reflSrv_;
    ID3D11ShaderResourceView* shForPack = shadowSrv_;
    if (tempOn && accGi_[0]) {
        const uint32_t rd = accPing_, wr = rd ^ 1u;
        ctx->CSSetShader(csCut_, nullptr, 0);
        ID3D11ShaderResourceView* cutIn[2] = { gridSrv_, cutSrv_[rd] };
        ctx->CSSetShaderResources(0, 2, cutIn);
        ctx->CSSetUnorderedAccessViews(0, 1, &cutUav_[wr], nullptr);
        ctx->Dispatch(1, 1, 1);
        ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
        ctx->CSSetShaderResources(0, 2, nullSrv8);

        ctx->CSSetShader(csAccum_, nullptr, 0);
        ID3D11ShaderResourceView* accIn[12] = {
            nullptr, cutSrv_[wr], lightBSrv_[cur], depthSrv_, reflSrv_, shadowSrv_,
            accGiSrv_[rd], accDepthSrv_[rd], accReflSrv_[rd], accShSrv_[rd],
            motion ? motion : dummyMotionSrv_, pan ? pan : dummyPanSrv_
        };
        ctx->CSSetShaderResources(0, 12, accIn);
        ID3D11UnorderedAccessView* accOut[4] = { accGiUav_[wr], accDepthUav_[wr], accReflUav_[wr], accShUav_[wr] };
        ctx->CSSetUnorderedAccessViews(1, 4, accOut, nullptr);
        ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
        ID3D11UnorderedAccessView* null4[4] = {};
        ctx->CSSetUnorderedAccessViews(1, 4, null4, nullptr);
        ID3D11ShaderResourceView* null12[12] = {};
        ctx->CSSetShaderResources(0, 12, null12);
        lightForPs = accGiSrv_[wr];
        reflForPack = accReflSrv_[wr];
        shForPack = accShSrv_[wr];
        accPing_ = wr;
        ssrtHist_ = tempMode != 0u;
    } else {
        ssrtHist_ = false;
    }
    if (wantRefl || wantContact) {
        ctx->CSSetShader(csPack_, nullptr, 0);
        ID3D11ShaderResourceView* pk[2] = { reflForPack, shForPack };
        ctx->CSSetShaderResources(0, 2, pk);
        ctx->CSSetUnorderedAccessViews(0, 1, &packedUav_, nullptr);
        ctx->Dispatch((lightW_ + 7) / 8, (lightH_ + 7) / 8, 1);
        ctx->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
        ctx->CSSetShaderResources(0, 2, nullSrv8);
    }

    historyValid_ = false;   // SSRT lighting is a different estimator; the next temporal SSGI frame resets
    D3D11_VIEWPORT vp{ 0.0f, 0.0f, (float)srcW_, (float)srcH_, 0.0f, 1.0f };
    ctx->OMSetRenderTargets(1, &out, nullptr);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(vs_, nullptr, 0);
    ctx->PSSetConstantBuffers(0, 2, cbs);
    if (wantRefl || wantContact) {
        // PSCompositeSsrt: t2 game, t3 lighting, t4 packed reflection + contact (see ssrt_composite.hlsl).
        ctx->PSSetShader(psSsrt_, nullptr, 0);
        ID3D11ShaderResourceView* psBind[5] = { nullptr, nullptr, game, lightForPs, packedSrv_ };
        ctx->PSSetShaderResources(0, 5, psBind);
    } else {
        ctx->PSSetShader(ps_, nullptr, 0);
        ID3D11ShaderResourceView* psBind[2] = { game, lightForPs };
        ctx->PSSetShaderResources(0, 2, psBind);
    }
    ctx->Draw(3, 0);
    ctx->PSSetShaderResources(0, 5, nullSrv8);
    ID3D11Buffer* nullCb[2] = { nullptr, nullptr };
    ctx->PSSetConstantBuffers(0, 2, nullCb);
    ctx->CSSetConstantBuffers(0, 2, nullCb);
    ++frame_;
    return true;
}

}  // namespace ufx::ssgi
