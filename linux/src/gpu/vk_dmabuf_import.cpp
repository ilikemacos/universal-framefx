#include "vk_dmabuf_import.h"

#include <cstring>
#include <unistd.h>
#include <vector>

void vk_imported_image_destroy(VkContext* ctx, ImportedImage* img) {
    if (!ctx || !img) return;
    if (img->view) vkDestroyImageView(ctx->device, img->view, nullptr);
    if (img->image) vkDestroyImage(ctx->device, img->image, nullptr);
    if (img->memory) vkFreeMemory(ctx->device, img->memory, nullptr);
    *img = {};
}

bool vk_import_dmabuf(VkContext* ctx, const ufx_capture_frame* frame, VkFormat format, ImportedImage* out) {
    *out = {};
    if (!ctx || !frame || frame->kind != UFX_CAPTURE_KIND_DMABUF || frame->fd < 0) return false;
    if (!ctx->has_dma_buf || !ctx->has_external_fd) return false;

    int fd = dup(frame->fd);
    if (fd < 0) return false;

    VkExternalMemoryImageCreateInfo ext{};
    ext.sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO;
    ext.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;

    VkImageDrmFormatModifierExplicitCreateInfoEXT mod{};
    VkSubresourceLayout layout{};
    layout.offset = 0;
    layout.rowPitch = frame->stride;
    layout.size = static_cast<VkDeviceSize>(frame->stride) * frame->height;

    VkImageCreateInfo ii{};
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.pNext = &ext;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = format;
    ii.extent = {frame->width, frame->height, 1};
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_LINEAR;
    ii.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;

    if (ctx->has_drm_mod && frame->modifier != 0) {
        mod.sType = VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT;
        mod.drmFormatModifier = frame->modifier;
        mod.drmFormatModifierPlaneCount = 1;
        mod.pPlaneLayouts = &layout;
        mod.pNext = &ext;
        ii.pNext = &mod;
        ii.tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
    }

    VkImage image = VK_NULL_HANDLE;
    if (vkCreateImage(ctx->device, &ii, nullptr, &image) != VK_SUCCESS) {
        close(fd);
        return false;
    }

    VkMemoryRequirements req{};
    vkGetImageMemoryRequirements(ctx->device, image, &req);

    VkImportMemoryFdInfoKHR imp{};
    imp.sType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR;
    imp.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
    imp.fd = fd;

    VkMemoryDedicatedAllocateInfo ded{};
    ded.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO;
    ded.pNext = &imp;
    ded.image = image;

    VkMemoryAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.pNext = &ded;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = vk_find_memory_type(ctx->phys, req.memoryTypeBits, 0);
    VkDeviceMemory memory = VK_NULL_HANDLE;
    if (vkAllocateMemory(ctx->device, &ai, nullptr, &memory) != VK_SUCCESS) {
        vkDestroyImage(ctx->device, image, nullptr);
        close(fd);
        return false;
    }
    vkBindImageMemory(ctx->device, image, memory, 0);

    VkImageViewCreateInfo vi{};
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = format;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    VkImageView view = VK_NULL_HANDLE;
    if (vkCreateImageView(ctx->device, &vi, nullptr, &view) != VK_SUCCESS) {
        vkDestroyImage(ctx->device, image, nullptr);
        vkFreeMemory(ctx->device, memory, nullptr);
        return false;
    }
    out->image = image;
    out->memory = memory;
    out->view = view;
    out->width = frame->width;
    out->height = frame->height;
    out->from_dmabuf = true;
    return true;
}

static void barrier(VkCommandBuffer cmd, VkImage img, VkImageLayout from, VkImageLayout to, VkAccessFlags srcA,
                    VkAccessFlags dstA, VkPipelineStageFlags srcS, VkPipelineStageFlags dstS) {
    VkImageMemoryBarrier b{};
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcA;
    b.dstAccessMask = dstA;
    b.oldLayout = from;
    b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(cmd, srcS, dstS, 0, 0, nullptr, 0, nullptr, 1, &b);
}

bool vk_upload_rgba8_staging(VkContext* ctx, const void* pixels, uint32_t w, uint32_t h, uint32_t stride_bytes,
                             bool bgra, VkImage dst) {
    const uint32_t dst_stride = w * 4;
    std::vector<float> f32(static_cast<size_t>(w) * h * 4);
    const uint8_t* src = static_cast<const uint8_t*>(pixels);
    for (uint32_t y = 0; y < h; ++y) {
        const uint8_t* row = src + static_cast<size_t>(y) * stride_bytes;
        for (uint32_t x = 0; x < w; ++x) {
            const uint8_t* p = row + x * 4;
            float r = p[bgra ? 2 : 0] / 255.0f;
            float g = p[1] / 255.0f;
            float b = p[bgra ? 0 : 2] / 255.0f;
            float a = p[3] / 255.0f;
            size_t i = (static_cast<size_t>(y) * w + x) * 4;
            f32[i + 0] = r;
            f32[i + 1] = g;
            f32[i + 2] = b;
            f32[i + 3] = a;
        }
    }
    (void)dst_stride;
    return vk_upload_rgba32f_staging(ctx, f32.data(), w, h, dst);
}

bool vk_upload_rgba32f_staging(VkContext* ctx, const float* pixels, uint32_t w, uint32_t h, VkImage dst) {
    VkDeviceSize size = static_cast<VkDeviceSize>(w) * h * 4 * sizeof(float);
    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    if (!vk_create_buffer(ctx, size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &buf, &mem)) {
        return false;
    }
    void* mapped = nullptr;
    vkMapMemory(ctx->device, mem, 0, size, 0, &mapped);
    std::memcpy(mapped, pixels, static_cast<size_t>(size));
    vkUnmapMemory(ctx->device, mem);

    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0,
            VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy copy{};
    copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copy.imageSubresource.layerCount = 1;
    copy.imageExtent = {w, h, 1};
    vkCmdCopyBufferToImage(cmd, buf, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    bool ok = vk_submit_once(ctx, cmd);
    vkDestroyBuffer(ctx->device, buf, nullptr);
    vkFreeMemory(ctx->device, mem, nullptr);
    return ok;
}

bool vk_upload_texels(VkContext* ctx, const void* pixels, uint32_t w, uint32_t h, VkDeviceSize bpp, VkImage dst) {
    VkDeviceSize size = static_cast<VkDeviceSize>(w) * h * bpp;
    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    if (!vk_create_buffer(ctx, size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &buf, &mem)) {
        return false;
    }
    void* mapped = nullptr;
    vkMapMemory(ctx->device, mem, 0, size, 0, &mapped);
    std::memcpy(mapped, pixels, static_cast<size_t>(size));
    vkUnmapMemory(ctx->device, mem);
    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy copy{};
    copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copy.imageSubresource.layerCount = 1;
    copy.imageExtent = {w, h, 1};
    vkCmdCopyBufferToImage(cmd, buf, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    bool ok = vk_submit_once(ctx, cmd);
    vkDestroyBuffer(ctx->device, buf, nullptr);
    vkFreeMemory(ctx->device, mem, nullptr);
    return ok;
}

bool vk_download_texels(VkContext* ctx, VkImage src, uint32_t w, uint32_t h, VkDeviceSize bpp, void* out) {
    VkDeviceSize size = static_cast<VkDeviceSize>(w) * h * bpp;
    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    if (!vk_create_buffer(ctx, size, VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &buf, &mem)) {
        return false;
    }
    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, src, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_ACCESS_SHADER_WRITE_BIT,
            VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy copy{};
    copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copy.imageSubresource.layerCount = 1;
    copy.imageExtent = {w, h, 1};
    vkCmdCopyImageToBuffer(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buf, 1, &copy);
    barrier(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_READ_BIT,
            VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    if (!vk_submit_once(ctx, cmd)) {
        vkDestroyBuffer(ctx->device, buf, nullptr);
        vkFreeMemory(ctx->device, mem, nullptr);
        return false;
    }
    void* mapped = nullptr;
    vkMapMemory(ctx->device, mem, 0, size, 0, &mapped);
    std::memcpy(out, mapped, static_cast<size_t>(size));
    vkUnmapMemory(ctx->device, mem);
    vkDestroyBuffer(ctx->device, buf, nullptr);
    vkFreeMemory(ctx->device, mem, nullptr);
    return true;
}

bool vk_copy_image_2d(VkContext* ctx, VkImage src, VkImage dst, uint32_t w, uint32_t h) {
    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, src, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_ACCESS_SHADER_WRITE_BIT,
            VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_ACCESS_SHADER_READ_BIT,
            VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkImageCopy c{};
    c.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    c.srcSubresource.layerCount = 1;
    c.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    c.dstSubresource.layerCount = 1;
    c.extent = {w, h, 1};
    vkCmdCopyImage(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &c);
    barrier(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_READ_BIT,
            VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    barrier(cmd, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    return vk_submit_once(ctx, cmd);
}

bool vk_clear_color_image(VkContext* ctx, VkImage img, float r, float g, float b, float a) {
    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, img, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkClearColorValue cv{};
    cv.float32[0] = r;
    cv.float32[1] = g;
    cv.float32[2] = b;
    cv.float32[3] = a;
    VkImageSubresourceRange rng{};
    rng.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    rng.levelCount = 1;
    rng.layerCount = 1;
    vkCmdClearColorImage(cmd, img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &cv, 1, &rng);
    barrier(cmd, img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    return vk_submit_once(ctx, cmd);
}

void vk_cmd_barrier_general(VkCommandBuffer cmd, VkImage img, VkAccessFlags srcA, VkAccessFlags dstA) {
    barrier(cmd, img, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_GENERAL, srcA, dstA, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
}

bool vk_download_rgba32f(VkContext* ctx, VkImage src, uint32_t w, uint32_t h, float* out) {
    VkDeviceSize size = static_cast<VkDeviceSize>(w) * h * 4 * sizeof(float);
    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    if (!vk_create_buffer(ctx, size, VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &buf, &mem)) {
        return false;
    }
    VkCommandBuffer cmd = vk_begin_once(ctx);
    barrier(cmd, src, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy copy{};
    copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copy.imageSubresource.layerCount = 1;
    copy.imageExtent = {w, h, 1};
    vkCmdCopyImageToBuffer(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buf, 1, &copy);
    barrier(cmd, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_READ_BIT,
            VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    if (!vk_submit_once(ctx, cmd)) {
        vkDestroyBuffer(ctx->device, buf, nullptr);
        vkFreeMemory(ctx->device, mem, nullptr);
        return false;
    }
    void* mapped = nullptr;
    vkMapMemory(ctx->device, mem, 0, size, 0, &mapped);
    std::memcpy(out, mapped, static_cast<size_t>(size));
    vkUnmapMemory(ctx->device, mem);
    vkDestroyBuffer(ctx->device, buf, nullptr);
    vkFreeMemory(ctx->device, mem, nullptr);
    return true;
}
