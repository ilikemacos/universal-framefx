cbuffer FgV2Params : register(b0)
{
    float InW, InH, OutW, OutH;
    float TileSize, TilesX, TilesY, InterpT;
    float HistoryValid, Lambda, CensusW, SadW;
    float OcclThresh, StaticThresh, SceneCutThresh, FrameCount;
    float JitterX, JitterY, BidirOn, Fallback;
    float MvGapN, WideSearch, ClassicCand, EdgeSmooth;
    float AmbiguityGate, TemporalExtrap, History2Valid, BicubicOn;
    float SharpBlendOn, PadFg2a, PadFg2b, PadFg2c, PadFg2d, PadFg2e, PadFg2f, PadFg2g, PadFg2h, PadFg2i, PadFg2j, PadFg2k;
};

cbuffer FgV2Global : register(b1)
{
    float A00, A01, A02, A10;
    float A11, A12, AffConf, SnapOn;
    float HwScale, HwGrid, RansacPx, GmvY;
    float ConfThresh, ColorGate, VecLenK, SoftOccK;
};

SamplerState linClamp : register(s0);
SamplerState pntClamp : register(s1);

static const float3 REC709 = float3(0.2126f, 0.7152f, 0.0722f);
float luma709(float3 c) { return dot(c, REC709); }
float max3(float3 v) { return max(v.x, max(v.y, v.z)); }
bool inside(float2 uv) { return all(uv >= 0.0f) && all(uv <= 1.0f); }
float2 inSize() { return float2(InW, InH); }
float2 outSize() { return float2(OutW, OutH); }

Texture2D<float4> tColor : register(t0);
Texture2D<float> tLumaIn : register(t1);
Texture2D<float> tPrevL : register(t2);
Texture2D<float> tCurrL : register(t3);
Texture2D<float4> tMv : register(t4);
Texture2D<float4> tFwd : register(t5);
Texture2D<float> tCount : register(t6);
Texture2D<float> tHud : register(t7);
Texture2D<float4> tSrc : register(t8);
Texture2D<float4> tCurrCol : register(t9);
Texture2D<float4> tClassic : register(t10);
Texture2D<float4> tSmooth : register(t11);
Texture2D<float4> tCoarse : register(t12);
Texture2D<float4> tMvHist2 : register(t13);
Texture2D<float4> tMvPrev : register(t15);
Texture2D<float> tChg : register(t16);
Texture2D<float2> tGmv : register(t17);
Texture2D<float> tHudLo : register(t18);
Texture2D<int2> tHw : register(t20);

RWTexture2D<float> uLuma : register(u0);
RWTexture2D<float4> uTv8 : register(u1);
RWTexture2D<float2> uTv4 : register(u2);
RWTexture2D<float4> uMv2 : register(u3);
RWTexture2D<float4> uFwd : register(u4);
RWTexture2D<float> uOccl : register(u5);
RWTexture2D<float> uCount : register(u6);
RWTexture2D<float> uHud : register(u7);

RWTexture2D<float4> uG1 : register(u0);
RWTexture2D<float4> uG2 : register(u1);
RWTexture2D<float4> uG3 : register(u2);
RWTexture2D<float4> uG4 : register(u3);
RWTexture2D<float4> uG5 : register(u4);
RWTexture2D<float4> uG6 : register(u5);
RWTexture2D<float4> uG7 : register(u6);
RWTexture2D<float> uChgGrid : register(u4);
RWTexture2D<float4> uClean : register(u0);
Texture2DArray<float> tPack : register(t14);
Texture2DArray<float> tNetA : register(t19);
Texture2DArray<float> tNetB : register(t21);
RWTexture2DArray<float> uFeatAi : register(u0);

#define FS_R 4
#define FS_N (8 + 2 * FS_R + 2)
#define FS_C ((2 * FS_R + 1) * (2 * FS_R + 1))
groupshared float gsPrev[FS_N][FS_N];
groupshared float gsCurr[10][10];
groupshared float gsCost[64][FS_C];
groupshared float gsBlock[FS_C];

groupshared float gsChg[256];
groupshared float gsCut[256];
groupshared uint gsHa[32];
groupshared uint gsHb[32];
groupshared float gsEarly;

int2 ring8(int k)
{
    return int2(k < 3 ? k - 1 : (k < 5 ? (k == 3 ? -1 : 1) : k - 6), k < 3 ? -1 : (k < 5 ? 0 : 1));
}

uint censusPx(Texture2D<float> tex, int2 p, int2 hi)
{
    float c = tex.Load(int3(clamp(p, 0, hi), 0));
    uint b = 0;
    [unroll] for (int k = 0; k < 8; ++k)
        if (tex.Load(int3(clamp(p + ring8(k), 0, hi), 0)) > c) b |= 1u << k;
    return b;
}

uint censusGs(int2 gc)
{
    float c = gsCurr[gc.y][gc.x];
    uint b = 0;
    [unroll] for (int k = 0; k < 8; ++k)
    {
        int2 q = gc + ring8(k);
        if (gsCurr[q.y][q.x] > c) b |= 1u << k;
    }
    return b;
}

uint censusGsPrev(int2 gp)
{
    float c = gsPrev[gp.y][gp.x];
    uint b = 0;
    [unroll] for (int k = 0; k < 8; ++k)
    {
        int2 q = gp + ring8(k);
        if (gsPrev[q.y][q.x] > c) b |= 1u << k;
    }
    return b;
}

float sad3t(Texture2D<float> a, Texture2D<float> b, int2 p, int2 d, int2 hi)
{
    float s = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx)
    {
        int2 q = p + int2(dx, dy);
        s += abs(a.Load(int3(clamp(q + d, 0, hi), 0)) - b.Load(int3(clamp(q, 0, hi), 0)));
    }
    return s;
}

float matchCost(Texture2D<float> a, Texture2D<float> b, int2 p, float2 v, int2 hi)
{
    int2 d = int2(round(v));
    float sad = sad3t(a, b, p, d, hi);
    uint ca = censusPx(a, p + d, hi);
    uint cb = censusPx(b, p, hi);
    float ham = (float)countbits(ca ^ cb);
    return SadW * sad + CensusW * ham + 0.004f * (abs(v.x) + abs(v.y));
}

[numthreads(8, 8, 1)]
void FG2_Luma(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uLuma.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    uLuma[id.xy] = luma709(tColor.Load(int3(id.xy, 0)).rgb);
}

[numthreads(8, 8, 1)]
void FG2_Halve(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uLuma.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    uLuma[id.xy] = tLumaIn.SampleLevel(linClamp, uv, 0);
}

[numthreads(8, 8, 1)]
void FG2_P8Search(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    uint W, H; uTv8.GetDimensions(W, H);
    int2 base = int2(gid.xy) * 8;
    int2 hi = int2(W - 1, H - 1);
    uint li = lt.y * 8 + lt.x;
    for (uint k = li; k < FS_N * FS_N; k += 64)
    {
        int2 q = clamp(base + int2(k % FS_N, k / FS_N) - (FS_R + 1), 0, hi);
        gsPrev[k / FS_N][k % FS_N] = tPrevL.Load(int3(q, 0));
    }
    for (uint k2 = li; k2 < 100; k2 += 64)
    {
        int2 q = clamp(base + int2(k2 % 10, k2 / 10) - 1, 0, hi);
        gsCurr[k2 / 10][k2 % 10] = tCurrL.Load(int3(q, 0));
    }
    GroupMemoryBarrierWithGroupSync();
    bool live = id.x < W && id.y < H;
    float c[9];
    [unroll] for (int j = 0; j < 9; ++j) c[j] = gsCurr[lt.y + j / 3][lt.x + j % 3];
    uint cenC = censusGs(int2(lt.x + 1, lt.y + 1));
    float bestCost = 1e20f; int2 best = 0; float bestSad = 0; float bestCen = 0; float second = 1e20f;
    for (int ci = 0; ci < FS_C; ++ci)
    {
        int dx = ci % (2 * FS_R + 1) - FS_R, dy = ci / (2 * FS_R + 1) - FS_R;
        float sad = 0;
        [unroll] for (int j2 = 0; j2 < 9; ++j2)
            sad += abs(gsPrev[lt.y + FS_R + dy + j2 / 3][lt.x + FS_R + dx + j2 % 3] - c[j2]);
        gsCost[li][ci] = live ? sad : 0.0f;
        uint cenP = censusGsPrev(int2(lt.x + FS_R + dx + 1, lt.y + FS_R + dy + 1));
        float ham = (float)countbits(cenP ^ cenC);
        float cost = SadW * sad + CensusW * ham + 0.004f * (abs(dx) + abs(dy));
        if (cost < bestCost) { if (AmbiguityGate > 0.5f) second = bestCost; bestCost = cost; best = int2(dx, dy); bestSad = sad; bestCen = ham; }
        else if (AmbiguityGate > 0.5f && cost < second) second = cost;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint cj = li; cj < FS_C; cj += 64)
    {
        float sum = 0;
        for (uint t = 0; t < 64; ++t) sum += gsCost[t][cj];
        int ddx = (int)(cj % (2 * FS_R + 1)) - FS_R, ddy = (int)(cj / (2 * FS_R + 1)) - FS_R;
        gsBlock[cj] = sum + 0.05f * (abs(ddx) + abs(ddy));
    }
    GroupMemoryBarrierWithGroupSync();
    if (!live) return;
    int bi = 0; float bc = 1e20f;
    for (int cb = 0; cb < FS_C; ++cb) if (gsBlock[cb] < bc) { bc = gsBlock[cb]; bi = cb; }
    int2 blockV = int2(bi % (2 * FS_R + 1) - FS_R, bi / (2 * FS_R + 1) - FS_R);
    float cBlock = gsCost[li][bi];
    if (cBlock <= bestSad + 0.06f)
    {
        best = blockV;
        bestSad = cBlock;
        uint cenP = censusGsPrev(int2(lt.x + FS_R + best.x + 1, lt.y + FS_R + best.y + 1));
        bestCen = (float)countbits(cenP ^ cenC);
        bestCost = SadW * bestSad + CensusW * bestCen + 0.004f * (abs(best.x) + abs(best.y));
    }
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    if (HistoryValid > 0.5f)
    {
        float2 tv = tMvPrev.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { bestCost = cost; best = int2(round(tv)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
    }
    if (HwScale > 0.5f)
    {
        uint hw, hh; tHw.GetDimensions(hw, hh);
        float grid = max(HwGrid, 1.0f);
        int2 hp = clamp(int2((float2(id.xy) + 0.5f) * 8.0f / grid), 0, int2(hw, hh) - 1);
        int2 hwv = tHw.Load(int3(hp, 0));
        float2 tv = float2(hwv) / HwScale / 8.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { bestCost = cost; best = int2(round(tv)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
    }
    {
        float2 tv = tGmv.Load(int3(0, 0, 0)) * inSize() / 8.0f;
        if (dot(tv, tv) > 1e-8f)
        {
            float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
            if (cost < bestCost) { bestCost = cost; best = int2(round(tv)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
        }
    }
    if (AffConf > 0.3f)
    {
        float2 c = (float2(id.xy) + 0.5f) * 8.0f;
        float2 p = float2(A00 * c.x + A01 * c.y + A02, A10 * c.x + A11 * c.y + A12);
        float2 tv = (p - c) / 8.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { if (AmbiguityGate > 0.5f) second = bestCost; bestCost = cost; best = int2(round(tv)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
        else if (AmbiguityGate > 0.5f && cost < second) second = cost;
    }
    if (WideSearch > 0.5f)
    {
        float2 tv = tCoarse.SampleLevel(pntClamp, uv, 0).xy * 2.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { if (AmbiguityGate > 0.5f) second = bestCost; bestCost = cost; best = int2(round(tv)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
        else if (AmbiguityGate > 0.5f && cost < second) second = cost;
        [unroll] for (int k = 0; k < 9; ++k)
        {
            float2 t2 = tv + float2(k % 3 - 1, k / 3 - 1);
            float c2 = matchCost(tPrevL, tCurrL, int2(id.xy), t2, hi);
            if (c2 < bestCost) { if (AmbiguityGate > 0.5f) second = bestCost; bestCost = c2; best = int2(round(t2)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
            else if (AmbiguityGate > 0.5f && c2 < second) second = c2;
        }
    }
    if (TemporalExtrap > 0.5f && History2Valid > 0.5f)
    {
        float2 h0 = tMvPrev.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float2 h1 = tMvHist2.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float2 ext = 2.0f * h0 - h1;
        bool use = true;
        if (AffConf > 0.3f)
        {
            float2 cpx = (float2(id.xy) + 0.5f) * 8.0f;
            float2 ap = float2(A00 * cpx.x + A01 * cpx.y + A02, A10 * cpx.x + A11 * cpx.y + A12);
            float2 aff = (ap - cpx) / 8.0f;
            if (length(ext - aff) > 4.0f) use = false;
        }
        if (use)
        {
            float cost = matchCost(tPrevL, tCurrL, int2(id.xy), ext, hi);
            if (cost < bestCost) { if (AmbiguityGate > 0.5f) second = bestCost; bestCost = cost; best = int2(round(ext)); bestSad = sad3t(tPrevL, tCurrL, int2(id.xy), best, hi); bestCen = (float)countbits(censusPx(tPrevL, int2(id.xy) + best, hi) ^ censusPx(tCurrL, int2(id.xy), hi)); }
            else if (AmbiguityGate > 0.5f && cost < second) second = cost;
        }
    }
    float ambW = AmbiguityGate > 0.5f ? max(second - bestCost, 0.0f) : bestCen;
    uTv8[id.xy] = float4(float2(best), bestSad, ambW);
}

#define P16_R 4
#define P16_STEP 4
#define P16_N (8 + 2 * P16_R * P16_STEP + 2)
#define P16_C ((2 * P16_R + 1) * (2 * P16_R + 1))
groupshared float gs16Prev[P16_N][P16_N];
groupshared float gs16Curr[10][10];
groupshared float gs16Cost[64][P16_C];
groupshared float gs16Block[P16_C];

[numthreads(8, 8, 1)]
void FG2_P16Search(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint3 lt : SV_GroupThreadID)
{
    uint W, H; uTv8.GetDimensions(W, H);
    int2 base = int2(gid.xy) * 8;
    int2 hi = int2(W - 1, H - 1);
    uint li = lt.y * 8 + lt.x;
    int step = (int)clamp(floor((MvGapN + 1.0f) * 0.5f), 1.0f, 4.0f);
    int rad = P16_R * step;
    for (uint k = li; k < (uint)(P16_N * P16_N); k += 64)
    {
        int2 q = clamp(base + int2(k % P16_N, k / P16_N) - (rad + 1), 0, hi);
        gs16Prev[k / P16_N][k % P16_N] = tPrevL.Load(int3(q, 0));
    }
    for (uint k2 = li; k2 < 100; k2 += 64)
    {
        int2 q = clamp(base + int2(k2 % 10, k2 / 10) - 1, 0, hi);
        gs16Curr[k2 / 10][k2 % 10] = tCurrL.Load(int3(q, 0));
    }
    GroupMemoryBarrierWithGroupSync();
    bool live = id.x < W && id.y < H;
    float c[9];
    [unroll] for (int j = 0; j < 9; ++j) c[j] = gs16Curr[lt.y + j / 3][lt.x + j % 3];
    uint cenC = 0;
    {
        float cc = gs16Curr[lt.y + 1][lt.x + 1];
        [unroll] for (int k = 0; k < 8; ++k)
        {
            int2 q = int2(lt.x + 1, lt.y + 1) + ring8(k);
            if (gs16Curr[q.y][q.x] > cc) cenC |= 1u << k;
        }
    }
    float bestCost = 1e20f; int2 best = 0;
    for (int ci = 0; ci < P16_C; ++ci)
    {
        int dx = (ci % (2 * P16_R + 1) - P16_R) * step, dy = (ci / (2 * P16_R + 1) - P16_R) * step;
        float sad = 0;
        [unroll] for (int j2 = 0; j2 < 9; ++j2)
            sad += abs(gs16Prev[lt.y + rad + dy + j2 / 3][lt.x + rad + dx + j2 % 3] - c[j2]);
        gs16Cost[li][ci] = live ? sad : 0.0f;
        uint cenP = 0;
        {
            int2 gp = int2(lt.x + rad + dx + 1, lt.y + rad + dy + 1);
            float pc = gs16Prev[gp.y][gp.x];
            [unroll] for (int k = 0; k < 8; ++k)
            {
                int2 q = gp + ring8(k);
                if (gs16Prev[q.y][q.x] > pc) cenP |= 1u << k;
            }
        }
        float ham = (float)countbits(cenP ^ cenC);
        float cost = SadW * sad + CensusW * ham + 0.004f * (abs(dx) + abs(dy));
        if (cost < bestCost) { bestCost = cost; best = int2(dx, dy); }
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint cj = li; cj < (uint)P16_C; cj += 64)
    {
        float sum = 0;
        for (uint t = 0; t < 64; ++t) sum += gs16Cost[t][cj];
        int ddx = ((int)(cj % (2 * P16_R + 1)) - P16_R) * step, ddy = ((int)(cj / (2 * P16_R + 1)) - P16_R) * step;
        gs16Block[cj] = sum + 0.05f * (abs(ddx) + abs(ddy));
    }
    GroupMemoryBarrierWithGroupSync();
    if (!live) return;
    int bi = 0; float bc = 1e20f;
    for (int cb = 0; cb < P16_C; ++cb) if (gs16Block[cb] < bc) { bc = gs16Block[cb]; bi = cb; }
    int2 blockV = int2((bi % (2 * P16_R + 1) - P16_R) * step, (bi / (2 * P16_R + 1) - P16_R) * step);
    float cBlock = gs16Cost[li][bi];
    int bix = clamp(best.x / max(step, 1) + P16_R, 0, 2 * P16_R);
    int biy = clamp(best.y / max(step, 1) + P16_R, 0, 2 * P16_R);
    if (cBlock <= gs16Cost[li][biy * (2 * P16_R + 1) + bix] + 0.06f)
        best = blockV;
    uTv8[id.xy] = float4(float2(best), 0, 0);
}

[numthreads(8, 8, 1)]
void FG2_P8Prop(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uTv8.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 hi = int2(W - 1, H - 1);
    int2 p = int2(id.xy);
    float2 vs[9];
    float sad[9];
    float ham[9];
    [unroll] for (int k = 0; k < 9; ++k)
    {
        int2 q = clamp(p + int2(k % 3 - 1, k / 3 - 1), 0, hi);
        float4 n = tMv.Load(int3(q, 0));
        vs[k] = n.xy;
        int2 d = int2(round(n.xy));
        sad[k] = sad3t(tPrevL, tCurrL, p, d, hi);
        ham[k] = (float)countbits(censusPx(tPrevL, p + d, hi) ^ censusPx(tCurrL, p, hi));
    }
    float4 self = tMv.Load(int3(p, 0));
    float lam = Lambda;
    if (AmbiguityGate > 0.5f) lam = Lambda * saturate(1.0f - self.w * 4.0f);
    int bestI = 0; float bestC = 1e20f;
    [unroll] for (int i = 0; i < 9; ++i)
    {
        float sm = 0;
        [unroll] for (int j = 0; j < 9; ++j)
        {
            if (j == i) continue;
            sm += length(vs[i] - vs[j]);
        }
        sm *= 0.125f;
        float cost = sad[i] + CensusW * ham[i] + lam * sm;
        if (cost < bestC) { bestC = cost; bestI = i; }
    }
    float2 outV = vs[bestI];
    if (AmbiguityGate > 0.5f && HistoryValid > 0.5f && self.w < 0.08f)
    {
        float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
        float2 hv = tMvPrev.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        int2 d = int2(round(hv));
        float hs = sad3t(tPrevL, tCurrL, p, d, hi);
        float hh = (float)countbits(censusPx(tPrevL, p + d, hi) ^ censusPx(tCurrL, p, hi));
        float hc = hs + CensusW * hh;
        if (hc < bestC) outV = hv;
    }
    uTv8[id.xy] = float4(outV, self.z, self.w);
}

[numthreads(8, 8, 1)]
void FG2_P4Refine(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uTv4.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    uint lw, lh; tCurrL.GetDimensions(lw, lh);
    int2 hi = int2(lw, lh) - 1;
    int2 p = int2((float2(id.xy) + 0.5f) * float2(lw, lh) / float2(W, H));
    float2 seed = tMv.Load(int3(id.xy, 0)).xy * 2.0f;
    float bestC = 1e20f; float2 best = seed;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx)
    {
        float2 v = seed + float2(dx, dy);
        float cost = matchCost(tPrevL, tCurrL, p, v, hi);
        if (cost < bestC) { bestC = cost; best = v; }
    }
    uTv4[id.xy] = best;
}

void lkAccum(int k, float cc, float gx, float gy, inout float cv[25], inout float2 gr[25], inout float3 a)
{
    float wk = exp(-0.25f * float((k % 5 - 2) * (k % 5 - 2) + (k / 5 - 2) * (k / 5 - 2)));
    cv[k] = cc; gr[k] = float2(gx, gy) * wk;
    a += float3(gx * gx, gx * gy, gy * gy) * wk;
}

void lkPrepTex(int2 pix, int2 hiL, out float cv[25], out float2 gr[25], out float3 a)
{
    a = 0;
    [unroll] for (int k = 0; k < 25; ++k)
    {
        int2 q = pix + int2(k % 5 - 2, k / 5 - 2);
        float cc = tCurrL.Load(int3(clamp(q, 0, hiL), 0));
        float gx = 0.5f * (tCurrL.Load(int3(clamp(q + int2(1, 0), 0, hiL), 0)) - tCurrL.Load(int3(clamp(q - int2(1, 0), 0, hiL), 0)));
        float gy = 0.5f * (tCurrL.Load(int3(clamp(q + int2(0, 1), 0, hiL), 0)) - tCurrL.Load(int3(clamp(q - int2(0, 1), 0, hiL), 0)));
        lkAccum(k, cc, gx, gy, cv, gr, a);
    }
}

float2 lkSolve(float cv[25], float2 gr[25], float3 a, float2 uv, float2 mvUV, float2 texel, float maxStep, int iters)
{
    float a11 = a.x + 1e-4f, a12 = a.y, a22 = a.z + 1e-4f;
    float det = a11 * a22 - a12 * a12;
    float2 d = 0;
    [unroll] for (int itr = 0; itr < 2; ++itr)
    {
        if (itr < iters)
        {
            float b1 = 0, b2 = 0;
            [unroll] for (int k2 = 0; k2 < 25; ++k2)
            {
                float2 o = float2(k2 % 5 - 2, k2 / 5 - 2);
                float e = tPrevL.SampleLevel(linClamp, uv + (o + d) * texel + mvUV, 0) - cv[k2];
                b1 += gr[k2].x * e; b2 += gr[k2].y * e;
            }
            float2 st = -float2(a22 * b1 - a12 * b2, a11 * b2 - a12 * b1) / det;
            d = clamp(d + clamp(st, -0.75f, 0.75f), -maxStep, maxStep);
        }
    }
    return mvUV + d * texel;
}

float2 camVecPx(float2 px)
{
    return float2(A00 * px.x + A01 * px.y + A02, A10 * px.x + A11 * px.y + A12) - px;
}

float2 maybeSnap(float2 vPx, float2 px)
{
    if (SnapOn < 0.5f || AffConf < 1e-4f) return vPx;
    float2 fit = camVecPx(px);
    if (length(vPx - fit) > RansacPx) return fit;
    return vPx;
}

[numthreads(8, 8, 1)]
void FG2_P2Final(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uMv2.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    uint lw, lh; tCurrL.GetDimensions(lw, lh);
    int2 hiL = int2(lw, lh) - 1;
    int2 pix = clamp(int2((float2(id.xy) + 0.5f) * float2(lw, lh) / float2(W, H)), 0, hiL);
    float2 texel = 1.0f / float2(lw, lh);
    float2 uv = (float2(pix) + 0.5f) * texel;
    float2 seedL1 = tMv.Load(int3(id.xy, 0)).xy * 2.0f;
    float2 mvUV = seedL1 * texel;
    float cv[25]; float2 gr[25]; float3 a;
    lkPrepTex(pix, hiL, cv, gr, a);
    mvUV = lkSolve(cv, gr, a, uv, mvUV, texel, 1.5f, 1);
    float2 vPx = mvUV * inSize();
    float2 cPx = (float2(id.xy) + 0.5f) * 8.0f;
    vPx = maybeSnap(vPx, cPx);
    float cost = matchCost(tPrevL, tCurrL, pix, mvUV / texel, hiL);
    float conf = saturate(1.0f - cost / max(OcclThresh * 9.0f, 1e-3f));
    uMv2[id.xy] = float4(vPx, conf, 0);
}

[numthreads(8, 8, 1)]
void FG2_Fwd(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uFwd.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 texel = 1.0f / float2(W, H);
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    float4 m = tMv.SampleLevel(linClamp, uv, 0);
    float2 f = -m.xy / inSize();
    [unroll] for (int i = 0; i < 3; ++i) { m = tMv.SampleLevel(linClamp, uv + f, 0); f = -m.xy / inSize(); }
    uFwd[id.xy] = float4(-f * inSize(), 0, 0);
}

[numthreads(8, 8, 1)]
void FG2_Consistency(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOccl.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 bwd = tMv.Load(int3(id.xy, 0)).xy;
    float2 tile = float2(id.xy) + 0.5f;
    float2 dest = tile + bwd / 8.0f;
    float2 uv = dest / float2(W, H);
    float2 fwd = tFwd.SampleLevel(linClamp, uv, 0).xy;
    float e = length(fwd + bwd);
    float thr = OcclThresh + SoftOccK * length(bwd);
    uOccl[id.xy] = saturate((e - 0.5f * thr) / max(0.5f * thr, 1e-6f));
}

[numthreads(8, 8, 1)]
void FG2_StaticCount(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uCount.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy);
    float d = abs(tCurrL.Load(int3(p, 0)) - tPrevL.Load(int3(p, 0)));
    float n = uCount[id.xy];
    uCount[id.xy] = (HistoryValid > 0.5f && d <= StaticThresh) ? min(n + 1.0f, 64.0f) : 0.0f;
}

[numthreads(8, 8, 1)]
void FG2_HudMask(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uHud.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy), hi = int2(W - 1, H - 1);
    float mn = 64.0f, lo = 1e9f, hiL = -1e9f;
    [unroll] for (int dy = -2; dy <= 2; ++dy)
    [unroll] for (int dx = -2; dx <= 2; ++dx)
    {
        int2 q = clamp(p + int2(dx, dy), 0, hi);
        mn = min(mn, tCount.Load(int3(q, 0)));
        if (abs(dx) <= 1 && abs(dy) <= 1) { float s = tCurrL.Load(int3(q, 0)); lo = min(lo, s); hiL = max(hiL, s); }
    }
    uHud[id.xy] = (mn >= 3.0f && hiL - lo > 0.06f) ? 1.0f : 0.0f;
}

[numthreads(16, 16, 1)]
void FG2_SceneCut(uint3 lt : SV_GroupThreadID)
{
    float n = 0.0f, nc = 0.0f;
    uint i = lt.y * 16 + lt.x;
    if (i < 32) { gsHa[i] = 0; gsHb[i] = 0; }
    GroupMemoryBarrierWithGroupSync();
    [loop] for (uint j = 0; j < 16; ++j)
    {
        uint2 q = lt.xy * 4 + uint2(j & 3, j >> 2);
        float2 uv = (float2(q) + 0.5f) / 64.0f;
        float l = luma709(tColor.SampleLevel(pntClamp, uv, 0).rgb);
        float dl = abs(l - uChgGrid[q]);
        if (HistoryValid < 0.5f || dl > StaticThresh) n += 1.0f;
        if (HistoryValid > 0.5f && dl > 0.15f) nc += 1.0f;
        uint dummy;
        InterlockedAdd(gsHa[min((uint)(saturate(uChgGrid[q]) * 32.0f), 31u)], 1u, dummy);
        InterlockedAdd(gsHb[min((uint)(saturate(l) * 32.0f), 31u)], 1u, dummy);
        uChgGrid[q] = l;
    }
    gsChg[i] = n; gsCut[i] = nc;
    GroupMemoryBarrierWithGroupSync();
    float hd = 0.0f;
    if (i == 0) { [loop] for (uint b = 0; b < 32; ++b) hd += abs((float)gsHa[b] - (float)gsHb[b]); hd *= 0.5f / 4096.0f; }
    [unroll] for (uint s = 128; s > 0; s >>= 1) { if (i < s) { gsChg[i] += gsChg[i + s]; gsCut[i] += gsCut[i + s]; } GroupMemoryBarrierWithGroupSync(); }
    if (i == 0) { uLuma[uint2(0, 0)] = gsChg[0]; uLuma[uint2(1, 0)] = hd > 0.35f ? gsCut[0] : 0.0f; }
}

bool cutOn() { return tChg.Load(int3(1, 0, 0)) > SceneCutThresh; }

float2 affineUv(float2 uv)
{
    return camVecPx(uv * inSize()) / inSize();
}

float4 pickTileV(float2 uv, float t)
{
    uint mw, mh; tMv.GetDimensions(mw, mh);
    float2 os = 1.0f / float2(mw, mh);
    float4 s0 = EdgeSmooth > 0.5f ? tSmooth.SampleLevel(linClamp, uv, 0) : tMv.SampleLevel(pntClamp, uv, 0);
    float2 best = s0.xy / inSize();
    float conf = s0.z;
    float bc = 1e20f;
    if (EdgeSmooth > 0.5f)
    {
        float2 pB0 = uv - (1.0f - t) * best;
        float2 pA0 = pB0 + best;
        float3 bB0 = tCurrCol.SampleLevel(linClamp, pB0, 0).rgb;
        float3 bA0 = tColor.SampleLevel(linClamp, pA0, 0).rgb;
        bc = dot(abs(bA0 - bB0), 1.0f);
    }
    else
    {
        [unroll] for (int k = 0; k < 9; ++k)
        {
            float2 o = float2(k % 3 - 1, k / 3 - 1);
            float4 s = tMv.SampleLevel(pntClamp, uv + o * os, 0);
            float2 v = s.xy / inSize();
            float2 pB = uv - (1.0f - t) * v;
            float2 pA = pB + v;
            float3 bB = tCurrCol.SampleLevel(linClamp, pB, 0).rgb;
            float3 bA = tColor.SampleLevel(linClamp, pA, 0).rgb;
            float c = dot(abs(bA - bB), 1.0f);
            if (c < bc) { bc = c; best = v; conf = s.z; }
        }
    }
    if (ClassicCand > 0.5f)
    {
        float4 s = tClassic.SampleLevel(linClamp, uv, 0);
        float2 v = s.xy / inSize();
        float2 pB = uv - (1.0f - t) * v;
        float2 pA = pB + v;
        float3 bB = tCurrCol.SampleLevel(linClamp, pB, 0).rgb;
        float3 bA = tColor.SampleLevel(linClamp, pA, 0).rgb;
        float c = dot(abs(bA - bB), 1.0f);
        if (c < bc) { bc = c; best = v; conf = s.z; }
    }
    if (conf < ConfThresh && AffConf > 0.3f) best = affineUv(uv);
    float2 vPx = maybeSnap(best * inSize(), uv * inSize());
    best = vPx / inSize();
    return float4(best, conf, 0);
}

float3 fg2Bicubic(Texture2D<float4> tex, float2 uv)
{
    uint w, h;
    tex.GetDimensions(w, h);
    float2 size = float2(w, h);
    int2 hi = int2(w, h) - 1;
    float2 pos = uv * size;
    float2 tc = floor(pos - 0.5f) + 0.5f;
    float2 f = pos - tc;
    float2 cw0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 cw1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 cw2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 cw3 = f * f * (-0.5f + 0.5f * f);
    float2 cw12 = cw1 + cw2;
    float2 t12 = (tc + cw2 / cw12) / size;
    float2 t0 = (tc - 1.0f) / size, t3 = (tc + 2.0f) / size;
    float3 r = tex.SampleLevel(linClamp, float2(t12.x, t0.y), 0).rgb * (cw12.x * cw0.y)
             + tex.SampleLevel(linClamp, float2(t0.x, t12.y), 0).rgb * (cw0.x * cw12.y)
             + tex.SampleLevel(linClamp, t12, 0).rgb * (cw12.x * cw12.y)
             + tex.SampleLevel(linClamp, float2(t3.x, t12.y), 0).rgb * (cw3.x * cw12.y)
             + tex.SampleLevel(linClamp, float2(t12.x, t3.y), 0).rgb * (cw12.x * cw3.y);
    float ws = cw12.x * cw0.y + cw0.x * cw12.y + cw12.x * cw12.y + cw3.x * cw12.y + cw12.x * cw3.y;
    r /= max(ws, 1e-4f);
    int2 p0 = int2(floor(pos - 0.5f));
    float3 c00 = tex.Load(int3(clamp(p0, 0, hi), 0)).rgb;
    float3 c10 = tex.Load(int3(clamp(p0 + int2(1, 0), 0, hi), 0)).rgb;
    float3 c01 = tex.Load(int3(clamp(p0 + int2(0, 1), 0, hi), 0)).rgb;
    float3 c11 = tex.Load(int3(clamp(p0 + int2(1, 1), 0, hi), 0)).rgb;
    float3 mn = min(min(c00, c10), min(c01, c11));
    float3 mx = max(max(c00, c10), max(c01, c11));
    return clamp(r, mn, mx);
}

float4 fg2Compose(float2 uv, float t, float2 vb, float conf, float occl)
{
    float3 a0 = tColor.SampleLevel(linClamp, uv, 0).rgb;
    float3 b0 = tCurrCol.SampleLevel(linClamp, uv, 0).rgb;
    float hud = tHud.SampleLevel(pntClamp, uv, 0);
    if (hud > 0.5f) return float4(b0, 1.0f);
    if (max3(abs(a0 - b0)) < StaticThresh) return float4(b0, 1.0f);
    float2 pB = uv - (1.0f - t) * vb, pA = pB + vb;
    float3 bB, bA;
    [branch] if (BicubicOn > 0.5f)
    {
        bB = fg2Bicubic(tCurrCol, pB);
        bA = fg2Bicubic(tColor, pA);
    }
    else
    {
        bB = tCurrCol.SampleLevel(linClamp, pB, 0).rgb;
        bA = tColor.SampleLevel(linClamp, pA, 0).rgb;
    }
    float dcol = max3(abs(bA - bB));
    float colW = saturate((dcol - ColorGate) / max(ColorGate, 1e-4f));
    float vlen = length(vb * inSize());
    float occ = occl * colW * saturate(vlen * VecLenK);
    bool okA = inside(pA), okB = inside(pB);
    float wA = (1.0f - t) * (okA ? 1.0f : 0.0f);
    float wB = t * (okB ? 1.0f : 0.0f);
    wA *= (1.0f - occ);
    wB *= (1.0f - occ * (1.0f - occ));
    if (!okA && okB) { wA = 0; wB = 1; }
    if (!okB && okA) { wB = 0; wA = 1; }
    float ws = wA + wB;
    float3 mix = ws > 1e-4f ? (bA * wA + bB * wB) / ws : lerp(a0, b0, t);
    [branch] if (SharpBlendOn > 0.5f && okA && okB && dcol > ColorGate)
    {
        float sadA = 0, sadB = 0;
        float2 texel = 1.0f / inSize();
        int2 hi = int2(inSize()) - 1;
        int2 ia = int2(floor(pA * inSize()));
        int2 ib = int2(floor(pB * inSize()));
        [unroll] for (int dy = -1; dy <= 1; ++dy)
        [unroll] for (int dx = -1; dx <= 1; ++dx)
        {
            float2 o = float2(dx, dy) * texel;
            float3 nA = tColor.Load(int3(clamp(ia + int2(dx, dy), 0, hi), 0)).rgb;
            float3 nB = tCurrCol.Load(int3(clamp(ib + int2(dx, dy), 0, hi), 0)).rgb;
            float3 wBo = tCurrCol.SampleLevel(linClamp, pB + o, 0).rgb;
            float3 wAo = tColor.SampleLevel(linClamp, pA + o, 0).rgb;
            sadA += dot(abs(nA - wBo), 1.0f);
            sadB += dot(abs(nB - wAo), 1.0f);
        }
        float wSharp = sadB / max(sadA + sadB, 1e-6f);
        mix = lerp(bB, bA, wSharp);
    }
    if (conf < ConfThresh)
    {
        float k = saturate(conf / max(ConfThresh, 1e-4f));
        if (Fallback < 0.5f)
            mix = lerp(t < 0.5f ? a0 : b0, mix, k);
        else if (Fallback < 1.5f)
            mix = lerp(lerp(a0, b0, t), mix, k);
    }
    return float4(mix, 1.0f);
}

float4 fg2Pixel(float2 uv, float t)
{
    float3 a0 = tColor.SampleLevel(linClamp, uv, 0).rgb;
    float3 b0 = tCurrCol.SampleLevel(linClamp, uv, 0).rgb;
    if (cutOn()) return float4(t < 0.5f ? a0 : b0, 1.0f);
    float4 pk = pickTileV(uv, t);
    float occl = tFwd.SampleLevel(pntClamp, uv, 0).x;
    return fg2Compose(uv, t, pk.xy, pk.z, occl);
}

[numthreads(8, 8, 1)]
void FG2_EdgeSmooth(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uMv2.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float3 colB = tCurrCol.SampleLevel(linClamp, uv, 0).rgb;
    float3 colA = tColor.SampleLevel(linClamp, uv, 0).rgb;
    float yB = luma709(colB), yA = luma709(colA);
    uint mw, mh; tMv.GetDimensions(mw, mh);
    float2 acc = 0;
    float wsum = 0;
    float conf = 0;
    [unroll] for (int dy = -2; dy <= 2; ++dy)
    [unroll] for (int dx = -2; dx <= 2; ++dx)
    {
        float2 ouv = uv + float2(dx, dy) / float2(mw, mh);
        float4 s = tMv.SampleLevel(pntClamp, ouv, 0);
        float2 suv = uv + float2(dx, dy) * 8.0f / inSize();
        float3 nB = tCurrCol.SampleLevel(linClamp, suv, 0).rgb;
        float3 nA = tColor.SampleLevel(linClamp, suv, 0).rgb;
        float dc = 0.5f * (abs(luma709(nB) - yB) + abs(luma709(nA) - yA));
        float wc = exp(-dc / 0.08f);
        float ws = exp(-length(float2(dx, dy)) / 1.5f);
        float w = wc * ws;
        acc += s.xy * w;
        conf += s.z * w;
        wsum += w;
    }
    uMv2[id.xy] = float4(acc / max(wsum, 1e-6f), conf / max(wsum, 1e-6f), 0);
}

[numthreads(8, 8, 1)]
void FG2_Interp(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    uMv2[id.xy] = fg2Pixel(uv, InterpT);
}

[numthreads(8, 8, 1)]
void FG2_InterpFast(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    min16float t = min16float(InterpT);
    if (cutOn())
    {
        min16float3 a0 = min16float3(tColor.SampleLevel(linClamp, uv, 0).rgb);
        min16float3 b0 = min16float3(tCurrCol.SampleLevel(linClamp, uv, 0).rgb);
        uMv2[id.xy] = float4(InterpT < 0.5f ? (float3)a0 : (float3)b0, 1.0f);
        return;
    }
    if (tHud.SampleLevel(pntClamp, uv, 0) > 0.5f)
    {
        uMv2[id.xy] = float4(tCurrCol.SampleLevel(linClamp, uv, 0).rgb, 1.0f);
        return;
    }
    float4 s0 = tMv.SampleLevel(pntClamp, uv, 0);
    float2 vb = s0.xy / inSize();
    float2 pB = uv - (1.0f - InterpT) * vb;
    float2 pA = pB + vb;
    min16float3 bB = min16float3(tCurrCol.SampleLevel(linClamp, pB, 0).rgb);
    min16float3 bA = min16float3(tColor.SampleLevel(linClamp, pA, 0).rgb);
    min16float3 mix = bA * (min16float(1.0f) - t) + bB * t;
    bool okA = inside(pA), okB = inside(pB);
    if (!okA && okB) mix = bB;
    else if (!okB && okA) mix = bA;
    else if (!okA && !okB)
    {
        min16float3 a0 = min16float3(tColor.SampleLevel(linClamp, uv, 0).rgb);
        min16float3 b0 = min16float3(tCurrCol.SampleLevel(linClamp, uv, 0).rgb);
        mix = a0 * (min16float(1.0f) - t) + b0 * t;
    }
    uMv2[id.xy] = float4((float3)mix, 1.0f);
}

[numthreads(8, 8, 1)]
void FG2_InterpOracle(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    float2 bwdPx = tMv.SampleLevel(pntClamp, uv, 0).xy;
    float2 vb = bwdPx / inSize();
    float2 dest = uv + vb;
    float2 fwdPx = tFwd.SampleLevel(linClamp, dest, 0).xy;
    float e = length(fwdPx + bwdPx);
    float thr = OcclThresh + SoftOccK * length(bwdPx);
    float occl = saturate((e - 0.5f * thr) / max(0.5f * thr, 1e-6f));
    uMv2[id.xy] = fg2Compose(uv, InterpT, vb, 1.0f, occl);
}

[numthreads(8, 8, 1)]
void FG2_InterpN(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    int n = (int)FrameCount;
    float inv = 1.0f / (FrameCount + 1.0f);
    uG1[id.xy] = fg2Pixel(uv, 1.0f * inv);
    if (n > 1) uG2[id.xy] = fg2Pixel(uv, 2.0f * inv);
    if (n > 2) uG3[id.xy] = fg2Pixel(uv, 3.0f * inv);
    if (n > 3) uG4[id.xy] = fg2Pixel(uv, 4.0f * inv);
    if (n > 4) uG5[id.xy] = fg2Pixel(uv, 5.0f * inv);
    if (n > 5) uG6[id.xy] = fg2Pixel(uv, 6.0f * inv);
    if (n > 6) uG7[id.xy] = fg2Pixel(uv, 7.0f * inv);
}

[numthreads(8, 8, 1)]
void FG2_Cleanup(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
{
    uint W, H; uClean.GetDimensions(W, H);
    if (gi == 0)
    {
        float2 uv = (float2(gid.xy) * 8.0f + 4.0f) / float2(W, H);
        gsEarly = tFwd.SampleLevel(pntClamp, uv, 0).x;
        float2 vb = tMv.SampleLevel(pntClamp, uv, 0).xy / inSize();
        float t = InterpT;
        float2 pB = uv - (1.0f - t) * vb, pA = pB + vb;
        float3 bB = tCurrCol.SampleLevel(linClamp, pB, 0).rgb;
        float3 bA = tColor.SampleLevel(linClamp, pA, 0).rgb;
        if (max3(abs(bA - bB)) > OcclThresh) gsEarly = 1.0f;
    }
    GroupMemoryBarrierWithGroupSync();
    if (id.x >= W || id.y >= H) return;
    float4 c = tSrc.Load(int3(id.xy, 0));
    if (gsEarly < 0.5f) { uClean[id.xy] = c; return; }
    int2 hi = int2(W, H) - 1;
    float3 acc = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx)
        acc += tSrc.Load(int3(clamp(int2(id.xy) + int2(dx, dy), 0, hi), 0)).rgb;
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float2 vb = tMv.SampleLevel(pntClamp, uv, 0).xy / inSize();
    float t = InterpT;
    float2 pB = uv - (1.0f - t) * vb, pA = pB + vb;
    float3 bB = tCurrCol.SampleLevel(linClamp, pB, 0).rgb;
    float3 bA = tColor.SampleLevel(linClamp, pA, 0).rgb;
    float resid = max3(abs(bA - bB));
    float w = saturate((resid - OcclThresh) / max(OcclThresh, 1e-3f));
    uClean[id.xy] = float4(lerp(c.rgb, acc / 9.0f, w), 1.0f);
}

#define GM_W 64
#define GM_H 36
#define GM_RX 12
#define GM_RY 6
#define GM_NC ((2 * GM_RX + 1) * (2 * GM_RY + 1))
groupshared float gmA[GM_H * GM_W];
groupshared float gmB[GM_H * GM_W];
groupshared float gmCost[GM_NC];

[numthreads(256, 1, 1)]
void FG2_GlobalMotion(uint3 lt : SV_GroupThreadID)
{
    uint li = lt.x;
    float2 q = 0.25f / float2(GM_W, GM_H);
    for (uint k = li; k < GM_W * GM_H; k += 256)
    {
        float2 uv = (float2(k % GM_W, k / GM_W) + 0.5f) / float2(GM_W, GM_H);
        float a = 0, b = 0;
        [unroll] for (int t = 0; t < 4; ++t)
        {
            float2 o = float2((t & 1) ? q.x : -q.x, (t & 2) ? q.y : -q.y);
            a += tPrevL.SampleLevel(linClamp, uv + o, 0);
            b += tCurrL.SampleLevel(linClamp, uv + o, 0);
        }
        gmA[k] = 0.25f * a;
        gmB[k] = 0.25f * b;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint c = li; c < GM_NC; c += 256)
    {
        int dx = (int)(c % (2 * GM_RX + 1)) - GM_RX, dy = (int)(c / (2 * GM_RX + 1)) - GM_RY;
        int x0 = max(0, -dx), x1 = min(GM_W, GM_W - dx), y0 = max(0, -dy), y1 = min(GM_H, GM_H - dy);
        float sum = 0, n = 0;
        for (int y = y0; y < y1; ++y)
            for (int x = x0; x < x1; ++x) { sum += abs(gmA[(y + dy) * GM_W + x + dx] - gmB[y * GM_W + x]); n += 1.0f; }
        gmCost[c] = (n >= 400.0f ? sum / n : 1e9f) + 0.0005f * float(abs(dx) + abs(dy));
    }
    GroupMemoryBarrierWithGroupSync();
    if (li != 0) return;
    uint bi = 0; float bc = 1e20f;
    for (uint c2 = 0; c2 < GM_NC; ++c2) if (gmCost[c2] < bc) { bc = gmCost[c2]; bi = c2; }
    int ix = (int)(bi % (2 * GM_RX + 1)), iy = (int)(bi / (2 * GM_RX + 1));
    float2 g = float2(ix - GM_RX, iy - GM_RY);
    if (ix > 0 && ix < 2 * GM_RX)
    {
        float cl = gmCost[bi - 1], cr = gmCost[bi + 1], d = cl + cr - 2.0f * bc;
        if (d > 1e-9f) g.x += clamp(0.5f * (cl - cr) / d, -0.5f, 0.5f);
    }
    if (iy > 0 && iy < 2 * GM_RY)
    {
        float cu = gmCost[bi - (2 * GM_RX + 1)], cd = gmCost[bi + (2 * GM_RX + 1)], d = cu + cd - 2.0f * bc;
        if (d > 1e-9f) g.y += clamp(0.5f * (cu - cd) / d, -0.5f, 0.5f);
    }
    uTv4[uint2(0, 0)] = g / float2(GM_W, GM_H);
}

[numthreads(8, 8, 1)]
void FG2_HudDownsample(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uLuma.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    uLuma[id.xy] = tHud.SampleLevel(linClamp, uv, 0);
}

[numthreads(8, 8, 1)]
void FG2_HudUpload(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uHud.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(w, h);
    float s = tHudLo.SampleLevel(pntClamp, uv, 0);
    uHud[id.xy] = max(uHud[id.xy], s);
}

[numthreads(8, 8, 1)]
void FG2_Extrap(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    float2 f = tFwd.SampleLevel(linClamp, uv, 0).xy / inSize();
    float2 src = uv - InterpT * f;
    if (inside(src))
    {
        uMv2[id.xy] = tColor.SampleLevel(linClamp, src, 0);
        return;
    }
    float3 acc = 0;
    float n = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx)
    {
        float2 q = src + float2(dx, dy) / float2(ow, oh);
        if (inside(q)) { acc += tColor.SampleLevel(linClamp, q, 0).rgb; n += 1.0f; }
    }
    float3 fb = tColor.SampleLevel(linClamp, uv, 0).rgb;
    uMv2[id.xy] = float4(n > 0.5f ? acc / n : fb, 1.0f);
}

[numthreads(8, 8, 1)]
void FG2_Luma8(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uLuma.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    uLuma[id.xy] = saturate(tLumaIn.Load(int3(id.xy, 0)));
}

[numthreads(8, 8, 1)]
void FG2_NvofImport(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uTv8.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    int2 hi = int2(W - 1, H - 1);
    float2 uv = (float2(id.xy) + 0.5f) / float2(W, H);
    float bestCost = matchCost(tPrevL, tCurrL, int2(id.xy), 0, hi);
    float2 best = 0;
    if (HistoryValid > 0.5f)
    {
        float2 tv = tMvPrev.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { bestCost = cost; best = tv; }
    }
    if (HwScale > 0.5f)
    {
        uint hw, hh; tHw.GetDimensions(hw, hh);
        float grid = max(HwGrid, 1.0f);
        int2 hp = clamp(int2((float2(id.xy) + 0.5f) * 8.0f / grid), 0, int2(hw, hh) - 1);
        int2 hwv = tHw.Load(int3(hp, 0));
        float2 tv = float2(hwv) / HwScale / 8.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { bestCost = cost; best = tv; }
    }
    if (WideSearch > 0.5f)
    {
        float2 tv = tCoarse.SampleLevel(pntClamp, uv, 0).xy * 2.0f;
        float cost = matchCost(tPrevL, tCurrL, int2(id.xy), tv, hi);
        if (cost < bestCost) { bestCost = cost; best = tv; }
    }
    if (TemporalExtrap > 0.5f && History2Valid > 0.5f)
    {
        float2 h0 = tMvPrev.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float2 h1 = tMvHist2.SampleLevel(pntClamp, uv, 0).xy / 8.0f;
        float2 ext = 2.0f * h0 - h1;
        bool use = true;
        if (AffConf > 0.3f)
        {
            float2 cpx = (float2(id.xy) + 0.5f) * 8.0f;
            float2 ap = float2(A00 * cpx.x + A01 * cpx.y + A02, A10 * cpx.x + A11 * cpx.y + A12);
            float2 aff = (ap - cpx) / 8.0f;
            if (length(ext - aff) > 4.0f) use = false;
        }
        if (use)
        {
            float cost = matchCost(tPrevL, tCurrL, int2(id.xy), ext, hi);
            if (cost < bestCost) { bestCost = cost; best = ext; }
        }
    }
    uTv8[id.xy] = float4(best, 0, 0);
}

float bilinearLoadSlice(Texture2DArray<float> tex, float2 uv, int slice, int2 size)
{
    float2 tc = uv * float2(size) - 0.5f;
    float2 i0f = floor(tc);
    float2 f = tc - i0f;
    int2 i0 = clamp((int2)i0f, 0, size - 1);
    int2 i1 = clamp(i0 + 1, 0, size - 1);
    float v00 = tex[uint3(i0.x, i0.y, slice)];
    float v10 = tex[uint3(i1.x, i0.y, slice)];
    float v01 = tex[uint3(i0.x, i1.y, slice)];
    float v11 = tex[uint3(i1.x, i1.y, slice)];
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

float3 bilinearLoadRgb(Texture2D<float4> tex, float2 uv, int2 size)
{
    float2 tc = uv * float2(size) - 0.5f;
    float2 i0f = floor(tc);
    float2 f = tc - i0f;
    int2 i0 = clamp((int2)i0f, 0, size - 1);
    int2 i1 = clamp(i0 + 1, 0, size - 1);
    float3 v00 = tex.Load(int3(i0.x, i0.y, 0)).rgb;
    float3 v10 = tex.Load(int3(i1.x, i0.y, 0)).rgb;
    float3 v01 = tex.Load(int3(i0.x, i1.y, 0)).rgb;
    float3 v11 = tex.Load(int3(i1.x, i1.y, 0)).rgb;
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

int2 texSize2d(Texture2D<float4> tex)
{
    uint w, h;
    tex.GetDimensions(w, h);
    return int2(w, h);
}

int2 texSizeArr(Texture2DArray<float> tex)
{
    uint w, h, e;
    tex.GetDimensions(w, h, e);
    return int2(w, h);
}

float2 csraiFgV(float2 uv)
{
    int2 ps = texSizeArr(tPack);
    int2 asz = texSizeArr(tNetA);
    float2 base = float2(bilinearLoadSlice(tPack, uv, 2, ps), bilinearLoadSlice(tPack, uv, 3, ps));
    float2 dv = float2(bilinearLoadSlice(tNetA, uv, 0, asz), bilinearLoadSlice(tNetA, uv, 1, asz));
    return (base + dv) * 32.0f / inSize();
}

[numthreads(8, 8, 1)]
void CSRAIFG_PackA(uint3 id : SV_DispatchThreadID)
{
    uint qw = (uint)OutW, qh = (uint)OutH;
    if (id.x >= qw || id.y >= qh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(qw, qh);
    uint cw, ch;
    tColor.GetDimensions(cw, ch);
    int2 hi = int2(cw, ch) - 1;
    int2 origin = int2(id.xy) * 4;
    float y0 = 0.0f, y1 = 0.0f;
    [unroll] for (int dy = 0; dy < 4; dy++)
    [unroll] for (int dx = 0; dx < 4; dx++)
    {
        int2 p = clamp(origin + int2(dx, dy), 0, hi);
        y0 += luma709(tColor.Load(int3(p, 0)).rgb);
        y1 += luma709(tCurrCol.Load(int3(p, 0)).rgb);
    }
    y0 *= 1.0f / 16.0f;
    y1 *= 1.0f / 16.0f;
    float4 pk = pickTileV(uv, 0.5f);
    float2 vbN = pk.xy * inSize() * (1.0f / 32.0f);
    float occl = tFwd.SampleLevel(pntClamp, uv, 0).x;
    float hud = tHud.SampleLevel(pntClamp, uv, 0);
    uFeatAi[uint3(id.xy, 0)] = y0;
    uFeatAi[uint3(id.xy, 1)] = y1;
    uFeatAi[uint3(id.xy, 2)] = vbN.x;
    uFeatAi[uint3(id.xy, 3)] = vbN.y;
    uFeatAi[uint3(id.xy, 4)] = pk.z;
    uFeatAi[uint3(id.xy, 5)] = occl;
    uFeatAi[uint3(id.xy, 6)] = abs(y1 - y0);
    uFeatAi[uint3(id.xy, 7)] = hud;
}

[numthreads(8, 8, 1)]
void CSRAIFG_PackB(uint3 id : SV_DispatchThreadID)
{
    uint hw = (uint)OutW, hh = (uint)OutH;
    if (id.x >= hw || id.y >= hh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(hw, hh);
    float t = InterpT;
    float2 v = csraiFgV(uv);
    float2 pA = uv + t * v;
    float2 pB = uv - (1.0f - t) * v;
    int2 colSize = texSize2d(tColor);
    int2 ps = texSizeArr(tPack);
    int2 asz = texSizeArr(tNetA);
    float y0 = luma709(bilinearLoadRgb(tColor, pA, colSize));
    float y1 = luma709(bilinearLoadRgb(tCurrCol, pB, colSize));
    float yu0 = luma709(bilinearLoadRgb(tColor, uv, colSize));
    float yu1 = luma709(bilinearLoadRgb(tCurrCol, uv, colSize));
    int2 texel = clamp(int2(floor(uv * float2(colSize))), 1, colSize - 2);
    float sumY = 0.0f, sumY2 = 0.0f;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float y = luma709(tCurrCol.Load(int3(texel + int2(dx, dy), 0)).rgb);
        sumY += y;
        sumY2 += y * y;
    }
    float meanY = sumY * (1.0f / 9.0f);
    float localVar = sumY2 * (1.0f / 9.0f) - meanY * meanY;
    uFeatAi[uint3(id.xy, 0)] = y0;
    uFeatAi[uint3(id.xy, 1)] = y1;
    uFeatAi[uint3(id.xy, 2)] = abs(y1 - y0);
    uFeatAi[uint3(id.xy, 3)] = 0.5f * (yu0 + yu1);
    uFeatAi[uint3(id.xy, 4)] = localVar;
    uFeatAi[uint3(id.xy, 5)] = bilinearLoadSlice(tPack, uv, 4, ps);
    uFeatAi[uint3(id.xy, 6)] = bilinearLoadSlice(tNetA, uv, 2, asz);
    uFeatAi[uint3(id.xy, 7)] = t;
    uFeatAi[uint3(id.xy, 8)] = bilinearLoadSlice(tNetA, uv, 3, asz);
    uFeatAi[uint3(id.xy, 9)] = bilinearLoadSlice(tNetA, uv, 4, asz);
    uFeatAi[uint3(id.xy, 10)] = bilinearLoadSlice(tNetA, uv, 5, asz);
    uFeatAi[uint3(id.xy, 11)] = bilinearLoadSlice(tNetA, uv, 6, asz);
}

[numthreads(8, 8, 1)]
void CSRAIFG_Compose(uint3 id : SV_DispatchThreadID)
{
    uint ow = (uint)OutW, oh = (uint)OutH;
    if (id.x >= ow || id.y >= oh) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(ow, oh);
    int2 colSize = texSize2d(tColor);
    int2 asz = texSizeArr(tNetA);
    int2 bsz = texSizeArr(tNetB);
    float3 a0 = bilinearLoadRgb(tColor, uv, colSize);
    float3 b0 = bilinearLoadRgb(tCurrCol, uv, colSize);
    if (cutOn())
    {
        uMv2[id.xy] = float4(InterpT < 0.5f ? a0 : b0, 1.0f);
        return;
    }
    if (tHud.SampleLevel(pntClamp, uv, 0) > 0.5f)
    {
        uMv2[id.xy] = float4(b0, 1.0f);
        return;
    }
    float t = InterpT;
    float2 v = csraiFgV(uv);
    float2 pA = uv + t * v;
    float2 pB = uv - (1.0f - t) * v;
    float3 a = bilinearLoadRgb(tColor, pA, colSize);
    float3 b = bilinearLoadRgb(tCurrCol, pB, colSize);
    float occ = bilinearLoadSlice(tNetA, uv, 2, asz);
    float md = bilinearLoadSlice(tNetB, uv, 0, bsz);
    float lpRaw = bilinearLoadSlice(tNetB, uv, 1, bsz);
    float3 resAdd = 0.0f;
    if (PadFg2d < 4.5f)
    {
        float lumaRes = bilinearLoadSlice(tNetB, uv, 2, bsz);
        resAdd = lumaRes;
    }
    else
    {
        float rR = bilinearLoadSlice(tNetB, uv, 2, bsz);
        float rG = bilinearLoadSlice(tNetB, uv, 3, bsz);
        float rB = bilinearLoadSlice(tNetB, uv, 4, bsz);
        resAdd = float3(rR, rG, rB);
    }
    float tc = clamp(t, 1e-6f, 1.0f - 1e-6f);
    float logit = log(tc / (1.0f - tc)) + occ * PadFg2a + PadFg2b + md;
    float w = 1.0f / (1.0f + exp(-logit));
    bool okA = inside(pA), okB = inside(pB);
    if (!okA && okB) w = 1.0f;
    if (!okB && okA) w = 0.0f;
    float3 warped = (1.0f - w) * a + w * b + resAdd;
    float lp = PadFg2c > 0.5f ? 1.0f / (1.0f + exp(-lpRaw)) : saturate(lpRaw);
    float3 avgUv = lerp(a0, b0, t);
    uMv2[id.xy] = float4(saturate(lerp(warped, avgUv, lp)), 1.0f);
}
