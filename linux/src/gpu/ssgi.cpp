#include "ssgi.h"
#include "ssgi_cpu_ref.h"
#include "vk_dmabuf_import.h"

#include <algorithm>
#include <cstring>
#include <vector>

static const VkImageUsageFlags kUse = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                      VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;

static bool img_make(VkContext* ctx, uint32_t w, uint32_t h, VkFormat fmt, SsgiImage* o) {
    *o = {};
    o->w = w;
    o->h = h;
    return vk_create_image_2d(ctx, w, h, fmt, kUse, &o->image, &o->mem, &o->view);
}

static void img_free(VkContext* ctx, SsgiImage* o) {
    if (!ctx || !ctx->device) return;
    if (o->view) vkDestroyImageView(ctx->device, o->view, nullptr);
    if (o->image) vkDestroyImage(ctx->device, o->image, nullptr);
    if (o->mem) vkFreeMemory(ctx->device, o->mem, nullptr);
    *o = {};
}

static VkSampler make_samp(VkDevice d) {
    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = VK_FILTER_LINEAR;
    sci.minFilter = VK_FILTER_LINEAR;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.maxLod = 0.f;
    VkSampler s = VK_NULL_HANDLE;
    vkCreateSampler(d, &sci, nullptr, &s);
    return s;
}

static VkDescriptorSetLayoutBinding B(uint32_t b, VkDescriptorType t) {
    VkDescriptorSetLayoutBinding x{};
    x.binding = b;
    x.descriptorType = t;
    x.descriptorCount = 1;
    x.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    return x;
}

static VkWriteDescriptorSet W(VkDescriptorSet set, uint32_t b, VkDescriptorType t, const VkDescriptorBufferInfo* bi,
                              const VkDescriptorImageInfo* ii) {
    VkWriteDescriptorSet w{};
    w.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    w.dstSet = set;
    w.dstBinding = b;
    w.descriptorCount = 1;
    w.descriptorType = t;
    w.pBufferInfo = bi;
    w.pImageInfo = ii;
    return w;
}

static VkPipeline make_cs(VkContext* ctx, VkPipelineLayout pl, const char* spv, std::string* err) {
    VkShaderModule mod = vk_load_shader(ctx, spv, err);
    if (!mod) return VK_NULL_HANDLE;
    VkPipelineShaderStageCreateInfo st{};
    st.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    st.module = mod;
    st.pName = "main";
    VkComputePipelineCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
    ci.stage = st;
    ci.layout = pl;
    VkPipeline p = VK_NULL_HANDLE;
    vkCreateComputePipelines(ctx->device, VK_NULL_HANDLE, 1, &ci, nullptr, &p);
    vkDestroyShaderModule(ctx->device, mod, nullptr);
    return p;
}

static VkDescriptorSet alloc_set(Ssgi* s) {
    VkDescriptorSetAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = s->pool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &s->set_layout;
    VkDescriptorSet set = VK_NULL_HANDLE;
    vkAllocateDescriptorSets(s->ctx->device, &ai, &set);
    return set;
}

bool ssgi_create(VkContext* ctx, Ssgi* out, std::string* err) {
    *out = {};
    out->ctx = ctx;
    out->linear = make_samp(ctx->device);
    VkDescriptorSetLayoutBinding b[6] = {
        B(0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER), B(1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER),
        B(2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE),          B(3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE),
        B(4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE),          B(5, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE)};
    VkDescriptorSetLayoutCreateInfo li{};
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 6;
    li.pBindings = b;
    if (vkCreateDescriptorSetLayout(ctx->device, &li, nullptr, &out->set_layout) != VK_SUCCESS) {
        if (err) *err = "SSGI descriptor layout failed.";
        ssgi_destroy(out);
        return false;
    }
    VkPushConstantRange pcr{};
    pcr.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    pcr.size = sizeof(SsgiParamsCPU);
    VkPipelineLayoutCreateInfo pli{};
    pli.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pli.setLayoutCount = 1;
    pli.pSetLayouts = &out->set_layout;
    pli.pushConstantRangeCount = 1;
    pli.pPushConstantRanges = &pcr;
    if (vkCreatePipelineLayout(ctx->device, &pli, nullptr, &out->pipe_layout) != VK_SUCCESS) {
        if (err) *err = "SSGI pipeline layout failed.";
        ssgi_destroy(out);
        return false;
    }
    out->down_pipe = make_cs(ctx, out->pipe_layout, "ssgi_downsample.spv", err);
    out->march_pipe = make_cs(ctx, out->pipe_layout, "ssgi_march.spv", err);
    out->filter_pipe = make_cs(ctx, out->pipe_layout, "ssgi_filter.spv", err);
    out->composite_pipe = make_cs(ctx, out->pipe_layout, "ssgi_composite.spv", err);
    if (!out->down_pipe || !out->march_pipe || !out->filter_pipe || !out->composite_pipe) {
        if (err && err->empty()) *err = "Failed to create SSGI pipelines";
        ssgi_destroy(out);
        return false;
    }
    VkDescriptorPoolSize sizes[3] = {{VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 16},
                                     {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 16},
                                     {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 64}};
    VkDescriptorPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    pci.maxSets = 16;
    pci.poolSizeCount = 3;
    pci.pPoolSizes = sizes;
    if (vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool) != VK_SUCCESS) {
        if (err) *err = "SSGI descriptor pool failed.";
        ssgi_destroy(out);
        return false;
    }
    return true;
}

void ssgi_destroy(Ssgi* s) {
    if (!s || !s->ctx || !s->ctx->device) {
        if (s) *s = {};
        return;
    }
    VkDevice d = s->ctx->device;
    vkDeviceWaitIdle(d);
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->light_a);
    img_free(s->ctx, &s->light_b);
    img_free(s->ctx, &s->occ_a);
    img_free(s->ctx, &s->occ_b);
    img_free(s->ctx, &s->dst);
    if (s->grid) vkDestroyBuffer(d, s->grid, nullptr);
    if (s->grid_mem) vkFreeMemory(d, s->grid_mem, nullptr);
    if (s->down_pipe) vkDestroyPipeline(d, s->down_pipe, nullptr);
    if (s->march_pipe) vkDestroyPipeline(d, s->march_pipe, nullptr);
    if (s->filter_pipe) vkDestroyPipeline(d, s->filter_pipe, nullptr);
    if (s->composite_pipe) vkDestroyPipeline(d, s->composite_pipe, nullptr);
    if (s->pipe_layout) vkDestroyPipelineLayout(d, s->pipe_layout, nullptr);
    if (s->set_layout) vkDestroyDescriptorSetLayout(d, s->set_layout, nullptr);
    if (s->pool) vkDestroyDescriptorPool(d, s->pool, nullptr);
    if (s->linear) vkDestroySampler(d, s->linear, nullptr);
    *s = {};
}

static void release_sized(Ssgi* s) {
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->light_a);
    img_free(s->ctx, &s->light_b);
    img_free(s->ctx, &s->occ_a);
    img_free(s->ctx, &s->occ_b);
    img_free(s->ctx, &s->dst);
    if (s->grid) vkDestroyBuffer(s->ctx->device, s->grid, nullptr);
    if (s->grid_mem) vkFreeMemory(s->ctx->device, s->grid_mem, nullptr);
    s->grid = VK_NULL_HANDLE;
    s->grid_mem = VK_NULL_HANDLE;
    s->src_w = s->src_h = s->light_w = s->light_h = 0;
}

static bool ensure(Ssgi* s, uint32_t w, uint32_t h) {
    if (s->src.image && s->src_w == w && s->src_h == h) return true;
    release_sized(s);
    s->src_w = w;
    s->src_h = h;
    s->light_w = std::max<uint32_t>(ssgi_ref::kGrid, w / ssgi_ref::kLightDivisor);
    s->light_h = std::max<uint32_t>(ssgi_ref::kGrid, h / ssgi_ref::kLightDivisor);
    s->frame = 0;
    if (!img_make(s->ctx, w, h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->src) ||
        !img_make(s->ctx, s->light_w, s->light_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->light_a) ||
        !img_make(s->ctx, s->light_w, s->light_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->light_b) ||
        !img_make(s->ctx, s->light_w, s->light_h, VK_FORMAT_R32_SFLOAT, &s->occ_a) ||
        !img_make(s->ctx, s->light_w, s->light_h, VK_FORMAT_R32_SFLOAT, &s->occ_b) ||
        !img_make(s->ctx, w, h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->dst))
        return false;
    const VkDeviceSize grid_bytes = static_cast<VkDeviceSize>(ssgi_ref::kGrid) * ssgi_ref::kGrid * 4 * sizeof(float);
    if (!vk_create_buffer(s->ctx, grid_bytes, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,
                          &s->grid, &s->grid_mem))
        return false;
    const SsgiImage* imgs[] = {&s->src, &s->light_a, &s->light_b, &s->occ_a, &s->occ_b, &s->dst};
    for (const SsgiImage* im : imgs) {
        if (!vk_clear_color_image(s->ctx, im->image, 0.f, 0.f, 0.f, 0.f)) return false;
    }
    return true;
}

static SsgiParamsCPU make_params(const Ssgi* s, float strength) {
    SsgiParamsCPU p{};
    p.srcW = static_cast<float>(s->src_w);
    p.srcH = static_cast<float>(s->src_h);
    p.lightW = static_cast<float>(s->light_w);
    p.lightH = static_cast<float>(s->light_h);
    p.invSrcW = 1.f / p.srcW;
    p.invSrcH = 1.f / p.srcH;
    p.invLightW = 1.f / p.lightW;
    p.invLightH = 1.f / p.lightH;
    p.frameIndex = static_cast<float>(s->frame);
    p.marchSteps = static_cast<float>(ssgi_ref::kMarchSteps);
    p.raysPerTexel = static_cast<float>(ssgi_ref::kRaysPerTexel);
    p.stepCells = ssgi_ref::kStepCells;
    p.strength = strength;
    p.aoStrength = ssgi_ref::kAoStrength;
    p.emitLo = ssgi_ref::kEmitLo;
    p.emitHi = ssgi_ref::kEmitHi;
    p.falloff = ssgi_ref::kFalloff;
    p.rangeK = ssgi_ref::kRangeK;
    p.heightScale = ssgi_ref::kHeightScale;
    p.stepGrowth = ssgi_ref::kStepGrowth;
    p.horizonBias = ssgi_ref::kHorizonBias;
    p.aoRadius = ssgi_ref::kAoRadius;
    p.aoGame = ssgi_ref::kAoGame;
    return p;
}

static void mem_barrier(VkCommandBuffer cmd) {
    VkMemoryBarrier mb{};
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT;
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, 0, 1, &mb, 0,
                         nullptr, 0, nullptr);
}

static void run(Ssgi* s, VkCommandBuffer cmd, VkPipeline pipe, VkDescriptorSet set, const SsgiParamsCPU& p, uint32_t w,
                uint32_t h) {
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->pipe_layout, 0, 1, &set, 0, nullptr);
    vkCmdPushConstants(cmd, s->pipe_layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(p), &p);
    vkCmdDispatch(cmd, (w + 7) / 8, (h + 7) / 8, 1);
    mem_barrier(cmd);
}

static bool dispatch(Ssgi* s, float strength, std::string* err) {
    SsgiParamsCPU p = make_params(s, strength);
    vkResetDescriptorPool(s->ctx->device, s->pool, 0);
    VkDescriptorBufferInfo grid{s->grid, 0, VK_WHOLE_SIZE};
    VkDescriptorImageInfo game{s->linear, s->src.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo la{VK_NULL_HANDLE, s->light_a.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo lb{VK_NULL_HANDLE, s->light_b.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo oa{VK_NULL_HANDLE, s->occ_a.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo ob{VK_NULL_HANDLE, s->occ_b.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo out{VK_NULL_HANDLE, s->dst.view, VK_IMAGE_LAYOUT_GENERAL};
    VkCommandBuffer cmd = vk_begin_once(s->ctx);

    VkDescriptorSet down = alloc_set(s);
    VkWriteDescriptorSet dw[2] = {W(down, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &game),
                                  W(down, 1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, &grid, nullptr)};
    vkUpdateDescriptorSets(s->ctx->device, 2, dw, 0, nullptr);
    run(s, cmd, s->down_pipe, down, p, ssgi_ref::kGrid, ssgi_ref::kGrid);

    VkDescriptorSet march = alloc_set(s);
    VkWriteDescriptorSet mw[3] = {W(march, 1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, &grid, nullptr),
                                  W(march, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &la),
                                  W(march, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &oa)};
    vkUpdateDescriptorSets(s->ctx->device, 3, mw, 0, nullptr);
    run(s, cmd, s->march_pipe, march, p, s->light_w, s->light_h);

    VkDescriptorSet filter = alloc_set(s);
    VkWriteDescriptorSet fw[4] = {W(filter, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &la),
                                  W(filter, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &oa),
                                  W(filter, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &lb),
                                  W(filter, 5, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &ob)};
    vkUpdateDescriptorSets(s->ctx->device, 4, fw, 0, nullptr);
    run(s, cmd, s->filter_pipe, filter, p, s->light_w, s->light_h);

    VkDescriptorSet comp = alloc_set(s);
    VkWriteDescriptorSet cw[4] = {W(comp, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &game),
                                  W(comp, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &lb),
                                  W(comp, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &ob),
                                  W(comp, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &out)};
    vkUpdateDescriptorSets(s->ctx->device, 4, cw, 0, nullptr);
    run(s, cmd, s->composite_pipe, comp, p, s->src_w, s->src_h);

    if (!vk_submit_once(s->ctx, cmd)) {
        if (err) *err = "SSGI dispatch failed";
        return false;
    }
    ++s->frame;
    return true;
}

bool ssgi_apply_encode(Ssgi* s, const float* src, uint32_t w, uint32_t h, float strength, float* dst, std::string* err) {
    if (!ensure(s, w, h)) {
        if (err) *err = "SSGI image alloc failed";
        return false;
    }
    if (!vk_upload_rgba32f_staging(s->ctx, src, w, h, s->src.image)) {
        if (err) *err = "SSGI upload failed";
        return false;
    }
    if (!dispatch(s, strength, err)) return false;
    return vk_download_rgba32f(s->ctx, s->dst.image, w, h, dst);
}

bool ssgi_apply_from_image(Ssgi* s, VkImage src, uint32_t w, uint32_t h, float strength, std::string* err) {
    if (!ensure(s, w, h)) {
        if (err) *err = "SSGI image alloc failed";
        return false;
    }
    if (!vk_copy_image_2d(s->ctx, src, s->src.image, w, h)) {
        if (err) *err = "SSGI copy failed";
        return false;
    }
    return dispatch(s, strength, err);
}
