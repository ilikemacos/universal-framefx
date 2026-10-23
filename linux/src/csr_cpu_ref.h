#pragma once
// CPU reference of CSR 1.2 EASU/RCAS, ported independently from
// macos/Resources/Shaders/Spatial.metal (not from the GLSL).
//
// Adapted from AMD FidelityFX Super Resolution 1 (MIT).
// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
// Modifications Copyright (c) 2026 Chopsticks HQ

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace csr_ref {

struct Vec2 {
    float x, y;
};
struct Vec3 {
    float r, g, b;
};

inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.r + b.r, a.g + b.g, a.b + b.b}; }
inline Vec3 operator*(Vec3 a, float s) { return {a.r * s, a.g * s, a.b * s}; }
inline Vec3 vmin(Vec3 a, Vec3 b) { return {std::min(a.r, b.r), std::min(a.g, b.g), std::min(a.b, b.b)}; }
inline Vec3 vmax(Vec3 a, Vec3 b) { return {std::max(a.r, b.r), std::max(a.g, b.g), std::max(a.b, b.b)}; }
inline Vec3 vclamp(Vec3 v, Vec3 lo, Vec3 hi) { return vmin(vmax(v, lo), hi); }

inline float saturate(float x) { return std::min(1.0f, std::max(0.0f, x)); }
inline float safeRcp(float x) { return x == 0.0f ? 0.0f : 1.0f / x; }
inline float edgeLuma(Vec3 c) { return c.g + 0.5f * (c.r + c.b); }
inline float max3f(float a, float b, float c) { return std::max(a, std::max(b, c)); }
inline float min3f(float a, float b, float c) { return std::min(a, std::min(b, c)); }

inline Vec3 sample_clamp(const float* img, int w, int h, int x, int y) {
    x = std::max(0, std::min(w - 1, x));
    y = std::max(0, std::min(h - 1, y));
    const float* p = img + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
    return {p[0], p[1], p[2]};
}

inline void easuSet(Vec2& dir, float& len, Vec2 pp, int corner, float lA, float lB, float lC, float lD, float lE) {
    float w = 0.0f;
    switch (corner) {
        case 0: w = (1.0f - pp.x) * (1.0f - pp.y); break;
        case 1: w = pp.x * (1.0f - pp.y); break;
        case 2: w = (1.0f - pp.x) * pp.y; break;
        default: w = pp.x * pp.y; break;
    }
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = safeRcp(std::max(std::fabs(dc), std::fabs(cb)));
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(std::fabs(dirX) * lenX);
    len += lenX * lenX * w;

    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = safeRcp(std::max(std::fabs(ec), std::fabs(ca)));
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(std::fabs(dirY) * lenY);
    len += lenY * lenY * w;
}

inline void easuTap(Vec3& accColor, float& accWeight, Vec2 off, Vec2 dir, Vec2 len2, float lob, float clp, Vec3 c) {
    Vec2 v;
    v.x = (off.x * dir.x) + (off.y * dir.y);
    v.y = (off.x * -dir.y) + (off.y * dir.x);
    v.x *= len2.x;
    v.y *= len2.y;
    float d2 = std::min(v.x * v.x + v.y * v.y, clp);
    float wB = (2.0f / 5.0f) * d2 - 1.0f;
    float wA = lob * d2 - 1.0f;
    wB *= wB;
    wA *= wA;
    wB = (25.0f / 16.0f) * wB - (25.0f / 16.0f - 1.0f);
    float w = wB * wA;
    accColor = accColor + (c * w);
    accWeight += w;
}

inline void easu(const float* src, int inW, int inH, float* dst, int outW, int outH) {
    const float scaleX = static_cast<float>(inW) / static_cast<float>(outW);
    const float scaleY = static_cast<float>(inH) / static_cast<float>(outH);
    for (int y = 0; y < outH; ++y) {
        for (int x = 0; x < outW; ++x) {
            Vec2 pp{ (static_cast<float>(x) + 0.5f) * scaleX - 0.5f,
                     (static_cast<float>(y) + 0.5f) * scaleY - 0.5f };
            float fpx = std::floor(pp.x);
            float fpy = std::floor(pp.y);
            pp.x -= fpx;
            pp.y -= fpy;
            int fxi = static_cast<int>(fpx);
            int fyi = static_cast<int>(fpy);

            auto TAP = [&](int dx, int dy) { return sample_clamp(src, inW, inH, fxi + dx, fyi + dy); };
            Vec3 bC = TAP(0, -1), cC = TAP(1, -1);
            Vec3 eC = TAP(-1, 0), fC = TAP(0, 0), gC = TAP(1, 0), hC = TAP(2, 0);
            Vec3 iC = TAP(-1, 1), jC = TAP(0, 1), kC = TAP(1, 1), lC = TAP(2, 1);
            Vec3 nC = TAP(0, 2), oC = TAP(1, 2);

            float bL = edgeLuma(bC), cL = edgeLuma(cC);
            float eL = edgeLuma(eC), fL = edgeLuma(fC), gL = edgeLuma(gC), hL = edgeLuma(hC);
            float iL = edgeLuma(iC), jL = edgeLuma(jC), kL = edgeLuma(kC), lL = edgeLuma(lC);
            float nL = edgeLuma(nC), oL = edgeLuma(oC);

            Vec2 dir{0.0f, 0.0f};
            float len = 0.0f;
            easuSet(dir, len, pp, 0, bL, eL, fL, gL, jL);
            easuSet(dir, len, pp, 1, cL, fL, gL, hL, kL);
            easuSet(dir, len, pp, 2, fL, iL, jL, kL, nL);
            easuSet(dir, len, pp, 3, gL, jL, kL, lL, oL);

            float dirR = dir.x * dir.x + dir.y * dir.y;
            bool zero = dirR < (1.0f / 32768.0f);
            dirR = zero ? 1.0f : 1.0f / std::sqrt(dirR);
            dir.x = zero ? 1.0f : dir.x;
            dir.x *= dirR;
            dir.y *= dirR;

            len = len * 0.5f;
            len *= len;

            float stretch = (dir.x * dir.x + dir.y * dir.y) * safeRcp(std::max(std::fabs(dir.x), std::fabs(dir.y)));
            Vec2 len2{1.0f + (stretch - 1.0f) * len, 1.0f - 0.5f * len};
            float lob = 0.5f + ((1.0f / 4.0f - 0.04f) - 0.5f) * len;
            float clp = safeRcp(lob);

            Vec3 accColor{0, 0, 0};
            float accWeight = 0.0f;
            easuTap(accColor, accWeight, Vec2{0.0f - pp.x, -1.0f - pp.y}, dir, len2, lob, clp, bC);
            easuTap(accColor, accWeight, Vec2{1.0f - pp.x, -1.0f - pp.y}, dir, len2, lob, clp, cC);
            easuTap(accColor, accWeight, Vec2{-1.0f - pp.x, 1.0f - pp.y}, dir, len2, lob, clp, iC);
            easuTap(accColor, accWeight, Vec2{0.0f - pp.x, 1.0f - pp.y}, dir, len2, lob, clp, jC);
            easuTap(accColor, accWeight, Vec2{0.0f - pp.x, 0.0f - pp.y}, dir, len2, lob, clp, fC);
            easuTap(accColor, accWeight, Vec2{-1.0f - pp.x, 0.0f - pp.y}, dir, len2, lob, clp, eC);
            easuTap(accColor, accWeight, Vec2{1.0f - pp.x, 1.0f - pp.y}, dir, len2, lob, clp, kC);
            easuTap(accColor, accWeight, Vec2{2.0f - pp.x, 1.0f - pp.y}, dir, len2, lob, clp, lC);
            easuTap(accColor, accWeight, Vec2{2.0f - pp.x, 0.0f - pp.y}, dir, len2, lob, clp, hC);
            easuTap(accColor, accWeight, Vec2{1.0f - pp.x, 0.0f - pp.y}, dir, len2, lob, clp, gC);
            easuTap(accColor, accWeight, Vec2{1.0f - pp.x, 2.0f - pp.y}, dir, len2, lob, clp, oC);
            easuTap(accColor, accWeight, Vec2{0.0f - pp.x, 2.0f - pp.y}, dir, len2, lob, clp, nC);

            Vec3 mn4 = vmin(vmin(fC, gC), vmin(jC, kC));
            Vec3 mx4 = vmax(vmax(fC, gC), vmax(jC, kC));
            Vec3 pix = vclamp(accColor * safeRcp(accWeight), mn4, mx4);
            float* out = dst + (static_cast<size_t>(y) * static_cast<size_t>(outW) + static_cast<size_t>(x)) * 4;
            out[0] = pix.r;
            out[1] = pix.g;
            out[2] = pix.b;
            out[3] = 1.0f;
        }
    }
}

inline void rcas(const float* src, float* dst, int w, int h, float sharpness) {
    const float kRcasLimit = 0.25f - (1.0f / 16.0f);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            auto RD = [&](int dx, int dy) { return sample_clamp(src, w, h, x + dx, y + dy); };
            Vec3 b = RD(0, -1);
            Vec3 d = RD(-1, 0);
            Vec3 e = RD(0, 0);
            Vec3 f = RD(1, 0);
            Vec3 hC = RD(0, 1);

            float bL = b.g, dL = d.g, eL = e.g, fL = f.g, hL = hC.g;
            float nz = 0.25f * (bL + dL + fL + hL) - eL;
            float range = max3f(max3f(bL, dL, eL), fL, hL) - min3f(min3f(bL, dL, eL), fL, hL);
            nz = saturate(std::fabs(nz) * safeRcp(range));
            nz = 1.0f - 0.5f * nz;

            Vec3 mn4 = vmin(vmin(b, d), vmin(f, hC));
            Vec3 mx4 = vmax(vmax(b, d), vmax(f, hC));
            Vec3 hitMin{mn4.r / std::max(4.0f * mx4.r, 1e-6f),
                        mn4.g / std::max(4.0f * mx4.g, 1e-6f),
                        mn4.b / std::max(4.0f * mx4.b, 1e-6f)};
            Vec3 hitMax{(1.0f - mx4.r) / std::min(4.0f * mn4.r - 4.0f, -1e-6f),
                        (1.0f - mx4.g) / std::min(4.0f * mn4.g - 4.0f, -1e-6f),
                        (1.0f - mx4.b) / std::min(4.0f * mn4.b - 4.0f, -1e-6f)};
            Vec3 lobeRGB{std::max(-hitMin.r, hitMax.r), std::max(-hitMin.g, hitMax.g), std::max(-hitMin.b, hitMax.b)};
            float lobe = std::max(-kRcasLimit, std::min(max3f(lobeRGB.r, lobeRGB.g, lobeRGB.b), 0.0f));
            lobe *= std::exp2(-sharpness);
            lobe *= nz;
            float rcpL = safeRcp(4.0f * lobe + 1.0f);
            Vec3 outC = ((b + d + f + hC) * lobe + e) * rcpL;
            float* o = dst + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
            o[0] = saturate(outC.r);
            o[1] = saturate(outC.g);
            o[2] = saturate(outC.b);
            o[3] = 1.0f;
        }
    }
}

}  // namespace csr_ref
