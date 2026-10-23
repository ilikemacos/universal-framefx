#pragma once

#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

bool ufx_png_load_rgba8(const char* path, int* w, int* h, std::vector<uint8_t>* rgba);
bool ufx_png_save_rgba8(const char* path, int w, int h, const uint8_t* rgba);
void ufx_rgba8_to_f32(const uint8_t* src, int n, std::vector<float>* dst);
void ufx_f32_to_rgba8(const float* src, int n, std::vector<uint8_t>* dst);
void ufx_rgba8_to_rgb8(const uint8_t* src, int n, std::vector<uint8_t>* dst);
double ufx_psnr_rgb8(const uint8_t* a, const uint8_t* b, int n);
double ufx_percentile(std::vector<double> xs, double p);

#ifdef UFX_PNG_IMPLEMENTATION
#define STB_IMAGE_IMPLEMENTATION
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "third_party/stb_image.h"
#include "third_party/stb_image_write.h"

#include <algorithm>

bool ufx_png_load_rgba8(const char* path, int* w, int* h, std::vector<uint8_t>* rgba) {
    int c = 0;
    unsigned char* p = stbi_load(path, w, h, &c, 4);
    if (!p) return false;
    rgba->assign(p, p + static_cast<size_t>(*w) * (*h) * 4);
    stbi_image_free(p);
    return true;
}

bool ufx_png_save_rgba8(const char* path, int w, int h, const uint8_t* rgba) {
    return stbi_write_png(path, w, h, 4, rgba, w * 4) != 0;
}

void ufx_rgba8_to_f32(const uint8_t* src, int n, std::vector<float>* dst) {
    dst->resize(static_cast<size_t>(n) * 4);
    for (int i = 0; i < n * 4; i++) (*dst)[static_cast<size_t>(i)] = src[i] / 255.0f;
}

void ufx_f32_to_rgba8(const float* src, int n, std::vector<uint8_t>* dst) {
    dst->resize(static_cast<size_t>(n) * 4);
    for (int i = 0; i < n * 4; i++) {
        float v = std::min(1.0f, std::max(0.0f, src[i]));
        (*dst)[static_cast<size_t>(i)] = static_cast<uint8_t>(v * 255.0f + 0.5f);
    }
}

void ufx_rgba8_to_rgb8(const uint8_t* src, int n, std::vector<uint8_t>* dst) {
    dst->resize(static_cast<size_t>(n) * 3);
    for (int i = 0; i < n; i++) {
        (*dst)[static_cast<size_t>(i) * 3 + 0] = src[i * 4 + 0];
        (*dst)[static_cast<size_t>(i) * 3 + 1] = src[i * 4 + 1];
        (*dst)[static_cast<size_t>(i) * 3 + 2] = src[i * 4 + 2];
    }
}

double ufx_psnr_rgb8(const uint8_t* a, const uint8_t* b, int n) {
    double s = 0;
    for (int i = 0; i < n; i++) {
        double d = static_cast<double>(a[i]) - static_cast<double>(b[i]);
        s += d * d;
    }
    if (s <= 0) return 99.0;
    return 10.0 * std::log10(255.0 * 255.0 / (s / n));
}

double ufx_percentile(std::vector<double> xs, double p) {
    if (xs.empty()) return 0;
    std::sort(xs.begin(), xs.end());
    if (xs.size() == 1) return xs[0];
    double x = p * static_cast<double>(xs.size() - 1);
    size_t lo = static_cast<size_t>(std::floor(x));
    size_t hi = std::min(lo + 1, xs.size() - 1);
    double f = x - static_cast<double>(lo);
    return xs[lo] * (1.0 - f) + xs[hi] * f;
}
#endif
