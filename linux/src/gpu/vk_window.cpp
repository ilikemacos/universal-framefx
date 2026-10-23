#include "vk_window.h"

#include <algorithm>
#include <cstdio>

bool vk_window_create(VkInstance instance, const char* title, int w, int h, uint32_t flags, SdlVkWindow* out) {
    *out = {};
    out->window = SDL_CreateWindow(title, SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED, w, h,
                                   SDL_WINDOW_VULKAN | SDL_WINDOW_SHOWN | flags);
    if (!out->window) return false;
    if (!SDL_Vulkan_CreateSurface(out->window, instance, &out->surface)) return false;
    return true;
}

void vk_window_destroy_swapchain(VkContext* ctx, SdlVkWindow* win) {
    if (!ctx || !win || !ctx->device) return;
    vkDeviceWaitIdle(ctx->device);
    for (auto fb : win->framebuffers) vkDestroyFramebuffer(ctx->device, fb, nullptr);
    for (auto v : win->views) vkDestroyImageView(ctx->device, v, nullptr);
    if (win->swapchain) vkDestroySwapchainKHR(ctx->device, win->swapchain, nullptr);
    if (win->render_pass) vkDestroyRenderPass(ctx->device, win->render_pass, nullptr);
    win->framebuffers.clear();
    win->views.clear();
    win->images.clear();
    win->swapchain = VK_NULL_HANDLE;
    win->render_pass = VK_NULL_HANDLE;
    for (int i = 0; i < 2; ++i) {
        if (win->image_available[i]) vkDestroySemaphore(ctx->device, win->image_available[i], nullptr);
        if (win->render_finished[i]) vkDestroySemaphore(ctx->device, win->render_finished[i], nullptr);
        if (win->inflight[i]) vkDestroyFence(ctx->device, win->inflight[i], nullptr);
        win->image_available[i] = win->render_finished[i] = VK_NULL_HANDLE;
        win->inflight[i] = VK_NULL_HANDLE;
    }
}

void vk_window_destroy(VkInstance instance, SdlVkWindow* win) {
    if (!win) return;
    if (win->surface) vkDestroySurfaceKHR(instance, win->surface, nullptr);
    if (win->window) SDL_DestroyWindow(win->window);
    *win = {};
}

bool vk_window_init_swapchain(VkContext* ctx, SdlVkWindow* win, bool present_src, bool fifo_vsync) {
    VkSurfaceCapabilitiesKHR caps{};
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(ctx->phys, win->surface, &caps);
    uint32_t nfmt = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(ctx->phys, win->surface, &nfmt, nullptr);
    std::vector<VkSurfaceFormatKHR> fmts(nfmt);
    vkGetPhysicalDeviceSurfaceFormatsKHR(ctx->phys, win->surface, &nfmt, fmts.data());
    win->format = fmts.empty() ? VK_FORMAT_B8G8R8A8_UNORM : fmts[0].format;
    for (auto& f : fmts) {
        if (f.format == VK_FORMAT_B8G8R8A8_UNORM || f.format == VK_FORMAT_B8G8R8A8_SRGB) {
            win->format = f.format;
            break;
        }
    }
    win->extent = caps.currentExtent;
    if (win->extent.width == 0xFFFFFFFFu) {
        int iw = 0, ih = 0;
        SDL_Vulkan_GetDrawableSize(win->window, &iw, &ih);
        win->extent.width = static_cast<uint32_t>(iw);
        win->extent.height = static_cast<uint32_t>(ih);
    }
    win->extent.width = std::max(1u, win->extent.width);
    win->extent.height = std::max(1u, win->extent.height);

    uint32_t nmodes = 0;
    vkGetPhysicalDeviceSurfacePresentModesKHR(ctx->phys, win->surface, &nmodes, nullptr);
    std::vector<VkPresentModeKHR> modes(nmodes);
    vkGetPhysicalDeviceSurfacePresentModesKHR(ctx->phys, win->surface, &nmodes, modes.data());
    VkPresentModeKHR pm = VK_PRESENT_MODE_FIFO_KHR;
    if (fifo_vsync) {
        pm = VK_PRESENT_MODE_FIFO_KHR;
    } else {
        for (auto m : modes) {
            if (m == VK_PRESENT_MODE_MAILBOX_KHR) pm = m;
        }
        if (pm == VK_PRESENT_MODE_FIFO_KHR) {
            for (auto m : modes) {
                if (m == VK_PRESENT_MODE_IMMEDIATE_KHR) pm = m;
            }
        }
    }

    uint32_t img_count = caps.minImageCount + 1;
    if (caps.maxImageCount) img_count = std::min(img_count, caps.maxImageCount);

    VkSwapchainCreateInfoKHR sci{};
    sci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    sci.surface = win->surface;
    sci.minImageCount = img_count;
    sci.imageFormat = win->format;
    sci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    sci.imageExtent = win->extent;
    sci.imageArrayLayers = 1;
    sci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | (present_src ? VK_IMAGE_USAGE_TRANSFER_DST_BIT : 0);
    sci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    sci.preTransform = caps.currentTransform;
    sci.presentMode = pm;
    sci.clipped = VK_TRUE;
    VkCompositeAlphaFlagBitsKHR alpha_order[] = {
        VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
        VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR,
        VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR,
        VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR,
    };
    VkResult create_result = VK_ERROR_INITIALIZATION_FAILED;
    for (auto alpha : alpha_order) {
        if (!(caps.supportedCompositeAlpha & alpha)) continue;
        sci.compositeAlpha = alpha;
        create_result = vkCreateSwapchainKHR(ctx->device, &sci, nullptr, &win->swapchain);
        if (create_result == VK_SUCCESS) break;
    }
    if (create_result != VK_SUCCESS) return false;

    uint32_t ni = 0;
    vkGetSwapchainImagesKHR(ctx->device, win->swapchain, &ni, nullptr);
    win->images.resize(ni);
    vkGetSwapchainImagesKHR(ctx->device, win->swapchain, &ni, win->images.data());
    win->views.resize(ni);
    for (uint32_t i = 0; i < ni; ++i) {
        VkImageViewCreateInfo vi{};
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = win->images[i];
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = win->format;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        vkCreateImageView(ctx->device, &vi, nullptr, &win->views[i]);
    }

    VkAttachmentDescription att{};
    att.format = win->format;
    att.samples = VK_SAMPLE_COUNT_1_BIT;
    att.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref{0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
    VkSubpassDescription sub{};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    VkSubpassDependency dep{};
    dep.srcSubpass = VK_SUBPASS_EXTERNAL;
    dep.dstSubpass = 0;
    dep.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    VkRenderPassCreateInfo rp{};
    rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rp.attachmentCount = 1;
    rp.pAttachments = &att;
    rp.subpassCount = 1;
    rp.pSubpasses = &sub;
    rp.dependencyCount = 1;
    rp.pDependencies = &dep;
    vkCreateRenderPass(ctx->device, &rp, nullptr, &win->render_pass);

    win->framebuffers.resize(ni);
    for (uint32_t i = 0; i < ni; ++i) {
        VkFramebufferCreateInfo fi{};
        fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
        fi.renderPass = win->render_pass;
        fi.attachmentCount = 1;
        fi.pAttachments = &win->views[i];
        fi.width = win->extent.width;
        fi.height = win->extent.height;
        fi.layers = 1;
        vkCreateFramebuffer(ctx->device, &fi, nullptr, &win->framebuffers[i]);
    }

    VkSemaphoreCreateInfo sei{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
    VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    fi.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    for (int i = 0; i < 2; ++i) {
        vkCreateSemaphore(ctx->device, &sei, nullptr, &win->image_available[i]);
        vkCreateSemaphore(ctx->device, &sei, nullptr, &win->render_finished[i]);
        vkCreateFence(ctx->device, &fi, nullptr, &win->inflight[i]);
    }
    VkCommandBufferAllocateInfo cai{};
    cai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    cai.commandPool = ctx->pool;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cai.commandBufferCount = 2;
    vkAllocateCommandBuffers(ctx->device, &cai, win->cmds);
    return true;
}

bool vk_window_acquire(VkContext* ctx, SdlVkWindow* win) {
    uint32_t f = win->frame % 2;
    vkWaitForFences(ctx->device, 1, &win->inflight[f], VK_TRUE, UINT64_MAX);
    vkResetFences(ctx->device, 1, &win->inflight[f]);
    VkResult r = vkAcquireNextImageKHR(ctx->device, win->swapchain, UINT64_MAX, win->image_available[f], VK_NULL_HANDLE,
                                       &win->image_index);
    return r == VK_SUCCESS || r == VK_SUBOPTIMAL_KHR;
}

bool vk_window_submit_present(VkContext* ctx, SdlVkWindow* win, VkCommandBuffer cmd) {
    uint32_t f = win->frame % 2;
    VkPipelineStageFlags wait = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    VkSubmitInfo si{};
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1;
    si.pWaitSemaphores = &win->image_available[f];
    si.pWaitDstStageMask = &wait;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &win->render_finished[f];
    if (vkQueueSubmit(ctx->queue, 1, &si, win->inflight[f]) != VK_SUCCESS) return false;
    VkPresentInfoKHR pi{};
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1;
    pi.pWaitSemaphores = &win->render_finished[f];
    pi.swapchainCount = 1;
    pi.pSwapchains = &win->swapchain;
    pi.pImageIndices = &win->image_index;
    vkQueuePresentKHR(ctx->queue, &pi);
    win->frame++;
    return true;
}
