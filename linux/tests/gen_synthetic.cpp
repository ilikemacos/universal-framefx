#define UFX_PNG_IMPLEMENTATION
#include "png_io.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

static float hash21(int x, int y) {
    uint32_t n = static_cast<uint32_t>(x) * 374761393u + static_cast<uint32_t>(y) * 668265263u;
    n = (n ^ (n >> 13)) * 1274126177u;
    return (n & 0xffffu) / 65535.0f;
}

int main(int argc, char** argv) {
    std::string out = argc > 1 ? argv[1] : "synthetic";
    int w = 160, h = 96, n = 24;
    std::filesystem::create_directories(out);
    std::ofstream csv(out + "/frames.csv");
    csv << "index,time_s,filename\n";
    const int cut_at = n / 2;
    for (int i = 0; i < n; i++) {
        std::vector<uint8_t> px(static_cast<size_t>(w) * h * 4);
        int bgx = i * 2;
        int fgx = i * 5;
        bool cut = i >= cut_at;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int sx = (x + bgx) % 64;
                int sy = y % 64;
                float t = hash21(sx, sy);
                float r = cut ? 0.15f + 0.7f * t : 0.2f + 0.5f * t;
                float g = cut ? 0.55f * (1.f - t) : 0.35f + 0.4f * hash21(sx + 3, sy);
                float b = cut ? 0.8f * hash21(sx, sy + 7) : 0.25f + 0.3f * hash21(sx + 9, sy + 2);
                int fx0 = 40 + (fgx % 80);
                int fy0 = 28;
                if (x >= fx0 && x < fx0 + 36 && y >= fy0 && y < fy0 + 28) {
                    r = 0.9f;
                    g = 0.2f + 0.1f * hash21(x - fx0, y);
                    b = 0.15f;
                }
                if (x >= 4 && x < 52 && y >= 4 && y < 18) {
                    r = 0.05f;
                    g = 0.95f;
                    b = 0.2f;
                    if (((x / 4) + (y / 4)) & 1) {
                        r = 0.95f;
                        g = 0.95f;
                        b = 0.1f;
                    }
                }
                size_t o = (static_cast<size_t>(y) * w + x) * 4;
                px[o] = static_cast<uint8_t>(r * 255.f);
                px[o + 1] = static_cast<uint8_t>(g * 255.f);
                px[o + 2] = static_cast<uint8_t>(b * 255.f);
                px[o + 3] = 255;
            }
        }
        char name[64];
        std::snprintf(name, sizeof(name), "frame_%04d.png", i);
        std::string path = out + "/" + name;
        if (!ufx_png_save_rgba8(path.c_str(), w, h, px.data())) {
            std::fprintf(stderr, "gen_synthetic: write failed %s\n", path.c_str());
            return 1;
        }
        csv << i << "," << (i / 30.0) << "," << name << "\n";
    }
    std::printf("gen_synthetic wrote %d frames %dx%d to %s (cut at %d)\n", n, w, h, out.c_str(), cut_at);
    return 0;
}
