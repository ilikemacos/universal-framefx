using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct Csr2Params
{
    public float InW, InH, OutW, OutH;
    public float HistoryValid, Sharpness, HasMotion, HasConf;
    public float Cheap, ClampK, BlendK, SharpK;
    public float JitterX, JitterY, Pad0, Pad1;
}

public sealed class Csr2Upscale : IDisposable
{
    public static readonly string[] Kernels = { "CSR2_Upsample", "CSR2_Accum", "CSR2_CAS" };

    readonly Gpu _g;
    readonly Dictionary<string, ID3D11ComputeShader> _cs = new();
    readonly ID3D11Buffer _cb;
    Tex? _mid, _accum, _histA, _histB;
    bool _hasHistory;
    int _ping;
    public bool Fast;
    public float JitterX, JitterY;

    static readonly bool TemporalOn = Environment.GetEnvironmentVariable("UFX_CSR2UP_TEMPORAL") != "0";
    static readonly float SharpK = EnvF("UFX_CSR2UP_SHARP_K", 1f);
    static readonly float ClampK = EnvF("UFX_CSR2UP_CLAMP", 1f);
    static readonly float BlendK = EnvF("UFX_CSR2UP_BLEND", 1f);

    static float EnvF(string n, float d) =>
        float.TryParse(Environment.GetEnvironmentVariable(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

    static string LoadSrc()
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", "Csr2Upscale.hlsl");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Csr2Upscale.hlsl") ?? throw new InvalidOperationException("Csr2Upscale.hlsl is missing.");
        return new StreamReader(s).ReadToEnd();
    }

    public Csr2Upscale(Gpu g)
    {
        _g = g;
        string src = LoadSrc();
        var blobs = ShaderCache.GetMany(src, Kernels.Select(k => (k, "cs_5_0")), "Csr2Upscale.hlsl");
        byte[] Fresh(string e) { using var bl = Gpu.CompileSource(src, e, "cs_5_0", "Csr2Upscale.hlsl"); return bl.AsSpan().ToArray(); }
        foreach (var k in Kernels)
        {
            try { _cs[k] = g.Device.CreateComputeShader(blobs[k]); }
            catch { _cs[k] = g.Device.CreateComputeShader(Fresh(k)); }
        }
        _cb = g.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 64, Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write
        });
    }

    public void ResetHistory() { _hasHistory = false; }

    void Ensure(int w, int h)
    {
        if (_mid != null && _mid.Width == w && _mid.Height == h) return;
        _mid?.Dispose(); _accum?.Dispose(); _histA?.Dispose(); _histB?.Dispose();
        _mid = new Tex(_g.Device, w, h, Format.R16G16B16A16_Float);
        _accum = new Tex(_g.Device, w, h, Format.R16G16B16A16_Float);
        _histA = new Tex(_g.Device, w, h, Format.R16G16B16A16_Float);
        _histB = new Tex(_g.Device, w, h, Format.R16G16B16A16_Float);
        _hasHistory = false;
    }

    void SetParams(in Csr2Params p)
    {
        var m = _g.Context.Map(_cb, 0, MapMode.WriteDiscard);
        try { unsafe { *(Csr2Params*)m.DataPointer = p; } }
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
        if (_g.Profiling) _g.Mark(kernel);
    }

    public void Encode(ID3D11ShaderResourceView input, int inW, int inH, Tex output,
                        float sharpness, Tex? motion, ID3D11ShaderResourceView? confidence, bool historyValid)
    {
        Ensure(output.Width, output.Height);
        var p = new Csr2Params
        {
            InW = inW, InH = inH, OutW = output.Width, OutH = output.Height,
            HistoryValid = historyValid && _hasHistory ? 1 : 0,
            Sharpness = sharpness, HasMotion = motion != null ? 1 : 0, HasConf = confidence != null ? 1 : 0,
            Cheap = Fast ? 1 : 0, ClampK = ClampK, BlendK = BlendK, SharpK = SharpK,
            JitterX = JitterX, JitterY = JitterY
        };
        SetParams(p);
        Run("CSR2_Upsample", output.Width, output.Height,
            new[] { (0, (ID3D11ShaderResourceView?)input) },
            new[] { (0, _mid!.Uav) });
        ID3D11ShaderResourceView casIn = _mid.Srv;
        if (TemporalOn)
        {
            var hIn = _ping == 0 ? _histA! : _histB!;
            var hOut = _ping == 0 ? _histB! : _histA!;
            Run("CSR2_Accum", output.Width, output.Height,
                new[] { (0, (ID3D11ShaderResourceView?)_mid.Srv), (1, hIn.Srv), (2, motion?.Srv), (3, confidence) },
                new[] { (0, _accum!.Uav), (1, hOut.Uav) });
            _ping = 1 - _ping;
            _hasHistory = true;
            casIn = _accum.Srv;
        }
        SetParams(p);
        Run("CSR2_CAS", output.Width, output.Height,
            new[] { (0, (ID3D11ShaderResourceView?)casIn) },
            new[] { (0, output.Uav) });
    }

    public void Dispose()
    {
        _mid?.Dispose(); _accum?.Dispose(); _histA?.Dispose(); _histB?.Dispose();
        _cb.Dispose();
        foreach (var cs in _cs.Values) cs.Dispose();
        _cs.Clear();
    }
}
