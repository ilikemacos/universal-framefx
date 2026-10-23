// Native test for the SSGI Phase 2 kernel + worker (no D3D needed):
//   1. scalar / SSE2 / AVX2 give bit-identical masks and stats on random and structured grids;
//   2. the C++ mask equals the Python harness's mask for the harness grid (grid.bin / mask_py.bin), if given;
//   3. kernel timing (bounded work: 16384 cells);
//   4. SsgiWorker end-to-end: a producer thread publishes grids like the render thread, the consumer polls masks,
//      checks frame ids are monotonic and each mask matches a direct BuildImportance of that grid. Build with
//      -fsanitize=thread to check the triple buffers for data races.
// Usage: importance_test [grid.bin mask_py.bin]
#include <chrono>
#include <cstdio>
#include <cstring>
#include <memory>
#include <random>
#include <vector>

#include "../src/ssgi_importance.h"
#include "../src/ssgi_worker.h"

using namespace ufx::ssgi;

static int fails = 0;
#define CHECK(c, ...) do { if (!(c)) { std::printf("FAIL: " __VA_ARGS__); std::printf("\n"); ++fails; } } while (0)

static void MakeGrid(std::vector<uint32_t>& g, int kind, uint32_t seed) {
    std::mt19937 rng(seed);
    for (int y = 0; y < kGrid; ++y)
        for (int x = 0; x < kGrid; ++x) {
            uint32_t l;
            if (kind == 0) l = rng() & 0xFF;                                          // noise
            else if (kind == 1) l = ((x / 9 + y / 7) & 1) ? 230 : 30;                   // checker: edges + emitters
            else l = (uint32_t)std::min(255, (x * 2 + y) / 2 + (int)(rng() % 9));      // gradient, few edges
            if (kind == 3) l = (x > 100 && y < 20) ? 255 : 20;                          // corner emitter (border cases)
            g[y * kGrid + x] = (rng() & 0xFFFFFF) | (l << 24);
        }
}

int main(int argc, char** argv) {
    auto scratch = std::make_unique<ImportanceScratch>();
    std::vector<uint32_t> grid(kCells), m0(kMaskWords), m1(kMaskWords), m2(kMaskWords);
    SimdLevel best = DetectSimd();
    std::printf("CPU SIMD level: %s\n", SimdName(best));
    ImportanceParams P;
    for (int kind = 0; kind < 4; ++kind)
        for (uint32_t seed = 1; seed <= 20; ++seed)
            for (int em = 0; em < 3; ++em) {
                MakeGrid(grid, kind, seed * 7 + kind);
                P.edgeMode = (EdgeMode)em; P.edge = (uint8_t)(8 + seed * 3);
                ImportanceStats a = BuildImportance(grid.data(), *scratch, m0.data(), P, SimdLevel::Scalar);
                ImportanceStats b = BuildImportance(grid.data(), *scratch, m1.data(), P, SimdLevel::SSE2);
                CHECK(m0 == m1 && a.emitCells == b.emitCells && a.edgeCells == b.edgeCells && a.setCells == b.setCells && a.meanLuma == b.meanLuma,
                      "SSE2 != scalar (kind %d seed %u mode %d)", kind, seed, em);
                if (best == SimdLevel::AVX2) {
                    ImportanceStats c = BuildImportance(grid.data(), *scratch, m2.data(), P, SimdLevel::AVX2);
                    CHECK(m0 == m2 && a.emitCells == c.emitCells && a.edgeCells == c.edgeCells && a.setCells == c.setCells,
                          "AVX2 != scalar (kind %d seed %u mode %d)", kind, seed, em);
                }
            }
    std::printf("[1] scalar/SSE2%s bit-identical on 240 grids: %s\n", best == SimdLevel::AVX2 ? "/AVX2" : "", fails ? "NO" : "yes");

    if (argc >= 3) {
        FILE* f = std::fopen(argv[1], "rb"); FILE* fm = std::fopen(argv[2], "rb");
        std::vector<uint32_t> py(kMaskWords);
        bool ok = f && fm && std::fread(grid.data(), 4, kCells, f) == (size_t)kCells && std::fread(py.data(), 4, kMaskWords, fm) == (size_t)kMaskWords;
        if (f) std::fclose(f);
        if (fm) std::fclose(fm);
        CHECK(ok, "could not read %s / %s", argv[1], argv[2]);
        if (ok) {
            ImportanceStats s = BuildImportance(grid.data(), *scratch, m0.data(), ImportanceParams{}, best);
            CHECK(m0 == py, "C++ mask != Python harness mask");
            std::printf("[2] harness grid: mean %u, emit threshold %u, emit %u, edge %u, final %u/16384 cells; matches Python: %s\n",
                        s.meanLuma, s.emitThreshold, s.emitCells, s.edgeCells, s.setCells, m0 == py ? "yes" : "NO");
        }
    }

    for (SimdLevel l : { SimdLevel::Scalar, SimdLevel::SSE2, SimdLevel::AVX2 }) {
        if (l == SimdLevel::AVX2 && best != SimdLevel::AVX2) continue;
        const int N = 2000;
        auto t0 = std::chrono::steady_clock::now();
        for (int i = 0; i < N; ++i) BuildImportance(grid.data(), *scratch, m0.data(), ImportanceParams{}, l);
        double us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count() / N;
        std::printf("[3] BuildImportance %-6s %.1f us per frame\n", SimdName(l), us);
    }

    {   // [4] worker end-to-end
        auto worker = std::make_unique<SsgiWorker>();
        std::vector<std::vector<uint32_t>> grids(8, std::vector<uint32_t>(kCells));
        for (int i = 0; i < 8; ++i) MakeGrid(grids[i], i & 3, 100 + i);
        uint64_t lastId = 0; int got = 0, mismatched = 0;
        const int frames = 3000;
        for (int f = 1; f <= frames; ++f) {
            GridFrame& g = worker->BeginGridWrite();
            std::memcpy(g.cells, grids[f & 7].data(), kCells * 4);
            worker->PublishGrid((uint64_t)f);
            const MaskFrame* mf = nullptr;
            if (worker->TryAcquireMask(mf)) {
                CHECK(mf->frameId > lastId, "frame id went backwards (%llu after %llu)", (unsigned long long)mf->frameId, (unsigned long long)lastId);
                lastId = mf->frameId; ++got;
                BuildImportance(grids[mf->frameId & 7].data(), *scratch, m1.data(), ImportanceParams{}, SimdLevel::Scalar);
                if (std::memcmp(m1.data(), mf->words, kMaskWords * 4) != 0) ++mismatched;
            }
            if ((f & 63) == 0) std::this_thread::sleep_for(std::chrono::microseconds(200));
        }
        auto t0 = std::chrono::steady_clock::now();
        while (worker->ProcessedFrames() == 0 && std::chrono::steady_clock::now() - t0 < std::chrono::seconds(2)) std::this_thread::yield();
        CHECK(mismatched == 0, "%d masks did not match their grid (torn hand-off?)", mismatched);
        CHECK(got > 0, "no mask received");
        std::printf("[4] worker: %d grids published, worker processed %llu, render side received %d masks (latest-wins), %d mismatched\n",
                    frames, (unsigned long long)worker->ProcessedFrames(), got, mismatched);
        auto t1 = std::chrono::steady_clock::now();
        worker.reset();                                         // stop + join must not hang
        std::printf("    shutdown (request_stop + join) took %.2f ms\n", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t1).count());
    }
    std::printf(fails ? "FAILED (%d)\n" : "ALL PASS\n", fails);
    return fails ? 1 : 0;
}
