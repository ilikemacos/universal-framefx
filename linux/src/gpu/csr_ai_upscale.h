#pragma once

#include "csr_ai_cpu_ref.h"
#include "vk_context.h"

#include <cstdint>
#include <string>
#include <vector>

struct CsrAiImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    uint32_t w = 0, h = 0, layers = 1;
};

struct CsrAiUpscale {
    VkContext* ctx = nullptr;
    CsrAiWeights weights{};
    std::string weights_path;
    std::vector<int> w_off, b_off;
    int max_c = 1;
    uint32_t in_w = 0, in_h = 0, out_w = 0, out_h = 0;
    bool src_general = false;
    CsrAiImage src{}, feat_a{}, feat_b{}, net{}, mid{}, dst{};
    VkSampler linear = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkDescriptorSetLayout set_layout = VK_NULL_HANDLE;
    VkPipelineLayout pipe_layout = VK_NULL_HANDLE;
    VkPipeline luma_pipe = VK_NULL_HANDLE;
    VkPipeline conv_pipe = VK_NULL_HANDLE;
    VkPipeline shuffle_pipe = VK_NULL_HANDLE;
    VkPipeline resample_pipe = VK_NULL_HANDLE;
    VkPipeline sharpen_pipe = VK_NULL_HANDLE;
    VkBuffer weights_buf = VK_NULL_HANDLE;
    VkDeviceMemory weights_mem = VK_NULL_HANDLE;
};

bool csr_ai_upscale_create(VkContext* ctx, CsrAiUpscale* out, std::string* err);
void csr_ai_upscale_destroy(CsrAiUpscale* s);
bool csr_ai_upscale_encode(CsrAiUpscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                           uint32_t out_h, float sharpness, float* dst, std::string* err);
bool csr_ai_upscale_from_image(CsrAiUpscale* s, VkImage src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                               uint32_t out_h, float sharpness, std::string* err);

