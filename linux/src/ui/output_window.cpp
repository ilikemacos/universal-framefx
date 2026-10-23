#include "output_window.h"

#include <cstring>
#include <string>
#include <vector>

struct VertPC {};

bool present_pass_create(VkContext* ctx, VkRenderPass rp, PresentPass* out, std::string* err) {
    *out = {};
    std::string e;
    VkShaderModule vs = vk_load_shader(ctx, "present.vert.spv", err ? err : &e);
    VkShaderModule fs = vk_load_shader(ctx, "present.frag.spv", err ? err : &e);
    if (!vs || !fs) return false;

    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = VK_FILTER_LINEAR;
    sci.minFilter = VK_FILTER_LINEAR;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    vkCreateSampler(ctx->device, &sci, nullptr, &out->linear);

    VkDescriptorSetLayoutBinding binds[2]{};
    binds[0].binding = 0;
    binds[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    binds[0].descriptorCount = 1;
    binds[0].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    binds[1].binding = 1;
    binds[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binds[1].descriptorCount = 1;
    binds[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo lci{};
    lci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    lci.bindingCount = 2;
    lci.pBindings = binds;
    vkCreateDescriptorSetLayout(ctx->device, &lci, nullptr, &out->set_layout);

    VkPipelineLayoutCreateInfo plci{};
    plci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    plci.setLayoutCount = 1;
    plci.pSetLayouts = &out->set_layout;
    vkCreatePipelineLayout(ctx->device, &plci, nullptr, &out->layout);

    VkPipelineShaderStageCreateInfo stages[2]{};
    stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    stages[0].module = vs;
    stages[0].pName = "main";
    stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    stages[1].module = fs;
    stages[1].pName = "main";

    VkPipelineVertexInputStateCreateInfo vi{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};
    VkPipelineInputAssemblyStateCreateInfo ia{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vp{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};
    vp.viewportCount = 1;
    vp.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineColorBlendAttachmentState att{};
    att.colorWriteMask = 0xF;
    VkPipelineColorBlendStateCreateInfo cb{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};
    cb.attachmentCount = 1;
    cb.pAttachments = &att;
    VkDynamicState dyns[] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
    VkPipelineDynamicStateCreateInfo dyn{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO};
    dyn.dynamicStateCount = 2;
    dyn.pDynamicStates = dyns;

    VkGraphicsPipelineCreateInfo gp{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
    gp.stageCount = 2;
    gp.pStages = stages;
    gp.pVertexInputState = &vi;
    gp.pInputAssemblyState = &ia;
    gp.pViewportState = &vp;
    gp.pRasterizationState = &rs;
    gp.pMultisampleState = &ms;
    gp.pColorBlendState = &cb;
    gp.pDynamicState = &dyn;
    gp.layout = out->layout;
    gp.renderPass = rp;
    if (vkCreateGraphicsPipelines(ctx->device, VK_NULL_HANDLE, 1, &gp, nullptr, &out->pipeline) != VK_SUCCESS) {
        if (err) *err = "present pipeline failed";
        vkDestroyShaderModule(ctx->device, vs, nullptr);
        vkDestroyShaderModule(ctx->device, fs, nullptr);
        return false;
    }
    vkDestroyShaderModule(ctx->device, vs, nullptr);
    vkDestroyShaderModule(ctx->device, fs, nullptr);

    if (!vk_create_buffer(ctx, sizeof(PresentUniformsCPU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &out->ubo,
                          &out->ubo_mem)) {
        return false;
    }
    VkDescriptorPoolSize sizes[2] = {{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 4},
                                     {VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 4}};
    VkDescriptorPoolCreateInfo pci{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
    pci.maxSets = 4;
    pci.poolSizeCount = 2;
    pci.pPoolSizes = sizes;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool);
    VkDescriptorSetAllocateInfo dai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
    dai.descriptorPool = out->pool;
    dai.descriptorSetCount = 1;
    dai.pSetLayouts = &out->set_layout;
    vkAllocateDescriptorSets(ctx->device, &dai, &out->set);
    return true;
}

void present_pass_destroy(VkContext* ctx, PresentPass* p) {
    if (!ctx || !p) return;
    if (p->pipeline) vkDestroyPipeline(ctx->device, p->pipeline, nullptr);
    if (p->layout) vkDestroyPipelineLayout(ctx->device, p->layout, nullptr);
    if (p->set_layout) vkDestroyDescriptorSetLayout(ctx->device, p->set_layout, nullptr);
    if (p->linear) vkDestroySampler(ctx->device, p->linear, nullptr);
    if (p->ubo) vkDestroyBuffer(ctx->device, p->ubo, nullptr);
    if (p->ubo_mem) vkFreeMemory(ctx->device, p->ubo_mem, nullptr);
    if (p->pool) vkDestroyDescriptorPool(ctx->device, p->pool, nullptr);
    *p = {};
}

void present_pass_write_ubo(VkContext* ctx, PresentPass* p, const PresentUniformsCPU* u) {
    void* mapped = nullptr;
    vkMapMemory(ctx->device, p->ubo_mem, 0, sizeof(*u), 0, &mapped);
    std::memcpy(mapped, u, sizeof(*u));
    vkUnmapMemory(ctx->device, p->ubo_mem);
}

void present_pass_bind_image(VkContext* ctx, PresentPass* p, VkImageView view) {
    VkDescriptorBufferInfo bi{p->ubo, 0, sizeof(PresentUniformsCPU)};
    VkDescriptorImageInfo ii{p->linear, view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL};
    VkWriteDescriptorSet w[2]{};
    w[0].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    w[0].dstSet = p->set;
    w[0].dstBinding = 0;
    w[0].descriptorCount = 1;
    w[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    w[0].pBufferInfo = &bi;
    w[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    w[1].dstSet = p->set;
    w[1].dstBinding = 1;
    w[1].descriptorCount = 1;
    w[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    w[1].pImageInfo = &ii;
    vkUpdateDescriptorSets(ctx->device, 2, w, 0, nullptr);
}

void present_compute_letterbox(uint32_t img_w, uint32_t img_h, uint32_t win_w, uint32_t win_h, PresentUniformsCPU* u) {
    std::memset(u, 0, sizeof(*u));
    u->background[0] = 0.05f;
    u->background[1] = 0.06f;
    u->background[2] = 0.08f;
    u->background[3] = 1.0f;
    if (!img_w || !img_h || !win_w || !win_h) {
        u->scale[0] = u->scale[1] = 1.0f;
        return;
    }
    float img_a = static_cast<float>(img_w) / static_cast<float>(img_h);
    float win_a = static_cast<float>(win_w) / static_cast<float>(win_h);
    float used_w = 1.0f, used_h = 1.0f;
    if (win_a > img_a) {
        used_w = img_a / win_a;
        used_h = 1.0f;
    } else {
        used_w = 1.0f;
        used_h = win_a / img_a;
    }
    u->scale[0] = 1.0f / used_w;
    u->scale[1] = 1.0f / used_h;
    u->offset[0] = -(1.0f - used_w) * 0.5f * u->scale[0];
    u->offset[1] = -(1.0f - used_h) * 0.5f * u->scale[1];
}

bool present_submit(VkContext* ctx, SdlVkWindow* win, PresentPass* p, VkImageView view, uint32_t img_w, uint32_t img_h) {
    PresentUniformsCPU pu{};
    present_compute_letterbox(img_w, img_h, win->extent.width, win->extent.height, &pu);
    present_pass_write_ubo(ctx, p, &pu);
    present_pass_bind_image(ctx, p, view);
    if (!vk_window_acquire(ctx, win)) return false;
    VkCommandBuffer pcmd = win->cmds[win->frame % 2];
    vkResetCommandBuffer(pcmd, 0);
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    vkBeginCommandBuffer(pcmd, &bi);
    present_record(pcmd, win, p, img_w, img_h);
    vkEndCommandBuffer(pcmd);
    return vk_window_submit_present(ctx, win, pcmd);
}

void present_record(VkCommandBuffer cmd, SdlVkWindow* win, PresentPass* p, uint32_t img_w, uint32_t img_h) {
    (void)img_w;
    (void)img_h;
    VkClearValue clear{};
    clear.color = {{0.05f, 0.06f, 0.08f, 1.0f}};
    VkRenderPassBeginInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
    rp.renderPass = win->render_pass;
    rp.framebuffer = win->framebuffers[win->image_index];
    rp.renderArea.extent = win->extent;
    rp.clearValueCount = 1;
    rp.pClearValues = &clear;
    vkCmdBeginRenderPass(cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
    VkViewport vp{};
    vp.width = static_cast<float>(win->extent.width);
    vp.height = static_cast<float>(win->extent.height);
    vp.maxDepth = 1.0f;
    VkRect2D sc{{0, 0}, win->extent};
    vkCmdSetViewport(cmd, 0, 1, &vp);
    vkCmdSetScissor(cmd, 0, 1, &sc);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, p->pipeline);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, p->layout, 0, 1, &p->set, 0, nullptr);
    vkCmdDraw(cmd, 3, 1, 0, 0);
    vkCmdEndRenderPass(cmd);
}
