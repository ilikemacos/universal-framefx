#include "csr2_upscale.h"
#include "vk_dmabuf_import.h"

#include <cstring>
#include <vector>

static const VkImageUsageFlags kUse = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                      VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;

static bool img_make(VkContext* ctx, uint32_t w, uint32_t h, VkFormat fmt, Csr2Image* o) {
    *o = {};
    o->w = w;
    o->h = h;
    return vk_create_image_2d(ctx, w, h, fmt, kUse, &o->image, &o->mem, &o->view);
}
static void img_free(VkContext* ctx, Csr2Image* o) {
    if (!ctx || !ctx->device) return;
    if (o->view) vkDestroyImageView(ctx->device, o->view, nullptr);
    if (o->image) vkDestroyImage(ctx->device, o->image, nullptr);
    if (o->mem) vkFreeMemory(ctx->device, o->mem, nullptr);
    *o = {};
}

static VkSampler samp(VkDevice d, VkFilter f) {
    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = f;
    sci.minFilter = f;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
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

bool csr2_upscale_create(VkContext* ctx, Csr2Upscale* out, std::string* err) {
    *out = {};
    out->ctx = ctx;
    out->use_fp16 = ctx->has_fp16;
    out->nearest = samp(ctx->device, VK_FILTER_NEAREST);
    out->linear = samp(ctx->device, VK_FILTER_LINEAR);
    auto UBO = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    auto CIS = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    auto STO = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
    VkDescriptorSetLayoutBinding upb[] = {B(0, UBO), B(1, CIS), B(2, STO)};
    VkDescriptorSetLayoutBinding accb[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, CIS), B(4, CIS), B(5, STO), B(6, STO)};
    VkDescriptorSetLayoutBinding casb[] = {B(0, STO), B(1, STO)};
    auto mkL = [&](VkDescriptorSetLayoutBinding* b, uint32_t n) {
        VkDescriptorSetLayoutCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
        ci.bindingCount = n;
        ci.pBindings = b;
        VkDescriptorSetLayout l = VK_NULL_HANDLE;
        vkCreateDescriptorSetLayout(ctx->device, &ci, nullptr, &l);
        return l;
    };
    out->up_layout = mkL(upb, 3);
    out->acc_layout = mkL(accb, 7);
    out->cas_layout = mkL(casb, 2);
    auto mkPL = [&](VkDescriptorSetLayout l, uint32_t pc) {
        VkPushConstantRange pcr{};
        pcr.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
        pcr.size = pc;
        VkPipelineLayoutCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        ci.setLayoutCount = 1;
        ci.pSetLayouts = &l;
        if (pc) {
            ci.pushConstantRangeCount = 1;
            ci.pPushConstantRanges = &pcr;
        }
        VkPipelineLayout pl = VK_NULL_HANDLE;
        vkCreatePipelineLayout(ctx->device, &ci, nullptr, &pl);
        return pl;
    };
    out->up_pl = mkPL(out->up_layout, 0);
    out->acc_pl = mkPL(out->acc_layout, 0);
    out->cas_pl = mkPL(out->cas_layout, 8);
    out->up_pipe = make_cs(ctx, out->up_pl, "csr2_upsample.spv", err);
    out->acc_pipe = make_cs(ctx, out->acc_pl, "csr2_accum.spv", err);
    out->cas_pipe = make_cs(ctx, out->cas_pl, "csr2_cas.spv", err);
    std::string ign;
    VkShaderModule m16 = vk_load_shader(ctx, "csr2_upsample_fp16.spv", &ign);
    if (m16) {
        vkDestroyShaderModule(ctx->device, m16, nullptr);
        out->up_pipe_fp16 = make_cs(ctx, out->up_pl, "csr2_upsample_fp16.spv", err);
        out->cas_pipe_fp16 = make_cs(ctx, out->cas_pl, "csr2_cas_fp16.spv", err);
    }
    if (!out->up_pipe || !out->cas_pipe || !out->acc_pipe) {
        if (err && err->empty()) *err = "Failed to create CSR2 pipelines";
        return false;
    }
    VkDescriptorPoolSize sizes[3] = {{UBO, 16}, {CIS, 32}, {STO, 32}};
    VkDescriptorPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    pci.maxSets = 16;
    pci.poolSizeCount = 3;
    pci.pPoolSizes = sizes;
    vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool);
    return vk_create_buffer(ctx, sizeof(Csr2ParamsCPU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &out->ubo,
                            &out->ubo_mem);
}

void csr2_upscale_destroy(Csr2Upscale* s) {
    if (!s || !s->ctx || !s->ctx->device) {
        if (s) *s = {};
        return;
    }
    VkDevice d = s->ctx->device;
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->mid);
    img_free(s->ctx, &s->accum);
    img_free(s->ctx, &s->hist_a);
    img_free(s->ctx, &s->hist_b);
    img_free(s->ctx, &s->scratch);
    img_free(s->ctx, &s->dst);
    img_free(s->ctx, &s->dummy_mv);
    img_free(s->ctx, &s->dummy_conf);
    if (s->up_pipe) vkDestroyPipeline(d, s->up_pipe, nullptr);
    if (s->up_pipe_fp16) vkDestroyPipeline(d, s->up_pipe_fp16, nullptr);
    if (s->acc_pipe) vkDestroyPipeline(d, s->acc_pipe, nullptr);
    if (s->cas_pipe) vkDestroyPipeline(d, s->cas_pipe, nullptr);
    if (s->cas_pipe_fp16) vkDestroyPipeline(d, s->cas_pipe_fp16, nullptr);
    if (s->up_pl) vkDestroyPipelineLayout(d, s->up_pl, nullptr);
    if (s->acc_pl) vkDestroyPipelineLayout(d, s->acc_pl, nullptr);
    if (s->cas_pl) vkDestroyPipelineLayout(d, s->cas_pl, nullptr);
    if (s->up_layout) vkDestroyDescriptorSetLayout(d, s->up_layout, nullptr);
    if (s->acc_layout) vkDestroyDescriptorSetLayout(d, s->acc_layout, nullptr);
    if (s->cas_layout) vkDestroyDescriptorSetLayout(d, s->cas_layout, nullptr);
    if (s->ubo) vkDestroyBuffer(d, s->ubo, nullptr);
    if (s->ubo_mem) vkFreeMemory(d, s->ubo_mem, nullptr);
    if (s->pool) vkDestroyDescriptorPool(d, s->pool, nullptr);
    if (s->nearest) vkDestroySampler(d, s->nearest, nullptr);
    if (s->linear) vkDestroySampler(d, s->linear, nullptr);
    *s = {};
}

void csr2_upscale_reset_history(Csr2Upscale* s) {
    if (s) s->has_history = false;
}

static bool ensure(Csr2Upscale* s, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h) {
    if (s->src.image && s->in_w == in_w && s->in_h == in_h && s->out_w == out_w && s->out_h == out_h) return true;
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->mid);
    img_free(s->ctx, &s->accum);
    img_free(s->ctx, &s->hist_a);
    img_free(s->ctx, &s->hist_b);
    img_free(s->ctx, &s->scratch);
    img_free(s->ctx, &s->dst);
    img_free(s->ctx, &s->dummy_mv);
    img_free(s->ctx, &s->dummy_conf);
    s->in_w = in_w;
    s->in_h = in_h;
    s->out_w = out_w;
    s->out_h = out_h;
    s->has_history = false;
    s->ping = 0;
    if (!img_make(s->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->src) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->mid) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->accum) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->hist_a) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->hist_b) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->scratch) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, &s->dst) ||
        !img_make(s->ctx, 1, 1, VK_FORMAT_R32G32B32A32_SFLOAT, &s->dummy_mv) ||
        !img_make(s->ctx, 1, 1, VK_FORMAT_R32_SFLOAT, &s->dummy_conf))
        return false;
    vk_clear_color_image(s->ctx, s->hist_a.image, 0, 0, 0, 0);
    vk_clear_color_image(s->ctx, s->hist_b.image, 0, 0, 0, 0);
    vk_clear_color_image(s->ctx, s->dummy_mv.image, 0, 0, 0, 0);
    vk_clear_color_image(s->ctx, s->dummy_conf.image, 0, 0, 0, 0);
    return true;
}

static VkDescriptorSet alloc(Csr2Upscale* s, VkDescriptorSetLayout l) {
    VkDescriptorSetAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = s->pool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &l;
    VkDescriptorSet set = VK_NULL_HANDLE;
    vkAllocateDescriptorSets(s->ctx->device, &ai, &set);
    return set;
}

static bool csr2_dispatch(Csr2Upscale* s, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h, float sharpness,
                          bool temporal, bool feeds_history, std::string* err) {
    Csr2ParamsCPU p{};
    p.InW = static_cast<float>(in_w);
    p.InH = static_cast<float>(in_h);
    p.OutW = static_cast<float>(out_w);
    p.OutH = static_cast<float>(out_h);
    p.HistoryValid = (temporal && feeds_history && s->has_history) ? 1.f : (temporal && s->has_history ? 1.f : 0.f);
    if (temporal && !feeds_history && s->has_history) p.HistoryValid = 1.f;
    p.Sharpness = sharpness;
    p.HasMotion = temporal ? 1.f : 0.f;
    p.HasConf = 0.f;
    p.Cheap = 0.f;
    p.ClampK = 1.f;
    p.BlendK = 1.f;
    p.SharpK = 1.f;
    void* mapped = nullptr;
    vkMapMemory(s->ctx->device, s->ubo_mem, 0, sizeof(p), 0, &mapped);
    std::memcpy(mapped, &p, sizeof(p));
    vkUnmapMemory(s->ctx->device, s->ubo_mem);

    vkResetDescriptorPool(s->ctx->device, s->pool, 0);
    VkDescriptorBufferInfo ubo{s->ubo, 0, sizeof(Csr2ParamsCPU)};
    VkDescriptorSet up = alloc(s, s->up_layout);
    VkDescriptorImageInfo up_src{s->nearest, s->src.view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo up_dst{VK_NULL_HANDLE, s->mid.view, VK_IMAGE_LAYOUT_GENERAL};
    VkWriteDescriptorSet wr[8];
    wr[0] = W(up, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr);
    wr[1] = W(up, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &up_src);
    wr[2] = W(up, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &up_dst);
    vkUpdateDescriptorSets(s->ctx->device, 3, wr, 0, nullptr);

    Csr2Image* hist_in = s->ping == 0 ? &s->hist_a : &s->hist_b;
    Csr2Image* hist_out = feeds_history ? (s->ping == 0 ? &s->hist_b : &s->hist_a) : &s->scratch;
    VkPipeline up_pipe = (s->use_fp16 && s->up_pipe_fp16) ? s->up_pipe_fp16 : s->up_pipe;
    VkPipeline cas_pipe = (s->use_fp16 && s->cas_pipe_fp16) ? s->cas_pipe_fp16 : s->cas_pipe;
    VkImage cas_src_img = s->mid.image;
    VkImageView cas_src_view = s->mid.view;

    VkCommandBuffer cmd = vk_begin_once(s->ctx);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, up_pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->up_pl, 0, 1, &up, 0, nullptr);
    vkCmdDispatch(cmd, (out_w + 7) / 8, (out_h + 7) / 8, 1);
    vk_cmd_barrier_general(cmd, s->mid.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);

    if (temporal) {
        VkDescriptorSet acc = alloc(s, s->acc_layout);
        VkDescriptorImageInfo c{s->nearest, s->mid.view, VK_IMAGE_LAYOUT_GENERAL};
        VkDescriptorImageInfo h{s->linear, hist_in->view, VK_IMAGE_LAYOUT_GENERAL};
        VkDescriptorImageInfo m{s->linear, s->dummy_mv.view, VK_IMAGE_LAYOUT_GENERAL};
        VkDescriptorImageInfo cf{s->linear, s->dummy_conf.view, VK_IMAGE_LAYOUT_GENERAL};
        VkDescriptorImageInfo ao{VK_NULL_HANDLE, s->accum.view, VK_IMAGE_LAYOUT_GENERAL};
        VkDescriptorImageInfo ho{VK_NULL_HANDLE, hist_out->view, VK_IMAGE_LAYOUT_GENERAL};
        wr[0] = W(acc, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr);
        wr[1] = W(acc, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &c);
        wr[2] = W(acc, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &h);
        wr[3] = W(acc, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &m);
        wr[4] = W(acc, 4, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &cf);
        wr[5] = W(acc, 5, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &ao);
        wr[6] = W(acc, 6, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &ho);
        vkUpdateDescriptorSets(s->ctx->device, 7, wr, 0, nullptr);
        vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->acc_pipe);
        vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->acc_pl, 0, 1, &acc, 0, nullptr);
        vkCmdDispatch(cmd, (out_w + 7) / 8, (out_h + 7) / 8, 1);
        vk_cmd_barrier_general(cmd, s->accum.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
        cas_src_img = s->accum.image;
        cas_src_view = s->accum.view;
    }

    VkDescriptorSet cas = alloc(s, s->cas_layout);
    VkDescriptorImageInfo cs{VK_NULL_HANDLE, cas_src_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo cd{VK_NULL_HANDLE, s->dst.view, VK_IMAGE_LAYOUT_GENERAL};
    wr[0] = W(cas, 0, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &cs);
    wr[1] = W(cas, 1, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &cd);
    vkUpdateDescriptorSets(s->ctx->device, 2, wr, 0, nullptr);
    float pc[2] = {sharpness, 1.f};
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, cas_pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->cas_pl, 0, 1, &cas, 0, nullptr);
    vkCmdPushConstants(cmd, s->cas_pl, VK_SHADER_STAGE_COMPUTE_BIT, 0, 8, pc);
    vkCmdDispatch(cmd, (out_w + 7) / 8, (out_h + 7) / 8, 1);
    (void)cas_src_img;
    if (!vk_submit_once(s->ctx, cmd)) {
        if (err) *err = "CSR2 dispatch failed";
        return false;
    }
    if (temporal && feeds_history) {
        s->ping = 1 - s->ping;
        s->has_history = true;
    }
    return true;
}

bool csr2_upscale_encode(Csr2Upscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                         float sharpness, bool temporal, bool feeds_history, const float* motion_rgba16, uint32_t mw,
                         uint32_t mh, float* dst, std::string* err) {
    if (!ensure(s, in_w, in_h, out_w, out_h)) {
        if (err) *err = "CSR2 image alloc failed";
        return false;
    }
    if (!vk_upload_rgba32f_staging(s->ctx, src, in_w, in_h, s->src.image)) {
        if (err) *err = "CSR2 upload failed";
        return false;
    }
    (void)mw;
    (void)mh;
    (void)motion_rgba16;
    if (!csr2_dispatch(s, in_w, in_h, out_w, out_h, sharpness, temporal, feeds_history, err)) return false;
    return vk_download_rgba32f(s->ctx, s->dst.image, out_w, out_h, dst);
}

bool csr2_upscale_from_image(Csr2Upscale* s, VkImage src, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h,
                             float sharpness, bool temporal, bool feeds_history, std::string* err) {
    if (!ensure(s, in_w, in_h, out_w, out_h)) {
        if (err) *err = "CSR2 image alloc failed";
        return false;
    }
    if (!vk_copy_image_2d(s->ctx, src, s->src.image, in_w, in_h)) {
        if (err) *err = "CSR2 copy failed";
        return false;
    }
    return csr2_dispatch(s, in_w, in_h, out_w, out_h, sharpness, temporal, feeds_history, err);
}

bool csr2_upscale_spatial_rgba32f(Csr2Upscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                                  uint32_t out_h, float sharpness, float* dst, std::string* err) {
    return csr2_upscale_encode(s, src, in_w, in_h, out_w, out_h, sharpness, false, false, nullptr, 0, 0, dst, err);
}

uint64_t csr2_history_checksum(Csr2Upscale* s) {
    if (!s->hist_a.image) return 0;
    Csr2Image* h = s->ping == 0 ? &s->hist_a : &s->hist_b;
    std::vector<float> buf(static_cast<size_t>(h->w) * h->h * 4);
    if (!vk_download_rgba32f(s->ctx, h->image, h->w, h->h, buf.data())) return 0;
    uint64_t c = 1469598103934665603ull;
    for (float v : buf) {
        uint32_t u;
        std::memcpy(&u, &v, 4);
        c ^= u;
        c *= 1099511628211ull;
    }
    return c;
}