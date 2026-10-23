#include "gpu/csr_spatial.h"
#include "gpu/csr2_upscale.h"
#include "gpu/fg2_pipeline.h"
#include "gpu/vk_context.h"
#include "csr_cpu_ref.h"
#include "csr2_cpu_ref.h"
#include "fg2_cpu_ref.h"
#include "ufx_version.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

int ufx_run_selftest() {
    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "selftest FAIL: vulkan init: %s\n", err.c_str());
        return 1;
    }
    CsrSpatial csr{};
    if (!csr_spatial_create(&ctx, &csr, &err)) {
        std::fprintf(stderr, "selftest FAIL: pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }

    const uint32_t in_w = 64, in_h = 64, out_w = 96, out_h = 96;
    std::vector<float> src(in_w * in_h * 4), dst(out_w * out_h * 4);
    for (uint32_t y = 0; y < in_h; ++y) {
        for (uint32_t x = 0; x < in_w; ++x) {
            float* p = &src[(y * in_w + x) * 4];
            const bool checker = ((x / 8) + (y / 8)) & 1;
            p[0] = checker ? 0.85f : (x / static_cast<float>(in_w));
            p[1] = checker ? 0.20f : (y / static_cast<float>(in_h));
            p[2] = 0.35f + 0.4f * std::sin(0.2f * static_cast<float>(x + y));
            p[3] = 1.0f;
        }
    }

    const float sharpness = 0.25f;
    if (!csr_spatial_execute(&csr, src.data(), in_w, in_h, out_w, out_h, sharpness, dst.data(), &err)) {
        std::fprintf(stderr, "selftest FAIL: dispatch: %s\n", err.c_str());
        csr_spatial_destroy(&csr);
        vk_context_shutdown(&ctx);
        return 1;
    }

    int nans = 0;
    double checksum = 0.0;
    for (float v : dst) {
        if (!std::isfinite(v)) ++nans;
        checksum += static_cast<double>(v);
    }

    std::vector<float> cpu_easu(out_w * out_h * 4), cpu_rcas(out_w * out_h * 4);
    csr_ref::easu(src.data(), static_cast<int>(in_w), static_cast<int>(in_h), cpu_easu.data(),
                  static_cast<int>(out_w), static_cast<int>(out_h));
    csr_ref::rcas(cpu_easu.data(), cpu_rcas.data(), static_cast<int>(out_w), static_cast<int>(out_h), sharpness);

    double max_abs = 0.0;
    for (size_t i = 0; i < dst.size(); ++i) {
        max_abs = std::max(max_abs, static_cast<double>(std::fabs(dst[i] - cpu_rcas[i])));
    }

    std::printf("Universal-FrameFX %s selftest\n", UFX_VERSION_STRING);
    std::printf("device: %s\n", ctx.device_name.c_str());
    std::printf("fp16: %s (InterpFast/CSR2 upsample+CAS variants compiled; selected at pipeline create)\n",
                ctx.has_fp16 ? "shaderFloat16 enabled" : "fp32 fallback");
    if (ctx.device_name.find("llvmpipe") != std::string::npos || ctx.device_name.find("lavapipe") != std::string::npos) {
        std::printf("NOTE: timings below are CPU-emulated, not representative of a real GPU (Lavapipe/CPU path)\n");
    }
    std::printf("pipeline: 64x64 checker/gradient -> EASU+RCAS 96x96\n");
    std::printf("checksum: %.6f  nan=%d  max_abs_vs_cpu=%.6g\n", checksum, nans, max_abs);

    csr_spatial_destroy(&csr);

    bool csr2_ok = false;
    {
        Csr2Upscale c2{};
        std::string e2;
        if (csr2_upscale_create(&ctx, &c2, &e2)) {
            const uint32_t c2w = 16, c2h = 16, c2ow = 32, c2oh = 32;
            std::vector<float> s2(c2w * c2h * 4), gpu(c2ow * c2oh * 4), mid(c2ow * c2oh * 4), cpu(c2ow * c2oh * 4);
            for (uint32_t y = 0; y < c2h; ++y) {
                for (uint32_t x = 0; x < c2w; ++x) {
                    float* p = &s2[(y * c2w + x) * 4];
                    p[0] = (x % 4 < 2) ? 0.95f : 0.05f;
                    p[1] = (y % 4 < 2) ? 0.80f : 0.15f;
                    p[2] = (x + y) / 32.0f;
                    p[3] = 1.0f;
                }
            }
            if (csr2_upscale_spatial_rgba32f(&c2, s2.data(), c2w, c2h, c2ow, c2oh, sharpness, gpu.data(), &e2)) {
                csr2_ref::upsample(s2.data(), static_cast<int>(c2w), static_cast<int>(c2h), mid.data(),
                                   static_cast<int>(c2ow), static_cast<int>(c2oh), 0.f, 0.f);
                csr2_ref::cas(mid.data(), cpu.data(), static_cast<int>(c2ow), static_cast<int>(c2oh), sharpness, 1.f);
                double m = 0;
                for (size_t i = 0; i < gpu.size(); ++i) {
                    if ((i % 4) == 3) continue;
                    m = std::max(m, static_cast<double>(std::fabs(gpu[i] - cpu[i])));
                }
                std::printf("csr2 spatial max_abs_vs_cpu=%.6g\n", m);
                csr2_ok = m < 5e-3;
                std::vector<float> tmp(c2ow * c2oh * 4);
                csr2_upscale_encode(&c2, s2.data(), c2w, c2h, c2ow, c2oh, sharpness, true, true, nullptr, 0, 0,
                                    tmp.data(), &e2);
                uint64_t h0 = csr2_history_checksum(&c2);
                csr2_upscale_encode(&c2, s2.data(), c2w, c2h, c2ow, c2oh, sharpness, true, false, nullptr, 0, 0,
                                    tmp.data(), &e2);
                uint64_t h1 = csr2_history_checksum(&c2);
                std::printf("csr2 history invariant %s (%llu)\n", h0 == h1 ? "ok" : "FAIL",
                            static_cast<unsigned long long>(h0));
                csr2_ok = csr2_ok && h0 == h1 && h0 != 0;
            } else {
                std::fprintf(stderr, "selftest FAIL: csr2 dispatch: %s\n", e2.c_str());
            }
            csr2_upscale_destroy(&c2);
        } else {
            std::fprintf(stderr, "selftest FAIL: csr2 pipeline: %s\n", e2.c_str());
        }
    }

    bool fg_ok = false;
    {
        Fg2Pipeline fg{};
        std::string e2;
        if (!fg2_pipeline_create(&ctx, &fg, &e2)) {
            std::fprintf(stderr, "selftest FAIL: fg2 pipeline: %s\n", e2.c_str());
        } else {
            const uint32_t fw = 64, fh = 48, shift = 8;
            std::vector<float> a(fw * fh * 4), b(fw * fh * 4);
            for (uint32_t y = 0; y < fh; ++y) {
                for (uint32_t x = 0; x < fw; ++x) {
                    float* p = &a[(y * fw + x) * 4];
                    const bool chk = ((x / 8) + (y / 8)) & 1;
                    p[0] = chk ? 0.9f : 0.1f;
                    p[1] = (x % 13) / 13.0f;
                    p[2] = (y % 11) / 11.0f;
                    p[3] = 1.0f;
                }
            }
            for (uint32_t y = 0; y < fh; ++y) {
                for (uint32_t x = 0; x < fw; ++x) {
                    int sx = static_cast<int>(x) - static_cast<int>(shift);
                    if (sx < 0) sx = 0;
                    const float* s = &a[(y * fw + static_cast<uint32_t>(sx)) * 4];
                    float* d = &b[(y * fw + x) * 4];
                    d[0] = s[0];
                    d[1] = s[1];
                    d[2] = s[2];
                    d[3] = 1.0f;
                }
            }
            if (!fg2_process_real_rgba32f(&fg, a.data(), fw, fh, &e2) ||
                !fg2_process_real_rgba32f(&fg, b.data(), fw, fh, &e2)) {
                std::fprintf(stderr, "selftest FAIL: fg2 process: %s\n", e2.c_str());
            } else {
                std::vector<float> vx, vy, cf;
                if (!fg2_read_mv2(&fg, &vx, &vy, &cf) || vx.empty()) {
                    std::fprintf(stderr, "selftest FAIL: fg2 read mv\n");
                } else {
                    std::vector<float> xs = vx;
                    std::sort(xs.begin(), xs.end());
                    float med = xs[xs.size() / 2];
                    std::printf("fg2 translation: median_vx=%.3f (expect ~-8)\n", med);
                    fg_ok = std::fabs(med + 8.0f) < 3.0f;
                    const int iw = 16, ih = 16, tw = 2, th = 2;
                    std::vector<float> pr(iw * ih * 4), cu(iw * ih * 4), hud(iw * ih, 0.f), mv(tw * th * 4, 0.f);
                    for (int y = 0; y < ih; ++y) {
                        for (int x = 0; x < iw; ++x) {
                            float* p = &pr[(y * iw + x) * 4];
                            p[0] = x / 16.f;
                            p[1] = y / 16.f;
                            p[2] = 0.4f;
                            p[3] = 1.f;
                            int sx = std::min(iw - 1, x + 2);
                            const float* s = &pr[(y * iw + sx) * 4];
                            float* c = &cu[(y * iw + x) * 4];
                            c[0] = s[0];
                            c[1] = s[1];
                            c[2] = s[2];
                            c[3] = 1.f;
                        }
                    }
                    for (int i = 0; i < tw * th; ++i) {
                        mv[i * 4] = -2.f;
                        mv[i * 4 + 2] = 1.f;
                    }
                    for (int y = 0; y < 4; ++y)
                        for (int x = 0; x < 4; ++x) hud[y * iw + x] = 1.f;
                    fg2_pipeline_ensure(&fg, iw, ih, &e2);
                    fg2_upload_mv2(&fg, mv.data(), tw, th);
                    fg2_set_hud_r16(&fg, hud.data(), iw, ih);
                    auto interp_case = [&](float t, float cut, const char* name) {
                        fg2_set_cut_flag(&fg, cut);
                        std::vector<float> gpu(iw * ih * 4), cpu(iw * ih * 4);
                        if (!fg2_generate_fast_rgba32f(&fg, pr.data(), cu.data(), iw, ih, t, gpu.data(), &e2)) return false;
                        fg2_ref::interp_fast(pr.data(), cu.data(), iw, ih, mv.data(), tw, th, hud.data(), t, cut, cpu.data());
                        double m = 0;
                        for (size_t i = 0; i < gpu.size(); ++i) {
                            if ((i % 4) == 3) continue;
                            m = std::max(m, static_cast<double>(std::fabs(gpu[i] - cpu[i])));
                        }
                        std::printf("fg2 interp %s max_abs_vs_cpu=%.6g\n", name, m);
                        return m < 5e-3;
                    };
                    fg_ok = fg_ok && interp_case(0.5f, 0.f, "translate") && interp_case(0.25f, 1.f, "scene-cut");
                }
            }
        }
        fg2_pipeline_destroy(&fg);
    }

    vk_context_shutdown(&ctx);

    const bool pass = nans == 0 && max_abs < 5e-3 && fg_ok && csr2_ok;
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
