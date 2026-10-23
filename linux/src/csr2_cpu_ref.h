#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace csr2_ref {

inline float clampf(float x) { return std::min(1.f, std::max(0.f, x)); }
inline float safeRcp(float x) { return x == 0.f ? 0.f : 1.f / x; }
inline float luma(float r, float g, float b) { return 0.2126f * r + 0.7152f * g + 0.0722f * b; }

inline void loadC(const float* img, int w, int h, int x, int y, float* rgb) {
    x = std::max(0, std::min(w - 1, x));
    y = std::max(0, std::min(h - 1, y));
    const float* p = img + (static_cast<size_t>(y) * w + x) * 4;
    rgb[0] = p[0];
    rgb[1] = p[1];
    rgb[2] = p[2];
}

inline float lanczos2(float x) {
    x = std::fabs(x);
    if (x < 1e-5f) return 1.f;
    if (x >= 2.f) return 0.f;
    float p = 3.14159265f * x;
    return (std::sin(p) * std::sin(p * 0.5f)) / (p * p * 0.5f);
}

inline void upsample(const float* src, int in_w, int in_h, float* dst, int out_w, int out_h, float jx, float jy) {
    for (int y = 0; y < out_h; y++) {
        for (int x = 0; x < out_w; x++) {
            float srcx = (x + 0.5f) * in_w / static_cast<float>(out_w) - jx;
            float srcy = (y + 0.5f) * in_h / static_cast<float>(out_h) - jy;
            float fpx = std::floor(srcx - 0.5f);
            float fpy = std::floor(srcy - 0.5f);
            float ppx = srcx - 0.5f - fpx;
            float ppy = srcy - 0.5f - fpy;
            int px = static_cast<int>(fpx);
            int py = static_cast<int>(fpy);
            float c[9][3];
            int k = 0;
            for (int dy = 0; dy <= 2; dy++)
                for (int dx = 0; dx <= 2; dx++) loadC(src, in_w, in_h, px + dx, py + dy, c[k++]);
            auto L = [&](int i) { return luma(c[i][0], c[i][1], c[i][2]); };
            float l00 = L(0), l10 = L(1), l20 = L(2);
            float l01 = L(3), l11 = L(4), l21 = L(5);
            float l02 = L(6), l12 = L(7), l22 = L(8);
            float gx = (l20 + 2.f * l21 + l22) - (l00 + 2.f * l01 + l02);
            float gy = (l02 + 2.f * l12 + l22) - (l00 + 2.f * l10 + l20);
            gx += (l11 - l01) + (l21 - l11);
            gy += (l11 - l10) + (l12 - l11);
            float ixx = gx * gx, ixy = gx * gy, iyy = gy * gy;
            float dirx = ixx - iyy, diry = 2.f * ixy;
            float dirR = dirx * dirx + diry * diry;
            bool zro = dirR < (1.f / 32768.f);
            if (zro) {
                dirx = 1.f;
                diry = 0.f;
            } else {
                float s = 1.f / std::sqrt(dirR);
                dirx *= s;
                diry *= s;
            }
            float aniso = clampf(std::sqrt(ixx + iyy) * 0.35f);
            float len0 = 1.f + 0.55f * aniso;
            float len1 = 1.f - 0.4f * aniso;
            float acc[3] = {};
            float wsum = 0.f;
            for (int oy = -1; oy <= 2; oy++)
                for (int ox = -1; ox <= 2; ox++) {
                    float offx = static_cast<float>(ox) - ppx;
                    float offy = static_cast<float>(oy) - ppy;
                    float vx = (offx * dirx + offy * diry) * len0;
                    float vy = (offx * -diry + offy * dirx) * len1;
                    float w = lanczos2(vx) * lanczos2(vy);
                    float rgb[3];
                    loadC(src, in_w, in_h, px + ox, py + oy, rgb);
                    acc[0] += rgb[0] * w;
                    acc[1] += rgb[1] * w;
                    acc[2] += rgb[2] * w;
                    wsum += w;
                }
            float rcp = safeRcp(wsum);
            float a[3], b[3], d[3], e[3], mn[3], mx[3], extra[3];
            loadC(src, in_w, in_h, px, py, a);
            loadC(src, in_w, in_h, px + 1, py, b);
            loadC(src, in_w, in_h, px, py + 1, d);
            loadC(src, in_w, in_h, px + 1, py + 1, e);
            for (int i = 0; i < 3; i++) {
                mn[i] = std::min(std::min(a[i], b[i]), std::min(d[i], e[i]));
                mx[i] = std::max(std::max(a[i], b[i]), std::max(d[i], e[i]));
            }
            loadC(src, in_w, in_h, px - 1, py, extra);
            for (int i = 0; i < 3; i++) mn[i] = std::min(mn[i], extra[i]);
            loadC(src, in_w, in_h, px, py - 1, extra);
            for (int i = 0; i < 3; i++) mn[i] = std::min(mn[i], extra[i]);
            loadC(src, in_w, in_h, px + 2, py, extra);
            for (int i = 0; i < 3; i++) mx[i] = std::max(mx[i], extra[i]);
            loadC(src, in_w, in_h, px, py + 2, extra);
            for (int i = 0; i < 3; i++) mx[i] = std::max(mx[i], extra[i]);
            float* o = dst + (static_cast<size_t>(y) * out_w + x) * 4;
            for (int i = 0; i < 3; i++) o[i] = std::min(mx[i], std::max(mn[i], acc[i] * rcp));
            o[3] = 1.f;
        }
    }
}

inline void cas(const float* src, float* dst, int w, int h, float sharpness, float sharp_k) {
    auto at = [&](int x, int y) -> const float* {
        x = std::max(0, std::min(w - 1, x));
        y = std::max(0, std::min(h - 1, y));
        return src + (static_cast<size_t>(y) * w + x) * 4;
    };
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            const float* b = at(x, y - 1);
            const float* d = at(x - 1, y);
            const float* e = at(x, y);
            const float* f = at(x + 1, y);
            const float* hh = at(x, y + 1);
            float nz = 0.25f * (b[1] + d[1] + f[1] + hh[1]) - e[1];
            float mxL = std::max(std::max(std::max(b[1], d[1]), std::max(e[1], f[1])), hh[1]);
            float mnL = std::min(std::min(std::min(b[1], d[1]), std::min(e[1], f[1])), hh[1]);
            float range = mxL - mnL;
            nz = clampf(std::fabs(nz) * safeRcp(range));
            nz = 1.f - 0.5f * nz;
            float mn[3], mx[3];
            for (int i = 0; i < 3; i++) {
                mn[i] = std::min(std::min(b[i], d[i]), std::min(f[i], hh[i]));
                mx[i] = std::max(std::max(b[i], d[i]), std::max(f[i], hh[i]));
            }
            float lobeRGB[3];
            for (int i = 0; i < 3; i++) {
                float hitMin = mn[i] / std::max(4.f * mx[i], 1e-6f);
                float hitMax = (1.f - mx[i]) / std::min(4.f * mn[i] - 4.f, -1e-6f);
                lobeRGB[i] = std::max(-hitMin, hitMax);
            }
            const float kLimit = 0.25f - (1.f / 16.f);
            float lobe = std::max(-kLimit, std::min(std::max(std::max(lobeRGB[0], lobeRGB[1]), lobeRGB[2]), 0.f));
            lobe *= std::exp2(-sharpness * std::max(sharp_k, 0.f)) * nz;
            float* o = dst + (static_cast<size_t>(y) * w + x) * 4;
            float rcp = safeRcp(4.f * lobe + 1.f);
            for (int i = 0; i < 3; i++) o[i] = clampf((lobe * (b[i] + d[i] + f[i] + hh[i]) + e[i]) * rcp);
            o[3] = 1.f;
        }
    }
}

}