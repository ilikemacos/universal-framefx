#pragma once

#include "vk_context.h"

#include <cstdint>
#include <string>

struct SpatialUniformsCPU {
    float inputSize[2];
    float outputSize[2];
    float invInputSize[2];
    float scale[2];
    float sharpness;
    float pad0;
    float pad1[2];
};

struct CsrSpatial {
    VkContext* ctx = nullptr;
    VkSampler nearest = VK_NULL_HANDLE;
    VkDescriptorSetLayout easu_layout = VK_NULL_HANDLE;
    VkDescriptorSetLayout rcas_layout = VK_NULL_HANDLE;
    VkPipelineLayout easu_pl = VK_NULL_HANDLE;
    VkPipelineLayout rcas_pl = VK_NULL_HANDLE;
    VkPipeline easu_pipe = VK_NULL_HANDLE;
    VkPipeline rcas_pipe = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkBuffer ubo = VK_NULL_HANDLE;
    VkDeviceMemory ubo_mem = VK_NULL_HANDLE;
};

bool csr_spatial_create(VkContext* ctx, CsrSpatial* out, std::string* err);
void csr_spatial_destroy(CsrSpatial* s);

/* Upload src RGBA32F, run EASU then RCAS, read back RGBA32F. */
bool csr_spatial_execute(CsrSpatial* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                         float sharpness, float* dst, std::string* err);

void csr_fill_uniforms(SpatialUniformsCPU* u, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                       float sharpness);
