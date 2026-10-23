#define UFX_PNG_IMPLEMENTATION
#include "offline_fg.h"
#include "gpu/fg2_pipeline.h"
#include "gpu/vk_context.h"
#include "png_io.h"
#include "ufx_version.h"

#include <algorithm>
#include <chrono>
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

int ufx_run_offline_fg(int argc, char** argv) {
    if (argc < 4) {
        std::fprintf(stderr, "Usage: ufx-linux --offline-fg <in_dir> <out_dir> [--fgx N] [--fast] [--score] [--save-every K]\n");
        return 2;
    }
    std::string in_dir = argv[2];
    std::string out_dir = argv[3];
    int n = 2;
    bool score = false;
    int save_every = 1;
    for (int i = 4; i < argc; i++) {
        if (std::strcmp(argv[i], "--fgx") == 0 && i + 1 < argc) {
            n = std::atoi(argv[++i]);
            if (n < 2) n = 2;
        } else if (std::strcmp(argv[i], "--score") == 0)
            score = true;
        else if (std::strcmp(argv[i], "--save-every") == 0 && i + 1 < argc)
            save_every = std::max(0, std::atoi(argv[++i]));
        else if (std::strcmp(argv[i], "--fast") == 0) {
        }
    }
    std::filesystem::create_directories(out_dir);
    std::vector<std::filesystem::path> files;
    for (auto& e : std::filesystem::directory_iterator(in_dir)) {
        if (e.is_regular_file() && is_png(e.path())) files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());
    if (static_cast<int>(files.size()) < n + 1) {
        std::fprintf(stderr, "offline-fg: need more than %d PNGs (got %zu)\n", n, files.size());
        return 2;
    }

    std::string err;
    VkContext ctx{};
    if (!vk_context_init_headless(&ctx, &err)) {
        std::fprintf(stderr, "offline-fg: vulkan: %s\n", err.c_str());
        return 1;
    }
    std::printf("offline-fg in=%s out=%s fgx=%d frames=%zu score=%d\n", in_dir.c_str(), out_dir.c_str(), n, files.size(),
                score ? 1 : 0);
    std::printf("gpu: %s\n", ctx.device_name.c_str());
    if (ctx.device_name.find("llvmpipe") != std::string::npos || ctx.device_name.find("lavapipe") != std::string::npos) {
        std::printf("NOTE: timings below are CPU-emulated, not representative of a real GPU (Lavapipe/CPU path)\n");
    }

    Fg2Pipeline fg{};
    if (!fg2_pipeline_create(&ctx, &fg, &err)) {
        std::fprintf(stderr, "offline-fg: pipeline: %s\n", err.c_str());
        vk_context_shutdown(&ctx);
        return 1;
    }

    std::ofstream timing(out_dir + "/timing.csv");
    timing << "index,kind,content_time_s,gpu_ms,filename\n";
    std::ofstream scsv;
    if (score) {
        scsv.open(out_dir + "/score.csv");
        scsv << "index,k,t,psnr,gpu_ms,psnr_repeat\n";
    }
    std::vector<double> psnrs, psnr_rep, gpu_ms;
    std::vector<float> prev_f;
    int prev_i = -1;
    int w = 0, h = 0;
    int gen_seq = 0;
    int warmup = 1;

    for (int i = 0; i < static_cast<int>(files.size()); i += n) {
        int iw = 0, ih = 0;
        std::vector<uint8_t> rgba;
        if (!ufx_png_load_rgba8(files[static_cast<size_t>(i)].c_str(), &iw, &ih, &rgba)) {
            std::fprintf(stderr, "offline-fg: load failed %s\n", files[static_cast<size_t>(i)].c_str());
            return 1;
        }
        w = iw;
        h = ih;
        std::vector<float> cur_f;
        ufx_rgba8_to_f32(rgba.data(), w * h, &cur_f);
        auto t0 = std::chrono::steady_clock::now();
        if (!fg2_process_real_rgba32f(&fg, cur_f.data(), static_cast<uint32_t>(w), static_cast<uint32_t>(h), &err)) {
            std::fprintf(stderr, "offline-fg: process: %s\n", err.c_str());
            return 1;
        }
        auto t1 = std::chrono::steady_clock::now();
        double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        std::string name = files[static_cast<size_t>(i)].filename().string();
        timing << i << ",real," << (i / 30.0) << "," << ms << "," << name << "\n";

        if (prev_i >= 0) {
            for (int k = 1; k < n; k++) {
                int gi = prev_i + k;
                if (gi >= static_cast<int>(files.size())) break;
                float t = static_cast<float>(k) / static_cast<float>(n);
                std::vector<float> gen(static_cast<size_t>(w) * h * 4);
                auto g0 = std::chrono::steady_clock::now();
                if (!fg2_generate_fast_rgba32f(&fg, prev_f.data(), cur_f.data(), static_cast<uint32_t>(w),
                                               static_cast<uint32_t>(h), t, gen.data(), &err)) {
                    std::fprintf(stderr, "offline-fg: generate: %s\n", err.c_str());
                    return 1;
                }
                auto g1 = std::chrono::steady_clock::now();
                double gms = std::chrono::duration<double, std::milli>(g1 - g0).count();
                std::vector<uint8_t> out8;
                ufx_f32_to_rgba8(gen.data(), w * h, &out8);
                std::string gname = files[static_cast<size_t>(gi)].filename().string();
                bool record = i / n >= warmup;
                if (record && save_every > 0 && (gen_seq % save_every) == 0) {
                    ufx_png_save_rgba8((out_dir + "/" + gname).c_str(), w, h, out8.data());
                }
                if (record) gen_seq++;
                if (record) timing << gi << ",generated," << (gi / 30.0) << "," << gms << "," << gname << "\n";
                if (score) {
                    int gw = 0, gh = 0;
                    std::vector<uint8_t> gt;
                    ufx_png_load_rgba8(files[static_cast<size_t>(gi)].c_str(), &gw, &gh, &gt);
                    std::vector<uint8_t> a, b, rep;
                    ufx_rgba8_to_rgb8(out8.data(), w * h, &a);
                    ufx_rgba8_to_rgb8(gt.data(), w * h, &b);
                    std::vector<uint8_t> prev8;
                    ufx_f32_to_rgba8(prev_f.data(), w * h, &prev8);
                    ufx_rgba8_to_rgb8(prev8.data(), w * h, &rep);
                    double p = ufx_psnr_rgb8(a.data(), b.data(), w * h * 3);
                    double pr = ufx_psnr_rgb8(rep.data(), b.data(), w * h * 3);
                    if (record) {
                        psnrs.push_back(p);
                        psnr_rep.push_back(pr);
                        gpu_ms.push_back(gms);
                        scsv << gi << "," << k << "," << t << "," << p << "," << gms << "," << pr << "\n";
                    }
                }
            }
        }
        prev_f = std::move(cur_f);
        prev_i = i;
    }

    std::ofstream sum(out_dir + "/summary.txt");
    sum << "mode=classic fgx=" << n << "\n";
    sum << "estimator=pyramid\n";
    sum << "version=" << UFX_VERSION_STRING << "\n";
    if (score && !psnrs.empty()) {
        double mean = 0, meanr = 0;
        for (double v : psnrs) mean += v;
        for (double v : psnr_rep) meanr += v;
        mean /= static_cast<double>(psnrs.size());
        meanr /= static_cast<double>(psnr_rep.size());
        sum << "mean_psnr=" << mean << "\n";
        sum << "mean_psnr_repeat=" << meanr << "\n";
        if (!gpu_ms.empty()) {
            sum << "median_gpu_ms=" << ufx_percentile(gpu_ms, 0.5) << "\n";
            sum << "p95_gpu_ms=" << ufx_percentile(gpu_ms, 0.95) << "\n";
        }
        std::printf("offline-fg mean_psnr=%.4f mean_psnr_repeat=%.4f\n", mean, meanr);
    }
    sum.close();
    std::printf("offline-fg: wrote %s/summary.txt\n", out_dir.c_str());
    fg2_pipeline_destroy(&fg);
    vk_context_shutdown(&ctx);
    return 0;
}