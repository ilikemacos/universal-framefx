#pragma once

#include <algorithm>
#include <cmath>

namespace fg2_pacing {

inline double hold_delay(double source_interval, double process_margin_s) {
    double h = source_interval + process_margin_s;
    double lo = std::max(0.0, source_interval * 0.5);
    double hi = std::max(lo, source_interval * 2.0 + 0.008);
    return std::clamp(h, lo, hi);
}

inline double playback_time(double vsync_time, double hold, bool extrapolate) {
    return extrapolate ? vsync_time : vsync_time - hold;
}

inline float interp_t(double playback, double prev_cap, double curr_cap, bool* show_curr) {
    double span = curr_cap - prev_cap;
    if (span <= 1e-6) {
        *show_curr = true;
        return 1.f;
    }
    if (playback >= curr_cap - 1e-6) {
        *show_curr = true;
        return 1.f;
    }
    double t = (playback - prev_cap) / span;
    t = std::clamp(t, 0.0, 1.0);
    *show_curr = t >= 1.0 - 1e-3;
    return static_cast<float>(t);
}

inline bool should_generate(double now_s, double last_gen_at_s, bool have_last, double source_interval, int cap) {
    if (!have_last) return true;
    int n = std::max(2, cap);
    double min_spacing = source_interval / static_cast<double>(n);
    return now_s - last_gen_at_s >= min_spacing - 1e-7;
}

inline int rate_cap_dispatches(int vsyncs, double source_interval, double refresh_interval, int cap) {
    int n = 0;
    double last = 0.0;
    bool have = false;
    for (int i = 0; i < vsyncs; i++) {
        double now = static_cast<double>(i) * refresh_interval;
        if (should_generate(now, last, have, source_interval, cap)) {
            n++;
            last = now;
            have = true;
        }
    }
    return n;
}

}