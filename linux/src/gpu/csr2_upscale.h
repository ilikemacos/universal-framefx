#pragma once

#include "vk_context.h"

#include <cstdint>
#include <string>
#include <vector>

struct Csr2ParamsCPU {
    float InW, InH, OutW, OutH;
    float HistoryValid, Sharpness, HasMotion, HasConf;
    float Cheap, ClampK, BlendK, SharpK;
    float JitterX, JitterY, Pad0, Pad1;
};

struct Csr2Image {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    uint32_t w = 0, h = 0;
};

struct Csr2Upscale {
    VkContext* ctx = nullptr;
    bool has_history = false;
    int ping = 0;
    uint32_t in_w = 0, in_h = 0, out_w = 0, out_h = 0;
    bool use_fp16 = false;
    Csr2Image src{}, mid{}, accum{}, hist_a{}, hist_b{}, scratch{}, dst{}, dummy_mv{}, dummy_conf{};
    VkSampler nearest = VK_NULL_HANDLE;
    VkSampler linear = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkBuffer ubo = VK_NULL_HANDLE;
    VkDeviceMemory ubo_mem = VK_NULL_HANDLE;
    VkDescriptorSetLayout up_layout = VK_NULL_HANDLE, acc_layout = VK_NULL_HANDLE, cas_layout = VK_NULL_HANDLE;
    VkPipelineLayout up_pl = VK_NULL_HANDLE, acc_pl = VK_NULL_HANDLE, cas_pl = VK_NULL_HANDLE;
    VkPipeline up_pipe = VK_NULL_HANDLE, up_pipe_fp16 = VK_NULL_HANDLE;
    VkPipeline acc_pipe = VK_NULL_HANDLE;
    VkPipeline cas_pipe = VK_NULL_HANDLE, cas_pipe_fp16 = VK_NULL_HANDLE;
};

bool csr2_upscale_create(VkContext* ctx, Csr2Upscale* out, std::string* err);
void csr2_upscale_destroy(Csr2Upscale* s);
void csr2_upscale_reset_history(Csr2Upscale* s);
bool csr2_upscale_spatial_rgba32f(Csr2Upscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                                  uint32_t out_h, float sharpness, float* dst, std::string* err);
bool csr2_upscale_encode(Csr2Upscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                         float sharpness, bool temporal, bool feeds_history, const float* motion_rgba16, uint32_t mw,
                         uint32_t mh, float* dst, std::string* err);
bool csr2_upscale_from_image(Csr2Upscale* s, VkImage src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                             float sharpness, bool temporal, bool feeds_history, std::string* err);
uint64_t csr2_history_checksum(Csr2Upscale* s);