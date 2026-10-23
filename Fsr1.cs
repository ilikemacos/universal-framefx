using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UniversalFrameFX;

/// <summary>"AMD FSR 1" option: AMD's own EASU + RCAS shader code (ffx_fsr1.h), constants per FsrEasuCon / FsrRcasCon.</summary>
public sealed class Fsr1 : IDisposable
{
    readonly Gpu _g;
    readonly ID3D11ComputeShader _easu, _rcas;
    readonly ID3D11Buffer _cb;
    Tex? _mid;

    static string Res(string name)
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", name);
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is missing.");
        return new StreamReader(s).ReadToEnd();
    }

    static Vortice.Direct3D11.ID3D11ComputeShader Create(Gpu g, string src, string entry)
    {
        try { return g.Device.CreateComputeShader(ShaderCache.Get(src, entry, "cs_5_0", "Fsr1.hlsl")); }
        catch { using var bl = Gpu.CompileSource(src, entry, "cs_5_0", "Fsr1.hlsl"); return g.Device.CreateComputeShader(bl.AsSpan().ToArray()); }
    }

    public Fsr1(Gpu g)
    {
        _g = g;
        // Inline AMD's headers (fxc include handling without files on disk).
        string src = Res("Fsr1.hlsl").Replace("#include \"ffx_a.h\"", Res("ffx_a.h")).Replace("#include \"ffx_fsr1.h\"", Res("ffx_fsr1.h"));
        _easu = Create(g, src, "EASU");
        _rcas = Create(g, src, "RCAS");
        _cb = g.Device.CreateBuffer(new BufferDescription { ByteWidth = 64, Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write });
    }

    static uint U(float f) => BitConverter.SingleToUInt32Bits(f);

    /// <summary>FsrEasuCon from ffx_fsr1.h (viewport = whole input).</summary>
    public static uint[] EasuCon(float vpW, float vpH, float inW, float inH, float outW, float outH) => new[]
    {
        U(vpW / outW), U(vpH / outH), U(0.5f * vpW / outW - 0.5f), U(0.5f * vpH / outH - 0.5f),
        U(1f / inW), U(1f / inH), U(1f / inW), U(-1f / inH),
        U(-1f / inW), U(2f / inH), U(1f / inW), U(2f / inH),
        U(0f / inW), U(4f / inH), 0u, 0u,
    };

    /// <summary>FsrRcasCon: sharpness in stops (0 = maximum).</summary>
    public static uint[] RcasCon(float stops)
    {
        float s = MathF.Pow(2f, -stops);
        var h = (ushort)BitConverter.HalfToInt16Bits((Half)s);
        return new uint[] { U(s), (uint)h | ((uint)h << 16), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    void SetCb(uint[] c)
    {
        var m = _g.Context.Map(_cb, 0, MapMode.WriteDiscard);
        Marshal.Copy(Array.ConvertAll(c, x => unchecked((int)x)), 0, m.DataPointer, 16);
        _g.Context.Unmap(_cb, 0);
    }

    void Run(ID3D11ComputeShader cs, ID3D11ShaderResourceView srv, ID3D11UnorderedAccessView uav, int w, int h)
    {
        var ctx = _g.Context;
        ctx.CSSetShader(cs);
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _g.LinearSampler);
        ctx.CSSetShaderResource(0, srv);
        ctx.CSSetUnorderedAccessView(0, uav);
        ctx.Dispatch((uint)((w + 15) / 16), (uint)((h + 15) / 16), 1);
        ctx.CSSetShaderResource(0, null);
        ctx.CSSetUnorderedAccessView(0, null);
    }

    public void Encode(ID3D11ShaderResourceView input, int inW, int inH, Tex output, float sharpnessStops)
    {
        if (_mid == null || _mid.Width != output.Width || _mid.Height != output.Height)
        {
            _mid?.Dispose(); _mid = new Tex(_g.Device, output.Width, output.Height, Format.R16G16B16A16_Float);
        }
        int texW = inW, texH = inH;
        using (var r = input.Resource) using (var t2 = r.QueryInterfaceOrNull<ID3D11Texture2D>())
            if (t2 != null) { texW = (int)t2.Description.Width; texH = (int)t2.Description.Height; }
        SetCb(EasuCon(inW, inH, texW, texH, output.Width, output.Height));
        Run(_easu, input, _mid.Uav!, output.Width, output.Height);
        SetCb(RcasCon(sharpnessStops));
        Run(_rcas, _mid.Srv, output.Uav!, output.Width, output.Height);
    }

    public void Dispose() { _mid?.Dispose(); _cb.Dispose(); _rcas.Dispose(); _easu.Dispose(); }
}
