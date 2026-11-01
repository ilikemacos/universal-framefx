#include "csr_ai_cpu_ref.h"
#include "gpu/csr_ai_upscale.h"
#include "gpu/vk_context.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

static bool compare(const std::vector<float>& gpu, const std::vector<float>& cpu, double* max_abs, double* mae) {
    *max_abs = 0;
    *mae = 0;
    int n = 0;
    for (size_t i = 0; i < gpu.size(); i++) {
        if ((i % 4) == 3) continue;
        double d = std::fabs(static_cast<double>(gpu[i]) - cpu[i]);
        *max_abs = std::max(*max_abs, d);
        *mae += d;
        ++n;
    }
    *mae /= std::max(1, n);
    return *max_abs < 5e-3 && *mae < 1e-3;
}

int main() {
    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "FAIL vulkan: %s\n", err.c_str());
        return 1;
    }
    CsrAiUpscale s{};
    if (!csr_ai_upscale_create(&ctx, &s, &err)) {
        std::fprintf(stderr, "FAIL pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }
    CsrAiWeights weights{};
    if (!csr_ai_weights_load(s.weights_path, &weights, &err)) {
        std::fprintf(stderr, "FAIL weights: %s\n", err.c_str());
        csr_ai_upscale_destroy(&s);
        vk_context_shutdown(&ctx);
        return 1;
    }
    const int in_w = 16, in_h = 16;
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
    bool pass = true;
    const int dims[2][2] = {{32, 32}, {40, 30}};
    for (int c = 0; c < 2; c++) {
        const int out_w = dims[c][0], out_h = dims[c][1];
        std::vector<float> gpu(out_w * out_h * 4), cpu(out_w * out_h * 4);
        if (!csr_ai_upscale_encode(&s, src.data(), in_w, in_h, out_w, out_h, sharp, gpu.data(), &err)) {
            std::fprintf(stderr, "FAIL gpu: %s\n", err.c_str());
            csr_ai_upscale_destroy(&s);
            vk_context_shutdown(&ctx);
            return 1;
        }
        csr_ai_ref::upscale(weights, src.data(), in_w, in_h, cpu.data(), out_w, out_h, sharp);
        double max_abs = 0, mae = 0;
        bool ok = compare(gpu, cpu, &max_abs, &mae);
        std::printf("csr_ai_numeric_test 16x16 -> %dx%d max_abs=%.6g mae=%.6g\n", out_w, out_h, max_abs, mae);
        pass = pass && ok;
    }
    csr_ai_upscale_destroy(&s);
    vk_context_shutdown(&ctx);
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
