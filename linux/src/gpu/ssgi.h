#pragma once

#include "vk_context.h"

#include <cstdint>
#include <string>

struct SsgiParamsCPU {
    float srcW, srcH, lightW, lightH;
    float invSrcW, invSrcH, invLightW, invLightH;
    float marchSteps, raysPerTexel, frameIndex, stepCells;
    float strength, aoStrength, emitLo, emitHi;
    float falloff, rangeK, heightScale, stepGrowth;
    float horizonBias, aoRadius, aoGame, pad;
};

struct SsgiImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    uint32_t w = 0, h = 0;
};

struct Ssgi {
    VkContext* ctx = nullptr;
    uint32_t src_w = 0, src_h = 0, light_w = 0, light_h = 0;
    uint32_t frame = 0;
    SsgiImage src{}, light_a{}, light_b{}, occ_a{}, occ_b{}, dst{};
    VkBuffer grid = VK_NULL_HANDLE;
    VkDeviceMemory grid_mem = VK_NULL_HANDLE;
    VkSampler linear = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkDescriptorSetLayout set_layout = VK_NULL_HANDLE;
    VkPipelineLayout pipe_layout = VK_NULL_HANDLE;
    VkPipeline down_pipe = VK_NULL_HANDLE;
    VkPipeline march_pipe = VK_NULL_HANDLE;
    VkPipeline filter_pipe = VK_NULL_HANDLE;
    VkPipeline composite_pipe = VK_NULL_HANDLE;
};

bool ssgi_create(VkContext* ctx, Ssgi* out, std::string* err);
void ssgi_destroy(Ssgi* s);
bool ssgi_apply_encode(Ssgi* s, const float* src, uint32_t w, uint32_t h, float strength, float* dst, std::string* err);
bool ssgi_apply_from_image(Ssgi* s, VkImage src, uint32_t w, uint32_t h, float strength, std::string* err);
