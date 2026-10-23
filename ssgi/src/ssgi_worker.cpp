// Universal-FrameFX SSGI - Phase 2 worker implementation. See ssgi_worker.h for the threading model.
#include "ssgi_worker.h"

#include <chrono>

namespace ufx::ssgi {

SsgiWorker::SsgiWorker(const ImportanceParams& params, SimdLevel level)
    : level_(level), params_(Pack(params)),
      thread_([this](std::stop_token st) { Run(st); }) {}           // the only allocation: thread start, at init

SsgiWorker::~SsgiWorker() {
    // jthread's destructor would request_stop() and join() anyway; the stop_callback in Run() wakes the wait.
    thread_.request_stop();
}

void SsgiWorker::PublishGrid(uint64_t frameId) noexcept {
    grids_.Write().frameId = frameId;
    grids_.Publish();                                               // release: the 64 KB copy is visible to the worker
#ifndef UFX_SSGI_POLL
    seq_.fetch_add(1, std::memory_order_release);
    seq_.notify_one();
#endif
}

bool SsgiWorker::TryAcquireMask(const MaskFrame*& out) noexcept {
    if (!masks_.Acquire()) return false;
    out = &masks_.Read();
    return true;
}

void SsgiWorker::SetParams(const ImportanceParams& p) noexcept { params_.store(Pack(p), std::memory_order_relaxed); }

uint32_t SsgiWorker::Pack(const ImportanceParams& p) noexcept {
    // 8 + 8 + 8 bits thresholds, 3 + 3 bits reach, 2 bits edge mode: one lock-free word.
    return p.emitFloor | (p.emitAboveMean << 8) | (p.edge << 16) | ((p.reachLog2X & 7u) << 24) | ((p.reachLog2Y & 7u) << 27) |
           (((uint32_t)p.edgeMode & 3u) << 30);
}
ImportanceParams SsgiWorker::Unpack(uint32_t v) noexcept {
    ImportanceParams p;
    p.emitFloor = (uint8_t)v; p.emitAboveMean = (uint8_t)(v >> 8); p.edge = (uint8_t)(v >> 16);
    p.reachLog2X = (uint8_t)((v >> 24) & 7); p.reachLog2Y = (uint8_t)((v >> 27) & 7); p.edgeMode = (EdgeMode)((v >> 30) & 3);
    return p;
}

void SsgiWorker::Run(std::stop_token st) noexcept {
    // Wake the wait below when a stop is requested (stop_callback runs on the requesting thread, nothing allocated
    // after construction; the callback object lives on this thread's stack for the whole loop).
    std::stop_callback wake(st, [this] { seq_.fetch_add(1, std::memory_order_release); seq_.notify_all(); });
    uint32_t seen = seq_.load(std::memory_order_acquire);
    while (!st.stop_requested()) {
#ifdef UFX_SSGI_POLL
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
#else
        seq_.wait(seen, std::memory_order_acquire);                 // sleeps while seq_ == seen
        seen = seq_.load(std::memory_order_acquire);
#endif
        if (st.stop_requested()) break;
        if (!grids_.Acquire()) continue;                            // spurious wake / already consumed
        const GridFrame& g = grids_.Read();                         // ours until the next Acquire
        MaskFrame& m = masks_.Write();                              // ours until Publish
        m.stats = BuildImportance(g.cells, scratch_, m.words, Unpack(params_.load(std::memory_order_relaxed)), level_);
        m.frameId = g.frameId;
        masks_.Publish();
        processed_.fetch_add(1, std::memory_order_relaxed);
    }
}

}  // namespace ufx::ssgi
