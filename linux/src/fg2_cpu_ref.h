#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace fg2_ref {

inline float clampf(float x, float a, float b) { return std::min(b, std::max(a, x)); }

inline void sample_linear(const float* img, int w, int h, float uvx, float uvy, float* rgb) {
    float x = clampf(uvx, 0.f, 1.f) * static_cast<float>(w) - 0.5f;
    float y = clampf(uvy, 0.f, 1.f) * static_cast<float>(h) - 0.5f;
    int x0 = static_cast<int>(std::floor(x));
    int y0 = static_cast<int>(std::floor(y));
    int x1 = x0 + 1;
    int y1 = y0 + 1;
    float fx = x - static_cast<float>(x0);
    float fy = y - static_cast<float>(y0);
    auto at = [&](int px, int py) -> const float* {
        px = std::max(0, std::min(w - 1, px));
        py = std::max(0, std::min(h - 1, py));
        return img + (static_cast<size_t>(py) * static_cast<size_t>(w) + static_cast<size_t>(px)) * 4;
    };
    const float* c00 = at(x0, y0);
    const float* c10 = at(x1, y0);
    const float* c01 = at(x0, y1);
    const float* c11 = at(x1, y1);
    for (int k = 0; k < 3; k++) {
        float a = c00[k] * (1.f - fx) + c10[k] * fx;
        float b = c01[k] * (1.f - fx) + c11[k] * fx;
        rgb[k] = a * (1.f - fy) + b * fy;
    }
}

inline float sample_point_r(const float* img, int w, int h, float uvx, float uvy, int nchan) {
    int x = std::max(0, std::min(w - 1, static_cast<int>(std::floor(uvx * static_cast<float>(w)))));
    int y = std::max(0, std::min(h - 1, static_cast<int>(std::floor(uvy * static_cast<float>(h)))));
    return img[(static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * static_cast<size_t>(nchan)];
}

inline void sample_point_rg(const float* img, int w, int h, float uvx, float uvy, int nchan, float* xy) {
    int x = std::max(0, std::min(w - 1, static_cast<int>(std::floor(uvx * static_cast<float>(w)))));
    int y = std::max(0, std::min(h - 1, static_cast<int>(std::floor(uvy * static_cast<float>(h)))));
    const float* p = img + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * static_cast<size_t>(nchan);
    xy[0] = p[0];
    xy[1] = p[1];
}

inline bool inside(float uvx, float uvy) { return uvx >= 0.f && uvy >= 0.f && uvx <= 1.f && uvy <= 1.f; }

inline void interp_fast(const float* prev, const float* curr, int w, int h, const float* mv_rgba, int tw, int th,
                        const float* hud, float t, float cut_flag, float* dst) {
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            float uvx = (static_cast<float>(x) + 0.5f) / static_cast<float>(w);
            float uvy = (static_cast<float>(y) + 0.5f) / static_cast<float>(h);
            float* o = dst + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
            o[3] = 1.f;
            if (cut_flag > 0.5f) {
                const float* s = (t < 0.5f ? prev : curr) + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
                o[0] = s[0];
                o[1] = s[1];
                o[2] = s[2];
                continue;
            }
            float hv = sample_point_r(hud, w, h, uvx, uvy, 1);
            if (hv > 0.5f) {
                const float* s = curr + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
                o[0] = s[0];
                o[1] = s[1];
                o[2] = s[2];
                continue;
            }
            float mxy[2];
            sample_point_rg(mv_rgba, tw, th, uvx, uvy, 4, mxy);
            float vb_x = mxy[0] / static_cast<float>(w);
            float vb_y = mxy[1] / static_cast<float>(h);
            float pBx = uvx - (1.f - t) * vb_x;
            float pBy = uvy - (1.f - t) * vb_y;
            float pAx = pBx + vb_x;
            float pAy = pBy + vb_y;
            float bB[3], bA[3];
            sample_linear(curr, w, h, pBx, pBy, bB);
            sample_linear(prev, w, h, pAx, pAy, bA);
            float mixv[3];
            for (int k = 0; k < 3; k++) mixv[k] = bA[k] * (1.f - t) + bB[k] * t;
            bool okA = inside(pAx, pAy), okB = inside(pBx, pBy);
            if (!okA && okB) {
                for (int k = 0; k < 3; k++) mixv[k] = bB[k];
            } else if (!okB && okA) {
                for (int k = 0; k < 3; k++) mixv[k] = bA[k];
            } else if (!okA && !okB) {
                sample_linear(prev, w, h, uvx, uvy, bA);
                sample_linear(curr, w, h, uvx, uvy, bB);
                for (int k = 0; k < 3; k++) mixv[k] = bA[k] * (1.f - t) + bB[k] * t;
            }
            o[0] = mixv[0];
            o[1] = mixv[1];
            o[2] = mixv[2];
        }
    }
}

}