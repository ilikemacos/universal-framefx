cbuffer Csr2Params : register(b0)
{
    float2 inSize;
    float2 outSize;
    float  historyValid;
    float  sharpness;
    float  hasMotion;
    float  hasConf;
    float  cheap;
    float  clampK;
    float  blendK;
    float  sharpK;
    float2 jitter;
    float2 _pad;
};

Texture2D<float4> tColor  : register(t0);
Texture2D<float4> tHist   : register(t1);
Texture2D<float4> tMotion : register(t2);
Texture2D<float>  tConf   : register(t3);
RWTexture2D<float4> uOut  : register(u0);
RWTexture2D<float4> uHist : register(u1);
SamplerState linClamp : register(s0);
SamplerState pntClamp : register(s1);

static const float3 REC709 = float3(0.2126f, 0.7152f, 0.0722f);

float luma(float3 c) { return dot(c, REC709); }
float safeRcp(float x) { return x == 0.0f ? 0.0f : 1.0f / x; }

float3 rgbToYCoCg(float3 c)
{
    return float3(0.25f * c.r + 0.5f * c.g + 0.25f * c.b, 0.5f * c.r - 0.5f * c.b, -0.25f * c.r + 0.5f * c.g - 0.25f * c.b);
}
float3 yCoCgToRGB(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

float3 clipToBox(float3 target, float3 bMin, float3 bMax)
{
    float3 c = 0.5f * (bMax + bMin), e = 0.5f * (bMax - bMin) + 1e-6f;
    float3 o = target - c;
    float3 u = abs(o / e);
    float m = max(u.x, max(u.y, u.z));
    return m > 1.0f ? c + o / m : target;
}

float lanczos2(float x)
{
    x = abs(x);
    if (x < 1e-5f) return 1.0f;
    if (x >= 2.0f) return 0.0f;
    float p = 3.14159265f * x;
    return (sin(p) * sin(p * 0.5f)) / (p * p * 0.5f);
}

float catmullRom(float x)
{
    x = abs(x);
    if (x <= 1.0f) return (1.5f * x - 2.5f) * x * x + 1.0f;
    if (x <= 2.0f) return ((-0.5f * x + 2.5f) * x - 4.0f) * x + 2.0f;
    return 0.0f;
}

float3 loadC(int2 p)
{
    int2 hi = int2(inSize) - 1;
    return tColor.Load(int3(clamp(p, 0, hi), 0)).rgb;
}

float3 historyCatmull(float2 uv)
{
    float2 size = outSize;
    float2 pos = uv * size - 0.5f;
    float2 f = frac(pos), i = floor(pos);
    float wx[4], wy[4];
    [unroll] for (int k = 0; k < 4; ++k) { wx[k] = catmullRom(float(k - 1) - f.x); wy[k] = catmullRom(float(k - 1) - f.y); }
    float4 acc = 0; float ws = 0;
    int2 hi = int2(size) - 1;
    [unroll] for (int y = 0; y < 4; ++y)
    [unroll] for (int x = 0; x < 4; ++x)
    {
        float w = wx[x] * wy[y];
        int2 tp = clamp(int2(i) + int2(x - 1, y - 1), 0, hi);
        acc += tHist.Load(int3(tp, 0)) * w; ws += w;
    }
    return (acc / max(ws, 1e-6f)).rgb;
}

[numthreads(8, 8, 1)]
void CSR2_Upsample(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 src = (float2(id.xy) + 0.5f) * inSize / outSize - jitter;
    float2 fp = floor(src - 0.5f);
    float2 pp = src - 0.5f - fp;
    int2 p = int2(fp);

    float l00 = luma(loadC(p + int2(0, 0)));
    float l10 = luma(loadC(p + int2(1, 0)));
    float l20 = luma(loadC(p + int2(2, 0)));
    float l01 = luma(loadC(p + int2(0, 1)));
    float l11 = luma(loadC(p + int2(1, 1)));
    float l21 = luma(loadC(p + int2(2, 1)));
    float l02 = luma(loadC(p + int2(0, 2)));
    float l12 = luma(loadC(p + int2(1, 2)));
    float l22 = luma(loadC(p + int2(2, 2)));

    float gx = (l20 + 2.0f * l21 + l22) - (l00 + 2.0f * l01 + l02);
    float gy = (l02 + 2.0f * l12 + l22) - (l00 + 2.0f * l10 + l20);
    gx += (l11 - l01) + (l21 - l11);
    gy += (l11 - l10) + (l12 - l11);
    float ixx = gx * gx, ixy = gx * gy, iyy = gy * gy;
    float2 dir = float2(ixx - iyy, 2.0f * ixy);
    float dirR = dot(dir, dir);
    bool zro = dirR < (1.0f / 32768.0f);
    dir = zro ? float2(1.0f, 0.0f) : dir * rsqrt(dirR);
    float aniso = saturate(sqrt(ixx + iyy) * 0.35f);
    float2 len2 = float2(1.0f + 0.55f * aniso, 1.0f - 0.4f * aniso);

    float3 acc = 0; float wsum = 0;
    [unroll] for (int y = -1; y <= 2; ++y)
    [unroll] for (int x = -1; x <= 2; ++x)
    {
        float2 off = float2(x, y) - pp;
        float2 v = float2(dot(off, dir), dot(off, float2(-dir.y, dir.x)));
        v *= len2;
        float w = lanczos2(v.x) * lanczos2(v.y);
        acc += loadC(p + int2(x, y)) * w;
        wsum += w;
    }
    float3 c = acc * safeRcp(wsum);
    float3 a = loadC(p);
    float3 b = loadC(p + int2(1, 0));
    float3 d = loadC(p + int2(0, 1));
    float3 e = loadC(p + int2(1, 1));
    float3 mn = min(min(a, b), min(d, e));
    float3 mx = max(max(a, b), max(d, e));
    mn = min(mn, min(loadC(p + int2(-1, 0)), loadC(p + int2(0, -1))));
    mx = max(mx, max(loadC(p + int2(2, 0)), loadC(p + int2(0, 2))));
    uOut[id.xy] = float4(clamp(c, mn, mx), 1.0f);
}

[numthreads(8, 8, 1)]
void CSR2_Accum(uint3 id : SV_DispatchThreadID)
{
    uint W, H; uOut.GetDimensions(W, H);
    if (id.x >= W || id.y >= H) return;
    float2 uv = (float2(id.xy) + 0.5f) / outSize;
    float3 curr = tColor.Load(int3(id.xy, 0)).rgb;
    if (historyValid < 0.5f)
    {
        uOut[id.xy] = float4(curr, 1.0f);
        uHist[id.xy] = float4(curr, 1.0f);
        return;
    }

    float4 mv = 0;
    if (hasMotion > 0.5f) mv = tMotion.SampleLevel(linClamp, uv, 0);
    float2 prevUV = uv + mv.xy / max(inSize, float2(1.0f, 1.0f));
    bool oob = any(prevUV < 0.0f) || any(prevUV > 1.0f);
    float3 histRgb = oob ? curr : (cheap > 0.5f ? tHist.SampleLevel(linClamp, prevUV, 0).rgb : historyCatmull(prevUV));
    float histN = oob ? 0.0f : tHist.SampleLevel(linClamp, prevUV, 0).a;

    float3 yC = rgbToYCoCg(curr);
    float3 yLo = yC, yHi = yC;
    int2 hi = int2(W, H) - 1;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
    [unroll] for (int dx = -1; dx <= 1; ++dx)
    {
        float3 n = rgbToYCoCg(tColor.Load(int3(clamp(int2(id.xy) + int2(dx, dy), 0, hi), 0)).rgb);
        yLo = min(yLo, n); yHi = max(yHi, n);
    }
    float pad = 0.05f * clampK;
    yLo -= pad; yHi += pad;
    float3 histY = clipToBox(rgbToYCoCg(histRgb), yLo, yHi);
    histRgb = yCoCgToRGB(histY);

    float occ = 0.0f;
    if (hasConf > 0.5f) occ = saturate(tConf.SampleLevel(linClamp, uv, 0));
    else
    {
        float currL = luma(curr), prevL = luma(histRgb);
        float lo = currL, hiL = currL;
        [unroll] for (int dy2 = -1; dy2 <= 1; ++dy2)
        [unroll] for (int dx2 = -1; dx2 <= 1; ++dx2)
        {
            float n = luma(tColor.Load(int3(clamp(int2(id.xy) + int2(dx2, dy2), 0, hi), 0)).rgb);
            lo = min(lo, n); hiL = max(hiL, n);
        }
        float range = max(hiL - lo, 1e-4f);
        occ = saturate((abs(currL - prevL) - range) / 0.18f);
        occ = max(occ, saturate(1.0f - mv.z) * 0.65f);
        if (cheap < 0.5f)
        {
            float nLen = length(mv.xy);
            if (nLen > 0.35f && hasMotion > 0.5f)
            {
                float minConv = 1.0f;
                float2 texel = 1.0f / outSize;
                [unroll] for (int dy3 = -1; dy3 <= 1; ++dy3)
                [unroll] for (int dx3 = -1; dx3 <= 1; ++dx3)
                {
                    if (dx3 == 0 && dy3 == 0) continue;
                    float2 nb = tMotion.SampleLevel(pntClamp, uv + float2(dx3, dy3) * texel, 0).xy;
                    minConv = min(minConv, dot(nb / max(length(nb), 1e-4f), mv.xy / nLen));
                }
                occ = max(occ, saturate(1.0f - minConv) * saturate(nLen / 4.0f));
            }
        }
    }
    if (oob) occ = 1.0f;

    float w = (1.0f - occ) * saturate(blendK);
    float n = min(max(histN, 1.0f), cheap > 0.5f ? 8.0f : 16.0f);
    float k = w * n / (n + 1.0f);
    float3 outRgb = lerp(curr, histRgb, k);
    float newN = w * n + 1.0f;
    uOut[id.xy] = float4(outRgb, 1.0f);
    uHist[id.xy] = float4(outRgb, newN);
}

[numthreads(8, 8, 1)]
void CSR2_CAS(uint3 id : SV_DispatchThreadID)
{
    uint w, h; uOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    int2 p = int2(id.xy), hi = int2(w - 1, h - 1);
    float3 b = tColor.Load(int3(clamp(p + int2(0, -1), 0, hi), 0)).rgb;
    float3 d = tColor.Load(int3(clamp(p + int2(-1, 0), 0, hi), 0)).rgb;
    float3 e = tColor.Load(int3(p, 0)).rgb;
    float3 f = tColor.Load(int3(clamp(p + int2(1, 0), 0, hi), 0)).rgb;
    float3 hh = tColor.Load(int3(clamp(p + int2(0, 1), 0, hi), 0)).rgb;
    float bL = b.g, dL = d.g, eL = e.g, fL = f.g, hL = hh.g;
    float nz = 0.25f * (bL + dL + fL + hL) - eL;
    float range = max(max(max(bL, dL), max(eL, fL)), hL) - min(min(min(bL, dL), min(eL, fL)), hL);
    nz = saturate(abs(nz) * safeRcp(range));
    nz = 1.0f - 0.5f * nz;
    float3 mn4 = min(min(b, d), min(f, hh));
    float3 mx4 = max(max(b, d), max(f, hh));
    float3 hitMin = mn4 / max(4.0f * mx4, 1e-6f);
    float3 hitMax = (1.0f - mx4) / min(4.0f * mn4 - 4.0f, -1e-6f);
    float3 lobeRGB = max(-hitMin, hitMax);
    const float kLimit = 0.25f - (1.0f / 16.0f);
    float lobe = max(-kLimit, min(max(max(lobeRGB.r, lobeRGB.g), lobeRGB.b), 0.0f));
    lobe *= exp2(-sharpness * max(sharpK, 0.0f)) * nz;
    float3 o = (lobe * (b + d + f + hh) + e) * safeRcp(4.0f * lobe + 1.0f);
    uOut[id.xy] = float4(saturate(o), 1.0f);
}
