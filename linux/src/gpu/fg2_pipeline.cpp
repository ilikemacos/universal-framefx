#include "fg2_pipeline.h"
#include "vk_dmabuf_import.h"

#include <algorithm>
#include <cstring>
#include <vector>

static float f16_to_f32(uint16_t h) {
    uint32_t s = (h >> 15) & 1u;
    uint32_t e = (h >> 10) & 0x1fu;
    uint32_t f = h & 0x3ffu;
    uint32_t v;
    if (e == 0) {
        if (f == 0) v = s << 31;
        else {
            e = 1;
            while ((f & 0x400u) == 0) {
                f <<= 1;
                e--;
            }
            f &= 0x3ffu;
            v = (s << 31) | ((e + 127 - 15) << 23) | (f << 13);
        }
    } else if (e == 31) {
        v = (s << 31) | 0x7f800000u | (f << 13);
    } else {
        v = (s << 31) | ((e + 127 - 15) << 23) | (f << 13);
    }
    float r;
    std::memcpy(&r, &v, 4);
    return r;
}

static uint16_t f32_to_f16(float x) {
    uint32_t v;
    std::memcpy(&v, &x, 4);
    uint32_t s = (v >> 16) & 0x8000u;
    int32_t e = static_cast<int32_t>((v >> 23) & 0xffu) - 127 + 15;
    uint32_t m = v & 0x7fffffu;
    if (e <= 0) {
        if (e < -10) return static_cast<uint16_t>(s);
        m = (m | 0x800000u) >> (1 - e);
        return static_cast<uint16_t>(s | (m >> 13));
    }
    if (e >= 31) return static_cast<uint16_t>(s | 0x7c00u);
    return static_cast<uint16_t>(s | (static_cast<uint32_t>(e) << 10) | (m >> 13));
}

static const VkImageUsageFlags kImgUse = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                         VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;

static bool img_create(VkContext* ctx, uint32_t w, uint32_t h, VkFormat fmt, Fg2Image* o) {
    *o = {};
    o->w = w;
    o->h = h;
    o->format = fmt;
    return vk_create_image_2d(ctx, w, h, fmt, kImgUse, &o->image, &o->mem, &o->view);
}

static void img_destroy(VkContext* ctx, Fg2Image* o) {
    if (!ctx || !ctx->device || !o) return;
    if (o->view) vkDestroyImageView(ctx->device, o->view, nullptr);
    if (o->image) vkDestroyImage(ctx->device, o->image, nullptr);
    if (o->mem) vkFreeMemory(ctx->device, o->mem, nullptr);
    *o = {};
}

static VkSampler make_samp(VkDevice d, VkFilter f) {
    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = f;
    sci.minFilter = f;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.maxLod = 0.f;
    VkSampler s = VK_NULL_HANDLE;
    vkCreateSampler(d, &sci, nullptr, &s);
    return s;
}

static VkDescriptorSetLayout make_layout(VkDevice d, const VkDescriptorSetLayoutBinding* b, uint32_t n) {
    VkDescriptorSetLayoutCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    ci.bindingCount = n;
    ci.pBindings = b;
    VkDescriptorSetLayout l = VK_NULL_HANDLE;
    vkCreateDescriptorSetLayout(d, &ci, nullptr, &l);
    return l;
}

static Fg2Pipe make_pipe(VkContext* ctx, const char* spv, VkDescriptorSetLayout layout, uint32_t pc_bytes, std::string* err) {
    Fg2Pipe p{};
    p.layout = layout;
    VkPushConstantRange pcr{};
    pcr.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    pcr.size = pc_bytes;
    VkPipelineLayoutCreateInfo lci{};
    lci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    lci.setLayoutCount = 1;
    lci.pSetLayouts = &layout;
    if (pc_bytes) {
        lci.pushConstantRangeCount = 1;
        lci.pPushConstantRanges = &pcr;
    }
    vkCreatePipelineLayout(ctx->device, &lci, nullptr, &p.pl);
    VkShaderModule mod = vk_load_shader(ctx, spv, err);
    if (!mod) return p;
    VkPipelineShaderStageCreateInfo st{};
    st.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    st.module = mod;
    st.pName = "main";
    VkComputePipelineCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
    ci.stage = st;
    ci.layout = p.pl;
    vkCreateComputePipelines(ctx->device, VK_NULL_HANDLE, 1, &ci, nullptr, &p.pipe);
    vkDestroyShaderModule(ctx->device, mod, nullptr);
    return p;
}

static void pipe_destroy(VkDevice d, Fg2Pipe* p, bool destroy_layout) {
    if (p->pipe) vkDestroyPipeline(d, p->pipe, nullptr);
    if (p->pl) vkDestroyPipelineLayout(d, p->pl, nullptr);
    if (destroy_layout && p->layout) vkDestroyDescriptorSetLayout(d, p->layout, nullptr);
    *p = {};
}

static VkDescriptorSetLayoutBinding B(uint32_t bind, VkDescriptorType t) {
    VkDescriptorSetLayoutBinding b{};
    b.binding = bind;
    b.descriptorType = t;
    b.descriptorCount = 1;
    b.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    return b;
}

static VkWriteDescriptorSet W(VkDescriptorSet set, uint32_t bind, VkDescriptorType t, const VkDescriptorBufferInfo* b,
                              const VkDescriptorImageInfo* i) {
    VkWriteDescriptorSet w{};
    w.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    w.dstSet = set;
    w.dstBinding = bind;
    w.descriptorCount = 1;
    w.descriptorType = t;
    w.pBufferInfo = b;
    w.pImageInfo = i;
    return w;
}

static VkDescriptorSet alloc_set(VkDevice d, VkDescriptorPool pool, VkDescriptorSetLayout layout) {
    VkDescriptorSetAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = pool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &layout;
    VkDescriptorSet s = VK_NULL_HANDLE;
    vkAllocateDescriptorSets(d, &ai, &s);
    return s;
}

void fg2_pipeline_destroy(Fg2Pipeline* p) {
    if (!p || !p->ctx || !p->ctx->device) {
        if (p) *p = {};
        return;
    }
    VkDevice d = p->ctx->device;
    vkDeviceWaitIdle(d);
    for (int i = 0; i < 4; i++) {
        img_destroy(p->ctx, &p->luma_a[i]);
        img_destroy(p->ctx, &p->luma_b[i]);
    }
    img_destroy(p->ctx, &p->tv8);
    img_destroy(p->ctx, &p->tv8_prop);
    img_destroy(p->ctx, &p->tv4);
    img_destroy(p->ctx, &p->mv2);
    img_destroy(p->ctx, &p->mv_hist);
    img_destroy(p->ctx, &p->fwd);
    img_destroy(p->ctx, &p->fwd_hist);
    img_destroy(p->ctx, &p->occl);
    img_destroy(p->ctx, &p->stat_count);
    img_destroy(p->ctx, &p->hud);
    img_destroy(p->ctx, &p->chg_prev);
    img_destroy(p->ctx, &p->chg_out);
    img_destroy(p->ctx, &p->gmv);
    img_destroy(p->ctx, &p->color_a);
    img_destroy(p->ctx, &p->color_b);
    img_destroy(p->ctx, &p->generated);
    img_destroy(p->ctx, &p->scratch);
    pipe_destroy(d, &p->p_luma, true);
    pipe_destroy(d, &p->p_halve, true);
    pipe_destroy(d, &p->p_p8, true);
    pipe_destroy(d, &p->p_prop, true);
    pipe_destroy(d, &p->p_p4, true);
    pipe_destroy(d, &p->p_p2, true);
    pipe_destroy(d, &p->p_fwd, true);
    pipe_destroy(d, &p->p_cons, true);
    pipe_destroy(d, &p->p_stat, true);
    pipe_destroy(d, &p->p_hud, true);
    pipe_destroy(d, &p->p_cut, true);
    pipe_destroy(d, &p->p_gmv, true);
    pipe_destroy(d, &p->p_interp, true);
    pipe_destroy(d, &p->p_interp_fp16, true);
    if (p->ubo) vkDestroyBuffer(d, p->ubo, nullptr);
    if (p->ubo_mem) vkFreeMemory(d, p->ubo_mem, nullptr);
    if (p->ubo2) vkDestroyBuffer(d, p->ubo2, nullptr);
    if (p->ubo2_mem) vkFreeMemory(d, p->ubo2_mem, nullptr);
    if (p->pool) vkDestroyDescriptorPool(d, p->pool, nullptr);
    if (p->nearest) vkDestroySampler(d, p->nearest, nullptr);
    if (p->linear) vkDestroySampler(d, p->linear, nullptr);
    *p = {};
}

bool fg2_pipeline_create(VkContext* ctx, Fg2Pipeline* out, std::string* err) {
    *out = {};
    out->ctx = ctx;
    out->use_fp16 = ctx->has_fp16;
    out->affine = affine_identity();
    out->nearest = make_samp(ctx->device, VK_FILTER_NEAREST);
    out->linear = make_samp(ctx->device, VK_FILTER_LINEAR);

    auto UBO = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    auto CIS = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    auto STO = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;

    VkDescriptorSetLayoutBinding luma_b[] = {B(1, CIS), B(2, STO)};
    VkDescriptorSetLayoutBinding p8_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, CIS), B(4, CIS), B(5, UBO), B(6, STO)};
    VkDescriptorSetLayoutBinding prop_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, CIS), B(4, CIS), B(5, STO)};
    VkDescriptorSetLayoutBinding p4_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, CIS), B(4, STO)};
    VkDescriptorSetLayoutBinding p2_b[] = {B(0, UBO), B(1, UBO), B(2, CIS), B(3, CIS), B(4, CIS), B(5, STO)};
    VkDescriptorSetLayoutBinding fwd_b[] = {B(0, UBO), B(1, CIS), B(2, STO)};
    VkDescriptorSetLayoutBinding cons_b[] = {B(0, UBO), B(1, UBO), B(2, CIS), B(3, CIS), B(4, STO)};
    VkDescriptorSetLayoutBinding stat_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, STO)};
    VkDescriptorSetLayoutBinding hud_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, STO)};
    VkDescriptorSetLayoutBinding cut_b[] = {B(0, UBO), B(1, CIS), B(2, STO), B(3, STO)};
    VkDescriptorSetLayoutBinding gmv_b[] = {B(0, UBO), B(1, CIS), B(2, CIS), B(3, STO)};
    VkDescriptorSetLayoutBinding interp_b[] = {B(0, CIS), B(1, CIS), B(2, CIS), B(3, CIS), B(4, STO)};

    out->p_luma = make_pipe(ctx, "fg2_luma.spv", make_layout(ctx->device, luma_b, 2), 0, err);
    out->p_halve = make_pipe(ctx, "fg2_halve.spv", make_layout(ctx->device, luma_b, 2), 0, err);
    out->p_p8 = make_pipe(ctx, "fg2_p8_search.spv", make_layout(ctx->device, p8_b, 7), 0, err);
    out->p_prop = make_pipe(ctx, "fg2_p8_prop.spv", make_layout(ctx->device, prop_b, 6), 0, err);
    out->p_p4 = make_pipe(ctx, "fg2_p4_refine.spv", make_layout(ctx->device, p4_b, 5), 0, err);
    out->p_p2 = make_pipe(ctx, "fg2_p2_final.spv", make_layout(ctx->device, p2_b, 6), 0, err);
    out->p_fwd = make_pipe(ctx, "fg2_fwd.spv", make_layout(ctx->device, fwd_b, 3), 0, err);
    out->p_cons = make_pipe(ctx, "fg2_consistency.spv", make_layout(ctx->device, cons_b, 5), 0, err);
    out->p_stat = make_pipe(ctx, "fg2_static_count.spv", make_layout(ctx->device, stat_b, 4), 0, err);
    out->p_hud = make_pipe(ctx, "fg2_hud_mask.spv", make_layout(ctx->device, hud_b, 4), 0, err);
    out->p_cut = make_pipe(ctx, "fg2_scene_cut.spv", make_layout(ctx->device, cut_b, 4), 0, err);
    out->p_gmv = make_pipe(ctx, "fg2_global_motion.spv", make_layout(ctx->device, gmv_b, 4), 0, err);
    std::string ign;
    VkShaderModule imod = vk_load_shader(ctx, "fg2_interp_fast.spv", &ign);
    if (imod) {
        vkDestroyShaderModule(ctx->device, imod, nullptr);
        out->p_interp = make_pipe(ctx, "fg2_interp_fast.spv", make_layout(ctx->device, interp_b, 5), 16, err);
        VkShaderModule i16 = vk_load_shader(ctx, "fg2_interp_fast_fp16.spv", &ign);
        if (i16) {
            vkDestroyShaderModule(ctx->device, i16, nullptr);
            out->p_interp_fp16 = make_pipe(ctx, "fg2_interp_fast_fp16.spv", make_layout(ctx->device, interp_b, 5), 16, err);
        }
    }

    if (!out->p_luma.pipe || !out->p_p8.pipe || !out->p_p2.pipe || !out->p_gmv.pipe) {
        if (err && err->empty()) *err = "Failed to create FG2 pipelines";
        return false;
    }

    VkDescriptorPoolSize sizes[3] = {
        {UBO, 64},
        {CIS, 128},
        {STO, 64},
    };
    VkDescriptorPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    pci.maxSets = 64;
    pci.poolSizeCount = 3;
    pci.pPoolSizes = sizes;
    vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool);
    if (!vk_create_buffer(ctx, sizeof(Fg2ParamsCPU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &out->ubo,
                          &out->ubo_mem) ||
        !vk_create_buffer(ctx, sizeof(Fg2GlobalCPU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, &out->ubo2,
                          &out->ubo2_mem)) {
        if (err) *err = "Failed to create FG2 UBO";
        return false;
    }
    return true;
}

bool fg2_pipeline_ensure(Fg2Pipeline* p, uint32_t in_w, uint32_t in_h, std::string* err) {
    if (p->in_w == in_w && p->in_h == in_h && p->mv2.image) return true;
    for (int i = 0; i < 4; i++) {
        img_destroy(p->ctx, &p->luma_a[i]);
        img_destroy(p->ctx, &p->luma_b[i]);
    }
    img_destroy(p->ctx, &p->tv8);
    img_destroy(p->ctx, &p->tv8_prop);
    img_destroy(p->ctx, &p->tv4);
    img_destroy(p->ctx, &p->mv2);
    img_destroy(p->ctx, &p->mv_hist);
    img_destroy(p->ctx, &p->fwd);
    img_destroy(p->ctx, &p->fwd_hist);
    img_destroy(p->ctx, &p->occl);
    img_destroy(p->ctx, &p->stat_count);
    img_destroy(p->ctx, &p->hud);
    img_destroy(p->ctx, &p->chg_prev);
    img_destroy(p->ctx, &p->chg_out);
    img_destroy(p->ctx, &p->gmv);
    img_destroy(p->ctx, &p->color_a);
    img_destroy(p->ctx, &p->color_b);
    img_destroy(p->ctx, &p->generated);
    img_destroy(p->ctx, &p->scratch);
    p->in_w = in_w;
    p->in_h = in_h;
    p->has_history = false;
    p->ping = 0;
    p->affine = affine_identity();
    uint32_t w = in_w, h = in_h;
    for (int i = 0; i < 4; i++) {
        if (!img_create(p->ctx, w, h, VK_FORMAT_R16_SFLOAT, &p->luma_a[i]) ||
            !img_create(p->ctx, w, h, VK_FORMAT_R16_SFLOAT, &p->luma_b[i])) {
            if (err) *err = "Failed to create luma pyramid";
            return false;
        }
        vk_clear_color_image(p->ctx, p->luma_a[i].image, 0, 0, 0, 0);
        vk_clear_color_image(p->ctx, p->luma_b[i].image, 0, 0, 0, 0);
        w = std::max(1u, w / 2);
        h = std::max(1u, h / 2);
    }
    uint32_t tx = std::max(1u, (in_w + 7) / 8);
    uint32_t ty = std::max(1u, (in_h + 7) / 8);
    if (!img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->tv8) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->tv8_prop) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16_SFLOAT, &p->tv4) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->mv2) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->mv_hist) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->fwd) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R16G16B16A16_SFLOAT, &p->fwd_hist) ||
        !img_create(p->ctx, tx, ty, VK_FORMAT_R8_UNORM, &p->occl) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R32_SFLOAT, &p->stat_count) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R16_SFLOAT, &p->hud) ||
        !img_create(p->ctx, 64, 64, VK_FORMAT_R32_SFLOAT, &p->chg_prev) ||
        !img_create(p->ctx, 2, 1, VK_FORMAT_R32_SFLOAT, &p->chg_out) ||
        !img_create(p->ctx, 1, 1, VK_FORMAT_R32G32_SFLOAT, &p->gmv) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, &p->color_a) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, &p->color_b) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, &p->generated) ||
        !img_create(p->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, &p->scratch)) {
        if (err) *err = "Failed to create FG2 images";
        return false;
    }
    vk_clear_color_image(p->ctx, p->stat_count.image, 0, 0, 0, 0);
    vk_clear_color_image(p->ctx, p->hud.image, 0, 0, 0, 0);
    vk_clear_color_image(p->ctx, p->chg_prev.image, 0, 0, 0, 0);
    vk_clear_color_image(p->ctx, p->chg_out.image, 0, 0, 0, 0);
    vk_clear_color_image(p->ctx, p->gmv.image, 0, 0, 0, 0);
    vk_clear_color_image(p->ctx, p->mv_hist.image, 0, 0, 0, 0);
    return true;
}

static Fg2ParamsCPU make_params(Fg2Pipeline* p, float t) {
    Fg2ParamsCPU u{};
    u.InW = static_cast<float>(p->in_w);
    u.InH = static_cast<float>(p->in_h);
    u.OutW = static_cast<float>(p->in_w);
    u.OutH = static_cast<float>(p->in_h);
    u.TileSize = 8.f;
    u.TilesX = static_cast<float>((p->in_w + 7) / 8);
    u.TilesY = static_cast<float>((p->in_h + 7) / 8);
    u.InterpT = t;
    u.HistoryValid = p->has_history ? 1.f : 0.f;
    u.Lambda = 0.02f;
    u.CensusW = 0.3f;
    u.SadW = 1.f;
    u.OcclThresh = p->occl_thresh;
    u.StaticThresh = p->static_thresh;
    u.SceneCutThresh = 0.6f * 4096.f;
    u.FrameCount = 1.f;
    u.MvGapN = 2.f;
    return u;
}

static Fg2GlobalCPU make_global(Fg2Pipeline* p) {
    Fg2GlobalCPU g{};
    g.A00 = p->affine.A00;
    g.A01 = p->affine.A01;
    g.A02 = p->affine.A02;
    g.A10 = p->affine.A10;
    g.A11 = p->affine.A11;
    g.A12 = p->affine.A12;
    g.AffConf = p->affine.Conf;
    g.SnapOn = p->affine.SnapOn;
    g.RansacPx = p->ransac_px;
    g.ConfThresh = 0.35f;
    g.ColorGate = 0.08f;
    g.VecLenK = 0.04f;
    g.SoftOccK = 0.f;
    return g;
}

static void map_ubo(VkContext* ctx, VkDeviceMemory mem, const void* src, size_t n) {
    void* mapped = nullptr;
    vkMapMemory(ctx->device, mem, 0, n, 0, &mapped);
    std::memcpy(mapped, src, n);
    vkUnmapMemory(ctx->device, mem);
}

static VkDescriptorImageInfo cis(VkSampler s, VkImageView v) {
    return {s, v, VK_IMAGE_LAYOUT_GENERAL};
}
static VkDescriptorImageInfo sto(VkImageView v) { return {VK_NULL_HANDLE, v, VK_IMAGE_LAYOUT_GENERAL}; }

static void dispatch(VkCommandBuffer cmd, const Fg2Pipe& pipe, VkDescriptorSet set, uint32_t gx, uint32_t gy) {
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pl, 0, 1, &set, 0, nullptr);
    vkCmdDispatch(cmd, std::max(1u, gx), std::max(1u, gy), 1);
}

static bool fg2_run_me(Fg2Pipeline* p, uint32_t w, uint32_t h, std::string* err) {
    Fg2Image* curr_col = p->ping == 0 ? &p->color_b : &p->color_a;
    Fg2Image* curr_l = p->ping == 0 ? p->luma_b : p->luma_a;
    Fg2Image* prev_l = p->ping == 0 ? p->luma_a : p->luma_b;
    Fg2ParamsCPU params = make_params(p, 0.5f);
    Fg2GlobalCPU glob = make_global(p);
    map_ubo(p->ctx, p->ubo_mem, &params, sizeof(params));
    map_ubo(p->ctx, p->ubo2_mem, &glob, sizeof(glob));
    vkResetDescriptorPool(p->ctx->device, p->pool, 0);
    VkDevice d = p->ctx->device;
    VkDescriptorBufferInfo ubo{p->ubo, 0, sizeof(Fg2ParamsCPU)};
    VkDescriptorBufferInfo ubo2{p->ubo2, 0, sizeof(Fg2GlobalCPU)};

    auto set_luma = [&](Fg2Image* src_color, Fg2Image* dst) {
        VkDescriptorSet s = alloc_set(d, p->pool, p->p_luma.layout);
        VkDescriptorImageInfo a = cis(p->nearest, src_color->view);
        VkDescriptorImageInfo b = sto(dst->view);
        VkWriteDescriptorSet wr[2] = {W(s, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(s, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &b)};
        vkUpdateDescriptorSets(d, 2, wr, 0, nullptr);
        return s;
    };
    auto set_halve = [&](Fg2Image* src, Fg2Image* dst) {
        VkDescriptorSet s = alloc_set(d, p->pool, p->p_halve.layout);
        VkDescriptorImageInfo a = cis(p->linear, src->view);
        VkDescriptorImageInfo b = sto(dst->view);
        VkWriteDescriptorSet wr[2] = {W(s, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(s, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &b)};
        vkUpdateDescriptorSets(d, 2, wr, 0, nullptr);
        return s;
    };

    VkDescriptorSet luma0 = set_luma(curr_col, &curr_l[0]);
    VkDescriptorSet h1 = set_halve(&curr_l[0], &curr_l[1]);
    VkDescriptorSet h2 = set_halve(&curr_l[1], &curr_l[2]);
    VkDescriptorSet h3 = set_halve(&curr_l[2], &curr_l[3]);

    VkDescriptorSet gmv_set = alloc_set(d, p->pool, p->p_gmv.layout);
    {
        VkDescriptorImageInfo a = cis(p->linear, prev_l[3].view);
        VkDescriptorImageInfo b = cis(p->linear, curr_l[3].view);
        VkDescriptorImageInfo c = sto(p->gmv.view);
        VkWriteDescriptorSet wr[4] = {W(gmv_set, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(gmv_set, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(gmv_set, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(gmv_set, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &c)};
        vkUpdateDescriptorSets(d, 4, wr, 0, nullptr);
    }

    VkDescriptorSet p8 = alloc_set(d, p->pool, p->p_p8.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, prev_l[3].view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[3].view);
        VkDescriptorImageInfo c = cis(p->nearest, p->mv_hist.view);
        VkDescriptorImageInfo g = cis(p->nearest, p->gmv.view);
        VkDescriptorImageInfo o = sto(p->tv8.view);
        VkWriteDescriptorSet wr[7] = {W(p8, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(p8, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(p8, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(p8, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &c),
                                      W(p8, 4, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &g),
                                      W(p8, 5, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo2, nullptr),
                                      W(p8, 6, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 7, wr, 0, nullptr);
    }
    VkDescriptorSet prop = alloc_set(d, p->pool, p->p_prop.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, prev_l[3].view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[3].view);
        VkDescriptorImageInfo c = cis(p->nearest, p->tv8.view);
        VkDescriptorImageInfo h = cis(p->nearest, p->mv_hist.view);
        VkDescriptorImageInfo o = sto(p->tv8_prop.view);
        VkWriteDescriptorSet wr[6] = {W(prop, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(prop, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(prop, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(prop, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &c),
                                      W(prop, 4, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &h),
                                      W(prop, 5, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 6, wr, 0, nullptr);
    }
    VkDescriptorSet p4 = alloc_set(d, p->pool, p->p_p4.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, prev_l[2].view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[2].view);
        VkDescriptorImageInfo c = cis(p->nearest, p->tv8_prop.view);
        VkDescriptorImageInfo o = sto(p->tv4.view);
        VkWriteDescriptorSet wr[5] = {W(p4, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(p4, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(p4, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(p4, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &c),
                                      W(p4, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 5, wr, 0, nullptr);
    }
    VkDescriptorSet p2 = alloc_set(d, p->pool, p->p_p2.layout);
    {
        VkDescriptorImageInfo a = cis(p->linear, prev_l[1].view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[1].view);
        VkDescriptorImageInfo c = cis(p->nearest, p->tv4.view);
        VkDescriptorImageInfo o = sto(p->mv2.view);
        VkWriteDescriptorSet wr[6] = {W(p2, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(p2, 1, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo2, nullptr),
                                      W(p2, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(p2, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(p2, 4, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &c),
                                      W(p2, 5, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 6, wr, 0, nullptr);
    }
    VkDescriptorSet fwd = alloc_set(d, p->pool, p->p_fwd.layout);
    {
        VkDescriptorImageInfo a = cis(p->linear, p->mv2.view);
        VkDescriptorImageInfo o = sto(p->fwd.view);
        VkWriteDescriptorSet wr[3] = {W(fwd, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(fwd, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(fwd, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 3, wr, 0, nullptr);
    }
    VkDescriptorSet cons = alloc_set(d, p->pool, p->p_cons.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, p->mv2.view);
        VkDescriptorImageInfo b = cis(p->linear, p->fwd.view);
        VkDescriptorImageInfo o = sto(p->occl.view);
        VkWriteDescriptorSet wr[5] = {W(cons, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(cons, 1, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo2, nullptr),
                                      W(cons, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(cons, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(cons, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 5, wr, 0, nullptr);
    }
    VkDescriptorSet stat = alloc_set(d, p->pool, p->p_stat.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, prev_l[0].view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[0].view);
        VkDescriptorImageInfo o = sto(p->stat_count.view);
        VkWriteDescriptorSet wr[4] = {W(stat, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(stat, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(stat, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(stat, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 4, wr, 0, nullptr);
    }
    VkDescriptorSet hud = alloc_set(d, p->pool, p->p_hud.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, p->stat_count.view);
        VkDescriptorImageInfo b = cis(p->nearest, curr_l[0].view);
        VkDescriptorImageInfo o = sto(p->hud.view);
        VkWriteDescriptorSet wr[4] = {W(hud, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(hud, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(hud, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                      W(hud, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
        vkUpdateDescriptorSets(d, 4, wr, 0, nullptr);
    }
    VkDescriptorSet cut = alloc_set(d, p->pool, p->p_cut.layout);
    {
        VkDescriptorImageInfo a = cis(p->nearest, curr_col->view);
        VkDescriptorImageInfo b = sto(p->chg_prev.view);
        VkDescriptorImageInfo c = sto(p->chg_out.view);
        VkWriteDescriptorSet wr[4] = {W(cut, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr),
                                      W(cut, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                      W(cut, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &b),
                                      W(cut, 3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &c)};
        vkUpdateDescriptorSets(d, 4, wr, 0, nullptr);
    }

    uint32_t tx = (w + 7) / 8, ty = (h + 7) / 8;
    VkCommandBuffer cmd = vk_begin_once(p->ctx);
    dispatch(cmd, p->p_luma, luma0, (curr_l[0].w + 7) / 8, (curr_l[0].h + 7) / 8);
    vk_cmd_barrier_general(cmd, curr_l[0].image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_halve, h1, (curr_l[1].w + 7) / 8, (curr_l[1].h + 7) / 8);
    vk_cmd_barrier_general(cmd, curr_l[1].image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_halve, h2, (curr_l[2].w + 7) / 8, (curr_l[2].h + 7) / 8);
    vk_cmd_barrier_general(cmd, curr_l[2].image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_halve, h3, (curr_l[3].w + 7) / 8, (curr_l[3].h + 7) / 8);
    vk_cmd_barrier_general(cmd, curr_l[3].image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_gmv, gmv_set, 1, 1);
    vk_cmd_barrier_general(cmd, p->gmv.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_p8, p8, (tx + 7) / 8, (ty + 7) / 8);
    vk_cmd_barrier_general(cmd, p->tv8.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_prop, prop, (tx + 7) / 8, (ty + 7) / 8);
    vk_cmd_barrier_general(cmd, p->tv8_prop.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_p4, p4, (tx + 7) / 8, (ty + 7) / 8);
    vk_cmd_barrier_general(cmd, p->tv4.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_p2, p2, (tx + 7) / 8, (ty + 7) / 8);
    vk_cmd_barrier_general(cmd, p->mv2.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_fwd, fwd, (tx + 7) / 8, (ty + 7) / 8);
    vk_cmd_barrier_general(cmd, p->fwd.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_cons, cons, (tx + 7) / 8, (ty + 7) / 8);
    dispatch(cmd, p->p_stat, stat, (w + 7) / 8, (h + 7) / 8);
    vk_cmd_barrier_general(cmd, p->stat_count.image, VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
    dispatch(cmd, p->p_hud, hud, (w + 7) / 8, (h + 7) / 8);
    dispatch(cmd, p->p_cut, cut, 1, 1);
    if (!vk_submit_once(p->ctx, cmd)) {
        if (err) *err = "FG2 dispatch failed";
        return false;
    }
    vk_copy_image_2d(p->ctx, p->mv2.image, p->mv_hist.image, tx, ty);
    vk_copy_image_2d(p->ctx, p->fwd.image, p->fwd_hist.image, tx, ty);
    float chg[2] = {};
    vk_download_texels(p->ctx, p->chg_out.image, 2, 1, sizeof(float), chg);
    p->last_cut = chg[1];

    std::vector<float> vx, vy, cf;
    if (fg2_read_mv2(p, &vx, &vy, &cf) && !vx.empty()) {
        p->affine = affine_fit_ransac(vx.data(), vy.data(), cf.data(), static_cast<int>(tx), static_cast<int>(ty),
                                      kAffineRansacIters, kAffineRansacSeed);
    }

    p->ping ^= 1;
    p->has_history = true;
    return true;
}

bool fg2_process_real_rgba32f(Fg2Pipeline* p, const float* rgba, uint32_t w, uint32_t h, std::string* err) {
    if (!fg2_pipeline_ensure(p, w, h, err)) return false;
    Fg2Image* curr_col = p->ping == 0 ? &p->color_b : &p->color_a;
    if (!vk_upload_rgba32f_staging(p->ctx, rgba, w, h, curr_col->image)) {
        if (err) *err = "FG2 color upload failed";
        return false;
    }
    return fg2_run_me(p, w, h, err);
}

bool fg2_process_real_image(Fg2Pipeline* p, VkImage src, uint32_t w, uint32_t h, std::string* err) {
    if (!fg2_pipeline_ensure(p, w, h, err)) return false;
    Fg2Image* curr_col = p->ping == 0 ? &p->color_b : &p->color_a;
    if (!vk_copy_image_2d(p->ctx, src, curr_col->image, w, h)) {
        if (err) *err = "FG2 color copy failed";
        return false;
    }
    return fg2_run_me(p, w, h, err);
}

bool fg2_generate_fast_gpu(Fg2Pipeline* p, float t, std::string* err) {
    if (!p->p_interp.pipe) {
        if (err) *err = "InterpFast pipeline not built";
        return false;
    }
    if (!p->in_w) {
        if (err) *err = "InterpFast has no frames";
        return false;
    }
    uint32_t w = p->in_w, h = p->in_h;
    Fg2Image* prev = p->ping == 1 ? &p->color_a : &p->color_b;
    Fg2Image* curr = p->ping == 1 ? &p->color_b : &p->color_a;
    vkResetDescriptorPool(p->ctx->device, p->pool, 0);
    VkDescriptorSet s = alloc_set(p->ctx->device, p->pool, p->p_interp.layout);
    VkDescriptorImageInfo a = cis(p->linear, prev->view);
    VkDescriptorImageInfo b = cis(p->linear, curr->view);
    VkDescriptorImageInfo m = cis(p->nearest, p->mv2.view);
    VkDescriptorImageInfo hudi = cis(p->nearest, p->hud.view);
    VkDescriptorImageInfo o = sto(p->generated.view);
    VkWriteDescriptorSet wr[5] = {W(s, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                  W(s, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                  W(s, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &m),
                                  W(s, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &hudi),
                                  W(s, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
    vkUpdateDescriptorSets(p->ctx->device, 5, wr, 0, nullptr);
    float pc[4] = {static_cast<float>(w), static_cast<float>(h), t, p->last_cut};
    const Fg2Pipe& pipe = (p->use_fp16 && p->p_interp_fp16.pipe) ? p->p_interp_fp16 : p->p_interp;
    VkCommandBuffer cmd = vk_begin_once(p->ctx);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pl, 0, 1, &s, 0, nullptr);
    vkCmdPushConstants(cmd, pipe.pl, VK_SHADER_STAGE_COMPUTE_BIT, 0, 16, pc);
    vkCmdDispatch(cmd, (w + 7) / 8, (h + 7) / 8, 1);
    if (!vk_submit_once(p->ctx, cmd)) {
        if (err) *err = "InterpFast dispatch failed";
        return false;
    }
    return true;
}

bool fg2_generate_fast_rgba32f(Fg2Pipeline* p, const float* prev, const float* curr, uint32_t w, uint32_t h, float t,
                               float* dst, std::string* err) {
    if (!p->p_interp.pipe) {
        if (err) *err = "InterpFast pipeline not built";
        return false;
    }
    if (!fg2_pipeline_ensure(p, w, h, err)) return false;
    if (!vk_upload_rgba32f_staging(p->ctx, prev, w, h, p->color_a.image) ||
        !vk_upload_rgba32f_staging(p->ctx, curr, w, h, p->color_b.image)) {
        if (err) *err = "InterpFast upload failed";
        return false;
    }
    vkResetDescriptorPool(p->ctx->device, p->pool, 0);
    VkDescriptorSet s = alloc_set(p->ctx->device, p->pool, p->p_interp.layout);
    VkDescriptorImageInfo a = cis(p->linear, p->color_a.view);
    VkDescriptorImageInfo b = cis(p->linear, p->color_b.view);
    VkDescriptorImageInfo m = cis(p->nearest, p->mv2.view);
    VkDescriptorImageInfo hudi = cis(p->nearest, p->hud.view);
    VkDescriptorImageInfo o = sto(p->generated.view);
    VkWriteDescriptorSet wr[5] = {W(s, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &a),
                                  W(s, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &b),
                                  W(s, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &m),
                                  W(s, 3, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &hudi),
                                  W(s, 4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &o)};
    vkUpdateDescriptorSets(p->ctx->device, 5, wr, 0, nullptr);
    float pc[4] = {static_cast<float>(w), static_cast<float>(h), t, p->last_cut};
    const Fg2Pipe& pipe = (p->use_fp16 && p->p_interp_fp16.pipe) ? p->p_interp_fp16 : p->p_interp;
    VkCommandBuffer cmd = vk_begin_once(p->ctx);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe.pl, 0, 1, &s, 0, nullptr);
    vkCmdPushConstants(cmd, pipe.pl, VK_SHADER_STAGE_COMPUTE_BIT, 0, 16, pc);
    vkCmdDispatch(cmd, (w + 7) / 8, (h + 7) / 8, 1);
    if (!vk_submit_once(p->ctx, cmd)) {
        if (err) *err = "InterpFast dispatch failed";
        return false;
    }
    return vk_download_rgba32f(p->ctx, p->generated.image, w, h, dst);
}

bool fg2_read_mv2(Fg2Pipeline* p, std::vector<float>* vx, std::vector<float>* vy, std::vector<float>* conf) {
    if (!p->mv2.image) return false;
    uint32_t n = p->mv2.w * p->mv2.h;
    std::vector<uint16_t> raw(static_cast<size_t>(n) * 4);
    if (!vk_download_texels(p->ctx, p->mv2.image, p->mv2.w, p->mv2.h, 8, raw.data())) return false;
    vx->resize(n);
    vy->resize(n);
    conf->resize(n);
    for (uint32_t i = 0; i < n; i++) {
        (*vx)[i] = f16_to_f32(raw[i * 4 + 0]);
        (*vy)[i] = f16_to_f32(raw[i * 4 + 1]);
        (*conf)[i] = f16_to_f32(raw[i * 4 + 2]);
    }
    return true;
}

bool fg2_upload_mv2(Fg2Pipeline* p, const float* rgba, uint32_t tw, uint32_t th) {
    if (!fg2_pipeline_ensure(p, tw * 8, th * 8, nullptr)) return false;
    std::vector<uint16_t> raw(static_cast<size_t>(tw) * th * 4);
    for (uint32_t i = 0; i < tw * th; i++) {
        raw[i * 4 + 0] = f32_to_f16(rgba[i * 4 + 0]);
        raw[i * 4 + 1] = f32_to_f16(rgba[i * 4 + 1]);
        raw[i * 4 + 2] = f32_to_f16(rgba[i * 4 + 2]);
        raw[i * 4 + 3] = f32_to_f16(rgba[i * 4 + 3]);
    }
    return vk_upload_texels(p->ctx, raw.data(), tw, th, 8, p->mv2.image);
}

bool fg2_set_hud_r16(Fg2Pipeline* p, const float* hud, uint32_t w, uint32_t h) {
    if (!fg2_pipeline_ensure(p, w, h, nullptr)) return false;
    std::vector<uint16_t> raw(static_cast<size_t>(w) * h);
    for (uint32_t i = 0; i < w * h; i++) raw[i] = f32_to_f16(hud[i]);
    return vk_upload_texels(p->ctx, raw.data(), w, h, 2, p->hud.image);
}

void fg2_set_cut_flag(Fg2Pipeline* p, float cut) {
    if (p) p->last_cut = cut;
}
