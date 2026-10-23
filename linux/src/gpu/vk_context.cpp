#include "vk_context.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <unistd.h>
#include <linux/limits.h>
#include <libgen.h>

static bool file_exists(const std::string& p) {
    FILE* f = std::fopen(p.c_str(), "rb");
    if (!f) return false;
    std::fclose(f);
    return true;
}

bool vk_find_shader_dir(std::string* out) {
    if (const char* env = std::getenv("UFX_SHADER_DIR")) {
        if (file_exists(std::string(env) + "/easu.spv")) {
            *out = env;
            return true;
        }
    }
    char self[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", self, sizeof(self) - 1);
    if (n > 0) {
        self[n] = 0;
        char buf[PATH_MAX];
        std::strncpy(buf, self, sizeof(buf));
        std::string dir = dirname(buf);
        const char* cands[] = {"/shaders", "/../shaders", "/../src/shaders"};
        for (const char* c : cands) {
            std::string p = dir + c;
            if (file_exists(p + "/easu.spv")) {
                *out = p;
                return true;
            }
        }
    }
    const char* extra[] = {"build/shaders", "linux/build/shaders", "./shaders"};
    for (const char* e : extra) {
        if (file_exists(std::string(e) + "/easu.spv")) {
            *out = e;
            return true;
        }
    }
    return false;
}

uint32_t vk_find_memory_type(VkPhysicalDevice phys, uint32_t type_bits, VkMemoryPropertyFlags flags) {
    VkPhysicalDeviceMemoryProperties mp{};
    vkGetPhysicalDeviceMemoryProperties(phys, &mp);
    for (uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
        if ((type_bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & flags) == flags) return i;
    }
    for (uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
        if (type_bits & (1u << i)) return i;
    }
    return 0;
}

static bool has_ext(const std::vector<VkExtensionProperties>& props, const char* name) {
    for (const auto& p : props) {
        if (std::strcmp(p.extensionName, name) == 0) return true;
    }
    return false;
}

bool vk_instance_create(VkContext* ctx, const char* const* inst_exts, uint32_t n_ext, std::string* err) {
    VkApplicationInfo app{};
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "Universal-FrameFX";
    app.applicationVersion = VK_MAKE_VERSION(1, 4, 0);
    app.pEngineName = "CSR";
    app.apiVersion = VK_API_VERSION_1_2;

    VkInstanceCreateInfo ici{};
    ici.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = n_ext;
    ici.ppEnabledExtensionNames = inst_exts;
    if (vkCreateInstance(&ici, nullptr, &ctx->instance) != VK_SUCCESS) {
        if (err) *err = "vkCreateInstance failed";
        return false;
    }
    return true;
}

bool vk_device_create(VkContext* ctx, VkSurfaceKHR present_surface, std::string* err) {
    uint32_t nphys = 0;
    vkEnumeratePhysicalDevices(ctx->instance, &nphys, nullptr);
    if (nphys == 0) {
        if (err) *err = "No Vulkan physical devices";
        return false;
    }
    std::vector<VkPhysicalDevice> physs(nphys);
    vkEnumeratePhysicalDevices(ctx->instance, &nphys, physs.data());
    ctx->phys = physs[0];
    vkGetPhysicalDeviceProperties(ctx->phys, &ctx->props);
    ctx->device_name = ctx->props.deviceName;

    uint32_t nq = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(ctx->phys, &nq, nullptr);
    std::vector<VkQueueFamilyProperties> qf(nq);
    vkGetPhysicalDeviceQueueFamilyProperties(ctx->phys, &nq, qf.data());
    ctx->queue_family = 0;
    bool found = false;
    for (uint32_t i = 0; i < nq; ++i) {
        const bool compute = (qf[i].queueFlags & VK_QUEUE_COMPUTE_BIT) != 0;
        const bool graphics = (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0;
        VkBool32 present = VK_TRUE;
        if (present_surface) {
            vkGetPhysicalDeviceSurfaceSupportKHR(ctx->phys, i, present_surface, &present);
        }
        if (compute && present && (!present_surface || graphics)) {
            ctx->queue_family = i;
            found = true;
            break;
        }
    }
    if (!found) {
        for (uint32_t i = 0; i < nq; ++i) {
            if (qf[i].queueFlags & VK_QUEUE_COMPUTE_BIT) {
                ctx->queue_family = i;
                break;
            }
        }
    }

    uint32_t next = 0;
    vkEnumerateDeviceExtensionProperties(ctx->phys, nullptr, &next, nullptr);
    std::vector<VkExtensionProperties> exts(next);
    vkEnumerateDeviceExtensionProperties(ctx->phys, nullptr, &next, exts.data());
    std::vector<const char*> enable;
    if (has_ext(exts, VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME)) {
        enable.push_back(VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME);
        ctx->has_external_fd = true;
    }
    if (has_ext(exts, VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME)) {
        enable.push_back(VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME);
        ctx->has_dma_buf = true;
    }
    if (has_ext(exts, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME)) {
        enable.push_back(VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME);
        ctx->has_drm_mod = true;
    }
    if (present_surface && has_ext(exts, VK_KHR_SWAPCHAIN_EXTENSION_NAME)) {
        enable.push_back(VK_KHR_SWAPCHAIN_EXTENSION_NAME);
    }

    VkPhysicalDeviceShaderFloat16Int8Features f16{};
    f16.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_FLOAT16_INT8_FEATURES;
    VkPhysicalDeviceFeatures2 f2{};
    f2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
    f2.pNext = &f16;
    vkGetPhysicalDeviceFeatures2(ctx->phys, &f2);
    ctx->has_fp16 = f16.shaderFloat16 == VK_TRUE;

    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci{};
    qci.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qci.queueFamilyIndex = ctx->queue_family;
    qci.queueCount = 1;
    qci.pQueuePriorities = &prio;

    VkDeviceCreateInfo dci{};
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = static_cast<uint32_t>(enable.size());
    dci.ppEnabledExtensionNames = enable.empty() ? nullptr : enable.data();
    VkPhysicalDeviceShaderFloat16Int8Features f16_en = f16;
    if (ctx->has_fp16) {
        f16_en.shaderFloat16 = VK_TRUE;
        dci.pNext = &f16_en;
    }
    if (vkCreateDevice(ctx->phys, &dci, nullptr, &ctx->device) != VK_SUCCESS) {
        if (err) *err = "vkCreateDevice failed";
        return false;
    }
    vkGetDeviceQueue(ctx->device, ctx->queue_family, 0, &ctx->queue);

    VkCommandPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pci.queueFamilyIndex = ctx->queue_family;
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    if (vkCreateCommandPool(ctx->device, &pci, nullptr, &ctx->pool) != VK_SUCCESS) {
        if (err) *err = "vkCreateCommandPool failed";
        return false;
    }
    return true;
}

bool vk_context_init_headless(VkContext* ctx, std::string* err) {
    *ctx = {};
    if (!vk_find_shader_dir(&ctx->shader_dir)) {
        if (err) *err = "Could not find shaders/easu.spv (set UFX_SHADER_DIR)";
        return false;
    }
    if (!vk_instance_create(ctx, nullptr, 0, err)) return false;
    return vk_device_create(ctx, VK_NULL_HANDLE, err);
}

void vk_context_shutdown(VkContext* ctx) {
    if (!ctx) return;
    if (ctx->device) vkDeviceWaitIdle(ctx->device);
    if (ctx->pool) vkDestroyCommandPool(ctx->device, ctx->pool, nullptr);
    if (ctx->device) vkDestroyDevice(ctx->device, nullptr);
    if (ctx->instance) vkDestroyInstance(ctx->instance, nullptr);
    *ctx = {};
}

bool vk_create_buffer(VkContext* ctx, VkDeviceSize size, VkBufferUsageFlags usage, VkMemoryPropertyFlags mem_flags,
                      VkBuffer* buf, VkDeviceMemory* mem) {
    VkBufferCreateInfo bi{};
    bi.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bi.size = size;
    bi.usage = usage;
    bi.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateBuffer(ctx->device, &bi, nullptr, buf) != VK_SUCCESS) return false;
    VkMemoryRequirements req{};
    vkGetBufferMemoryRequirements(ctx->device, *buf, &req);
    VkMemoryAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = vk_find_memory_type(ctx->phys, req.memoryTypeBits, mem_flags);
    if (vkAllocateMemory(ctx->device, &ai, nullptr, mem) != VK_SUCCESS) return false;
    vkBindBufferMemory(ctx->device, *buf, *mem, 0);
    return true;
}

bool vk_create_image_2d(VkContext* ctx, uint32_t w, uint32_t h, VkFormat format, VkImageUsageFlags usage, VkImage* img,
                        VkDeviceMemory* mem, VkImageView* view) {
    VkImageCreateInfo ii{};
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = format;
    ii.extent = {w, h, 1};
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = usage;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (vkCreateImage(ctx->device, &ii, nullptr, img) != VK_SUCCESS) {
        // Lavapipe sometimes prefers linear tiling for host-visible images.
        ii.tiling = VK_IMAGE_TILING_LINEAR;
        if (vkCreateImage(ctx->device, &ii, nullptr, img) != VK_SUCCESS) return false;
    }
    VkMemoryRequirements req{};
    vkGetImageMemoryRequirements(ctx->device, *img, &req);
    VkMemoryAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = vk_find_memory_type(
        ctx->phys, req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (vkAllocateMemory(ctx->device, &ai, nullptr, mem) != VK_SUCCESS) {
        ai.memoryTypeIndex = vk_find_memory_type(ctx->phys, req.memoryTypeBits, 0);
        if (vkAllocateMemory(ctx->device, &ai, nullptr, mem) != VK_SUCCESS) return false;
    }
    vkBindImageMemory(ctx->device, *img, *mem, 0);
    VkImageViewCreateInfo vi{};
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = *img;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = format;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    return vkCreateImageView(ctx->device, &vi, nullptr, view) == VK_SUCCESS;
}

VkShaderModule vk_load_shader(VkContext* ctx, const char* filename, std::string* err) {
    std::string path = ctx->shader_dir + "/" + filename;
    std::ifstream f(path, std::ios::binary);
    if (!f) {
        if (err) *err = "Failed to open " + path;
        return VK_NULL_HANDLE;
    }
    std::vector<char> bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (bytes.size() < 4) {
        if (err) *err = "Shader too small: " + path;
        return VK_NULL_HANDLE;
    }
    VkShaderModuleCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = bytes.size();
    ci.pCode = reinterpret_cast<const uint32_t*>(bytes.data());
    VkShaderModule mod = VK_NULL_HANDLE;
    if (vkCreateShaderModule(ctx->device, &ci, nullptr, &mod) != VK_SUCCESS) {
        if (err) *err = "vkCreateShaderModule failed for " + path;
        return VK_NULL_HANDLE;
    }
    return mod;
}

VkCommandBuffer vk_begin_once(VkContext* ctx) {
    VkCommandBufferAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool = ctx->pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    VkCommandBuffer cmd = VK_NULL_HANDLE;
    vkAllocateCommandBuffers(ctx->device, &ai, &cmd);
    VkCommandBufferBeginInfo bi{};
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vkBeginCommandBuffer(cmd, &bi);
    return cmd;
}

bool vk_submit_once(VkContext* ctx, VkCommandBuffer cmd) {
    vkEndCommandBuffer(cmd);
    VkSubmitInfo si{};
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    if (vkQueueSubmit(ctx->queue, 1, &si, VK_NULL_HANDLE) != VK_SUCCESS) return false;
    vkQueueWaitIdle(ctx->queue);
    vkFreeCommandBuffers(ctx->device, ctx->pool, 1, &cmd);
    return true;
}
