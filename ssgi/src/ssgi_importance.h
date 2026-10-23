// Universal-FrameFX SSGI - Phase 2 kernel: build the 128 x 128 1-bit ImportanceMap from the packed proxy grid.
// Pure function over caller-owned memory: no allocations, no locks, no globals written, fixed (bounded) work of
// 16384 cells per call. Thread-safe as long as each concurrent caller passes its own scratch / output (the worker
// thread owns exactly one ImportanceScratch, so there is no sharing).
#pragma once
#include <cstdint>

namespace ufx::ssgi {

inline constexpr int kGrid      = 128;                 // proxy grid is kGrid x kGrid
inline constexpr int kCells     = kGrid * kGrid;       // 16384 packed uint32 cells (64 KB)
inline constexpr int kMaskWords = kCells / 32;         // 512 uint32 = 2 KB, 4 words per row
inline constexpr int kLumaStride = 160;                // scratch row stride: 128 cells + replicated pad (aligned loads)
inline constexpr int kLumaRows   = kGrid + 1;          // + replicated last row (vertical neighbour of row 127)

enum class EdgeMode : uint8_t {
    Off = 0,            // edges not used
    NearEmitters = 1,   // default: edge cells (dilated 1) count only within 2x the emitter reach
    Always = 2,         // every edge cell (dilated 1) is marched, even far from any emitter (debug / AO-only use)
};

struct ImportanceParams {
    uint8_t emitFloor = 96;     // a cell is a "light emitting zone" if luma >= max(emitFloor, mean + emitAboveMean)
    uint8_t emitAboveMean = 40;
    uint8_t edge = 20;           // "shadow trap": max(|dL/dx|, |dL/dy|) between neighbouring cells >= edge
    uint8_t reachLog2X = 5;      // emitter zones are dilated by (2^n - 1) cells: the march's reach (r4 default 5 / 6). x: 31 cells,
    uint8_t reachLog2Y = 6;      // y: 63 cells (march steps are aspect-corrected, so they cover more cells along y)
    EdgeMode edgeMode = EdgeMode::NearEmitters;
};
// Why emitters are dilated so far: bounce light lands on the flat, dull areas AROUND bright zones, which are neither
// emitters nor edges. With only a 1-cell dilation the offline harness lost up to 45/255 of added light (mean 1.7/255
// of 3.7/255 added); dilating emitters by the march reach brings the gated result within 1.6/255 (max) of the
// ungated one while still skipping about half of the march groups on the test frame (tests/harness).

struct ImportanceStats {
    uint32_t emitCells = 0, edgeCells = 0, setCells = 0;   // raw emitter / edge cells, set = final map
    uint8_t  meanLuma = 0, emitThreshold = 0;
};

enum class SimdLevel : uint8_t { Scalar = 0, SSE2 = 1, AVX2 = 2 };

// Scratch owned by the caller (the worker). 32-byte aligned for AVX2 loads. ~27 KB, no heap use inside the kernel.
struct alignas(32) ImportanceScratch {
    uint8_t luma[kLumaRows * kLumaStride];
    uint32_t emit[kMaskWords], edge[kMaskWords], reach2[kMaskWords], tmp[kMaskWords];
};

// Best level the running CPU supports (and this build was compiled with). Call once at init.
SimdLevel DetectSimd() noexcept;
const char* SimdName(SimdLevel) noexcept;

// grid: kCells packed cells (RGB8 + luma8 in the top byte, layout in shaders/ssgi_common.hlsli).
// outMask: kMaskWords words; cell (x, y) is bit (x & 31) of word y * 4 + (x >> 5).
// Every SimdLevel produces bit-identical output (tests/importance_test.cpp checks this).
ImportanceStats BuildImportance(const uint32_t* grid, ImportanceScratch& scratch, uint32_t* outMask,
                                const ImportanceParams& params, SimdLevel level) noexcept;

}  // namespace ufx::ssgi
