// Universal-FrameFX SSGI - Phase 2 importance kernel (scalar, SSE2 and AVX2 paths, bit-identical results).
//
// Steps (bounded: always exactly 16384 cells):
//   1. luma[x, y] = cell >> 24, into a padded scratch (row 128 and column 128.. replicate the last row / column, so the
//      +1 neighbour loads never need a branch). SIMD: shift + pack.
//   2. mean luma (SIMD: SAD against zero), emit threshold T = max(emitFloor, mean + emitAboveMean) clamped to 255.
//   3. emit = L >= T, edge = max(|L - L(x+1)|, |L - L(y+1)|) >= edge. SIMD: saturating subtracts give |a - b|, compares
//      are done as max(a, b) == a, and movemask turns 16 / 32 byte lanes straight into mask bits (bit j = cell x0 + j).
//   4. bitwise dilation on 128-bit rows (4 words) by doubling shifts (1, 2, 4, 8.. cells: radius 2^n - 1 in n steps):
//      map = dilate(emit, reach) | (dilate(edge, 1) & dilate(emit, 2 x reach))   [EdgeMode::NearEmitters]
// No allocations, no exceptions, no shared state: safe to run on the worker thread while the render thread works.
#include "ssgi_importance.h"

#include <algorithm>
#include <bit>
#include <cstdlib>
#include <cstring>

#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__) || defined(_M_IX86)
#define UFX_SSGI_X86 1
#include <immintrin.h>
#if defined(_MSC_VER) && !defined(__clang__)
#include <intrin.h>
#define UFX_TARGET_AVX2
#else
#include <cpuid.h>
#define UFX_TARGET_AVX2 __attribute__((target("avx2")))
#endif
#endif

namespace ufx::ssgi {
namespace {

inline uint8_t EmitThreshold(uint32_t mean, const ImportanceParams& p) noexcept {
    return (uint8_t)std::min<uint32_t>(255u, std::max<uint32_t>(p.emitFloor, mean + p.emitAboveMean));
}

// Replicate column 127 into the pad and row 127 into row 128 (so neighbour(x+1) / (y+1) of the last cell is itself,
// i.e. zero gradient at the screen border).
inline void PadScratch(uint8_t* L) noexcept {
    for (int y = 0; y < kGrid; ++y) std::memset(L + y * kLumaStride + kGrid, L[y * kLumaStride + kGrid - 1], kLumaStride - kGrid);
    std::memcpy(L + kGrid * kLumaStride, L + (kGrid - 1) * kLumaStride, kLumaStride);
}

// m |= m shifted by +-s cells along x (s in 1..16), carrying bits across the 4 words of each row.
void ShiftOrX(uint32_t* m, int s) noexcept {
    for (int y = 0; y < kGrid; ++y) {
        uint32_t* r = m + y * 4;
        const uint32_t r0 = r[0], r1 = r[1], r2 = r[2], r3 = r[3];
        const uint32_t v[4] = { r0, r1, r2, r3 };
        for (int w = 0; w < 4; ++w) {
            uint32_t up = (v[w] << s) | (w > 0 ? v[w - 1] >> (32 - s) : 0u);      // cell x -> x + s
            uint32_t dn = (v[w] >> s) | (w < 3 ? v[w + 1] << (32 - s) : 0u);      // cell x -> x - s
            r[w] = v[w] | up | dn;
        }
    }
}
// m |= m shifted by +-s rows.
void ShiftOrY(uint32_t* m, uint32_t* tmp, int s) noexcept {
    std::memcpy(tmp, m, kMaskWords * 4);
    for (int y = 0; y < kGrid; ++y)
        for (int w = 0; w < 4; ++w)
            m[y * 4 + w] |= (y >= s ? tmp[(y - s) * 4 + w] : 0u) | (y + s < kGrid ? tmp[(y + s) * 4 + w] : 0u);
}
// Dilate by (2^lx - 1) cells along x and (2^ly - 1) along y with lx + ly doubling steps (bounded: lx, ly <= 5).
void DilateLog2(uint32_t* m, uint32_t* tmp, int lx, int ly) noexcept {
    for (int k = 0; k < std::min(lx, 5); ++k) ShiftOrX(m, 1 << k);
    for (int k = 0; k < std::min(ly, 6); ++k) ShiftOrY(m, tmp, 1 << k);
}

// ------------------------------------------------------------------------------------------------ scalar reference
ImportanceStats BuildScalar(const uint32_t* grid, uint8_t* L, uint32_t* em, uint32_t* ed, const ImportanceParams& p) noexcept {
    uint32_t sum = 0;
    for (int y = 0; y < kGrid; ++y)
        for (int x = 0; x < kGrid; ++x) { uint8_t v = (uint8_t)(grid[y * kGrid + x] >> 24); L[y * kLumaStride + x] = v; sum += v; }
    PadScratch(L);
    ImportanceStats st; st.meanLuma = (uint8_t)(sum / kCells); st.emitThreshold = EmitThreshold(st.meanLuma, p);
    std::memset(em, 0, kMaskWords * 4); std::memset(ed, 0, kMaskWords * 4);
    for (int y = 0; y < kGrid; ++y) {
        const uint8_t* r = L + y * kLumaStride; const uint8_t* d = r + kLumaStride;
        for (int x = 0; x < kGrid; ++x) {
            int gx = std::abs((int)r[x] - (int)r[x + 1]), gy = std::abs((int)r[x] - (int)d[x]);
            bool e = r[x] >= st.emitThreshold, g = std::max(gx, gy) >= p.edge;
            st.emitCells += e; st.edgeCells += g;
            if (e) em[y * 4 + (x >> 5)] |= 1u << (x & 31);
            if (g) ed[y * 4 + (x >> 5)] |= 1u << (x & 31);
        }
    }
    return st;
}

#if UFX_SSGI_X86
// ------------------------------------------------------------------------------------------------ SSE2 (16 cells/op)
ImportanceStats BuildSSE2(const uint32_t* grid, uint8_t* L, uint32_t* em, uint32_t* ed, const ImportanceParams& p) noexcept {
    __m128i sum = _mm_setzero_si128();
    for (int y = 0; y < kGrid; ++y)
        for (int x = 0; x < kGrid; x += 16) {
            const __m128i* g = (const __m128i*)(grid + y * kGrid + x);
            __m128i a = _mm_srli_epi32(_mm_loadu_si128(g + 0), 24), b = _mm_srli_epi32(_mm_loadu_si128(g + 1), 24);
            __m128i c = _mm_srli_epi32(_mm_loadu_si128(g + 2), 24), d = _mm_srli_epi32(_mm_loadu_si128(g + 3), 24);
            __m128i v = _mm_packus_epi16(_mm_packs_epi32(a, b), _mm_packs_epi32(c, d));   // values <= 255: no saturation
            _mm_store_si128((__m128i*)(L + y * kLumaStride + x), v);
            sum = _mm_add_epi64(sum, _mm_sad_epu8(v, _mm_setzero_si128()));
        }
    PadScratch(L);
    uint64_t s2[2]; _mm_storeu_si128((__m128i*)s2, sum);
    ImportanceStats st; st.meanLuma = (uint8_t)((s2[0] + s2[1]) / kCells); st.emitThreshold = EmitThreshold(st.meanLuma, p);
    const __m128i T = _mm_set1_epi8((char)st.emitThreshold), E = _mm_set1_epi8((char)p.edge);
    for (int y = 0; y < kGrid; ++y) {
        const uint8_t* r = L + y * kLumaStride; const uint8_t* dn = r + kLumaStride;
        uint32_t e16[8], g16[8];
        for (int c = 0; c < 8; ++c) {
            __m128i v = _mm_load_si128((const __m128i*)(r + 16 * c));
            __m128i rt = _mm_loadu_si128((const __m128i*)(r + 16 * c + 1));      // x + 1 (pad makes this safe)
            __m128i dv = _mm_load_si128((const __m128i*)(dn + 16 * c));          // y + 1
            __m128i gx = _mm_or_si128(_mm_subs_epu8(v, rt), _mm_subs_epu8(rt, v));
            __m128i gy = _mm_or_si128(_mm_subs_epu8(v, dv), _mm_subs_epu8(dv, v));
            __m128i g = _mm_max_epu8(gx, gy);
            __m128i em = _mm_cmpeq_epi8(_mm_max_epu8(v, T), v);                  // v >= T
            __m128i ed = _mm_cmpeq_epi8(_mm_max_epu8(g, E), g);                  // g >= E
            e16[c] = (uint32_t)_mm_movemask_epi8(em); g16[c] = (uint32_t)_mm_movemask_epi8(ed);
            st.emitCells += (uint32_t)std::popcount(e16[c]); st.edgeCells += (uint32_t)std::popcount(g16[c]);
        }
        for (int w = 0; w < 4; ++w) { em[y * 4 + w] = e16[2 * w] | (e16[2 * w + 1] << 16); ed[y * 4 + w] = g16[2 * w] | (g16[2 * w + 1] << 16); }
    }
    return st;
}

// ------------------------------------------------------------------------------------------------ AVX2 (32 cells/op)
UFX_TARGET_AVX2 ImportanceStats BuildAVX2(const uint32_t* grid, uint8_t* L, uint32_t* em, uint32_t* ed, const ImportanceParams& p) noexcept {
    __m256i sum = _mm256_setzero_si256();
    const __m256i perm = _mm256_setr_epi32(0, 4, 1, 5, 2, 6, 3, 7);   // undo the per-lane packing order
    for (int y = 0; y < kGrid; ++y)
        for (int x = 0; x < kGrid; x += 32) {
            const __m256i* g = (const __m256i*)(grid + y * kGrid + x);
            __m256i a = _mm256_srli_epi32(_mm256_loadu_si256(g + 0), 24), b = _mm256_srli_epi32(_mm256_loadu_si256(g + 1), 24);
            __m256i c = _mm256_srli_epi32(_mm256_loadu_si256(g + 2), 24), d = _mm256_srli_epi32(_mm256_loadu_si256(g + 3), 24);
            __m256i v = _mm256_packus_epi16(_mm256_packs_epi32(a, b), _mm256_packs_epi32(c, d));
            v = _mm256_permutevar8x32_epi32(v, perm);
            _mm256_store_si256((__m256i*)(L + y * kLumaStride + x), v);
            sum = _mm256_add_epi64(sum, _mm256_sad_epu8(v, _mm256_setzero_si256()));
        }
    PadScratch(L);
    alignas(32) uint64_t s4[4]; _mm256_store_si256((__m256i*)s4, sum);
    ImportanceStats st; st.meanLuma = (uint8_t)((s4[0] + s4[1] + s4[2] + s4[3]) / kCells); st.emitThreshold = EmitThreshold(st.meanLuma, p);
    const __m256i T = _mm256_set1_epi8((char)st.emitThreshold), E = _mm256_set1_epi8((char)p.edge);
    for (int y = 0; y < kGrid; ++y) {
        const uint8_t* r = L + y * kLumaStride; const uint8_t* dn = r + kLumaStride;
        for (int c = 0; c < 4; ++c) {
            __m256i v = _mm256_load_si256((const __m256i*)(r + 32 * c));
            __m256i rt = _mm256_loadu_si256((const __m256i*)(r + 32 * c + 1));
            __m256i dv = _mm256_load_si256((const __m256i*)(dn + 32 * c));
            __m256i gx = _mm256_or_si256(_mm256_subs_epu8(v, rt), _mm256_subs_epu8(rt, v));
            __m256i gy = _mm256_or_si256(_mm256_subs_epu8(v, dv), _mm256_subs_epu8(dv, v));
            __m256i g = _mm256_max_epu8(gx, gy);
            uint32_t e = (uint32_t)_mm256_movemask_epi8(_mm256_cmpeq_epi8(_mm256_max_epu8(v, T), v));
            uint32_t d = (uint32_t)_mm256_movemask_epi8(_mm256_cmpeq_epi8(_mm256_max_epu8(g, E), g));
            em[y * 4 + c] = e; ed[y * 4 + c] = d;
            st.emitCells += (uint32_t)std::popcount(e); st.edgeCells += (uint32_t)std::popcount(d);
        }
    }
    return st;
}
#endif
}  // namespace

SimdLevel DetectSimd() noexcept {
#if UFX_SSGI_X86
#if defined(_MSC_VER) && !defined(__clang__)
    int r[4]; __cpuid(r, 0); int maxLeaf = r[0];
    __cpuid(r, 1); bool osxsave = (r[2] >> 27) & 1, avx = (r[2] >> 28) & 1;
    bool ymm = osxsave && avx && ((_xgetbv(0) & 6) == 6);
    if (maxLeaf >= 7 && ymm) { __cpuidex(r, 7, 0); if ((r[1] >> 5) & 1) return SimdLevel::AVX2; }
#else
    if (__builtin_cpu_supports("avx2")) return SimdLevel::AVX2;
#endif
    return SimdLevel::SSE2;   // baseline on every x86-64 CPU
#else
    return SimdLevel::Scalar;
#endif
}

const char* SimdName(SimdLevel l) noexcept { return l == SimdLevel::AVX2 ? "AVX2" : l == SimdLevel::SSE2 ? "SSE2" : "scalar"; }

ImportanceStats BuildImportance(const uint32_t* grid, ImportanceScratch& scratch, uint32_t* outMask,
                                const ImportanceParams& params, SimdLevel level) noexcept {
    ImportanceStats st;
    uint32_t* em = scratch.emit; uint32_t* ed = scratch.edge;
#if UFX_SSGI_X86
    if (level == SimdLevel::AVX2) st = BuildAVX2(grid, scratch.luma, em, ed, params);
    else if (level == SimdLevel::SSE2) st = BuildSSE2(grid, scratch.luma, em, ed, params);
    else
#endif
        st = BuildScalar(grid, scratch.luma, em, ed, params);
    // map = dilate(emit, reach) | edge part (all bitwise on 512 words; bounded: at most 5 + 6 doubling steps each)
    std::memcpy(outMask, em, kMaskWords * 4);
    DilateLog2(outMask, scratch.tmp, params.reachLog2X, params.reachLog2Y);
    if (params.edgeMode != EdgeMode::Off) {
        DilateLog2(ed, scratch.tmp, 1, 1);
        if (params.edgeMode == EdgeMode::NearEmitters) {
            std::memcpy(scratch.reach2, em, kMaskWords * 4);
            DilateLog2(scratch.reach2, scratch.tmp, params.reachLog2X + 1, params.reachLog2Y + 1);
            for (int i = 0; i < kMaskWords; ++i) ed[i] &= scratch.reach2[i];
        }
        for (int i = 0; i < kMaskWords; ++i) outMask[i] |= ed[i];
    }
    for (int i = 0; i < kMaskWords; ++i) st.setCells += (uint32_t)std::popcount(outMask[i]);
    return st;
}

}  // namespace ufx::ssgi
