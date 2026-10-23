#include "offline_upscale.h"
#include "csr2_cpu_ref.h"
#include "gpu/csr2_upscale.h"
#include "gpu/csr_spatial.h"
#include "gpu/vk_context.h"
#include "png_io.h"
#include "ufx_version.h"

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

static bool is_png(const std::filesystem::path& p) {
    std::string e = p.extension().string();
    for (char& c : e) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return e == ".png";
}

static void box_down2(const float* src, int w, int h, std::vector<float>* dst, int* ow, int* oh) {
    *ow = std::max(1, w / 2);
    *oh = std::max(1, h / 2);
    dst->assign(static_cast<size_t>(*ow) * (*oh) * 4, 0.f);
    for (int y = 0; y < *oh; y++) {
        for (int x = 0; x < *ow; x++) {
            float acc[4] = {};
            int n = 0;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++) {
                    int sx = std::min(w - 1, x * 2 + dx);
                    int sy = std::min(h - 1, y * 2 + dy);
                    const float* p = src + (static_cast<size_t>(sy) * w + sx) * 4;
                    for (int k = 0; k < 4; k++) acc[k] += p[k];
                    n++;
                }
            float* o = dst->data() + (static_cast<size_t>(y) * (*ow) + x) * 4;
            for (int k = 0; k < 4; k++) o[k] = acc[k] / static_cast<float>(n);
        }
    }
}

static void bilinear_up(const float* src, int iw, int ih, float* dst, int ow, int oh) {
    for (int y = 0; y < oh; y++) {
        for (int x = 0; x < ow; x++) {
            float u = (x + 0.5f) * iw / static_cast<float>(ow) - 0.5f;
            float v = (y + 0.5f) * ih / static_cast<float>(oh) - 0.5f;
            int x0 = std::max(0, std::min(iw - 1, static_cast<int>(std::floor(u))));
            int y0 = std::max(0, std::min(ih - 1, static_cast<int>(std::floor(v))));
            int x1 = std::min(iw - 1, x0 + 1);
            int y1 = std::min(ih - 1, y0 + 1);
            float fx = u - std::floor(u);
            float fy = v - std::floor(v);
            auto at = [&](int xx, int yy) { return src + (static_cast<size_t>(yy) * iw + xx) * 4; };
            float* o = dst + (static_cast<size_t>(y) * ow + x) * 4;
            for (int k = 0; k < 3; k++) {
                float a = at(x0, y0)[k] * (1 - fx) + at(x1, y0)[k] * fx;
                float b = at(x0, y1)[k] * (1 - fx) + at(x1, y1)[k] * fx;
                o[k] = a * (1 - fy) + b * fy;
            }
            o[3] = 1.f;
        }
    }
}

int ufx_run_offline_upscale(int argc, char** argv) {
    if (argc < 4) {
        std::fprintf(stderr, "Usage: ufx-linux --offline-upscale <in_dir> <out_dir> [--mode csr1|csr2|bilinear] [--temporal] [--sharpness S]\n");
        return 2;
    }
    std::string in_dir = argv[2], out_dir = argv[3], mode = "csr2";
    bool temporal = false;
    float sharp = 0.25f;
    for (int i = 4; i < argc; i++) {
        if (std::strcmp(argv[i], "--mode") == 0 && i + 1 < argc) mode = argv[++i];
        else if (std::strcmp(argv[i], "--temporal") == 0)
            temporal = true;
        else if (std::strcmp(argv[i], "--sharpness") == 0 && i + 1 < argc)
            sharp = std::strtof(argv[++i], nullptr);
    }
    std::filesystem::create_directories(out_dir);
    std::vector<std::filesystem::path> files;
    for (auto& e : std::filesystem::directory_iterator(in_dir)) {
        if (e.is_regular_file() && is_png(e.path())) files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());
    if (files.empty()) {
        std::fprintf(stderr, "offline-upscale: no PNGs\n");
        return 2;
    }

    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "offline-upscale: %s\n", err.c_str());
        return 1;
    }
    std::printf("offline-upscale in=%s out=%s mode=%s temporal=%d frames=%zu\n", in_dir.c_str(), out_dir.c_str(),
                mode.c_str(), temporal ? 1 : 0, files.size());
    std::printf("gpu: %s\n", ctx.device_name.c_str());
    if (ctx.device_name.find("llvmpipe") != std::string::npos || ctx.device_name.find("lavapipe") != std::string::npos) {
        std::printf("NOTE: timings below are CPU-emulated, not representative of a real GPU (Lavapipe/CPU path)\n");
    }

    Csr2Upscale csr2{};
    CsrSpatial csr1{};
    bool need2 = mode == "csr2";
    bool need1 = mode == "csr1";
    if (need2 && !csr2_upscale_create(&ctx, &csr2, &err)) {
        std::fprintf(stderr, "offline-upscale csr2: %s\n", err.c_str());
        return 1;
    }
    if (need1 && !csr_spatial_create(&ctx, &csr1, &err)) {
        std::fprintf(stderr, "offline-upscale csr1: %s\n", err.c_str());
        return 1;
    }

    std::ofstream csv(out_dir + "/timing.csv");
    csv << "index,gpu_ms,psnr,filename\n";
    std::vector<double> psnrs;
    uint64_t hist0 = 0, hist1 = 0;
    bool hist_checked = false;
    for (size_t i = 0; i < files.size(); i++) {
        int w = 0, h = 0;
        std::vector<uint8_t> rgba;
        ufx_png_load_rgba8(files[i].c_str(), &w, &h, &rgba);
        std::vector<float> native, low, outf;
        ufx_rgba8_to_f32(rgba.data(), w * h, &native);
        int lw = 0, lh = 0;
        box_down2(native.data(), w, h, &low, &lw, &lh);
        outf.resize(static_cast<size_t>(w) * h * 4);
        if (mode == "bilinear") {
            bilinear_up(low.data(), lw, lh, outf.data(), w, h);
        } else if (mode == "csr1") {
            if (!csr_spatial_execute(&csr1, low.data(), lw, lh, w, h, sharp, outf.data(), &err)) {
                std::fprintf(stderr, "offline-upscale csr1 exec: %s\n", err.c_str());
                return 1;
            }
        } else {
            bool feed = true;
            if (temporal && i > 0 && (i % 2) == 1) feed = false;
            if (!csr2_upscale_encode(&csr2, low.data(), lw, lh, w, h, sharp, temporal, feed, nullptr, 0, 0, outf.data(),
                                     &err)) {
                std::fprintf(stderr, "offline-upscale csr2 exec: %s\n", err.c_str());
                return 1;
            }
            if (temporal && i == 1) {
                hist0 = csr2_history_checksum(&csr2);
            }
            if (temporal && i == 2) {
                hist1 = csr2_history_checksum(&csr2);
                hist_checked = true;
            }
        }
        std::vector<uint8_t> out8, a, b;
        ufx_f32_to_rgba8(outf.data(), w * h, &out8);
        ufx_png_save_rgba8((out_dir + "/" + files[i].filename().string()).c_str(), w, h, out8.data());
        ufx_rgba8_to_rgb8(out8.data(), w * h, &a);
        ufx_rgba8_to_rgb8(rgba.data(), w * h, &b);
        double p = ufx_psnr_rgb8(a.data(), b.data(), w * h * 3);
        psnrs.push_back(p);
        csv << i << ",," << p << "," << files[i].filename().string() << "\n";
    }
    double mean = 0;
    for (double v : psnrs) mean += v;
    mean /= static_cast<double>(psnrs.size());
    std::ofstream sum(out_dir + "/summary.txt");
    sum << "mode=" << mode << "\n";
    sum << "frames=" << files.size() << "\n";
    sum << "mean_psnr=" << mean << "\n";
    if (hist_checked) sum << "history_invariant=" << (hist0 == hist1 ? 1 : 0) << "\n";
    sum.close();
    std::printf("offline-upscale mean_psnr=%.4f\n", mean);
    if (need2) csr2_upscale_destroy(&csr2);
    if (need1) csr_spatial_destroy(&csr1);
    vk_context_shutdown(&ctx);
    return 0;
}