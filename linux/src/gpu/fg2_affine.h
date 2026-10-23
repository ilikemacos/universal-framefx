#pragma once

#include <cstdint>
#include <vector>

struct AffineModel {
    float A00 = 1.f, A01 = 0.f, A02 = 0.f;
    float A10 = 0.f, A11 = 1.f, A12 = 0.f;
    float Conf = 0.f, SnapOn = 0.f;
};

struct Fg2Affine {
    AffineModel last{};
    int w = 0, h = 0;
};

inline AffineModel affine_identity() {
    AffineModel m{};
    m.A00 = 1.f;
    m.A11 = 1.f;
    return m;
}

float affine_median_vec_len(const float* vx, const float* vy, int n);
bool affine_snap_tile(float vx, float vy, float fit_x, float fit_y, float snap_on, float thresh);
void affine_fitted_at(const AffineModel& m, float cx, float cy, float* vx, float* vy);
bool affine_solve3(const float* vx, const float* vy, int w, const int pick[3], AffineModel* m);
AffineModel affine_fit_ransac(const float* vx, const float* vy, const float* conf, int w, int h, int iters, int seed);
float affine_candidate_cost(float local_sad, float aff_sad, float local_conf, float aff_conf);

constexpr int kAffineRansacIters = 96;
constexpr float kAffineRansacThresh = 1.75f;
constexpr float kAffineSnapMedian = 12.f;
constexpr int kAffineRansacSeed = 1;