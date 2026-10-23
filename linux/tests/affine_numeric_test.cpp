#include "gpu/fg2_affine.h"

#include <cmath>
#include <cstdio>
#include <vector>

static int g_fails = 0;

static void check(bool ok, const char* what) {
    std::printf("%s  %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++g_fails;
}

int main() {
    const int w = 20, h = 12, n = w * h;
    std::vector<float> vx(n), vy(n), conf(n, 1.f);
    AffineModel truth{};
    truth.A00 = 1.02f;
    truth.A01 = 0.01f;
    truth.A02 = 6.5f;
    truth.A10 = -0.008f;
    truth.A11 = 0.99f;
    truth.A12 = -3.25f;
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            int i = y * w + x;
            float cx = (x + 0.5f) * 8.f, cy = (y + 0.5f) * 8.f;
            affine_fitted_at(truth, cx, cy, &vx[i], &vy[i]);
        }
    }
    int outliers = 0;
    for (int i = 0; i < n; i += 11) {
        vx[i] += 40.f;
        vy[i] -= 25.f;
        conf[i] = 0.2f;
        outliers++;
    }
    AffineModel fit = affine_fit_ransac(vx.data(), vy.data(), conf.data(), w, h, kAffineRansacIters, kAffineRansacSeed);
    float e00 = std::fabs(fit.A00 - truth.A00);
    float e01 = std::fabs(fit.A01 - truth.A01);
    float e02 = std::fabs(fit.A02 - truth.A02);
    float e10 = std::fabs(fit.A10 - truth.A10);
    float e11 = std::fabs(fit.A11 - truth.A11);
    float e12 = std::fabs(fit.A12 - truth.A12);
    std::printf("fit A=[%.5f %.5f %.5f; %.5f %.5f %.5f] conf=%.3f snap=%.0f\n", fit.A00, fit.A01, fit.A02, fit.A10,
                fit.A11, fit.A12, fit.Conf, fit.SnapOn);
    check(e00 < 0.02f && e01 < 0.02f && e10 < 0.02f && e11 < 0.02f, "rotation/scale within 0.02");
    check(e02 < 0.75f && e12 < 0.75f, "translation within 0.75px");
    check(fit.Conf > 0.6f, "inlier fraction high");

    AffineModel id = affine_identity();
    float fx, fy;
    affine_fitted_at(id, 10.f, 20.f, &fx, &fy);
    check(fx == 0.f && fy == 0.f, "identity fitted delta is 0");

    check(affine_snap_tile(10.f, 0.f, 0.f, 0.f, 1.f, 1.75f), "snap when far from fit");
    check(!affine_snap_tile(1.f, 0.f, 0.f, 0.f, 1.f, 1.75f), "no snap when inside thresh");
    check(!affine_snap_tile(10.f, 0.f, 0.f, 0.f, 0.f, 1.75f), "no snap when SnapOn off");

    check(affine_candidate_cost(0.2f, 0.1f, 0.2f, 0.5f) == 0.1f, "affine candidate preferred when local conf low");
    check(affine_candidate_cost(0.2f, 0.1f, 0.5f, 0.5f) == 0.2f, "local cost kept when local conf high");

    {
        std::vector<float> z(4, 0.f);
        AffineModel empty = affine_fit_ransac(z.data(), z.data(), z.data(), 2, 2, 16, 1);
        check(empty.Conf == 0.f, "too few confident tiles returns identity");
    }

    (void)outliers;
    std::printf("%s\n", g_fails == 0 ? "PASS" : "FAIL");
    return g_fails == 0 ? 0 : 1;
}