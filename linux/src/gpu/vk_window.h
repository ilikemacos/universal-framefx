#pragma once

#include "vk_context.h"

#include <SDL.h>
#include <SDL_vulkan.h>
#include <vector>

struct SdlVkWindow {
    SDL_Window* window = nullptr;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    VkFormat format = VK_FORMAT_B8G8R8A8_UNORM;
    VkExtent2D extent{};
    std::vector<VkImage> images;
    std::vector<VkImageView> views;
    std::vector<VkFramebuffer> framebuffers;
    VkRenderPass render_pass = VK_NULL_HANDLE;
    VkCommandBuffer cmds[2]{};
    VkSemaphore image_available[2]{};
    VkSemaphore render_finished[2]{};
    VkFence inflight[2]{};
    uint32_t frame = 0;
    uint32_t image_index = 0;
};

bool vk_window_create(VkInstance instance, const char* title, int w, int h, uint32_t flags, SdlVkWindow* out);
bool vk_window_init_swapchain(VkContext* ctx, SdlVkWindow* win, bool present_src, bool fifo_vsync);
void vk_window_destroy_swapchain(VkContext* ctx, SdlVkWindow* win);
void vk_window_destroy(VkInstance instance, SdlVkWindow* win);
bool vk_window_acquire(VkContext* ctx, SdlVkWindow* win);
bool vk_window_submit_present(VkContext* ctx, SdlVkWindow* win, VkCommandBuffer cmd);
