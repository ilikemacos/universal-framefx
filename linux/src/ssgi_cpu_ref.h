#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace ssgi_ref {

inline constexpr int kGrid = 128;
inline constexpr int kLightDivisor = 4;
inline constexpr float kAoStrength = 0.5f;
inline constexpr float kEmitLo = 0.35f;
inline constexpr float kEmitHi = 0.9f;
inline constexpr float kFalloff = 0.002f;
inline constexpr float kRangeK = 40.0f;
inline constexpr float kHeightScale = 24.0f;
inline constexpr float kStepGrowth = 1.12f;
inline constexpr float kHorizonBias = 0.25f;
inline constexpr float kAoRadius = 6.0f;
inline constexpr float kAoGame = 0.35f;
inline constexpr float kStepCells = 1.5f;
inline constexpr int kMarchSteps = 12;
inline constexpr int kRaysPerTexel = 2;

struct Params {
    float strength = 1.5f;
    float aoStrength = kAoStrength;
    float emitLo = kEmitLo;
    float emitHi = kEmitHi;
    float falloff = kFalloff;
    float rangeK = kRangeK;
    float heightScale = kHeightScale;
    float stepGrowth = kStepGrowth;
    float horizonBias = kHorizonBias;
    float aoRadius = kAoRadius;
    float aoGame = kAoGame;
    float stepCells = kStepCells;
    int marchSteps = kMarchSteps;
    int raysPerTexel = kRaysPerTexel;
    uint32_t frame = 0;
};

inline Params make_params(float strength) {
    Params p;
    p.strength = strength;
    return p;
}

inline float clampf(float x) { return std::min(1.f, std::max(0.f, x)); }
inline float saturatef(float x) { return clampf(x); }
inline float fracf(float x) { return x - std::floor(x); }
inline float luma(float r, float g, float b) { return 0.2126f * r + 0.7152f * g + 0.0722f * b; }
inline float bright(float r, float g, float b) { return std::max(luma(r, g, b), 0.75f * std::max(r, std::max(g, b))); }

inline float smoothstepf(float a, float b, float x) {
    float t = clampf((x - a) / (b - a));
    return t * t * (3.f - 2.f * t);
}

inline float interleave3(int x, int y) {
    return static_cast<float>((x % 3) + 3 * (y % 3)) * (1.f / 9.f);
}

inline float ign(float px, float py, uint32_t frame) {
    px += 5.588238f * static_cast<float>(frame & 63u);
    py += 5.588238f * static_cast<float>(frame & 63u);
    return fracf(52.9829189f * fracf(px * 0.06711056f + py * 0.00583715f));
}

inline const float* cell_at(const std::vector<float>& g, int x, int y) {
    x = std::max(0, std::min(kGrid - 1, x));
    y = std::max(0, std::min(kGrid - 1, y));
    return &g[(static_cast<size_t>(y) * kGrid + x) * 4];
}

inline void sample_bilinear(const float* src, int w, int h, float u, float v, float* rgb) {
    float x = u * static_cast<float>(w) - 0.5f;
    float y = v * static_cast<float>(h) - 0.5f;
    int x0 = static_cast<int>(std::floor(x));
    int y0 = static_cast<int>(std::floor(y));
    float fx = x - static_cast<float>(x0);
    float fy = y - static_cast<float>(y0);
    int x1 = x0 + 1;
    int y1 = y0 + 1;
    x0 = std::max(0, std::min(w - 1, x0));
    x1 = std::max(0, std::min(w - 1, x1));
    y0 = std::max(0, std::min(h - 1, y0));
    y1 = std::max(0, std::min(h - 1, y1));
    const float* p00 = src + (static_cast<size_t>(y0) * w + x0) * 4;
    const float* p10 = src + (static_cast<size_t>(y0) * w + x1) * 4;
    const float* p01 = src + (static_cast<size_t>(y1) * w + x0) * 4;
    const float* p11 = src + (static_cast<size_t>(y1) * w + x1) * 4;
    for (int c = 0; c < 3; c++) {
        float a = p00[c] * (1.f - fx) + p10[c] * fx;
        float b = p01[c] * (1.f - fx) + p11[c] * fx;
        rgb[c] = a * (1.f - fy) + b * fy;
    }
}

inline void downsample(const float* src, int w, int h, std::vector<float>& grid) {
    grid.assign(static_cast<size_t>(kGrid) * kGrid * 4, 0.f);
    float cellPxX = static_cast<float>(w) / static_cast<float>(kGrid);
    float cellPxY = static_cast<float>(h) / static_cast<float>(kGrid);
    for (int y = 0; y < kGrid; y++) {
        for (int x = 0; x < kGrid; x++) {
            float orgX = static_cast<float>(x) * cellPxX;
            float orgY = static_cast<float>(y) * cellPxY;
            float sum[3] = {};
            for (int i = 0; i < 16; i++) {
                float subX = (static_cast<float>(i & 3) + 0.5f) * 0.25f;
                float subY = (static_cast<float>(i >> 2) + 0.5f) * 0.25f;
                float cx = std::floor(orgX + subX * cellPxX + 0.5f);
                float cy = std::floor(orgY + subY * cellPxY + 0.5f);
                cx = std::max(cx, 1.f);
                cy = std::max(cy, 1.f);
                cx = std::min(cx, static_cast<float>(w) - 1.f);
                cy = std::min(cy, static_cast<float>(h) - 1.f);
                float rgb[3];
                sample_bilinear(src, w, h, cx / static_cast<float>(w), cy / static_cast<float>(h), rgb);
                for (int c = 0; c < 3; c++) sum[c] += rgb[c];
            }
            float* o = &grid[(static_cast<size_t>(y) * kGrid + x) * 4];
            for (int c = 0; c < 3; c++) o[c] = sum[c] * (1.f / 16.f);
            o[3] = bright(o[0], o[1], o[2]);
        }
    }
}

inline void march(const std::vector<float>& grid, int srcW, int srcH, int lightW, int lightH, const Params& p,
                  std::vector<float>& light, std::vector<float>& occ) {
    light.assign(static_cast<size_t>(lightW) * lightH * 4, 0.f);
    occ.assign(static_cast<size_t>(lightW) * lightH, 0.f);
    float aspect = static_cast<float>(srcW) / static_cast<float>(srcH);
    float rot = fracf(static_cast<float>(p.frame & 7u) * 0.38196601f);
    int rays = std::max(1, std::min(8, p.raysPerTexel));
    int steps = std::max(8, std::min(32, p.marchSteps));
    for (int y = 0; y < lightH; y++) {
        for (int x = 0; x < lightW; x++) {
            float u = (static_cast<float>(x) + 0.5f) / static_cast<float>(lightW);
            float v = (static_cast<float>(y) + 0.5f) / static_cast<float>(lightH);
            float ccx = u * static_cast<float>(kGrid);
            float ccy = v * static_cast<float>(kGrid);
            const float* centre = cell_at(grid, static_cast<int>(ccx), static_cast<int>(ccy));
            float lc = centre[3];
            float n0 = fracf(interleave3(x, y) + rot);
            float jit = fracf(interleave3(y + 1, x + 1) * 0.875f + 0.0625f + rot * 0.5f);
            float hc = p.heightScale * lc;
            float bounce[3] = {};
            float aoAcc = 0.f;
            for (int r = 0; r < rays; r++) {
                float aj = (ign(static_cast<float>(x) + 17.f * static_cast<float>(r),
                                static_cast<float>(y) + 17.f * static_cast<float>(r), p.frame) -
                            0.5f) *
                           (1.f / 9.f);
                float jr = fracf(jit + 0.61803399f * static_cast<float>(r));
                float ang = 6.2831853f * (n0 + aj + static_cast<float>(r) / static_cast<float>(rays));
                float dirX = std::cos(ang);
                float dirY = std::sin(ang) * aspect;
                float sinH = -p.horizonBias;
                float sinA = 0.f;
                float d = 0.f;
                float len = p.stepCells;
                float rayB[3] = {};
                for (int s = 0; s < steps; s++) {
                    d += len * (s == 0 ? (0.5f + jr) : 1.f);
                    len *= p.stepGrowth;
                    float px = ccx + dirX * d;
                    float py = ccy + dirY * d;
                    if (px < 0.f || py < 0.f || px >= static_cast<float>(kGrid) || py >= static_cast<float>(kGrid)) break;
                    const float* q = cell_at(grid, static_cast<int>(px), static_cast<int>(py));
                    float l = q[3];
                    float dz = p.heightScale * l - hc;
                    float dc = d * p.stepCells;
                    float sinS = dz / std::sqrt(dz * dz + dc * dc);
                    float dzA = std::min(dz, 0.25f * p.heightScale);
                    float sinO = dzA / std::sqrt(dzA * dzA + dc * dc) *
                                 (1.f - smoothstepf(p.emitLo, p.emitHi, l));
                    sinA = std::max(sinA, sinO * clampf(1.f - dc / p.aoRadius));
                    if (sinS > sinH) {
                        float emit = smoothstepf(p.emitLo, p.emitHi, l);
                        float w = (sinS - sinH) * emit / (1.f + p.falloff * dc * dc);
                        for (int c = 0; c < 3; c++) rayB[c] += w * q[c] * q[c];
                        sinH = sinS;
                    }
                }
                for (int c = 0; c < 3; c++) bounce[c] += rayB[c];
                aoAcc += sinA;
            }
            float inv = 1.f / (static_cast<float>(rays) * (1.f + p.horizonBias));
            float self = 1.f - smoothstepf(p.emitLo, p.emitHi, lc);
            float o = clampf(aoAcc / static_cast<float>(rays)) * self;
            float ao = 1.f - p.aoStrength * o;
            float* lo = &light[(static_cast<size_t>(y) * lightW + x) * 4];
            for (int c = 0; c < 3; c++) lo[c] = std::sqrt(clampf(bounce[c] * inv * self * ao));
            lo[3] = lc;
            occ[static_cast<size_t>(y) * lightW + x] = o;
        }
    }
}

inline void filter(const std::vector<float>& lightIn, const std::vector<float>& occIn, int lightW, int lightH, float rangeK,
                   std::vector<float>& lightOut, std::vector<float>& occOut) {
    lightOut.assign(static_cast<size_t>(lightW) * lightH * 4, 0.f);
    occOut.assign(static_cast<size_t>(lightW) * lightH, 0.f);
    for (int y = 0; y < lightH; y++) {
        for (int x = 0; x < lightW; x++) {
            const float* c = &lightIn[(static_cast<size_t>(y) * lightW + x) * 4];
            float cg = c[3];
            float acc[3] = {};
            float occAcc = 0.f;
            float ws = 0.f;
            for (int k = 0; k < 9; k++) {
                int ox = k % 3 - 1;
                int oy = k / 3 - 1;
                int sx = std::max(0, std::min(lightW - 1, x + ox));
                int sy = std::max(0, std::min(lightH - 1, y + oy));
                const float* s = &lightIn[(static_cast<size_t>(sy) * lightW + sx) * 4];
                float dl = s[3] - cg;
                float w = static_cast<float>((2 - std::abs(ox)) * (2 - std::abs(oy))) * std::exp(-rangeK * dl * dl);
                for (int ch = 0; ch < 3; ch++) acc[ch] += w * s[ch] * s[ch];
                occAcc += w * occIn[static_cast<size_t>(sy) * lightW + sx];
                ws += w;
            }
            float* o = &lightOut[(static_cast<size_t>(y) * lightW + x) * 4];
            for (int ch = 0; ch < 3; ch++) o[ch] = std::sqrt(acc[ch] / ws);
            o[3] = cg;
            occOut[static_cast<size_t>(y) * lightW + x] = occAcc / ws;
        }
    }
}

inline void composite(const float* game, int w, int h, const std::vector<float>& light, const std::vector<float>& occ,
                      int lightW, int lightH, const Params& p, float* dst) {
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            const float* gsrc = game + (static_cast<size_t>(y) * w + x) * 4;
            float bg = bright(gsrc[0], gsrc[1], gsrc[2]);
            float lpx = (static_cast<float>(x) + 0.5f) * static_cast<float>(lightW) / static_cast<float>(w) - 0.5f;
            float lpy = (static_cast<float>(y) + 0.5f) * static_cast<float>(lightH) / static_cast<float>(h) - 0.5f;
            int b0x = static_cast<int>(std::floor(lpx));
            int b0y = static_cast<int>(std::floor(lpy));
            float fx = lpx - static_cast<float>(b0x);
            float fy = lpy - static_cast<float>(b0y);
            float acc[3] = {};
            float occAcc = 0.f;
            float wsum = 0.f;
            for (int k = 0; k < 4; k++) {
                int ox = k & 1;
                int oy = k >> 1;
                int sx = std::max(0, std::min(lightW - 1, b0x + ox));
                int sy = std::max(0, std::min(lightH - 1, b0y + oy));
                float wb = (ox ? fx : 1.f - fx) * (oy ? fy : 1.f - fy);
                const float* s = &light[(static_cast<size_t>(sy) * lightW + sx) * 4];
                float dl = bg - s[3];
                float w = wb * std::exp(-p.rangeK * dl * dl);
                for (int ch = 0; ch < 3; ch++) acc[ch] += w * s[ch] * s[ch];
                occAcc += w * occ[static_cast<size_t>(sy) * lightW + sx];
                wsum += w;
            }
            float denom = std::max(wsum, 0.25f);
            float g[3] = {gsrc[0] * gsrc[0], gsrc[1] * gsrc[1], gsrc[2] * gsrc[2]};
            float lg = luma(g[0], g[1], g[2]);
            float inv = 1.f / std::max(lg, 0.02f);
            float albedo[3];
            for (int ch = 0; ch < 3; ch++) albedo[ch] = 0.5f * std::max(0.f, std::min(2.f, g[ch] * inv));
            float aoGame = 1.f - p.aoGame * (occAcc / denom);
            float* o = dst + (static_cast<size_t>(y) * w + x) * 4;
            for (int ch = 0; ch < 3; ch++) {
                float lit = (acc[ch] / denom) * albedo[ch] * p.strength;
                o[ch] = std::sqrt(clampf(g[ch] * aoGame + lit));
            }
            o[3] = 1.f;
        }
    }
}

inline void apply(const float* src, int w, int h, float strength, float* dst) {
    Params p = make_params(strength);
    int lightW = std::max(kGrid, w / kLightDivisor);
    int lightH = std::max(kGrid, h / kLightDivisor);
    std::vector<float> grid, lightA, lightB, occA, occB;
    downsample(src, w, h, grid);
    march(grid, w, h, lightW, lightH, p, lightA, occA);
    filter(lightA, occA, lightW, lightH, p.rangeK, lightB, occB);
    composite(src, w, h, lightB, occB, lightW, lightH, p, dst);
}

}  // namespace ssgi_ref
