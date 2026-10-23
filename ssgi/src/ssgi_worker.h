// Universal-FrameFX SSGI - Phase 2 worker thread and the lock-free hand-offs around it.
//
// THREADING MODEL (read this before touching anything here)
//   Render thread (the only thread that touches D3D11):
//     - maps the oldest staging buffer with D3D11_MAP_FLAG_DO_NOT_WAIT; if the GPU is not done it skips this frame
//       (never blocks); on success it memcpy()s the 64 KB proxy grid into SsgiWorker::BeginGridWrite(), unmaps, and
//       calls PublishGrid(). Both calls are wait-free (one atomic exchange + one atomic increment + notify).
//     - calls TryAcquireMask(); if the worker published a newer ImportanceMap it gets a pointer to it and uploads the
//       2 KB with Map(WRITE_DISCARD) on a DYNAMIC buffer. Wait-free (one atomic load + at most one exchange).
//   Worker thread (std::jthread, owns nothing D3D):
//     - sleeps on an atomic sequence counter (std::atomic::wait, C++20), wakes when a grid is published or a stop is
//       requested, takes the newest grid (older ones are simply overwritten: bounded latency, bounded work), runs
//       BuildImportance (fixed 16384 cells), and publishes the mask.
//   Hand-offs are two single-producer / single-consumer TRIPLE BUFFERS: three slots, the producer always owns one, the
//   consumer always owns one, and the third ("middle") is swapped with an atomic exchange that also carries a "fresh"
//   flag. Neither side ever waits for the other, and neither side can see a slot the other is writing.
//
// ALLOCATIONS: everything (3 x 64 KB grids, 3 x 2 KB masks, 20 KB scratch) lives inside SsgiWorker, which is created
// once at init (std::make_unique, off the per-frame path). The std::jthread is started in the constructor. Nothing in
// the per-frame path or the worker loop allocates, throws, or takes a lock that the render thread can contend on.
// Note on notify: std::atomic::notify_one is a futex / WakeByAddress call (sub-microsecond); some standard libraries
// implement it with a small internal lock, which the worker holds only for nanoseconds. If that is ever measured as a
// problem, define UFX_SSGI_POLL to make the worker poll every 2 ms instead (the render thread then never notifies).
#pragma once
#include <array>
#include <atomic>
#include <cstdint>
#include <stop_token>
#include <thread>

#include "ssgi_importance.h"

namespace ufx::ssgi {

// Single-producer / single-consumer triple buffer. Wait-free on both sides.
// Producer: Write() -> fill the slot -> Publish().   Consumer: if (Acquire()) use Read().
template <class T>
class TripleBuffer {
public:
    T& Write() noexcept { return slots_[back_]; }                  // producer-only
    void Publish() noexcept {                                       // producer-only
        // Hand our freshly written slot to the middle and take the old middle as our next back buffer.
        // acq_rel: release our writes to the slot, acquire the consumer's reads of the slot we get back.
        back_ = middle_.exchange(back_ | kFresh, std::memory_order_acq_rel) & kIndex;
    }
    bool Acquire() noexcept {                                       // consumer-only; true if a newer slot was taken
        if (!(middle_.load(std::memory_order_relaxed) & kFresh)) return false;
        front_ = middle_.exchange(front_, std::memory_order_acq_rel) & kIndex;
        return true;
    }
    const T& Read() const noexcept { return slots_[front_]; }      // consumer-only
private:
    static constexpr uint32_t kIndex = 3, kFresh = 4;
    std::array<T, 3> slots_{};
    uint32_t back_ = 0;                                             // touched only by the producer
    uint32_t front_ = 1;                                            // touched only by the consumer
    alignas(64) std::atomic<uint32_t> middle_{2};                   // own cache line: no false sharing with the slots
};

struct alignas(64) GridFrame { uint32_t cells[kCells]; uint64_t frameId; };
struct alignas(64) MaskFrame { uint32_t words[kMaskWords]; uint64_t frameId; ImportanceStats stats; };

class SsgiWorker {
public:
    explicit SsgiWorker(const ImportanceParams& params = {}, SimdLevel level = DetectSimd());
    ~SsgiWorker();                                                  // request_stop + wake + join (jthread)
    SsgiWorker(const SsgiWorker&) = delete;
    SsgiWorker& operator=(const SsgiWorker&) = delete;

    // ---- render thread only ----
    GridFrame& BeginGridWrite() noexcept { return grids_.Write(); }
    void PublishGrid(uint64_t frameId) noexcept;
    // Returns true and sets 'out' when a mask newer than the last one returned is available. The pointer stays valid
    // (and unchanged by the worker) until the next successful TryAcquireMask call.
    bool TryAcquireMask(const MaskFrame*& out) noexcept;
    // Any thread: thresholds are packed into one atomic word, picked up by the worker on its next frame.
    void SetParams(const ImportanceParams& p) noexcept;

    SimdLevel Level() const noexcept { return level_; }
    uint64_t ProcessedFrames() const noexcept { return processed_.load(std::memory_order_relaxed); }

private:
    void Run(std::stop_token st) noexcept;
    static uint32_t Pack(const ImportanceParams& p) noexcept;
    static ImportanceParams Unpack(uint32_t v) noexcept;

    TripleBuffer<GridFrame> grids_;                                 // render -> worker
    TripleBuffer<MaskFrame> masks_;                                 // worker -> render
    ImportanceScratch scratch_;                                     // worker-only
    SimdLevel level_;
    alignas(64) std::atomic<uint32_t> params_;
    alignas(64) std::atomic<uint32_t> seq_{0};                      // bumped on every publish / stop: the wake signal
    std::atomic<uint64_t> processed_{0};
    std::jthread thread_;                                           // LAST member: starts after everything above exists,
                                                                    // and is joined first on destruction
};

}  // namespace ufx::ssgi
