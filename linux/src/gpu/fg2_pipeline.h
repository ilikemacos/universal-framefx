#pragma once

#include "fg2_affine.h"
#include "vk_context.h"

#include <cstdint>
#include <string>
#include <vector>

struct Fg2Image {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    uint32_t w = 0, h = 0;
    VkFormat format = VK_FORMAT_UNDEFINED;
};

struct Fg2Pipe {
    VkDescriptorSetLayout layout = VK_NULL_HANDLE;
    VkPipelineLayout pl = VK_NULL_HANDLE;
    VkPipeline pipe = VK_NULL_HANDLE;
};

struct Fg2ParamsCPU {
    float InW, InH, OutW, OutH;
    float TileSize, TilesX, TilesY, InterpT;
    float HistoryValid, Lambda, CensusW, SadW;
    float OcclThresh, StaticThresh, SceneCutThresh, FrameCount;
    float JitterX, JitterY, BidirOn, Fallback;
    float MvGapN, WideSearch, ClassicCand, EdgeSmooth;
    float AmbiguityGate, TemporalExtrap, History2Valid, BicubicOn;
    float SharpBlendOn, PadFg2a, PadFg2b, PadFg2c;
};

struct Fg2GlobalCPU {
    float A00, A01, A02, A10;
    float A11, A12, AffConf, SnapOn;
    float HwScale, HwGrid, RansacPx, GmvY;
    float ConfThresh, ColorGate, VecLenK, SoftOccK;
};

struct Fg2Pipeline {
    VkContext* ctx = nullptr;
    uint32_t in_w = 0, in_h = 0;
    bool has_history = false;
    int ping = 0;
    Fg2Image luma_a[4]{};
    Fg2Image luma_b[4]{};
    Fg2Image tv8{}, tv8_prop{}, tv4{}, mv2{}, mv_hist{}, fwd{}, fwd_hist{};
    Fg2Image occl{}, stat_count{}, hud{}, chg_prev{}, chg_out{}, gmv{};
    Fg2Image color_a{}, color_b{}, generated{}, scratch{};
    VkSampler nearest = VK_NULL_HANDLE;
    VkSampler linear = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkBuffer ubo = VK_NULL_HANDLE, ubo2 = VK_NULL_HANDLE;
    VkDeviceMemory ubo_mem = VK_NULL_HANDLE, ubo2_mem = VK_NULL_HANDLE;
    Fg2Pipe p_luma{}, p_halve{}, p_p8{}, p_prop{}, p_p4{}, p_p2{};
    Fg2Pipe p_fwd{}, p_cons{}, p_stat{}, p_hud{}, p_cut{}, p_gmv{}, p_interp{}, p_interp_fp16{};
    AffineModel affine{};
    float occl_thresh = 1.f;
    float static_thresh = 1.5f / 255.f;
    float ransac_px = 1.75f;
    float last_cut = 0.f;
    bool use_fp16 = false;
};

bool fg2_pipeline_create(VkContext* ctx, Fg2Pipeline* out, std::string* err);
void fg2_pipeline_destroy(Fg2Pipeline* p);
bool fg2_pipeline_ensure(Fg2Pipeline* p, uint32_t in_w, uint32_t in_h, std::string* err);
bool fg2_process_real_rgba32f(Fg2Pipeline* p, const float* rgba, uint32_t w, uint32_t h, std::string* err);
bool fg2_process_real_image(Fg2Pipeline* p, VkImage src, uint32_t w, uint32_t h, std::string* err);
bool fg2_generate_fast_rgba32f(Fg2Pipeline* p, const float* prev, const float* curr, uint32_t w, uint32_t h, float t,
                               float* dst, std::string* err);
bool fg2_generate_fast_gpu(Fg2Pipeline* p, float t, std::string* err);
bool fg2_read_mv2(Fg2Pipeline* p, std::vector<float>* vx, std::vector<float>* vy, std::vector<float>* conf);
bool fg2_upload_mv2(Fg2Pipeline* p, const float* rgba16, uint32_t tw, uint32_t th);
bool fg2_set_hud_r16(Fg2Pipeline* p, const float* hud, uint32_t w, uint32_t h);
void fg2_set_cut_flag(Fg2Pipeline* p, float cut);