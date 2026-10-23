#include "fg2_pacing.h"

#include <cmath>
#include <cstdio>

static int g_fails = 0;

static void check(bool ok, const char* what) {
    std::printf("%s  %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++g_fails;
}

static bool near(double a, double b, double eps) { return std::fabs(a - b) <= eps; }

int main() {
    using namespace fg2_pacing;

    check(near(hold_delay(1.0 / 30.0, 0.0), 1.0 / 30.0, 1e-12), "HoldDelay(1/30, 0) = 1/30");
    check(near(hold_delay(1.0 / 60.0, 0.002), 1.0 / 60.0 + 0.002, 1e-12), "HoldDelay(1/60, 2ms) = interval+margin");
    {
        double lo = 0.05;
        double hi = std::max(lo, 0.2 + 0.008);
        check(near(hold_delay(0.1, 0.5), hi, 1e-12), "HoldDelay clamps to 2*interval+8ms");
    }
    {
        double h = hold_delay(0.05, -10.0);
        check(near(h, 0.025, 1e-12), "HoldDelay clamps to 0.5*interval");
    }

    check(near(playback_time(10.0, 0.04, false), 9.96, 1e-12), "PlaybackTime holds one source frame");
    check(near(playback_time(10.0, 0.04, true), 10.0, 1e-12), "PlaybackTime extrapolate is vsyncTime");

    {
        bool show = false;
        float t = interp_t(1.0, 1.0, 1.0, &show);
        check(show && t == 1.f, "InterpT degenerate span shows current");
    }
    {
        bool show = false;
        float t = interp_t(2.0, 1.0, 2.0, &show);
        check(show && t == 1.f, "InterpT at currCap shows current");
    }
    {
        bool show = true;
        float t = interp_t(1.5, 1.0, 2.0, &show);
        check(!show && near(t, 0.5, 1e-6), "InterpT midpoint is 0.5");
    }
    {
        bool show = false;
        float t = interp_t(1.9995, 1.0, 2.0, &show);
        check(show && near(t, 0.9995, 1e-6), "InterpT near 1 shows current");
    }
    {
        bool show = true;
        float t = interp_t(0.0, 1.0, 2.0, &show);
        check(!show && t == 0.f, "InterpT clamps below prevCap to 0");
    }

    check(should_generate(0.0, 0.0, false, 1.0 / 30.0, 2), "ShouldGenerate first is true");
    check(should_generate(1.0 / 60.0, 0.0, true, 1.0 / 30.0, 2), "2x at 30fps source allows every 60Hz vsync");
    check(!should_generate(1.0 / 90.0, 0.0, true, 1.0 / 30.0, 2), "2x drops vsyncs faster than min spacing");

    {
        int n = rate_cap_dispatches(60, 1.0 / 30.0, 1.0 / 60.0, 2);
        check(n == 60, "RateCapDispatches 30fps source / 60Hz / 2x = 60");
    }
    {
        int n = rate_cap_dispatches(90, 1.0 / 30.0, 1.0 / 90.0, 2);
        check(n == 45, "RateCapDispatches 30fps / 90Hz / 2x = 45");
    }
    {
        int n = rate_cap_dispatches(90, 1.0 / 30.0, 1.0 / 90.0, 3);
        check(n == 90, "RateCapDispatches 30fps / 90Hz / 3x = 90");
    }
    {
        int n4 = rate_cap_dispatches(90, 1.0 / 22.0, 1.0 / 90.0, 4);
        int n2 = rate_cap_dispatches(90, 1.0 / 22.0, 1.0 / 90.0, 2);
        check(n4 == 45 && n2 == 30, "RateCapDispatches 22fps / 90Hz 4x=45 2x=30");
    }

    std::printf("%s\n", g_fails == 0 ? "PASS" : "FAIL");
    return g_fails == 0 ? 0 : 1;
}