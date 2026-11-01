#pragma once

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <string>
#include <vector>

struct CsrAiLayer {
    std::string type;
    int in = 0;
    int out = 0;
    std::string act = "none";
    int dilation = 1;
    std::vector<float> w;
    std::vector<float> b;
};

struct CsrAiWeights {
    int version = 0;
    int scale = 2;
    float luma[3] = {0.2126f, 0.7152f, 0.0722f};
    std::vector<CsrAiLayer> layers;
    int shuffle = 2;
    std::string residual = "bilinear";
};

bool csr_ai_weights_parse(const std::string& text, CsrAiWeights* out, std::string* err);
bool csr_ai_weights_load(const std::string& path, CsrAiWeights* out, std::string* err);
std::string csr_ai_weights_path();

namespace csr_ai_ref {

inline float clampf(float x) { return std::min(1.f, std::max(0.f, x)); }
inline float safeRcp(float x) { return x == 0.f ? 0.f : 1.f / x; }
inline float luma(const float* rgb, const float* l) { return rgb[0] * l[0] + rgb[1] * l[1] + rgb[2] * l[2]; }

inline float sharp_offset() {
    const char* s = std::getenv("UFX_CSRAI_SHARP_OFFSET");
    if (!s || !*s) return 2.f;
    char* end = nullptr;
    float v = std::strtof(s, &end);
    return end && end != s ? v : 2.f;
}

inline void sample_norm(const float* img, int w, int h, float u, float v, float* rgb) {
    float fx = u * static_cast<float>(w) - 0.5f;
    float fy = v * static_cast<float>(h) - 0.5f;
    fx = std::min(std::max(fx, 0.f), static_cast<float>(w - 1));
    fy = std::min(std::max(fy, 0.f), static_cast<float>(h - 1));
    int x0 = static_cast<int>(std::floor(fx));
    int y0 = static_cast<int>(std::floor(fy));
    int x1 = std::min(x0 + 1, w - 1);
    int y1 = std::min(y0 + 1, h - 1);
    float tx = fx - static_cast<float>(x0);
    float ty = fy - static_cast<float>(y0);
    const float* a = img + (static_cast<size_t>(y0) * w + x0) * 4;
    const float* b = img + (static_cast<size_t>(y0) * w + x1) * 4;
    const float* c = img + (static_cast<size_t>(y1) * w + x0) * 4;
    const float* d = img + (static_cast<size_t>(y1) * w + x1) * 4;
    for (int i = 0; i < 3; i++) rgb[i] = (a[i] * (1.f - tx) + b[i] * tx) * (1.f - ty) + (c[i] * (1.f - tx) + d[i] * tx) * ty;
}

inline void upscale(const CsrAiWeights& weights, const float* src, int in_w, int in_h, float* dst, int out_w, int out_h,
                    float sharpness) {
    const int net_w = in_w * 2, net_h = in_h * 2;
    int max_c = 1;
    for (const auto& l : weights.layers) max_c = std::max(max_c, std::max(l.in, l.out));

    std::vector<float> cur(static_cast<size_t>(max_c) * in_w * in_h, 0.f);
    for (int y = 0; y < in_h; y++) {
        for (int x = 0; x < in_w; x++) {
            const float* p = src + (static_cast<size_t>(y) * in_w + x) * 4;
            cur[static_cast<size_t>(y) * in_w + x] = luma(p, weights.luma);
        }
    }
    int cur_c = 1;
    std::vector<float> next(static_cast<size_t>(max_c) * in_w * in_h, 0.f);
    for (const auto& l : weights.layers) {
        std::fill(next.begin(), next.end(), 0.f);
        for (int oc = 0; oc < l.out; oc++) {
            for (int y = 0; y < in_h; y++) {
                for (int x = 0; x < in_w; x++) {
                    float acc = l.b[oc];
                    for (int ic = 0; ic < l.in; ic++) {
                        const int k = (oc * l.in + ic) * 9;
                        for (int ky = 0; ky < 3; ky++) {
                            int sy = std::min(std::max(y + ky - 1, 0), in_h - 1);
                            for (int kx = 0; kx < 3; kx++) {
                                int sx = std::min(std::max(x + kx - 1, 0), in_w - 1);
                                acc += l.w[k + ky * 3 + kx] *
                                       cur[(static_cast<size_t>(ic) * in_h + sy) * in_w + sx];
                            }
                        }
                    }
                    if (l.act == "leaky0.1") acc = acc >= 0.f ? acc : 0.1f * acc;
                    next[(static_cast<size_t>(oc) * in_h + y) * in_w + x] = acc;
                }
            }
        }
        cur.swap(next);
        cur_c = l.out;
    }
    (void)cur_c;

    std::vector<float> net(static_cast<size_t>(net_w) * net_h * 4);
    for (int y = 0; y < net_h; y++) {
        for (int x = 0; x < net_w; x++) {
            int lx = x >> 1, ly = y >> 1;
            int c = ((y & 1) << 1) | (x & 1);
            float y_delta = cur[(static_cast<size_t>(c) * in_h + ly) * in_w + lx];
            float u = (static_cast<float>(x) + 0.5f) * 0.5f / static_cast<float>(in_w);
            float v = (static_cast<float>(y) + 0.5f) * 0.5f / static_cast<float>(in_h);
            float rgb[3];
            sample_norm(src, in_w, in_h, u, v, rgb);
            float* o = net.data() + (static_cast<size_t>(y) * net_w + x) * 4;
            for (int i = 0; i < 3; i++) o[i] = clampf(rgb[i] + y_delta);
            o[3] = 1.f;
        }
    }

    const float sharp = std::max(0.f, sharpness) + sharp_offset();
    std::vector<float> mid;
    const float* sharp_src = net.data();
    if (out_w != net_w || out_h != net_h) {
        mid.resize(static_cast<size_t>(out_w) * out_h * 4);
        for (int y = 0; y < out_h; y++) {
            for (int x = 0; x < out_w; x++) {
                float u = (static_cast<float>(x) + 0.5f) / static_cast<float>(out_w);
                float v = (static_cast<float>(y) + 0.5f) / static_cast<float>(out_h);
                float* o = mid.data() + (static_cast<size_t>(y) * out_w + x) * 4;
                sample_norm(net.data(), net_w, net_h, u, v, o);
                o[3] = 1.f;
            }
        }
        sharp_src = mid.data();
    }

    auto at = [&](int x, int y) -> const float* {
        x = std::min(std::max(x, 0), out_w - 1);
        y = std::min(std::max(y, 0), out_h - 1);
        return sharp_src + (static_cast<size_t>(y) * out_w + x) * 4;
    };
    const float kLimit = 0.25f - (1.f / 16.f);
    for (int y = 0; y < out_h; y++) {
        for (int x = 0; x < out_w; x++) {
            const float* b = at(x, y - 1);
            const float* d = at(x - 1, y);
            const float* e = at(x, y);
            const float* f = at(x + 1, y);
            const float* hh = at(x, y + 1);
            float nz = 0.25f * (b[1] + d[1] + f[1] + hh[1]) - e[1];
            float mx_l = std::max(std::max(std::max(b[1], d[1]), std::max(e[1], f[1])), hh[1]);
            float mn_l = std::min(std::min(std::min(b[1], d[1]), std::min(e[1], f[1])), hh[1]);
            nz = clampf(std::fabs(nz) * safeRcp(mx_l - mn_l));
            nz = 1.f - 0.5f * nz;
            float mn[3], mx[3], lobe_rgb[3];
            for (int i = 0; i < 3; i++) {
                mn[i] = std::min(std::min(b[i], d[i]), std::min(f[i], hh[i]));
                mx[i] = std::max(std::max(b[i], d[i]), std::max(f[i], hh[i]));
                float hit_min = mn[i] / std::max(4.f * mx[i], 1e-6f);
                float hit_max = (1.f - mx[i]) / std::min(4.f * mn[i] - 4.f, -1e-6f);
                lobe_rgb[i] = std::max(-hit_min, hit_max);
            }
            float lobe = std::max(-kLimit, std::min(std::max(std::max(lobe_rgb[0], lobe_rgb[1]), lobe_rgb[2]), 0.f));
            lobe *= std::exp2(-sharp) * nz;
            float* o = dst + (static_cast<size_t>(y) * out_w + x) * 4;
            float rcp = safeRcp(4.f * lobe + 1.f);
            for (int i = 0; i < 3; i++) o[i] = clampf((lobe * (b[i] + d[i] + f[i] + hh[i]) + e[i]) * rcp);
            o[3] = 1.f;
        }
    }
}

}
