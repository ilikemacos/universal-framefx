#include "csr2_cpu_ref.h"
#include "gpu/csr2_upscale.h"
#include "gpu/vk_context.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

int main() {
    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "FAIL vulkan: %s\n", err.c_str());
        return 1;
    }
    Csr2Upscale s{};
    if (!csr2_upscale_create(&ctx, &s, &err)) {
        std::fprintf(stderr, "FAIL pipeline: %s\n", err.c_str());
        return 1;
    }
    const int in_w = 16, in_h = 16, out_w = 32, out_h = 32;
    const float sharp = 0.25f;
    std::vector<float> src(in_w * in_h * 4);
    for (int y = 0; y < in_h; y++) {
        for (int x = 0; x < in_w; x++) {
            float* p = &src[(y * in_w + x) * 4];
            p[0] = (x % 4 < 2) ? 0.95f : 0.05f;
            p[1] = (y % 4 < 2) ? 0.80f : 0.15f;
            p[2] = (x + y) / 32.0f;
            p[3] = 1.f;
        }
    }
    std::vector<float> gpu(out_w * out_h * 4), mid(out_w * out_h * 4), cpu(out_w * out_h * 4);
    if (!csr2_upscale_spatial_rgba32f(&s, src.data(), in_w, in_h, out_w, out_h, sharp, gpu.data(), &err)) {
        std::fprintf(stderr, "FAIL gpu: %s\n", err.c_str());
        return 1;
    }
    csr2_ref::upsample(src.data(), in_w, in_h, mid.data(), out_w, out_h, 0.f, 0.f);
    csr2_ref::cas(mid.data(), cpu.data(), out_w, out_h, sharp, 1.f);
    double max_abs = 0, mae = 0;
    int n = 0;
    for (size_t i = 0; i < gpu.size(); i++) {
        if ((i % 4) == 3) continue;
        double d = std::fabs(gpu[i] - cpu[i]);
        max_abs = std::max(max_abs, d);
        mae += d;
        ++n;
    }
    mae /= std::max(1, n);
    std::printf("csr2_numeric_test 16x16 -> 32x32 max_abs=%.6g mae=%.6g\n", max_abs, mae);

    csr2_upscale_reset_history(&s);
    std::vector<float> tmp(out_w * out_h * 4);
    csr2_upscale_encode(&s, src.data(), in_w, in_h, out_w, out_h, sharp, true, true, nullptr, 0, 0, tmp.data(), &err);
    uint64_t c0 = csr2_history_checksum(&s);
    csr2_upscale_encode(&s, src.data(), in_w, in_h, out_w, out_h, sharp, true, false, nullptr, 0, 0, tmp.data(), &err);
    uint64_t c1 = csr2_history_checksum(&s);
    std::printf("history checksum real=%llu gen=%llu\n", static_cast<unsigned long long>(c0),
                static_cast<unsigned long long>(c1));
    bool hist_ok = c0 == c1 && c0 != 0;

    csr2_upscale_destroy(&s);
    vk_context_shutdown(&ctx);
    bool pass = max_abs < 5e-3 && mae < 1e-3 && hist_ok;
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}