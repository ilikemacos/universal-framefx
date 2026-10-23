using System.Reflection;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

public sealed class CsrAiMsb
{
    public CsrAiLayer Shared = new();
    public CsrAiLayer[] Branches = Array.Empty<CsrAiLayer>();
}

public sealed class CsrAiFgWeights
{
    public int Version;
    public CsrAiLayer[] NetA = Array.Empty<CsrAiLayer>();
    public CsrAiLayer[] NetB = Array.Empty<CsrAiLayer>();
    public CsrAiMsb? Msb;
    public float MaskA;
    public float MaskB;
    public float FlowScale = 32f;

    public bool LpSigmoid => Version >= 3;
    public int NetBOut => NetB.Length > 0 ? NetB[^1].Out : 5;

    public static CsrAiFgWeights Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var w = new CsrAiFgWeights();
        if (root.TryGetProperty("version", out var ver)) w.Version = ver.GetInt32();
        if (w.Version != 2 && w.Version != 3) throw new InvalidOperationException("CSR Flux weights version is not supported.");
        w.NetA = ReadNet(root, "netA");
        w.NetB = ReadNet(root, "netB");
        if (w.NetA[0].In != 8 || w.NetA[^1].Out != 7)
            throw new InvalidOperationException("CSR Flux net A must take 8 channels and produce 7.");
        int netBOut = w.NetB[^1].Out;
        if (w.Version == 2 && netBOut != 5)
            throw new InvalidOperationException("CSR Flux v2 net B must produce 5 channels.");
        if (w.Version == 3 && netBOut != 3 && netBOut != 5)
            throw new InvalidOperationException("CSR Flux v3 net B must produce 3 or 5 channels.");
        if (w.NetB[0].In != 12)
            throw new InvalidOperationException("CSR Flux net B must take 12 channels.");
        if (root.TryGetProperty("msb", out var msbEl))
            w.Msb = ReadMsb(msbEl, w.NetA);
        if (root.TryGetProperty("maskA", out var ma)) w.MaskA = ma.GetSingle();
        else if (root.TryGetProperty("maskBiasA", out var mba)) w.MaskA = mba.GetSingle();
        if (root.TryGetProperty("maskB", out var mb)) w.MaskB = mb.GetSingle();
        else if (root.TryGetProperty("maskBiasB", out var mbb)) w.MaskB = mbb.GetSingle();
        if (root.TryGetProperty("flowScale", out var fs)) w.FlowScale = fs.GetSingle();
        return w;
    }

    static CsrAiMsb ReadMsb(JsonElement root, CsrAiLayer[] netA)
    {
        if (netA.Length < 2)
            throw new InvalidOperationException("CSR Flux MSB requires net A to have a hidden layer before the tail.");
        int hiddenW = netA[^2].Out;
        var msb = new CsrAiMsb();
        if (!root.TryGetProperty("shared", out var sharedEl))
            throw new InvalidOperationException("CSR Flux MSB is missing shared.");
        msb.Shared = ReadConvLayer(sharedEl, requireDilation: false);
        if (msb.Shared.In != 8)
            throw new InvalidOperationException("CSR Flux MSB shared conv must take 8 input channels.");
        if (!root.TryGetProperty("branches", out var branchesEl) || branchesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CSR Flux MSB is missing branches.");
        int nb = branchesEl.GetArrayLength();
        if (nb != 3)
            throw new InvalidOperationException("CSR Flux MSB must have exactly 3 branches.");
        msb.Branches = new CsrAiLayer[nb];
        for (int i = 0; i < nb; i++)
            msb.Branches[i] = ReadConvLayer(branchesEl[i], requireDilation: true);
        int bc = msb.Shared.Out;
        for (int i = 0; i < nb; i++)
        {
            var b = msb.Branches[i];
            if (b.In != bc || b.Out != bc)
                throw new InvalidOperationException("CSR Flux MSB branch channel counts must match shared output width.");
            if (b.Dilation < 1)
                throw new InvalidOperationException("CSR Flux MSB branch dilation must be positive.");
        }
        if (3 * bc != hiddenW)
            throw new InvalidOperationException("CSR Flux MSB concat width (3 × shared.out) must equal net A hidden width.");
        if (root.TryGetProperty("input", out var inp) && inp.GetString() != "netA_input")
            throw new InvalidOperationException("CSR Flux MSB input must be netA_input.");
        if (root.TryGetProperty("add_to", out var add) && add.GetString() != "netA_last_hidden")
            throw new InvalidOperationException("CSR Flux MSB add_to must be netA_last_hidden.");
        return msb;
    }

    static CsrAiLayer ReadConvLayer(JsonElement e, bool requireDilation)
    {
        var layer = new CsrAiLayer();
        layer.Type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
        if (layer.Type != "conv3x3") throw new InvalidOperationException("CSR Flux weights contain an unsupported layer.");
        layer.In = e.GetProperty("in").GetInt32();
        layer.Out = e.GetProperty("out").GetInt32();
        if (layer.In < 1 || layer.In > 24 || layer.Out < 1 || layer.Out > 24)
            throw new InvalidOperationException("CSR Flux layer channel count is out of range.");
        layer.Act = e.TryGetProperty("act", out var act) ? act.GetString() ?? "none" : "none";
        if (requireDilation)
        {
            if (!e.TryGetProperty("dilation", out var dil))
                throw new InvalidOperationException("CSR Flux MSB branch is missing dilation.");
            layer.Dilation = dil.GetInt32();
        }
        layer.W = ReadFloats(e.GetProperty("w"), layer.Out * layer.In * 9);
        layer.B = ReadFloats(e.GetProperty("b"), layer.Out);
        return layer;
    }

    static CsrAiLayer[] ReadNet(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CSR Flux weights are missing " + name + ".");
        int n = layersEl.GetArrayLength();
        if (n < 1 || n > 8) throw new InvalidOperationException("CSR Flux " + name + " layer count is out of range.");
        var layers = new CsrAiLayer[n];
        for (int i = 0; i < n; i++)
            layers[i] = ReadConvLayer(layersEl[i], requireDilation: false);
        for (int i = 1; i < n; i++)
            if (layers[i].In != layers[i - 1].Out)
                throw new InvalidOperationException("CSR Flux " + name + " layer channel counts do not connect.");
        return layers;
    }

    static float[] ReadFloats(JsonElement el, int expect)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != expect)
            throw new InvalidOperationException("CSR Flux weight array length does not match the layer.");
        var a = new float[expect];
        int i = 0;
        foreach (var v in el.EnumerateArray()) a[i++] = v.GetSingle();
        return a;
    }
}

public sealed class CsrAiFg : IDisposable
{
    public static readonly string[] Kernels = { "CSRAIFG_Conv", "CSRAIFG_ConvDilated" };
    public static string WeightsPath => Path.Combine(AppContext.BaseDirectory, "Models", "csr_ai_fg.weights.json");

    readonly Gpu _g;
    readonly Dictionary<string, ID3D11ComputeShader> _cs = new();
    readonly ID3D11Buffer _cb;
    readonly bool _packOnly;
    CsrAiFgWeights? _weights;
    ID3D11Buffer? _wBuf;
    ID3D11ShaderResourceView? _wSrv;
    int[] _wOffA = Array.Empty<int>(), _bOffA = Array.Empty<int>();
    int[] _wOffB = Array.Empty<int>(), _bOffB = Array.Empty<int>();
    int _wOffMsbShared, _bOffMsbShared;
    int[] _wOffMsbBranch = Array.Empty<int>(), _bOffMsbBranch = Array.Empty<int>();
    CsrAiFeat? _pack, _a0, _a1, _netA, _packB, _b0, _b1, _netB, _msbS;
    Tex? _out;
    int _qw, _qh, _hw, _hh;
    bool _pairReady;

    static string LoadSrc()
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", "CsrAiFg.hlsl");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CsrAiFg.hlsl") ?? throw new InvalidOperationException("CsrAiFg.hlsl is missing.");
        return new StreamReader(s).ReadToEnd();
    }

    public CsrAiFg(Gpu g, bool packOnly = false)
    {
        _g = g;
        _packOnly = packOnly;
        _cb = g.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 80, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write,
        });
        if (packOnly) return;
        string src = LoadSrc();
        var blobs = ShaderCache.GetMany(src, Kernels.Select(k => (k, "cs_5_0")), "CsrAiFg.hlsl");
        byte[] Fresh(string e) { using var bl = Gpu.CompileSource(src, e, "cs_5_0", "CsrAiFg.hlsl"); return bl.AsSpan().ToArray(); }
        foreach (var k in Kernels)
        {
            try { _cs[k] = g.Device.CreateComputeShader(blobs[k]); }
            catch { _cs[k] = g.Device.CreateComputeShader(Fresh(k)); }
        }
        if (!File.Exists(WeightsPath)) throw new InvalidOperationException("CSR Flux model files are not installed.");
        InitWeights();
    }

    void InitWeights()
    {
        _weights = CsrAiFgWeights.Parse(File.ReadAllText(WeightsPath));
        var packed = new List<float>();
        _wOffA = new int[_weights.NetA.Length];
        _bOffA = new int[_weights.NetA.Length];
        for (int i = 0; i < _weights.NetA.Length; i++)
        {
            _wOffA[i] = packed.Count;
            packed.AddRange(_weights.NetA[i].W);
            _bOffA[i] = packed.Count;
            packed.AddRange(_weights.NetA[i].B);
        }
        _wOffB = new int[_weights.NetB.Length];
        _bOffB = new int[_weights.NetB.Length];
        for (int i = 0; i < _weights.NetB.Length; i++)
        {
            _wOffB[i] = packed.Count;
            packed.AddRange(_weights.NetB[i].W);
            _bOffB[i] = packed.Count;
            packed.AddRange(_weights.NetB[i].B);
        }
        if (_weights.Msb != null)
        {
            _wOffMsbShared = packed.Count;
            packed.AddRange(_weights.Msb.Shared.W);
            _bOffMsbShared = packed.Count;
            packed.AddRange(_weights.Msb.Shared.B);
            _wOffMsbBranch = new int[_weights.Msb.Branches.Length];
            _bOffMsbBranch = new int[_weights.Msb.Branches.Length];
            for (int i = 0; i < _weights.Msb.Branches.Length; i++)
            {
                _wOffMsbBranch[i] = packed.Count;
                packed.AddRange(_weights.Msb.Branches[i].W);
                _bOffMsbBranch[i] = packed.Count;
                packed.AddRange(_weights.Msb.Branches[i].B);
            }
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

    public void BeginPair() => _pairReady = false;

    public void PackA(FgV2 v2, Tex prev, Tex curr)
    {
        int qw = (prev.Width + 3) / 4, qh = (prev.Height + 3) / 4;
        EnsurePack(qw, qh);
        v2.PackAi(prev, curr, _pack!.Uav, qw, qh);
    }

    public byte[] ReadPackF16()
    {
        if (_pack == null) throw new InvalidOperationException("CSR Flux pack has not run.");
        return _g.ReadR16ArrayCOrder(_pack.Texture, _pack.Width, _pack.Height, 8);
    }

    public byte[] ReadNetAF16()
    {
        if (_netA == null) throw new InvalidOperationException("CSR Flux net A has not run.");
        return _g.ReadR16ArrayCOrder(_netA.Texture, _netA.Width, _netA.Height, 7);
    }

    public byte[] ReadPackBF16()
    {
        if (_packB == null) throw new InvalidOperationException("CSR Flux pack B has not run.");
        return _g.ReadR16ArrayCOrder(_packB.Texture, _packB.Width, _packB.Height, 12);
    }

    public byte[] ReadNetBF16()
    {
        if (_netB == null) throw new InvalidOperationException("CSR Flux net B has not run.");
        return _g.ReadR16ArrayCOrder(_netB.Texture, _netB.Width, _netB.Height, _weights!.NetBOut);
    }

    public Tex Generate(FgV2 v2, Tex prevOutput, Tex currOutput, float t)
    {
        var dest = EnsureOut(prevOutput.Width, prevOutput.Height);
        GenerateInto(v2, prevOutput, currOutput, t, dest);
        return dest;
    }

    public Tex[] GenerateMulti(FgV2 v2, Tex prevOutput, Tex currOutput, int n, Tex[] dests)
    {
        n = Math.Clamp(n, 2, 20);
        int g = n - 1;
        var list = new Tex[g];
        for (int i = 0; i < g; i++)
        {
            if (dests != null && i < dests.Length && dests[i] != null) list[i] = dests[i];
            else list[i] = EnsureOut(prevOutput.Width, prevOutput.Height);
            GenerateInto(v2, prevOutput, currOutput, (i + 1) / (float)n, list[i]);
        }
        return list;
    }

    void GenerateInto(FgV2 v2, Tex prev, Tex curr, float t, Tex dest)
    {
        if (_packOnly || _weights == null) throw new InvalidOperationException("CSR Flux model files are not installed.");
        int qw = (prev.Width + 3) / 4, qh = (prev.Height + 3) / 4;
        int hw = (prev.Width + 1) / 2, hh = (prev.Height + 1) / 2;
        if (!_pairReady)
        {
            EnsureA(qw, qh);
            v2.PackAi(prev, curr, _pack!.Uav, qw, qh);
            var src = _pack;
            var dst = _a0!;
            int tail = _weights.NetA.Length - 1;
            for (int i = 0; i < tail; i++)
            {
                RunConv(_weights.NetA[i], _wOffA[i], _bOffA[i], qw, qh, src, dst);
                src = dst;
                dst = src == _a0 ? _a1! : _a0!;
            }
            if (_weights.Msb != null)
                RunMsb(qw, qh, src);
            RunConv(_weights.NetA[tail], _wOffA[tail], _bOffA[tail], qw, qh, src, dst);
            _netA = dst;
            _pairReady = true;
        }
        EnsureB(hw, hh);
        v2.PackAiB(prev, curr, _pack!.Srv, _netA!.Srv, _packB!.Uav, hw, hh, t);
        var bSrc = _packB;
        var bDst = _b0!;
        for (int i = 0; i < _weights.NetB.Length; i++)
        {
            RunConv(_weights.NetB[i], _wOffB[i], _bOffB[i], hw, hh, bSrc, bDst);
            bSrc = bDst;
            bDst = bSrc == _b0 ? _b1! : _b0!;
        }
        _netB = bSrc;
        v2.ComposeAi(prev, curr, _pack.Srv, _netA.Srv, _netB.Srv, dest, t, _weights.MaskA, _weights.MaskB, _weights.LpSigmoid, _weights.NetBOut);
    }

    void RunMsb(int qw, int qh, CsrAiFeat yMain)
    {
        var msb = _weights!.Msb!;
        int bc = msb.Shared.Out;
        if (_msbS == null || _msbS.Width != qw || _msbS.Height != qh || _msbS.Slices < bc)
        {
            _msbS?.Dispose();
            _msbS = new CsrAiFeat(_g.Device, qw, qh, bc);
        }
        RunConv(msb.Shared, _wOffMsbShared, _bOffMsbShared, qw, qh, _pack!, _msbS);
        for (int i = 0; i < msb.Branches.Length; i++)
        {
            int off = i * bc;
            RunConv(msb.Branches[i], _wOffMsbBranch[i], _bOffMsbBranch[i], qw, qh, _msbS, yMain,
                outChOff: off, accumulate: true);
        }
    }

    void EnsurePack(int qw, int qh)
    {
        if (_pack != null && _qw == qw && _qh == qh) return;
        _pack?.Dispose();
        _pack = new CsrAiFeat(_g.Device, qw, qh, 8);
        _qw = qw; _qh = qh;
        _pairReady = false;
    }

    void EnsureA(int qw, int qh)
    {
        EnsurePack(qw, qh);
        int c = 16;
        foreach (var l in _weights!.NetA) c = Math.Max(c, Math.Max(l.In, l.Out));
        if (_weights.Msb != null) c = Math.Max(c, _weights.NetA[^2].Out);
        if (_a0 != null && _a0.Width == qw && _a0.Height == qh && _a0.Slices >= c) return;
        _a0?.Dispose(); _a1?.Dispose();
        _a0 = new CsrAiFeat(_g.Device, qw, qh, c);
        _a1 = new CsrAiFeat(_g.Device, qw, qh, c);
        _pairReady = false;
    }

    void EnsureB(int hw, int hh)
    {
        int c = 8;
        foreach (var l in _weights!.NetB) c = Math.Max(c, Math.Max(l.In, l.Out));
        int packC = Math.Max(12, _weights.NetB.Length > 0 ? _weights.NetB[0].In : 12);
        if (_b0 != null && _hw == hw && _hh == hh && _b0.Slices >= c && _packB != null && _packB.Slices >= packC) return;
        _b0?.Dispose(); _b1?.Dispose(); _packB?.Dispose();
        _b0 = new CsrAiFeat(_g.Device, hw, hh, c);
        _b1 = new CsrAiFeat(_g.Device, hw, hh, c);
        _packB = new CsrAiFeat(_g.Device, hw, hh, packC);
        _hw = hw; _hh = hh;
    }

    Tex EnsureOut(int w, int h)
    {
        if (_out != null && _out.Width == w && _out.Height == h) return _out;
        _out?.Dispose();
        _out = new Tex(_g.Device, w, h, Format.R8G8B8A8_UNorm);
        return _out;
    }

    void SetParams(in CsrAiParams p)
    {
        var m = _g.Context.Map(_cb, 0, MapMode.WriteDiscard);
        try { unsafe { *(CsrAiParams*)m.DataPointer = p; } }
        finally { _g.Context.Unmap(_cb, 0); }
    }

    void RunConv(CsrAiLayer layer, int wOff, int bOff, int w, int h, CsrAiFeat src, CsrAiFeat dst,
        int outChOff = 0, bool accumulate = false)
    {
        var p = new CsrAiParams
        {
            SrcW = w, SrcH = h, DstW = w, DstH = h,
            InC = layer.In, OutC = layer.Out,
            ActLeaky = layer.Act == "leaky0.1" ? 1 : 0,
            ActRelu = layer.Act == "relu" ? 1 : 0,
            WeightOff = wOff, BiasOff = bOff,
            Dilation = layer.Dilation,
            OutChOff = outChOff,
            Accumulate = accumulate ? 1 : 0,
        };
        SetParams(p);
        var ctx = _g.Context;
        string kernel = layer.Dilation > 1 ? "CSRAIFG_ConvDilated" : "CSRAIFG_Conv";
        ctx.CSSetShader(_cs[kernel]);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _g.LinearSampler);
        ctx.CSSetSampler(1, _g.PointSampler);
        ctx.CSSetShaderResource(1, _wSrv);
        ctx.CSSetShaderResource(2, src.Srv);
        ctx.CSSetUnorderedAccessView(0, dst.Uav);
        ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        ctx.CSSetShaderResource(1, null);
        ctx.CSSetShaderResource(2, null);
        ctx.CSSetUnorderedAccessView(0, null);
        if (_g.Profiling) _g.Mark(kernel);
    }

    public void Dispose()
    {
        _pack?.Dispose(); _a0?.Dispose(); _a1?.Dispose();
        _packB?.Dispose(); _b0?.Dispose(); _b1?.Dispose();
        _msbS?.Dispose();
        _out?.Dispose();
        _wSrv?.Dispose(); _wBuf?.Dispose();
        _cb.Dispose();
        foreach (var cs in _cs.Values) cs.Dispose();
        _cs.Clear();
    }
}
