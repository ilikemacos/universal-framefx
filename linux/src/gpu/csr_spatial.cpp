#include "csr_spatial.h"
#include "vk_dmabuf_import.h"

#include <cstring>
#include <vector>

void csr_fill_uniforms(SpatialUniformsCPU* u, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                       float sharpness) {
    std::memset(u, 0, sizeof(*u));
    u->inputSize[0] = static_cast<float>(in_w);
    u->inputSize[1] = static_cast<float>(in_h);
    u->outputSize[0] = static_cast<float>(out_w);
    u->outputSize[1] = static_cast<float>(out_h);
    u->invInputSize[0] = in_w ? 1.0f / static_cast<float>(in_w) : 0.0f;
    u->invInputSize[1] = in_h ? 1.0f / static_cast<float>(in_h) : 0.0f;
    u->scale[0] = static_cast<float>(in_w) / static_cast<float>(out_w);
    u->scale[1] = static_cast<float>(in_h) / static_cast<float>(out_h);
    u->sharpness = sharpness;
}

static VkDescriptorSetLayout make_layout(VkDevice dev, VkDescriptorType b1, VkDescriptorType b2) {
    VkDescriptorSetLayoutBinding binds[3]{};
    binds[0].binding = 0;
    binds[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    binds[0].descriptorCount = 1;
    binds[0].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    binds[1].binding = 1;
    binds[1].descriptorType = b1;
    binds[1].descriptorCount = 1;
    binds[1].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    binds[2].binding = 2;
    binds[2].descriptorType = b2;
    binds[2].descriptorCount = 1;
    binds[2].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    VkDescriptorSetLayoutCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    ci.bindingCount = 3;
    ci.pBindings = binds;
    VkDescriptorSetLayout layout = VK_NULL_HANDLE;
    vkCreateDescriptorSetLayout(dev, &ci, nullptr, &layout);
    return layout;
}

static VkPipeline make_compute(VkDevice dev, VkPipelineLayout pl, VkShaderModule mod) {
    VkPipelineShaderStageCreateInfo st{};
    st.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    st.module = mod;
    st.pName = "main";
    VkComputePipelineCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
    ci.stage = st;
    ci.layout = pl;
    VkPipeline pipe = VK_NULL_HANDLE;
    vkCreateComputePipelines(dev, VK_NULL_HANDLE, 1, &ci, nullptr, &pipe);
    return pipe;
}

bool csr_spatial_create(VkContext* ctx, CsrSpatial* out, std::string* err) {
    *out = {};
    out->ctx = ctx;
    std::string e;
    VkShaderModule easu = vk_load_shader(ctx, "easu.spv", err ? err : &e);
    VkShaderModule rcas = vk_load_shader(ctx, "rcas.spv", err ? err : &e);
    if (!easu || !rcas) {
        if (easu) vkDestroyShaderModule(ctx->device, easu, nullptr);
        if (rcas) vkDestroyShaderModule(ctx->device, rcas, nullptr);
        return false;
    }

    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = VK_FILTER_NEAREST;
    sci.minFilter = VK_FILTER_NEAREST;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    vkCreateSampler(ctx->device, &sci, nullptr, &out->nearest);

    out->easu_layout = make_layout(ctx->device, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE);
    out->rcas_layout = make_layout(ctx->device, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE);

    VkPipelineLayoutCreateInfo lci{};
    lci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    lci.setLayoutCount = 1;
    lci.pSetLayouts = &out->easu_layout;
    vkCreatePipelineLayout(ctx->device, &lci, nullptr, &out->easu_pl);
    lci.pSetLayouts = &out->rcas_layout;
    vkCreatePipelineLayout(ctx->device, &lci, nullptr, &out->rcas_pl);

    out->easu_pipe = make_compute(ctx->device, out->easu_pl, easu);
    out->rcas_pipe = make_compute(ctx->device, out->rcas_pl, rcas);
    vkDestroyShaderModule(ctx->device, easu, nullptr);
    vkDestroyShaderModule(ctx->device, rcas, nullptr);
    if (!out->easu_pipe || !out->rcas_pipe) {
        if (err) *err = "Failed to create compute pipelines";
        return false;
    }

    VkDescriptorPoolSize sizes[3];
    sizes[0] = {VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 8};
    sizes[1] = {VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 8};
    sizes[2] = {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 16};
    VkDescriptorPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    pci.maxSets = 16;
    pci.poolSizeCount = 3;
    pci.pPoolSizes = sizes;
    vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool);

    if (!vk_create_buffer(ctx, sizeof(SpatialUniformsCPU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &out->ubo,
                          &out->ubo_mem)) {
        if (err) *err = "Failed to create UBO";
        return false;
    }
    return true;
}

void csr_spatial_destroy(CsrSpatial* s) {
    if (!s || !s->ctx) return;
    VkDevice d = s->ctx->device;
    if (s->ubo) vkDestroyBuffer(d, s->ubo, nullptr);
    if (s->ubo_mem) vkFreeMemory(d, s->ubo_mem, nullptr);
    if (s->easu_pipe) vkDestroyPipeline(d, s->easu_pipe, nullptr);
    if (s->rcas_pipe) vkDestroyPipeline(d, s->rcas_pipe, nullptr);
    if (s->easu_pl) vkDestroyPipelineLayout(d, s->easu_pl, nullptr);
    if (s->rcas_pl) vkDestroyPipelineLayout(d, s->rcas_pl, nullptr);
    if (s->easu_layout) vkDestroyDescriptorSetLayout(d, s->easu_layout, nullptr);
    if (s->rcas_layout) vkDestroyDescriptorSetLayout(d, s->rcas_layout, nullptr);
    if (s->pool) vkDestroyDescriptorPool(d, s->pool, nullptr);
    if (s->nearest) vkDestroySampler(d, s->nearest, nullptr);
    *s = {};
}

static void barrier_img(VkCommandBuffer cmd, VkImage img, VkAccessFlags srcA, VkAccessFlags dstA,
                        VkPipelineStageFlags srcS, VkPipelineStageFlags dstS) {
    VkImageMemoryBarrier b{};
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcA;
    b.dstAccessMask = dstA;
    b.oldLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(cmd, srcS, dstS, 0, 0, nullptr, 0, nullptr, 1, &b);
}

bool csr_spatial_execute(CsrSpatial* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                         float sharpness, float* dst, std::string* err) {
    VkContext* ctx = s->ctx;
    const VkFormat fmt = VK_FORMAT_R32G32B32A32_SFLOAT;
    VkImage src_img = VK_NULL_HANDLE, mid_img = VK_NULL_HANDLE, dst_img = VK_NULL_HANDLE;
    VkDeviceMemory src_mem = VK_NULL_HANDLE, mid_mem = VK_NULL_HANDLE, dst_mem = VK_NULL_HANDLE;
    VkImageView src_view = VK_NULL_HANDLE, mid_view = VK_NULL_HANDLE, dst_view = VK_NULL_HANDLE;
    const VkImageUsageFlags usage =
        VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
        VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    if (!vk_create_image_2d(ctx, in_w, in_h, fmt, usage, &src_img, &src_mem, &src_view) ||
        !vk_create_image_2d(ctx, out_w, out_h, fmt, usage, &mid_img, &mid_mem, &mid_view) ||
        !vk_create_image_2d(ctx, out_w, out_h, fmt, usage, &dst_img, &dst_mem, &dst_view)) {
        if (err) *err = "Failed to create compute images";
        return false;
    }
    if (!vk_upload_rgba32f_staging(ctx, src, in_w, in_h, src_img)) {
        if (err) *err = "Failed to upload source image";
        return false;
    }

    SpatialUniformsCPU u{};
    csr_fill_uniforms(&u, in_w, in_h, out_w, out_h, sharpness);
    void* mapped = nullptr;
    vkMapMemory(ctx->device, s->ubo_mem, 0, sizeof(u), 0, &mapped);
    std::memcpy(mapped, &u, sizeof(u));
    vkUnmapMemory(ctx->device, s->ubo_mem);

    vkResetDescriptorPool(ctx->device, s->pool, 0);
    VkDescriptorSetAllocateInfo dai{};
    dai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    dai.descriptorPool = s->pool;
    dai.descriptorSetCount = 1;
    dai.pSetLayouts = &s->easu_layout;
    VkDescriptorSet easu_set = VK_NULL_HANDLE, rcas_set = VK_NULL_HANDLE;
    vkAllocateDescriptorSets(ctx->device, &dai, &easu_set);
    dai.pSetLayouts = &s->rcas_layout;
    vkAllocateDescriptorSets(ctx->device, &dai, &rcas_set);

    VkDescriptorBufferInfo ubo_info{s->ubo, 0, sizeof(SpatialUniformsCPU)};
    VkDescriptorImageInfo easu_src{s->nearest, src_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo easu_dst{VK_NULL_HANDLE, mid_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo rcas_src{VK_NULL_HANDLE, mid_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo rcas_dst{VK_NULL_HANDLE, dst_view, VK_IMAGE_LAYOUT_GENERAL};

    VkWriteDescriptorSet writes[6]{};
    auto W = [](VkDescriptorSet set, uint32_t binding, VkDescriptorType t, const VkDescriptorBufferInfo* b,
                const VkDescriptorImageInfo* i) {
        VkWriteDescriptorSet w{};
        w.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        w.dstSet = set;
        w.dstBinding = binding;
        w.descriptorCount = 1;
        w.descriptorType = t;
        w.pBufferInfo = b;
        w.pImageInfo = i;
        return w;
    };
    writes[0] = W(easu_set, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo_info, nullptr);
    writes[1] = W(easu_set, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &easu_src);
    writes[2] = W(easu_set, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &easu_dst);
    writes[3] = W(rcas_set, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo_info, nullptr);
    writes[4] = W(rcas_set, 1, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &rcas_src);
    writes[5] = W(rcas_set, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &rcas_dst);
    vkUpdateDescriptorSets(ctx->device, 6, writes, 0, nullptr);

    VkCommandBuffer cmd = vk_begin_once(ctx);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->easu_pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->easu_pl, 0, 1, &easu_set, 0, nullptr);
    vkCmdDispatch(cmd, (out_w + 7) / 8, (out_h + 7) / 8, 1);
    barrier_img(cmd, mid_img, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->rcas_pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->rcas_pl, 0, 1, &rcas_set, 0, nullptr);
    vkCmdDispatch(cmd, (out_w + 7) / 8, (out_h + 7) / 8, 1);
    if (!vk_submit_once(ctx, cmd)) {
        if (err) *err = "Compute submit failed";
        return false;
    }
    bool ok = vk_download_rgba32f(ctx, dst_img, out_w, out_h, dst);
    if (!ok && err) *err = "Readback failed";

    vkDestroyImageView(ctx->device, src_view, nullptr);
    vkDestroyImageView(ctx->device, mid_view, nullptr);
    vkDestroyImageView(ctx->device, dst_view, nullptr);
    vkDestroyImage(ctx->device, src_img, nullptr);
    vkDestroyImage(ctx->device, mid_img, nullptr);
    vkDestroyImage(ctx->device, dst_img, nullptr);
    vkFreeMemory(ctx->device, src_mem, nullptr);
    vkFreeMemory(ctx->device, mid_mem, nullptr);
    vkFreeMemory(ctx->device, dst_mem, nullptr);
    return ok;
}
