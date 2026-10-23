#include "gpu/csr_spatial.h"
#include "gpu/vk_context.h"
#include "csr_cpu_ref.h"

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
    CsrSpatial csr{};
    if (!csr_spatial_create(&ctx, &csr, &err)) {
        std::fprintf(stderr, "FAIL pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }

    const int in_w = 16, in_h = 16, out_w = 32, out_h = 32;
    const float sharpness = 0.25f;
    std::vector<float> src(in_w * in_h * 4);
    for (int y = 0; y < in_h; ++y) {
        for (int x = 0; x < in_w; ++x) {
            float* p = &src[(y * in_w + x) * 4];
            p[0] = (x % 4 < 2) ? 0.95f : 0.05f;
            p[1] = (y % 4 < 2) ? 0.80f : 0.15f;
            p[2] = (x + y) / 32.0f;
            p[3] = 1.0f;
        }
    }

    std::vector<float> gpu(out_w * out_h * 4);
    if (!csr_spatial_execute(&csr, src.data(), in_w, in_h, out_w, out_h, sharpness, gpu.data(), &err)) {
        std::fprintf(stderr, "FAIL gpu: %s\n", err.c_str());
        csr_spatial_destroy(&csr);
        vk_context_shutdown(&ctx);
        return 1;
    }

    std::vector<float> cpu_easu(out_w * out_h * 4), cpu(out_w * out_h * 4);
    csr_ref::easu(src.data(), in_w, in_h, cpu_easu.data(), out_w, out_h);
    csr_ref::rcas(cpu_easu.data(), cpu.data(), out_w, out_h, sharpness);

    double max_abs = 0.0, mae = 0.0;
    int n = 0;
    for (size_t i = 0; i < gpu.size(); ++i) {
        if ((i % 4) == 3) continue;
        double d = std::fabs(gpu[i] - cpu[i]);
        max_abs = std::max(max_abs, d);
        mae += d;
        ++n;
    }
    mae /= std::max(1, n);

    std::printf("easu_rcas_numeric_test 16x16 -> 32x32 sharpness=0.25\n");
    std::printf("device: %s\n", ctx.device_name.c_str());
    std::printf("max_abs=%.6g  mae=%.6g\n", max_abs, mae);

    csr_spatial_destroy(&csr);
    vk_context_shutdown(&ctx);

    const bool pass = max_abs < 5e-3 && mae < 1e-3;
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
