#include "fg2_affine.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

float affine_median_vec_len(const float* vx, const float* vy, int n) {
    if (n <= 0) return 0.f;
    std::vector<float> xs(vx, vx + n);
    std::vector<float> ys(vy, vy + n);
    std::sort(xs.begin(), xs.end());
    std::sort(ys.begin(), ys.end());
    int m = n / 2;
    float mx = (n & 1) == 0 ? 0.5f * (xs[m - 1] + xs[m]) : xs[m];
    float my = (n & 1) == 0 ? 0.5f * (ys[m - 1] + ys[m]) : ys[m];
    return std::sqrt(mx * mx + my * my);
}

bool affine_snap_tile(float vx, float vy, float fit_x, float fit_y, float snap_on, float thresh) {
    float dx = vx - fit_x, dy = vy - fit_y;
    return snap_on >= 0.5f && std::sqrt(dx * dx + dy * dy) > thresh;
}

void affine_fitted_at(const AffineModel& m, float cx, float cy, float* vx, float* vy) {
    *vx = m.A00 * cx + m.A01 * cy + m.A02 - cx;
    *vy = m.A10 * cx + m.A11 * cy + m.A12 - cy;
}

static bool solve_n(float* a, float* x, int n) {
    for (int i = 0; i < n; i++) {
        int piv = i;
        float best = std::fabs(a[i * n + i]);
        for (int r = i + 1; r < n; r++) {
            float v = std::fabs(a[r * n + i]);
            if (v > best) {
                best = v;
                piv = r;
            }
        }
        if (best < 1e-8f) return false;
        if (piv != i) {
            for (int c = 0; c < n; c++) std::swap(a[i * n + c], a[piv * n + c]);
            std::swap(x[i], x[piv]);
        }
        float d = a[i * n + i];
        for (int c = i; c < n; c++) a[i * n + c] /= d;
        x[i] /= d;
        for (int r = 0; r < n; r++) {
            if (r == i) continue;
            float f = a[r * n + i];
            for (int c = i; c < n; c++) a[r * n + c] -= f * a[i * n + c];
            x[r] -= f * x[i];
        }
    }
    return true;
}

bool affine_solve3(const float* vx, const float* vy, int w, const int pick[3], AffineModel* m) {
    *m = affine_identity();
    float a[36];
    float b[6];
    std::memset(a, 0, sizeof(a));
    for (int k = 0; k < 3; k++) {
        int i = pick[k];
        int x = i % w, y = i / w;
        float cx = (x + 0.5f) * 8.f, cy = (y + 0.5f) * 8.f;
        float px = cx + vx[i], py = cy + vy[i];
        int r0 = k * 2, r1 = r0 + 1;
        a[r0 * 6 + 0] = cx;
        a[r0 * 6 + 1] = cy;
        a[r0 * 6 + 2] = 1.f;
        a[r1 * 6 + 3] = cx;
        a[r1 * 6 + 4] = cy;
        a[r1 * 6 + 5] = 1.f;
        b[r0] = px;
        b[r1] = py;
    }
    float xsol[6];
    std::memcpy(xsol, b, sizeof(b));
    if (!solve_n(a, xsol, 6)) return false;
    m->A00 = xsol[0];
    m->A01 = xsol[1];
    m->A02 = xsol[2];
    m->A10 = xsol[3];
    m->A11 = xsol[4];
    m->A12 = xsol[5];
    m->Conf = 1.f;
    return true;
}

struct CsRng {
    int inext = 0;
    int inextp = 21;
    int seed_array[56]{};
    explicit CsRng(int seed) {
        int subtraction = seed == INT32_MIN ? INT32_MAX : std::abs(seed);
        int mj = 161803398 - subtraction;
        seed_array[55] = mj;
        int mk = 1;
        for (int i = 1; i < 55; i++) {
            int ii = (21 * i) % 55;
            seed_array[ii] = mk;
            mk = mj - mk;
            if (mk < 0) mk += 2147483563;
            mj = seed_array[ii];
        }
        for (int k = 1; k < 5; k++) {
            for (int i = 1; i < 56; i++) {
                seed_array[i] -= seed_array[1 + (i + 30) % 55];
                if (seed_array[i] < 0) seed_array[i] += 2147483563;
            }
        }
        inext = 0;
        inextp = 21;
    }
    int next(int max) {
        int locINext = inext + 1;
        int locINextp = inextp + 1;
        if (locINext >= 56) locINext = 1;
        if (locINextp >= 56) locINextp = 1;
        int retVal = seed_array[locINext] - seed_array[locINextp];
        if (retVal == 2147483562) retVal--;
        if (retVal < 0) retVal += 2147483563;
        seed_array[locINext] = retVal;
        inext = locINext;
        inextp = locINextp;
        double sample = retVal * (1.0 / 2147483563.0);
        return static_cast<int>(sample * max);
    }
};

AffineModel affine_fit_ransac(const float* vx, const float* vy, const float* conf, int w, int h, int iters, int seed) {
    int n = w * h;
    CsRng rng(seed);
    AffineModel best = affine_identity();
    float best_in = -1.f;
    std::vector<int> idx;
    idx.reserve(static_cast<size_t>(n));
    for (int i = 0; i < n; i++) {
        if (conf[i] > 0.05f) idx.push_back(i);
    }
    if (static_cast<int>(idx.size()) < 6) {
        best.SnapOn = affine_median_vec_len(vx, vy, n) > kAffineSnapMedian ? 1.f : 0.f;
        return best;
    }
    int pick[3];
    for (int it = 0; it < iters; it++) {
        for (int k = 0; k < 3; k++) pick[k] = idx[rng.next(static_cast<int>(idx.size()))];
        AffineModel m{};
        if (!affine_solve3(vx, vy, w, pick, &m)) continue;
        float inn = 0, tot = 0;
        int nin = 0;
        for (int i : idx) {
            int x = i % w, y = i / w;
            float cx = (x + 0.5f) * 8.f, cy = (y + 0.5f) * 8.f;
            float px = m.A00 * cx + m.A01 * cy + m.A02;
            float py = m.A10 * cx + m.A11 * cy + m.A12;
            float dx = px - (cx + vx[i]), dy = py - (cy + vy[i]);
            float e = std::sqrt(dx * dx + dy * dy);
            float wt = conf[i];
            tot += wt;
            if (e < kAffineRansacThresh) {
                inn += wt;
                nin++;
            }
        }
        float frac = tot > 1e-4f ? inn / tot : 0.f;
        if (nin >= 12 && frac > best_in) {
            best_in = frac;
            m.Conf = frac;
            best = m;
        }
    }
    best.SnapOn = affine_median_vec_len(vx, vy, n) > kAffineSnapMedian ? 1.f : 0.f;
    return best;
}

float affine_candidate_cost(float local_sad, float aff_sad, float local_conf, float aff_conf) {
    return local_conf < 0.35f && aff_conf > 0.4f && aff_sad <= local_sad + 0.05f ? aff_sad : local_sad;
}