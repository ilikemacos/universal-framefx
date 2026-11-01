#include "csr_ai_upscale.h"
#include "vk_dmabuf_import.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <linux/limits.h>
#include <libgen.h>
#include <unistd.h>
#include <vector>

struct CsrAiParamsCPU {
    float SrcW, SrcH, DstW, DstH;
    float Sharpness, InC, OutC, ActLeaky;
    float WeightOff, BiasOff, LumaR, LumaG;
    float LumaB, TexW, TexH, Dilation;
    float OutChOff, Accumulate, ActRelu;
};

struct JVal {
    enum Type { Null, Bool, Num, Str, Arr, Obj } t = Null;
    bool b = false;
    double n = 0;
    std::string s;
    std::vector<JVal> a;
    std::vector<std::pair<std::string, JVal>> o;

    const JVal* get(const char* k) const {
        if (t != Obj) return nullptr;
        for (const auto& kv : o)
            if (kv.first == k) return &kv.second;
        return nullptr;
    }
};

struct JParser {
    const char* p = nullptr;
    const char* end = nullptr;
    std::string* err = nullptr;

    void ws() {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p;
    }
    bool fail(const char* m) {
        if (err) *err = m;
        return false;
    }
    bool lit(const char* s) {
        size_t n = std::strlen(s);
        if (static_cast<size_t>(end - p) < n || std::strncmp(p, s, n) != 0) return false;
        p += n;
        return true;
    }
    bool str(std::string& out) {
        if (p >= end || *p != '"') return fail("CSR AI weights contain invalid JSON.");
        ++p;
        out.clear();
        while (p < end) {
            char c = *p++;
            if (c == '"') return true;
            if (c != '\\') {
                out.push_back(c);
                continue;
            }
            if (p >= end) return fail("CSR AI weights contain invalid JSON.");
            char e = *p++;
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    if (end - p < 4) return fail("CSR AI weights contain invalid JSON.");
                    unsigned v = 0;
                    for (int i = 0; i < 4; i++) {
                        char h = *p++;
                        v <<= 4;
                        if (h >= '0' && h <= '9') v |= static_cast<unsigned>(h - '0');
                        else if (h >= 'a' && h <= 'f') v |= static_cast<unsigned>(h - 'a' + 10);
                        else if (h >= 'A' && h <= 'F') v |= static_cast<unsigned>(h - 'A' + 10);
                        else return fail("CSR AI weights contain invalid JSON.");
                    }
                    out.push_back(v < 0x80 ? static_cast<char>(v) : '?');
                    break;
                }
                default: return fail("CSR AI weights contain invalid JSON.");
            }
        }
        return fail("CSR AI weights contain invalid JSON.");
    }
    bool num(double& out) {
        if (p >= end) return fail("CSR AI weights contain invalid JSON.");
        char* q = nullptr;
        out = std::strtod(p, &q);
        if (q == p) return fail("CSR AI weights contain invalid JSON.");
        p = q;
        return true;
    }
    bool value(JVal& v) {
        ws();
        if (p >= end) return fail("CSR AI weights contain invalid JSON.");
        char c = *p;
        if (c == '{') {
            ++p;
            v.t = JVal::Obj;
            ws();
            if (p < end && *p == '}') {
                ++p;
                return true;
            }
            while (true) {
                ws();
                std::string k;
                if (!str(k)) return false;
                ws();
                if (p >= end || *p != ':') return fail("CSR AI weights contain invalid JSON.");
                ++p;
                JVal child;
                if (!value(child)) return false;
                v.o.emplace_back(std::move(k), std::move(child));
                ws();
                if (p < end && *p == ',') {
                    ++p;
                    continue;
                }
                if (p < end && *p == '}') {
                    ++p;
                    return true;
                }
                return fail("CSR AI weights contain invalid JSON.");
            }
        }
        if (c == '[') {
            ++p;
            v.t = JVal::Arr;
            ws();
            if (p < end && *p == ']') {
                ++p;
                return true;
            }
            while (true) {
                JVal child;
                if (!value(child)) return false;
                v.a.push_back(std::move(child));
                ws();
                if (p < end && *p == ',') {
                    ++p;
                    continue;
                }
                if (p < end && *p == ']') {
                    ++p;
                    return true;
                }
                return fail("CSR AI weights contain invalid JSON.");
            }
        }
        if (c == '"') {
            v.t = JVal::Str;
            return str(v.s);
        }
        if (lit("true")) {
            v.t = JVal::Bool;
            v.b = true;
            return true;
        }
        if (lit("false")) {
            v.t = JVal::Bool;
            v.b = false;
            return true;
        }
        if (lit("null")) {
            v.t = JVal::Null;
            return true;
        }
        v.t = JVal::Num;
        return num(v.n);
    }
};

static bool jint(const JVal* v, int* out) {
    if (!v || v->t != JVal::Num) return false;
    *out = static_cast<int>(v->n);
    return true;
}

static bool read_floats(const JVal* el, int expect, std::vector<float>* out, std::string* err) {
    if (!el || el->t != JVal::Arr || static_cast<int>(el->a.size()) != expect) {
        if (err) *err = "CSR AI weight array length does not match the layer.";
        return false;
    }
    out->resize(static_cast<size_t>(expect));
    for (int i = 0; i < expect; i++) {
        if (el->a[i].t != JVal::Num) {
            if (err) *err = "CSR AI weight array length does not match the layer.";
            return false;
        }
        (*out)[i] = static_cast<float>(el->a[i].n);
    }
    return true;
}

bool csr_ai_weights_parse(const std::string& text, CsrAiWeights* out, std::string* err) {
    JVal root;
    JParser jp;
    jp.p = text.data();
    jp.end = text.data() + text.size();
    jp.err = err;
    if (!jp.value(root) || root.t != JVal::Obj) {
        if (err && err->empty()) *err = "CSR AI weights contain invalid JSON.";
        return false;
    }
    CsrAiWeights w;
    if (const JVal* ver = root.get("version")) {
        if (!jint(ver, &w.version)) {
            if (err) *err = "CSR AI weights version is not supported.";
            return false;
        }
    }
    if (w.version != 1) {
        if (err) *err = "CSR AI weights version is not supported.";
        return false;
    }
    if (const JVal* sc = root.get("scale")) {
        if (!jint(sc, &w.scale)) {
            if (err) *err = "CSR AI only supports 2x weights.";
            return false;
        }
    }
    if (w.scale != 2) {
        if (err) *err = "CSR AI only supports 2x weights.";
        return false;
    }
    if (const JVal* luma = root.get("luma")) {
        if (luma->t == JVal::Arr && luma->a.size() >= 3 && luma->a[0].t == JVal::Num &&
            luma->a[1].t == JVal::Num && luma->a[2].t == JVal::Num) {
            w.luma[0] = static_cast<float>(luma->a[0].n);
            w.luma[1] = static_cast<float>(luma->a[1].n);
            w.luma[2] = static_cast<float>(luma->a[2].n);
        }
    }
    if (const JVal* sh = root.get("shuffle")) {
        if (!jint(sh, &w.shuffle)) {
            if (err) *err = "CSR AI only supports pixel-shuffle 2.";
            return false;
        }
    }
    if (w.shuffle != 2) {
        if (err) *err = "CSR AI only supports pixel-shuffle 2.";
        return false;
    }
    if (const JVal* res = root.get("residual")) {
        if (res->t == JVal::Str) w.residual = res->s;
    }
    const JVal* layers = root.get("layers");
    if (!layers || layers->t != JVal::Arr) {
        if (err) *err = "CSR AI weights have no layers.";
        return false;
    }
    const int n = static_cast<int>(layers->a.size());
    if (n < 2 || n > 6) {
        if (err) *err = "CSR AI weights must have 2 to 6 layers.";
        return false;
    }
    w.layers.resize(static_cast<size_t>(n));
    for (int i = 0; i < n; i++) {
        const JVal& e = layers->a[i];
        CsrAiLayer& l = w.layers[i];
        if (e.t != JVal::Obj) {
            if (err) *err = "CSR AI weights contain an unsupported layer.";
            return false;
        }
        if (const JVal* ty = e.get("type")) {
            if (ty->t == JVal::Str) l.type = ty->s;
        }
        if (l.type != "conv3x3") {
            if (err) *err = "CSR AI weights contain an unsupported layer.";
            return false;
        }
        if (!jint(e.get("in"), &l.in) || !jint(e.get("out"), &l.out)) {
            if (err) *err = "CSR AI layer channel count is out of range.";
            return false;
        }
        if (l.in < 1 || l.in > 16 || l.out < 1 || l.out > 16) {
            if (err) *err = "CSR AI layer channel count is out of range.";
            return false;
        }
        if (const JVal* act = e.get("act")) {
            if (act->t == JVal::Str) l.act = act->s;
        }
        if (!read_floats(e.get("w"), l.out * l.in * 9, &l.w, err)) return false;
        if (!read_floats(e.get("b"), l.out, &l.b, err)) return false;
    }
    if (w.layers.front().in != 1) {
        if (err) *err = "CSR AI first layer must take 1 channel.";
        return false;
    }
    if (w.layers.back().out != 4) {
        if (err) *err = "CSR AI last layer must produce 4 channels.";
        return false;
    }
    *out = std::move(w);
    return true;
}

static bool file_exists(const std::string& p) {
    FILE* f = std::fopen(p.c_str(), "rb");
    if (!f) return false;
    std::fclose(f);
    return true;
}

std::string csr_ai_weights_path() {
    if (const char* env = std::getenv("UFX_CSR_AI_WEIGHTS")) {
        if (file_exists(env)) return env;
    }
    const char* rel[] = {
        "/models/csr_ai_x2.weights.json",
        "/Models/csr_ai_x2.weights.json",
        "/../models/csr_ai_x2.weights.json",
        "/../Models/csr_ai_x2.weights.json",
        "/../share/ufx-linux/models/csr_ai_x2.weights.json",
        "/../share/ufx-linux/Models/csr_ai_x2.weights.json",
        "/../../share/ufx-linux/models/csr_ai_x2.weights.json",
        "/../../Models/csr_ai_x2.weights.json",
        "/../../models/csr_ai_x2.weights.json",
    };
    char self[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", self, sizeof(self) - 1);
    if (n > 0) {
        self[n] = 0;
        char buf[PATH_MAX];
        std::strncpy(buf, self, sizeof(buf));
        std::string dir = dirname(buf);
        for (const char* r : rel) {
            std::string p = dir + r;
            if (file_exists(p)) return p;
        }
    }
    const char* extra[] = {
        "models/csr_ai_x2.weights.json",
        "Models/csr_ai_x2.weights.json",
        "linux/build/models/csr_ai_x2.weights.json",
        "/usr/share/ufx-linux/models/csr_ai_x2.weights.json",
        "/usr/local/share/ufx-linux/models/csr_ai_x2.weights.json",
    };
    for (const char* e : extra) {
        if (file_exists(e)) return e;
    }
    return {};
}

bool csr_ai_weights_load(const std::string& path, CsrAiWeights* out, std::string* err) {
    std::ifstream f(path, std::ios::binary);
    if (!f) {
        if (err) *err = "CSR AI weights file is missing: " + path;
        return false;
    }
    std::string text((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    return csr_ai_weights_parse(text, out, err);
}

static const VkImageUsageFlags kUse = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                      VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;

static bool img_make(VkContext* ctx, uint32_t w, uint32_t h, VkFormat fmt, uint32_t layers, CsrAiImage* o) {
    *o = {};
    o->w = w;
    o->h = h;
    o->layers = layers;
    VkImageCreateInfo ii{};
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = fmt;
    ii.extent = {w, h, 1};
    ii.mipLevels = 1;
    ii.arrayLayers = layers;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = kUse;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (vkCreateImage(ctx->device, &ii, nullptr, &o->image) != VK_SUCCESS) return false;
    VkMemoryRequirements req{};
    vkGetImageMemoryRequirements(ctx->device, o->image, &req);
    VkMemoryAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = vk_find_memory_type(ctx->phys, req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (vkAllocateMemory(ctx->device, &ai, nullptr, &o->mem) != VK_SUCCESS) {
        ai.memoryTypeIndex = vk_find_memory_type(ctx->phys, req.memoryTypeBits, 0);
        if (vkAllocateMemory(ctx->device, &ai, nullptr, &o->mem) != VK_SUCCESS) return false;
    }
    vkBindImageMemory(ctx->device, o->image, o->mem, 0);
    VkImageViewCreateInfo vi{};
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = o->image;
    vi.viewType = layers > 1 ? VK_IMAGE_VIEW_TYPE_2D_ARRAY : VK_IMAGE_VIEW_TYPE_2D;
    vi.format = fmt;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = layers;
    return vkCreateImageView(ctx->device, &vi, nullptr, &o->view) == VK_SUCCESS;
}

static void img_free(VkContext* ctx, CsrAiImage* o) {
    if (!ctx || !ctx->device) return;
    if (o->view) vkDestroyImageView(ctx->device, o->view, nullptr);
    if (o->image) vkDestroyImage(ctx->device, o->image, nullptr);
    if (o->mem) vkFreeMemory(ctx->device, o->mem, nullptr);
    *o = {};
}

static void img_barrier(VkCommandBuffer cmd, VkImage img, uint32_t layers, VkImageLayout from, VkImageLayout to,
                        VkAccessFlags srcA, VkAccessFlags dstA, VkPipelineStageFlags srcS, VkPipelineStageFlags dstS) {
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
    b.subresourceRange.layerCount = layers;
    vkCmdPipelineBarrier(cmd, srcS, dstS, 0, 0, nullptr, 0, nullptr, 1, &b);
}

static bool img_init_general(VkContext* ctx, CsrAiImage* o) {
    VkCommandBuffer cmd = vk_begin_once(ctx);
    img_barrier(cmd, o->image, o->layers, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_GENERAL, 0,
                VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    return vk_submit_once(ctx, cmd);
}

static VkSampler samp(VkDevice d) {
    VkSamplerCreateInfo sci{};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = VK_FILTER_LINEAR;
    sci.minFilter = VK_FILTER_LINEAR;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    VkSampler s = VK_NULL_HANDLE;
    vkCreateSampler(d, &sci, nullptr, &s);
    return s;
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

static VkDescriptorSet alloc_set(CsrAiUpscale* s) {
    VkDescriptorSetAllocateInfo ai{};
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = s->pool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &s->set_layout;
    VkDescriptorSet set = VK_NULL_HANDLE;
    vkAllocateDescriptorSets(s->ctx->device, &ai, &set);
    return set;
}

static VkDescriptorSet make_set(CsrAiUpscale* s, VkImageView sampled, VkImageView feat_in, VkImageView feat_out,
                                VkImageView out) {
    VkDescriptorSet set = alloc_set(s);
    VkDescriptorBufferInfo bi{s->weights_buf, 0, VK_WHOLE_SIZE};
    VkDescriptorImageInfo si{s->linear, sampled, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo fi{VK_NULL_HANDLE, feat_in, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo fo{VK_NULL_HANDLE, feat_out, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo oi{VK_NULL_HANDLE, out, VK_IMAGE_LAYOUT_GENERAL};
    VkWriteDescriptorSet w[5]{};
    for (int i = 0; i < 5; i++) {
        w[i].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        w[i].dstSet = set;
        w[i].dstBinding = static_cast<uint32_t>(i);
        w[i].descriptorCount = 1;
    }
    w[0].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
    w[0].pBufferInfo = &bi;
    w[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    w[1].pImageInfo = &si;
    w[2].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
    w[2].pImageInfo = &fi;
    w[3].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
    w[3].pImageInfo = &fo;
    w[4].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
    w[4].pImageInfo = &oi;
    vkUpdateDescriptorSets(s->ctx->device, 5, w, 0, nullptr);
    return set;
}

static void mem_barrier(VkCommandBuffer cmd) {
    VkMemoryBarrier mb{};
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT;
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, 0, 1, &mb, 0,
                         nullptr, 0, nullptr);
}

static void run(CsrAiUpscale* s, VkCommandBuffer cmd, VkPipeline pipe, VkDescriptorSet set, const CsrAiParamsCPU& p,
                uint32_t w, uint32_t h) {
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, pipe);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, s->pipe_layout, 0, 1, &set, 0, nullptr);
    vkCmdPushConstants(cmd, s->pipe_layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(p), &p);
    vkCmdDispatch(cmd, (w + 7) / 8, (h + 7) / 8, 1);
    mem_barrier(cmd);
}

bool csr_ai_upscale_create(VkContext* ctx, CsrAiUpscale* out, std::string* err) {
    *out = {};
    out->ctx = ctx;
    out->weights_path = csr_ai_weights_path();
    if (out->weights_path.empty()) {
        if (err) *err = "CSR AI weights are missing (Models/csr_ai_x2.weights.json).";
        return false;
    }
    if (!csr_ai_weights_load(out->weights_path, &out->weights, err)) {
        csr_ai_upscale_destroy(out);
        return false;
    }
    std::vector<float> packed;
    out->max_c = 1;
    out->w_off.resize(out->weights.layers.size());
    out->b_off.resize(out->weights.layers.size());
    for (size_t i = 0; i < out->weights.layers.size(); i++) {
        const CsrAiLayer& l = out->weights.layers[i];
        out->max_c = std::max(out->max_c, std::max(l.in, l.out));
        out->w_off[i] = static_cast<int>(packed.size());
        packed.insert(packed.end(), l.w.begin(), l.w.end());
        out->b_off[i] = static_cast<int>(packed.size());
        packed.insert(packed.end(), l.b.begin(), l.b.end());
    }
    VkDeviceSize bytes = std::max<VkDeviceSize>(16, static_cast<VkDeviceSize>(packed.size()) * sizeof(float));
    if (!vk_create_buffer(ctx, bytes, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                          VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                          &out->weights_buf, &out->weights_mem)) {
        if (err) *err = "CSR AI weights buffer allocation failed.";
        csr_ai_upscale_destroy(out);
        return false;
    }
    void* mapped = nullptr;
    vkMapMemory(ctx->device, out->weights_mem, 0, bytes, 0, &mapped);
    std::memcpy(mapped, packed.data(), packed.size() * sizeof(float));
    vkUnmapMemory(ctx->device, out->weights_mem);

    out->linear = samp(ctx->device);
    VkDescriptorSetLayoutBinding b[5]{};
    b[0] = {0, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr};
    b[1] = {1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr};
    b[2] = {2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr};
    b[3] = {3, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr};
    b[4] = {4, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr};
    VkDescriptorSetLayoutCreateInfo li{};
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 5;
    li.pBindings = b;
    if (vkCreateDescriptorSetLayout(ctx->device, &li, nullptr, &out->set_layout) != VK_SUCCESS) {
        if (err) *err = "CSR AI descriptor layout failed.";
        csr_ai_upscale_destroy(out);
        return false;
    }
    VkPushConstantRange pcr{};
    pcr.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    pcr.size = sizeof(CsrAiParamsCPU);
    VkPipelineLayoutCreateInfo pli{};
    pli.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pli.setLayoutCount = 1;
    pli.pSetLayouts = &out->set_layout;
    pli.pushConstantRangeCount = 1;
    pli.pPushConstantRanges = &pcr;
    if (vkCreatePipelineLayout(ctx->device, &pli, nullptr, &out->pipe_layout) != VK_SUCCESS) {
        if (err) *err = "CSR AI pipeline layout failed.";
        csr_ai_upscale_destroy(out);
        return false;
    }
    out->luma_pipe = make_cs(ctx, out->pipe_layout, "csr_ai_luma.spv", err);
    out->conv_pipe = make_cs(ctx, out->pipe_layout, "csr_ai_conv.spv", err);
    out->shuffle_pipe = make_cs(ctx, out->pipe_layout, "csr_ai_shuffle.spv", err);
    out->resample_pipe = make_cs(ctx, out->pipe_layout, "csr_ai_resample.spv", err);
    out->sharpen_pipe = make_cs(ctx, out->pipe_layout, "csr_ai_sharpen.spv", err);
    if (!out->luma_pipe || !out->conv_pipe || !out->shuffle_pipe || !out->resample_pipe || !out->sharpen_pipe) {
        if (err && err->empty()) *err = "Failed to create CSR AI pipelines";
        csr_ai_upscale_destroy(out);
        return false;
    }
    VkDescriptorPoolSize sizes[3] = {{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 32},
                                     {VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 32},
                                     {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 64}};
    VkDescriptorPoolCreateInfo pci{};
    pci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    pci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    pci.maxSets = 64;
    pci.poolSizeCount = 3;
    pci.pPoolSizes = sizes;
    if (vkCreateDescriptorPool(ctx->device, &pci, nullptr, &out->pool) != VK_SUCCESS) {
        if (err) *err = "CSR AI descriptor pool failed.";
        csr_ai_upscale_destroy(out);
        return false;
    }
    return true;
}

void csr_ai_upscale_destroy(CsrAiUpscale* s) {
    if (!s) return;
    if (!s->ctx || !s->ctx->device) {
        *s = {};
        return;
    }
    VkDevice d = s->ctx->device;
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->feat_a);
    img_free(s->ctx, &s->feat_b);
    img_free(s->ctx, &s->net);
    img_free(s->ctx, &s->mid);
    img_free(s->ctx, &s->dst);
    if (s->luma_pipe) vkDestroyPipeline(d, s->luma_pipe, nullptr);
    if (s->conv_pipe) vkDestroyPipeline(d, s->conv_pipe, nullptr);
    if (s->shuffle_pipe) vkDestroyPipeline(d, s->shuffle_pipe, nullptr);
    if (s->resample_pipe) vkDestroyPipeline(d, s->resample_pipe, nullptr);
    if (s->sharpen_pipe) vkDestroyPipeline(d, s->sharpen_pipe, nullptr);
    if (s->pipe_layout) vkDestroyPipelineLayout(d, s->pipe_layout, nullptr);
    if (s->set_layout) vkDestroyDescriptorSetLayout(d, s->set_layout, nullptr);
    if (s->weights_buf) vkDestroyBuffer(d, s->weights_buf, nullptr);
    if (s->weights_mem) vkFreeMemory(d, s->weights_mem, nullptr);
    if (s->pool) vkDestroyDescriptorPool(d, s->pool, nullptr);
    if (s->linear) vkDestroySampler(d, s->linear, nullptr);
    *s = {};
}

static bool ensure(CsrAiUpscale* s, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h) {
    if (s->src.image && s->in_w == in_w && s->in_h == in_h && s->out_w == out_w && s->out_h == out_h) return true;
    img_free(s->ctx, &s->src);
    img_free(s->ctx, &s->feat_a);
    img_free(s->ctx, &s->feat_b);
    img_free(s->ctx, &s->net);
    img_free(s->ctx, &s->mid);
    img_free(s->ctx, &s->dst);
    s->in_w = in_w;
    s->in_h = in_h;
    s->out_w = out_w;
    s->out_h = out_h;
    s->src_general = false;
    const uint32_t net_w = in_w * 2, net_h = in_h * 2;
    const uint32_t c = static_cast<uint32_t>(s->max_c);
    if (!img_make(s->ctx, in_w, in_h, VK_FORMAT_R32G32B32A32_SFLOAT, 1, &s->src) ||
        !img_make(s->ctx, in_w, in_h, VK_FORMAT_R32_SFLOAT, c, &s->feat_a) ||
        !img_make(s->ctx, in_w, in_h, VK_FORMAT_R32_SFLOAT, c, &s->feat_b) ||
        !img_make(s->ctx, net_w, net_h, VK_FORMAT_R32G32B32A32_SFLOAT, 1, &s->net) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, 1, &s->mid) ||
        !img_make(s->ctx, out_w, out_h, VK_FORMAT_R32G32B32A32_SFLOAT, 1, &s->dst))
        return false;
    if (!img_init_general(s->ctx, &s->feat_a) || !img_init_general(s->ctx, &s->feat_b) ||
        !img_init_general(s->ctx, &s->net) || !img_init_general(s->ctx, &s->mid) ||
        !img_init_general(s->ctx, &s->dst))
        return false;
    return true;
}

static bool dispatch(CsrAiUpscale* s, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h, float sharpness,
                     std::string* err) {
    const uint32_t net_w = in_w * 2, net_h = in_h * 2;
    CsrAiParamsCPU p{};
    p.SrcW = static_cast<float>(in_w);
    p.SrcH = static_cast<float>(in_h);
    p.DstW = static_cast<float>(net_w);
    p.DstH = static_cast<float>(net_h);
    p.Sharpness = std::max(0.f, sharpness) + csr_ai_ref::sharp_offset();
    p.InC = 1;
    p.OutC = 1;
    p.ActLeaky = 0;
    p.LumaR = s->weights.luma[0];
    p.LumaG = s->weights.luma[1];
    p.LumaB = s->weights.luma[2];
    p.TexW = static_cast<float>(in_w);
    p.TexH = static_cast<float>(in_h);

    vkResetDescriptorPool(s->ctx->device, s->pool, 0);
    VkCommandBuffer cmd = vk_begin_once(s->ctx);
    CsrAiImage* a = &s->feat_a;
    CsrAiImage* b = &s->feat_b;
    run(s, cmd, s->luma_pipe, make_set(s, s->src.view, b->view, a->view, s->net.view), p, in_w, in_h);
    for (size_t i = 0; i < s->weights.layers.size(); i++) {
        const CsrAiLayer& l = s->weights.layers[i];
        p.InC = static_cast<float>(l.in);
        p.OutC = static_cast<float>(l.out);
        p.ActLeaky = l.act == "leaky0.1" ? 1.f : 0.f;
        p.WeightOff = static_cast<float>(s->w_off[i]);
        p.BiasOff = static_cast<float>(s->b_off[i]);
        run(s, cmd, s->conv_pipe, make_set(s, s->src.view, a->view, b->view, s->net.view), p, in_w, in_h);
        std::swap(a, b);
    }
    CsrAiImage* spare = a == &s->feat_a ? &s->feat_b : &s->feat_a;
    run(s, cmd, s->shuffle_pipe, make_set(s, s->src.view, a->view, spare->view, s->net.view), p, net_w, net_h);
    const CsrAiImage* sharp_src = &s->net;
    if (out_w != net_w || out_h != net_h) {
        p.SrcW = static_cast<float>(net_w);
        p.SrcH = static_cast<float>(net_h);
        p.DstW = static_cast<float>(out_w);
        p.DstH = static_cast<float>(out_h);
        run(s, cmd, s->resample_pipe, make_set(s, s->net.view, spare->view, spare->view, s->mid.view), p, out_w, out_h);
        sharp_src = &s->mid;
    }
    p.SrcW = static_cast<float>(sharp_src->w);
    p.SrcH = static_cast<float>(sharp_src->h);
    p.DstW = static_cast<float>(out_w);
    p.DstH = static_cast<float>(out_h);
    run(s, cmd, s->sharpen_pipe, make_set(s, sharp_src->view, spare->view, spare->view, s->dst.view), p, out_w, out_h);
    if (!vk_submit_once(s->ctx, cmd)) {
        if (err) *err = "CSR AI dispatch failed";
        return false;
    }
    return true;
}

bool csr_ai_upscale_encode(CsrAiUpscale* s, const float* src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                           uint32_t out_h, float sharpness, float* dst, std::string* err) {
    if (!ensure(s, in_w, in_h, out_w, out_h)) {
        if (err) *err = "CSR AI image alloc failed";
        return false;
    }
    if (s->src_general) {
        VkCommandBuffer cmd = vk_begin_once(s->ctx);
        img_barrier(cmd, s->src.image, 1, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_UNDEFINED, 0, 0,
                    VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT);
        if (!vk_submit_once(s->ctx, cmd)) {
            if (err) *err = "CSR AI upload failed";
            return false;
        }
        s->src_general = false;
    }
    if (!vk_upload_rgba32f_staging(s->ctx, src, in_w, in_h, s->src.image)) {
        if (err) *err = "CSR AI upload failed";
        return false;
    }
    s->src_general = true;
    if (!dispatch(s, in_w, in_h, out_w, out_h, sharpness, err)) return false;
    return vk_download_rgba32f(s->ctx, s->dst.image, out_w, out_h, dst);
}

bool csr_ai_upscale_from_image(CsrAiUpscale* s, VkImage src, uint32_t in_w, uint32_t in_h, uint32_t out_w,
                               uint32_t out_h, float sharpness, std::string* err) {
    if (!ensure(s, in_w, in_h, out_w, out_h)) {
        if (err) *err = "CSR AI image alloc failed";
        return false;
    }
    if (!s->src_general) {
        VkCommandBuffer cmd = vk_begin_once(s->ctx);
        img_barrier(cmd, s->src.image, 1, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_GENERAL, 0,
                    VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                    VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
        if (!vk_submit_once(s->ctx, cmd)) {
            if (err) *err = "CSR AI copy failed";
            return false;
        }
        s->src_general = true;
    }
    if (!vk_copy_image_2d(s->ctx, src, s->src.image, in_w, in_h)) {
        if (err) *err = "CSR AI copy failed";
        return false;
    }
    return dispatch(s, in_w, in_h, out_w, out_h, sharpness, err);
}
