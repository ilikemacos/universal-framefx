cbuffer CsrAiFgParams : register(b0)
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

StructuredBuffer<float> tW : register(t1);
Texture2DArray<float> tFeat : register(t2);
RWTexture2DArray<float> uFeat : register(u0);

float bilinearLoadSlice(Texture2DArray<float> tex, float2 uv, int slice, int2 size)
{
    float2 tc = uv * float2(size) - 0.5f;
    float2 i0f = floor(tc);
    float2 f = tc - i0f;
    int2 i0 = clamp((int2)i0f, 0, size - 1);
    int2 i2 = clamp(i0 + 1, 0, size - 1);
    float v00 = tex[uint3(i0.x, i0.y, slice)];
    float v10 = tex[uint3(i2.x, i0.y, slice)];
    float v01 = tex[uint3(i0.x, i2.y, slice)];
    float v11 = tex[uint3(i2.x, i2.y, slice)];
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

float3 bilinearLoadRgb(Texture2D<float4> tex, float2 uv, int2 size)
{
    float2 tc = uv * float2(size) - 0.5f;
    float2 i0f = floor(tc);
    float2 f = tc - i0f;
    int2 i0 = clamp((int2)i0f, 0, size - 1);
    int2 i2 = clamp(i0 + 1, 0, size - 1);
    float3 v00 = tex.Load(int3(i0.x, i0.y, 0)).rgb;
    float3 v10 = tex.Load(int3(i2.x, i0.y, 0)).rgb;
    float3 v01 = tex.Load(int3(i0.x, i2.y, 0)).rgb;
    float3 v11 = tex.Load(int3(i2.x, i2.y, 0)).rgb;
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

#define TG 8
groupshared min16float sm[24][TG + 2][TG + 2];

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

void ApplyAct(inout float acc)
{
    if (ActRelu > 0.5f)
        acc = max(0.0f, acc);
    else if (ActLeaky > 0.5f)
        acc = acc >= 0.0f ? acc : 0.1f * acc;
}

void WriteOut(uint2 xy, int oc, float acc)
{
    int slice = oc + (int)OutChOff;
    if (Accumulate > 0.5f)
        uFeat[uint3(xy, slice)] += acc;
    else
        uFeat[uint3(xy, slice)] = acc;
}

[numthreads(8, 8, 1)]
void CSRAIFG_ConvDilated(uint3 id : SV_DispatchThreadID)
{
    int inC = (int)InC;
    int outC = (int)OutC;
    int d = max(1, (int)Dilation);
    int2 sz = int2((int)SrcW, (int)SrcH);
    if ((int)id.x >= sz.x || (int)id.y >= sz.y) return;
    int wBase = (int)WeightOff;
    int bBase = (int)BiasOff;
    int2 p0 = int2(id.xy);
    [loop] for (int oc = 0; oc < outC; oc++)
    {
        float acc = tW[bBase + oc];
        [loop] for (int ic = 0; ic < inC; ic++)
        {
            int k = wBase + (oc * inC + ic) * 9;
            [unroll] for (int ky = 0; ky < 3; ky++)
            [unroll] for (int kx = 0; kx < 3; kx++)
            {
                int2 tap = clamp(p0 + int2((kx - 1) * d, (ky - 1) * d), 0, sz - 1);
                acc += tW[k + ky * 3 + kx] * tFeat[uint3(tap, ic)];
            }
        }
        ApplyAct(acc);
        WriteOut(id.xy, oc, acc);
    }
}

[numthreads(8, 8, 1)]
void CSRAIFG_Conv(uint3 id : SV_DispatchThreadID, uint3 gt : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    int inC = (int)InC;
    int outC = (int)OutC;
    int2 sz = int2((int)SrcW, (int)SrcH);
    int wBase = (int)WeightOff;
    int bBase = (int)BiasOff;

    int2 origin = int2(gid.xy) * TG;
    LoadHalo(origin, sz, gt.xy, inC);
    GroupMemoryBarrierWithGroupSync();
    if ((int)id.x >= sz.x || (int)id.y >= sz.y) return;
    int lx = (int)gt.x + 1;
    int ly = (int)gt.y + 1;
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
        ApplyAct(acc);
        WriteOut(id.xy, oc, acc);
    }
}
