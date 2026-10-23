#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <vulkan/vulkan.h>

struct VkContext {
    VkInstance instance = VK_NULL_HANDLE;
    VkPhysicalDevice phys = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    uint32_t queue_family = 0;
    VkQueue queue = VK_NULL_HANDLE;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkPhysicalDeviceProperties props{};
    bool has_external_fd = false;
    bool has_dma_buf = false;
    bool has_drm_mod = false;
    bool has_fp16 = false;
    std::string device_name;
    std::string shader_dir;
};

bool vk_find_shader_dir(std::string* out);
bool vk_context_init_headless(VkContext* ctx, std::string* err);
bool vk_instance_create(VkContext* ctx, const char* const* inst_exts, uint32_t n_ext, std::string* err);
bool vk_device_create(VkContext* ctx, VkSurfaceKHR present_surface, std::string* err);
void vk_context_shutdown(VkContext* ctx);

uint32_t vk_find_memory_type(VkPhysicalDevice phys, uint32_t type_bits, VkMemoryPropertyFlags flags);

bool vk_create_buffer(VkContext* ctx, VkDeviceSize size, VkBufferUsageFlags usage, VkMemoryPropertyFlags mem_flags,
                      VkBuffer* buf, VkDeviceMemory* mem);
bool vk_create_image_2d(VkContext* ctx, uint32_t w, uint32_t h, VkFormat format, VkImageUsageFlags usage, VkImage* img,
                        VkDeviceMemory* mem, VkImageView* view);

VkShaderModule vk_load_shader(VkContext* ctx, const char* filename, std::string* err);
VkCommandBuffer vk_begin_once(VkContext* ctx);
bool vk_submit_once(VkContext* ctx, VkCommandBuffer cmd);
