cbuffer CsrAiParams : register(b0)
{
    float SrcW;
    float SrcH;
    float DstW;
    float DstH;
    float Sharpness;
    float InC;
    float OutC;
    float ActLeaky;
    float WeightOff;
    float BiasOff;
    float LumaR;
    float LumaG;
    float LumaB;
    float TexW;
    float TexH;
    float Dilation;
    float OutChOff;
    float Accumulate;
    float ActRelu;
};

Texture2D<float4> tColor : register(t0);
StructuredBuffer<float> tW : register(t1);
Texture2DArray<float> tFeat : register(t2);
RWTexture2DArray<float> uFeat : register(u0);
RWTexture2D<float4> uOut : register(u1);
SamplerState linClamp : register(s0);
SamplerState pntClamp : register(s1);

#define TG 8
groupshared min16float sm[16][TG + 2][TG + 2];

float safeRcp(float x) { return x == 0.0f ? 0.0f : 1.0f / x; }

[numthreads(8, 8, 1)]
void CSRAI_Luma(uint3 id : SV_DispatchThreadID)
{
    int w = (int)SrcW, h = (int)SrcH;
    if ((int)id.x >= w || (int)id.y >= h) return;
    float3 rgb = tColor.Load(int3(id.xy, 0)).rgb;
    uFeat[uint3(id.xy, 0)] = dot(rgb, float3(LumaR, LumaG, LumaB));
}

void LoadHalo(int2 origin, int2 sz, uint2 gt, int inC)
{
    int lx = (int)gt.x + 1;
    int ly = (int)gt.y + 1;
    int2 p = clamp(origin + int2(gt), 0, sz - 1);
    [loop] for (int c = 0; c < inC; c++)
        sm[c][ly][lx] = (min16float)tFeat[uint3(p, c)];
    if (gt.x == 0)
    {
        int2 pl = clamp(origin + int2(-1, (int)gt.y), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][ly][0] = (min16float)tFeat[uint3(pl, c)];
    }
    if (gt.x == TG - 1)
    {
        int2 pr = clamp(origin + int2(TG, (int)gt.y), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][ly][TG + 1] = (min16float)tFeat[uint3(pr, c)];
    }
    if (gt.y == 0)
    {
        int2 pt = clamp(origin + int2((int)gt.x, -1), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][0][lx] = (min16float)tFeat[uint3(pt, c)];
    }
    if (gt.y == TG - 1)
    {
        int2 pb = clamp(origin + int2((int)gt.x, TG), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][TG + 1][lx] = (min16float)tFeat[uint3(pb, c)];
    }
    if (gt.x == 0 && gt.y == 0)
    {
        int2 pc = clamp(origin + int2(-1, -1), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][0][0] = (min16float)tFeat[uint3(pc, c)];
    }
    if (gt.x == TG - 1 && gt.y == 0)
    {
        int2 pc = clamp(origin + int2(TG, -1), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][0][TG + 1] = (min16float)tFeat[uint3(pc, c)];
    }
    if (gt.x == 0 && gt.y == TG - 1)
    {
        int2 pc = clamp(origin + int2(-1, TG), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][TG + 1][0] = (min16float)tFeat[uint3(pc, c)];
    }
    if (gt.x == TG - 1 && gt.y == TG - 1)
    {
        int2 pc = clamp(origin + int2(TG, TG), 0, sz - 1);
        [loop] for (int c = 0; c < inC; c++)
            sm[c][TG + 1][TG + 1] = (min16float)tFeat[uint3(pc, c)];
    }
}

[numthreads(8, 8, 1)]
void CSRAI_Conv(uint3 id : SV_DispatchThreadID, uint3 gt : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    int inC = (int)InC;
    int outC = (int)OutC;
    int2 sz = int2((int)SrcW, (int)SrcH);
    int2 origin = int2(gid.xy) * TG;
    LoadHalo(origin, sz, gt.xy, inC);
    GroupMemoryBarrierWithGroupSync();
    if ((int)id.x >= sz.x || (int)id.y >= sz.y) return;
    int lx = (int)gt.x + 1;
    int ly = (int)gt.y + 1;
    int wBase = (int)WeightOff;
    int bBase = (int)BiasOff;
    [loop] for (int oc = 0; oc < outC; oc++)
    {
        float acc = tW[bBase + oc];
        [loop] for (int ic = 0; ic < inC; ic++)
        {
            int k = wBase + (oc * inC + ic) * 9;
            [unroll] for (int ky = 0; ky < 3; ky++)
            [unroll] for (int kx = 0; kx < 3; kx++)
                acc += tW[k + ky * 3 + kx] * (float)sm[ic][ly + ky - 1][lx + kx - 1];
        }
        if (ActLeaky > 0.5f)
            acc = acc >= 0.0f ? acc : 0.1f * acc;
        uFeat[uint3(id.xy, oc)] = acc;
    }
}

[numthreads(8, 8, 1)]
void CSRAI_Shuffle(uint3 id : SV_DispatchThreadID)
{
    int w = (int)DstW, h = (int)DstH;
    if ((int)id.x >= w || (int)id.y >= h) return;
    int2 hr = int2(id.xy);
    int2 lr = hr >> 1;
    int c = ((hr.y & 1) << 1) | (hr.x & 1);
    float yDelta = tFeat[uint3(lr, c)];
    float2 uv = (float2(hr) + 0.5f) * 0.5f / float2(TexW, TexH);
    float3 rgb = tColor.SampleLevel(linClamp, uv, 0).rgb;
    float yUp = dot(rgb, float3(LumaR, LumaG, LumaB));
    float yNet = yDelta + yUp;
    uOut[id.xy] = float4(saturate(rgb + (yNet - yUp)), 1.0f);
}

[numthreads(8, 8, 1)]
void CSRAI_Resample(uint3 id : SV_DispatchThreadID)
{
    int w = (int)DstW, h = (int)DstH;
    if ((int)id.x >= w || (int)id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5f) / float2(DstW, DstH);
    uOut[id.xy] = tColor.SampleLevel(linClamp, uv, 0);
}

[numthreads(8, 8, 1)]
void CSRAI_Sharpen(uint3 id : SV_DispatchThreadID)
{
    int w = (int)DstW, h = (int)DstH;
    if ((int)id.x >= w || (int)id.y >= h) return;
    int2 p = int2(id.xy), hi = int2(w - 1, h - 1);
    float3 e = tColor.Load(int3(p, 0)).rgb;
    float3 b = tColor.Load(int3(clamp(p + int2(0, -1), 0, hi), 0)).rgb;
    float3 d = tColor.Load(int3(clamp(p + int2(-1, 0), 0, hi), 0)).rgb;
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
    lobe *= exp2(-Sharpness) * nz;
    float3 o = (lobe * (b + d + f + hh) + e) * safeRcp(4.0f * lobe + 1.0f);
    uOut[id.xy] = float4(saturate(o), 1.0f);
}
