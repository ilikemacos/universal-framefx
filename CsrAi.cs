using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace UniversalFrameFX;

public sealed class CsrAiLayer
{
    public string Type = "";
    public int In, Out;
    public string Act = "none";
    public int Dilation = 1;
    public float[] W = Array.Empty<float>();
    public float[] B = Array.Empty<float>();
}

public sealed class CsrAiWeights
{
    public int Version;
    public int Scale = 2;
    public float[] Luma = { 0.2126f, 0.7152f, 0.0722f };
    public CsrAiLayer[] Layers = Array.Empty<CsrAiLayer>();
    public int Shuffle = 2;
    public string Residual = "bilinear";

    public static CsrAiWeights Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var w = new CsrAiWeights();
        if (root.TryGetProperty("version", out var ver)) w.Version = ver.GetInt32();
        if (w.Version != 1) throw new InvalidOperationException("CSR AI weights version is not supported.");
        if (root.TryGetProperty("scale", out var sc)) w.Scale = sc.GetInt32();
        if (w.Scale != 2) throw new InvalidOperationException("CSR AI only supports 2× weights.");
        if (root.TryGetProperty("luma", out var luma) && luma.ValueKind == JsonValueKind.Array && luma.GetArrayLength() >= 3)
        {
            w.Luma = new[] { luma[0].GetSingle(), luma[1].GetSingle(), luma[2].GetSingle() };
        }
        if (root.TryGetProperty("shuffle", out var sh)) w.Shuffle = sh.GetInt32();
        if (w.Shuffle != 2) throw new InvalidOperationException("CSR AI only supports pixel-shuffle 2.");
        if (root.TryGetProperty("residual", out var res) && res.ValueKind == JsonValueKind.String)
            w.Residual = res.GetString() ?? "bilinear";
        if (!root.TryGetProperty("layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CSR AI weights have no layers.");
        int n = layersEl.GetArrayLength();
        if (n < 2 || n > 6) throw new InvalidOperationException("CSR AI weights must have 2 to 6 layers.");
        w.Layers = new CsrAiLayer[n];
        for (int i = 0; i < n; i++)
        {
            var e = layersEl[i];
            var layer = new CsrAiLayer();
            layer.Type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            if (layer.Type != "conv3x3") throw new InvalidOperationException("CSR AI weights contain an unsupported layer.");
            layer.In = e.GetProperty("in").GetInt32();
            layer.Out = e.GetProperty("out").GetInt32();
            if (layer.In < 1 || layer.In > 16 || layer.Out < 1 || layer.Out > 16)
                throw new InvalidOperationException("CSR AI layer channel count is out of range.");
            layer.Act = e.TryGetProperty("act", out var act) ? act.GetString() ?? "none" : "none";
            layer.W = ReadFloats(e.GetProperty("w"), layer.Out * layer.In * 9);
            layer.B = ReadFloats(e.GetProperty("b"), layer.Out);
            w.Layers[i] = layer;
        }
        if (w.Layers[0].In != 1) throw new InvalidOperationException("CSR AI first layer must take 1 channel.");
        if (w.Layers[^1].Out != 4) throw new InvalidOperationException("CSR AI last layer must produce 4 channels.");
        return w;
    }

    static float[] ReadFloats(JsonElement el, int expect)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != expect)
            throw new InvalidOperationException("CSR AI weight array length does not match the layer.");
        var a = new float[expect];
        int i = 0;
        foreach (var v in el.EnumerateArray()) a[i++] = v.GetSingle();
        return a;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct CsrAiParams
{
    public float SrcW, SrcH, DstW, DstH;
    public float Sharpness, InC, OutC, ActLeaky;
    public float WeightOff, BiasOff, LumaR, LumaG;
    public float LumaB, TexW, TexH, Dilation;
    public float OutChOff, Accumulate, ActRelu;
}

sealed class CsrAiFeat : IDisposable
{
    public readonly ID3D11Texture2D Texture;
    public readonly ID3D11ShaderResourceView Srv;
    public readonly ID3D11UnorderedAccessView Uav;
    public readonly int Width, Height, Slices;

    public CsrAiFeat(ID3D11Device dev, int w, int h, int slices)
    {
        Width = w; Height = h; Slices = slices;
        Texture = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = (uint)slices,
            Format = Format.R16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        Srv = dev.CreateShaderResourceView(Texture, new ShaderResourceViewDescription
        {
            Format = Format.R16_Float,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = { MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = (uint)slices },
        });
        Uav = dev.CreateUnorderedAccessView(Texture, new UnorderedAccessViewDescription
        {
            Format = Format.R16_Float,
            ViewDimension = UnorderedAccessViewDimension.Texture2DArray,
            Texture2DArray = { MipSlice = 0, FirstArraySlice = 0, ArraySize = (uint)slices },
        });
    }

    public void Dispose() { Uav.Dispose(); Srv.Dispose(); Texture.Dispose(); }
}

public sealed class CsrAiUpscale : IDisposable
{
    public static readonly string[] Kernels = { "CSRAI_Luma", "CSRAI_Conv", "CSRAI_Shuffle", "CSRAI_Resample", "CSRAI_Sharpen" };

    readonly Gpu _g;
    readonly Dictionary<string, ID3D11ComputeShader> _cs = new();
    readonly ID3D11Buffer _cb;
    readonly ID3D11Query[] _dj = new ID3D11Query[3], _t0 = new ID3D11Query[3], _t1 = new ID3D11Query[3];
    int _qi, _qIssued;
    CsrAiWeights? _weights;
    ID3D11Buffer? _wBuf;
    ID3D11ShaderResourceView? _wSrv;
    int[] _wOff = Array.Empty<int>(), _bOff = Array.Empty<int>();
    CsrAiFeat? _featA, _featB;
    Tex? _netOut, _mid;
    int _featW, _featH, _featC;
    InferenceSession? _session;
    SessionOptions? _sessionOpts;
    DenseTensor<float>? _lr;
    ID3D11Texture2D? _stageRead, _stageWrite;
    int _dmlW, _dmlH;
    Format _stageFmt;
    readonly int _dmlDeviceId;
    public static readonly float SharpOffset = float.TryParse(Environment.GetEnvironmentVariable("UFX_CSRAI_SHARP_OFFSET"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var so) ? so : 2f;
    public double LastMs { get; private set; }
    public string Engine { get; }

    static string LoadSrc()
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", "CsrAi.hlsl");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CsrAi.hlsl") ?? throw new InvalidOperationException("CsrAi.hlsl is missing.");
        return new StreamReader(s).ReadToEnd();
    }

    public static string WeightsPath => Path.Combine(AppContext.BaseDirectory, "Models", "csr_ai_x2.weights.json");
    public static string OnnxPath => Path.Combine(AppContext.BaseDirectory, "Models", "csr_ai_x2_fp16.onnx");

    public CsrAiUpscale(Gpu g)
    {
        _g = g;
        string src = LoadSrc();
        var blobs = ShaderCache.GetMany(src, Kernels.Select(k => (k, "cs_5_0")), "CsrAi.hlsl");
        byte[] Fresh(string e) { using var bl = Gpu.CompileSource(src, e, "cs_5_0", "CsrAi.hlsl"); return bl.AsSpan().ToArray(); }
        foreach (var k in Kernels)
        {
            try { _cs[k] = g.Device.CreateComputeShader(blobs[k]); }
            catch { _cs[k] = g.Device.CreateComputeShader(Fresh(k)); }
        }
        _cb = g.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 80, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write,
        });
        for (int i = 0; i < 3; i++)
        {
            _dj[i] = g.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _t0[i] = g.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _t1[i] = g.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
        _dmlDeviceId = DmlAdapterIndex(g);
        string force = (Environment.GetEnvironmentVariable("UFX_CSRAI_ENGINE") ?? "").Trim().ToLowerInvariant();
        bool wantDml = force == "dml";
        bool haveW = File.Exists(WeightsPath);
        bool haveO = File.Exists(OnnxPath);
        if (wantDml)
        {
            if (!haveO) throw new InvalidOperationException("CSR AI ONNX model is missing.");
            InitDml();
            Engine = "dml";
        }
        else if (haveW)
        {
            InitShader();
            Engine = "shader";
        }
        else if (haveO)
        {
            InitDml();
            Engine = "dml";
        }
        else throw new InvalidOperationException("CSR AI model files are missing.");
    }

    static int DmlAdapterIndex(Gpu g)
    {
        using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; f.EnumAdapters1(i, out IDXGIAdapter1? a).Success && a != null; i++)
        {
            using (a)
            {
                if (a.Description1.Luid.LowPart == g.Adapter.Description.Luid.LowPart &&
                    a.Description1.Luid.HighPart == g.Adapter.Description.Luid.HighPart)
                    return (int)i;
            }
        }
        return 0;
    }

    void InitShader()
    {
        _weights = CsrAiWeights.Parse(File.ReadAllText(WeightsPath));
        var packed = new List<float>();
        _wOff = new int[_weights.Layers.Length];
        _bOff = new int[_weights.Layers.Length];
        for (int i = 0; i < _weights.Layers.Length; i++)
        {
            _wOff[i] = packed.Count;
            packed.AddRange(_weights.Layers[i].W);
            _bOff[i] = packed.Count;
            packed.AddRange(_weights.Layers[i].B);
        }
        var data = packed.ToArray();
        int bytes = Math.Max(16, (data.Length * 4 + 15) & ~15);
        _wBuf = _g.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)bytes, Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 4,
        });
        _g.Context.UpdateSubresource(data, _wBuf);
        uint nElem = (uint)Math.Max(1, bytes / 4);
        _wSrv = _g.Device.CreateShaderResourceView(_wBuf, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = { FirstElement = 0, NumElements = nElem },
        });
    }

    void InitDml()
    {
        _sessionOpts = new SessionOptions();
        _sessionOpts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _sessionOpts.AppendExecutionProvider_DML(_dmlDeviceId);
        _session = new InferenceSession(OnnxPath, _sessionOpts);
    }

    void EnsureFeat(int w, int h)
    {
        int c = 1;
        foreach (var l in _weights!.Layers) c = Math.Max(c, Math.Max(l.In, l.Out));
        if (_featA != null && _featW == w && _featH == h && _featC == c) return;
        _featA?.Dispose(); _featB?.Dispose();
        _featA = new CsrAiFeat(_g.Device, w, h, c);
        _featB = new CsrAiFeat(_g.Device, w, h, c);
        _featW = w; _featH = h; _featC = c;
    }

    void EnsureNet(int w, int h, Format fmt)
    {
        if (_netOut != null && _netOut.Width == w && _netOut.Height == h && _netOut.Format == fmt) return;
        _netOut?.Dispose();
        _netOut = new Tex(_g.Device, w, h, fmt);
    }

    void SetParams(in CsrAiParams p)
    {
        var m = _g.Context.Map(_cb, 0, MapMode.WriteDiscard);
        try { unsafe { *(CsrAiParams*)m.DataPointer = p; } }
        finally { _g.Context.Unmap(_cb, 0); }
    }

    void Run(string kernel, int w, int h,
        (int slot, ID3D11ShaderResourceView? srv)[] srvs,
        (int slot, ID3D11UnorderedAccessView? uav)[] uavs)
    {
        var ctx = _g.Context;
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _g.LinearSampler);
        ctx.CSSetSampler(1, _g.PointSampler);
        foreach (var (slot, srv) in srvs) ctx.CSSetShaderResource((uint)slot, srv);
        foreach (var (slot, uav) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, uav);
        ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        foreach (var (slot, _) in srvs) ctx.CSSetShaderResource((uint)slot, null);
        foreach (var (slot, _) in uavs) ctx.CSSetUnorderedAccessView((uint)slot, null);
    }

    void BeginGpuTime()
    {
        _g.Context.Begin(_dj[_qi]);
        _g.Context.End(_t0[_qi]);
    }

    void EndGpuTime()
    {
        _g.Context.End(_t1[_qi]);
        _g.Context.End(_dj[_qi]);
        _qi = (_qi + 1) % 3;
        _qIssued++;
        if (_qIssued < 3) return;
        int r = _qi;
        if (!_g.Context.GetData(_dj[r], out QueryDataTimestampDisjoint dj) || dj.Disjoint || dj.Frequency == 0) return;
        if (!_g.Context.GetData(_t0[r], out ulong a) || !_g.Context.GetData(_t1[r], out ulong b) || b < a) return;
        LastMs = (b - a) * 1000.0 / dj.Frequency;
    }

    public void Encode(ID3D11ShaderResourceView input, int inW, int inH, Tex output, float sharpness)
    {
        sharpness = Math.Max(0f, sharpness) + SharpOffset;
        if (Engine == "dml") EncodeDml(input, inW, inH, output, sharpness);
        else EncodeShader(input, inW, inH, output, sharpness);
    }

    void EncodeShader(ID3D11ShaderResourceView input, int inW, int inH, Tex output, float sharpness)
    {
        EnsureFeat(inW, inH);
        int netW = inW * 2, netH = inH * 2;
        EnsureNet(netW, netH, Format.R16G16B16A16_Float);
        var luma = _weights!.Luma;
        int texW = inW, texH = inH;
        using (var r = input.Resource) using (var t2 = r.QueryInterfaceOrNull<ID3D11Texture2D>())
            if (t2 != null) { texW = (int)t2.Description.Width; texH = (int)t2.Description.Height; }
        BeginGpuTime();
        var p = new CsrAiParams
        {
            SrcW = inW, SrcH = inH, DstW = netW, DstH = netH,
            Sharpness = sharpness, InC = 1, OutC = 1, ActLeaky = 0,
            LumaR = luma[0], LumaG = luma[1], LumaB = luma[2], TexW = texW, TexH = texH,
        };
        SetParams(p);
        Run("CSRAI_Luma", inW, inH, new[] { (0, (ID3D11ShaderResourceView?)input) }, new[] { (0, (ID3D11UnorderedAccessView?)_featA!.Uav) });
        var src = _featA;
        var dst = _featB!;
        for (int i = 0; i < _weights.Layers.Length; i++)
        {
            var layer = _weights.Layers[i];
            p.InC = layer.In; p.OutC = layer.Out;
            p.ActLeaky = layer.Act == "leaky0.1" ? 1 : 0;
            p.WeightOff = _wOff[i]; p.BiasOff = _bOff[i];
            SetParams(p);
            Run("CSRAI_Conv", inW, inH,
                new[] { (1, _wSrv), (2, (ID3D11ShaderResourceView?)src.Srv) },
                new[] { (0, (ID3D11UnorderedAccessView?)dst.Uav) });
            (src, dst) = (dst, src);
        }
        p.DstW = netW; p.DstH = netH;
        SetParams(p);
        Run("CSRAI_Shuffle", netW, netH,
            new[] { (0, (ID3D11ShaderResourceView?)input), (2, src.Srv) },
            new[] { (1, _netOut!.Uav) });
        FinishResizeSharpen(_netOut, output, sharpness, p);
        EndGpuTime();
    }

    void FinishResizeSharpen(Tex net, Tex output, float sharpness, CsrAiParams p)
    {
        bool exact = output.Width == net.Width && output.Height == net.Height;
        if (!exact)
        {
            if (_mid == null || _mid.Width != output.Width || _mid.Height != output.Height)
            {
                _mid?.Dispose();
                _mid = new Tex(_g.Device, output.Width, output.Height, Format.R16G16B16A16_Float);
            }
            p.SrcW = net.Width; p.SrcH = net.Height; p.DstW = output.Width; p.DstH = output.Height; p.Sharpness = sharpness;
            SetParams(p);
            Run("CSRAI_Resample", output.Width, output.Height,
                new[] { (0, (ID3D11ShaderResourceView?)net.Srv) },
                new[] { (1, _mid.Uav) });
            net = _mid;
        }
        p.SrcW = net.Width; p.SrcH = net.Height; p.DstW = output.Width; p.DstH = output.Height; p.Sharpness = sharpness;
        SetParams(p);
        Run("CSRAI_Sharpen", output.Width, output.Height,
            new[] { (0, (ID3D11ShaderResourceView?)net.Srv) },
            new[] { (1, output.Uav) });
    }

    void EncodeDml(ID3D11ShaderResourceView input, int inW, int inH, Tex output, float sharpness)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var res = input.Resource;
        using var t2 = res.QueryInterfaceOrNull<ID3D11Texture2D>() ?? throw new InvalidOperationException("CSR AI input is not a 2D texture.");
        var desc = t2.Description;
        EnsureDml(inW, inH, desc.Format);
        var ctx = _g.Context;
        var box = new Box(0, 0, 0, inW, inH, 1);
        ctx.CopySubresourceRegion(_stageRead!, 0, 0, 0, 0, t2, 0, box);
        var map = ctx.Map(_stageRead!, 0, MapMode.Read);
        var rgb = new float[inW * inH * 3];
        bool bgra = desc.Format == Format.B8G8R8A8_UNorm;
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)map.DataPointer;
                int pitch = (int)map.RowPitch;
                Parallel.For(0, inH, y =>
                {
                    byte* row = basePtr + y * (long)pitch;
                    int o = y * inW * 3;
                    for (int x = 0; x < inW; x++)
                    {
                        byte c0 = row[x * 4], c1 = row[x * 4 + 1], c2 = row[x * 4 + 2];
                        rgb[o + x * 3] = (bgra ? c2 : c0) / 255f;
                        rgb[o + x * 3 + 1] = c1 / 255f;
                        rgb[o + x * 3 + 2] = (bgra ? c0 : c2) / 255f;
                    }
                });
            }
        }
        finally { ctx.Unmap(_stageRead!, 0); }
        var lr = _lr!;
        float lr_ = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        Parallel.For(0, inH, y =>
        {
            int o = y * inW * 3;
            for (int x = 0; x < inW; x++)
                lr[0, 0, y, x] = rgb[o + x * 3] * lr_ + rgb[o + x * 3 + 1] * lg + rgb[o + x * 3 + 2] * lb;
        });
        using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor("lr", lr) });
        var hr = results[0].AsTensor<float>();
        int netW = inW * 2, netH = inH * 2;
        var outRgba = new byte[netW * netH * 4];
        Parallel.For(0, netH, y =>
        {
            for (int x = 0; x < netW; x++)
            {
                float sx = (x + 0.5f) / 2f - 0.5f;
                float sy = (y + 0.5f) / 2f - 0.5f;
                float r = SampleBilin(rgb, inW, inH, 3, 0, sx, sy);
                float g = SampleBilin(rgb, inW, inH, 3, 1, sx, sy);
                float b = SampleBilin(rgb, inW, inH, 3, 2, sx, sy);
                float yUp = r * lr_ + g * lg + b * lb;
                float yNet = hr[0, 0, y, x];
                float d = yNet - yUp;
                int o = (y * netW + x) * 4;
                outRgba[o] = ToByte(r + d);
                outRgba[o + 1] = ToByte(g + d);
                outRgba[o + 2] = ToByte(b + d);
                outRgba[o + 3] = 255;
            }
        });
        EnsureNet(netW, netH, Format.R8G8B8A8_UNorm);
        var wm = ctx.Map(_stageWrite!, 0, MapMode.Write);
        try
        {
            unsafe
            {
                for (int y = 0; y < netH; y++)
                    Marshal.Copy(outRgba, y * netW * 4, IntPtr.Add(wm.DataPointer, y * (int)wm.RowPitch), netW * 4);
            }
        }
        finally { ctx.Unmap(_stageWrite!, 0); }
        ctx.CopyResource(_netOut!.Texture, _stageWrite!);
        var p = new CsrAiParams { LumaR = lr_, LumaG = lg, LumaB = lb };
        FinishResizeSharpen(_netOut, output, sharpness, p);
        LastMs = sw.Elapsed.TotalMilliseconds;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    static float SampleBilin(float[] pix, int w, int h, int nch, int ch, float x, float y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = x - x0, ty = y - y0;
        float a = pix[(y0 * w + x0) * nch + ch];
        float b = pix[(y0 * w + x1) * nch + ch];
        float c = pix[(y1 * w + x0) * nch + ch];
        float d = pix[(y1 * w + x1) * nch + ch];
        return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
    }

    void EnsureDml(int w, int h, Format fmt)
    {
        if (_lr != null && _dmlW == w && _dmlH == h && _stageFmt == fmt && _stageRead != null && _stageWrite != null) return;
        _stageRead?.Dispose(); _stageWrite?.Dispose();
        _dmlW = w; _dmlH = h; _stageFmt = fmt;
        _lr = new DenseTensor<float>(new[] { 1, 1, h, w });
        _stageRead = _g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = fmt,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        _stageWrite = _g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(w * 2), Height = (uint)(h * 2), MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write, BindFlags = BindFlags.None,
        });
    }

    public void Dispose()
    {
        _featA?.Dispose(); _featB?.Dispose();
        _netOut?.Dispose(); _mid?.Dispose();
        _wSrv?.Dispose(); _wBuf?.Dispose();
        _stageRead?.Dispose(); _stageWrite?.Dispose();
        _session?.Dispose(); _sessionOpts?.Dispose();
        _cb.Dispose();
        foreach (var q in _dj) q.Dispose();
        foreach (var q in _t0) q.Dispose();
        foreach (var q in _t1) q.Dispose();
        foreach (var cs in _cs.Values) cs.Dispose();
        _cs.Clear();
    }
}
