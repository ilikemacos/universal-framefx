#pragma once

#include "gpu/csr_spatial.h"
#include "gpu/vk_window.h"

struct PresentPass {
    VkPipeline pipeline = VK_NULL_HANDLE;
    VkPipelineLayout layout = VK_NULL_HANDLE;
    VkDescriptorSetLayout set_layout = VK_NULL_HANDLE;
    VkSampler linear = VK_NULL_HANDLE;
    VkBuffer ubo = VK_NULL_HANDLE;
    VkDeviceMemory ubo_mem = VK_NULL_HANDLE;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    VkDescriptorSet set = VK_NULL_HANDLE;
};

struct PresentUniformsCPU {
    float scale[2];
    float offset[2];
    float background[4];
};

bool present_pass_create(VkContext* ctx, VkRenderPass rp, PresentPass* out, std::string* err);
void present_pass_destroy(VkContext* ctx, PresentPass* p);
void present_pass_bind_image(VkContext* ctx, PresentPass* p, VkImageView view);
void present_pass_write_ubo(VkContext* ctx, PresentPass* p, const PresentUniformsCPU* u);
void present_compute_letterbox(uint32_t img_w, uint32_t img_h, uint32_t win_w, uint32_t win_h, PresentUniformsCPU* u);
void present_record(VkCommandBuffer cmd, SdlVkWindow* win, PresentPass* p, uint32_t img_w, uint32_t img_h);
bool present_submit(VkContext* ctx, SdlVkWindow* win, PresentPass* p, VkImageView view, uint32_t img_w, uint32_t img_h);
