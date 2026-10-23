#pragma once

#include "vk_context.h"
#include "../capture/capture.h"

struct ImportedImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    uint32_t width = 0;
    uint32_t height = 0;
    bool from_dmabuf = false;
};

/* Best-effort DMA-BUF import. Returns false if extensions/modifiers are
 * missing or the import fails — caller should use memfd/staging upload. */
bool vk_import_dmabuf(VkContext* ctx, const ufx_capture_frame* frame, VkFormat format, ImportedImage* out);
void vk_imported_image_destroy(VkContext* ctx, ImportedImage* img);

bool vk_upload_rgba8_staging(VkContext* ctx, const void* pixels, uint32_t w, uint32_t h, uint32_t stride_bytes,
                             bool bgra, VkImage dst);
bool vk_upload_rgba32f_staging(VkContext* ctx, const float* pixels, uint32_t w, uint32_t h, VkImage dst);
bool vk_download_rgba32f(VkContext* ctx, VkImage src, uint32_t w, uint32_t h, float* out);
bool vk_upload_texels(VkContext* ctx, const void* pixels, uint32_t w, uint32_t h, VkDeviceSize bpp, VkImage dst);
bool vk_download_texels(VkContext* ctx, VkImage src, uint32_t w, uint32_t h, VkDeviceSize bpp, void* out);
bool vk_copy_image_2d(VkContext* ctx, VkImage src, VkImage dst, uint32_t w, uint32_t h);
bool vk_clear_color_image(VkContext* ctx, VkImage img, float r, float g, float b, float a);
void vk_cmd_barrier_general(VkCommandBuffer cmd, VkImage img, VkAccessFlags srcA, VkAccessFlags dstA);
