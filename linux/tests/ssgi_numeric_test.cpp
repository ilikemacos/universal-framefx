#include "gpu/ssgi.h"
#include "gpu/vk_context.h"
#include "ssgi_cpu_ref.h"

#include <algorithm>
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
    Ssgi s{};
    if (!ssgi_create(&ctx, &s, &err)) {
        std::fprintf(stderr, "FAIL pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }
    const int w = 64, h = 64;
    const float strength = 1.5f;
    std::vector<float> src(static_cast<size_t>(w) * h * 4);
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            float* p = &src[(static_cast<size_t>(y) * w + x) * 4];
            float glow = std::max(0.f, 1.f - std::sqrt(static_cast<float>((x - 20) * (x - 20) + (y - 18) * (y - 18))) / 14.f);
            p[0] = 0.05f + 0.85f * glow + 0.15f * static_cast<float>(x) / static_cast<float>(w);
            p[1] = 0.06f + 0.75f * glow + 0.10f * static_cast<float>(y) / static_cast<float>(h);
            p[2] = 0.08f + 0.65f * glow + 0.12f * static_cast<float>(x + y) / static_cast<float>(w + h);
            p[3] = 1.f;
        }
    }
    std::vector<float> gpu(static_cast<size_t>(w) * h * 4);
    std::vector<float> cpu(static_cast<size_t>(w) * h * 4);
    if (!ssgi_apply_encode(&s, src.data(), w, h, strength, gpu.data(), &err)) {
        std::fprintf(stderr, "FAIL gpu: %s\n", err.c_str());
        ssgi_destroy(&s);
        vk_context_shutdown(&ctx);
        return 1;
    }
    ssgi_ref::apply(src.data(), w, h, strength, cpu.data());
    double max_abs = 0;
    double mae = 0;
    double delta = 0;
    int n = 0;
    for (size_t i = 0; i < gpu.size(); i++) {
        if ((i % 4) == 3) continue;
        double d = std::fabs(static_cast<double>(gpu[i]) - cpu[i]);
        max_abs = std::max(max_abs, d);
        mae += d;
        delta += std::fabs(static_cast<double>(gpu[i]) - src[i]);
        ++n;
    }
    mae /= std::max(1, n);
    delta /= std::max(1, n);
    std::printf("ssgi_numeric_test 64x64 strength=%.2f max_abs=%.6g mae=%.6g delta=%.6g\n", strength, max_abs, mae, delta);
    ssgi_destroy(&s);
    vk_context_shutdown(&ctx);
    bool pass = max_abs < 5e-3 && mae < 1e-3 && delta > 1e-4;
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
