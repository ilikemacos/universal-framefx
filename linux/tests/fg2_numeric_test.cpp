#include "fg2_cpu_ref.h"
#include "gpu/fg2_pipeline.h"
#include "gpu/vk_context.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

static double max_abs_rgb(const float* a, const float* b, size_t n) {
    double m = 0;
    for (size_t i = 0; i < n; i++) {
        if ((i % 4) == 3) continue;
        m = std::max(m, static_cast<double>(std::fabs(a[i] - b[i])));
    }
    return m;
}

int main() {
    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "FAIL vulkan: %s\n", err.c_str());
        return 1;
    }
    Fg2Pipeline fg{};
    if (!fg2_pipeline_create(&ctx, &fg, &err)) {
        std::fprintf(stderr, "FAIL pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }
    const int w = 16, h = 16, tw = 2, th = 2;
    std::vector<float> prev(w * h * 4), curr(w * h * 4), hud(w * h, 0.f), mv(tw * th * 4, 0.f);
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            float* p = &prev[(y * w + x) * 4];
            float* c = &curr[(y * w + x) * 4];
            p[0] = x / 16.f;
            p[1] = y / 16.f;
            p[2] = 0.25f;
            p[3] = 1.f;
            int sx = std::min(w - 1, x + 2);
            const float* s = &prev[(y * w + sx) * 4];
            c[0] = s[0];
            c[1] = s[1];
            c[2] = s[2];
            c[3] = 1.f;
        }
    }
    for (int i = 0; i < tw * th; i++) {
        mv[i * 4 + 0] = -2.f;
        mv[i * 4 + 1] = 0.f;
        mv[i * 4 + 2] = 1.f;
    }
    for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++) hud[y * w + x] = 1.f;

    if (!fg2_pipeline_ensure(&fg, w, h, &err) || !fg2_upload_mv2(&fg, mv.data(), tw, th) ||
        !fg2_set_hud_r16(&fg, hud.data(), w, h)) {
        std::fprintf(stderr, "FAIL setup: %s\n", err.c_str());
        return 1;
    }

    auto run = [&](float t, float cut, const char* name) -> bool {
        fg2_set_cut_flag(&fg, cut);
        std::vector<float> gpu(w * h * 4), cpu(w * h * 4);
        if (!fg2_generate_fast_rgba32f(&fg, prev.data(), curr.data(), w, h, t, gpu.data(), &err)) {
            std::fprintf(stderr, "FAIL gpu %s: %s\n", name, err.c_str());
            return false;
        }
        fg2_ref::interp_fast(prev.data(), curr.data(), w, h, mv.data(), tw, th, hud.data(), t, cut, cpu.data());
        double m = max_abs_rgb(gpu.data(), cpu.data(), gpu.size());
        std::printf("%s max_abs=%.6g\n", name, m);
        return m < 5e-3;
    };

    bool ok = true;
    ok = run(0.5f, 0.f, "translate") && ok;
    ok = run(0.5f, 0.f, "hud-region") && ok;
    ok = run(0.25f, 1.f, "scene-cut") && ok;

    fg2_pipeline_destroy(&fg);
    vk_context_shutdown(&ctx);
    std::printf("%s\n", ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}