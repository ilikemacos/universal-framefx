using UniversalFrameFX.Agent;
using Xunit;

public class ContextSizerTests
{
    const long GB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(16, 131_072)] // 9070 XT
    [InlineData(8, 131_072)]
    [InlineData(6, 131_072)]  // 3060 laptop, nothing else on the GPU (estimate ~4.9 GB)
    public void BigEnoughGpus_GetFull128k_FullOffload(int gb, int ctx)
    {
        var p = ContextSizer.Pick(gb * GB);
        Assert.Equal(ctx, p.ContextTokens);
        Assert.Equal(KvCacheType.Q8_0, p.KvType);
        Assert.True(p.FullOffload(new ModelSpec()));
        Assert.True(p.UseYarn);
        Assert.Equal(4, p.YarnFactor);
    }

    [Fact]
    public void Gpu6GB_WhileGaming_StepsDown()
    {
        var p = ContextSizer.Pick(6 * GB, reserveBytes: ContextSizer.GamingReserveBytes);
        Assert.Equal(65_536, p.ContextTokens);
        Assert.Equal(KvCacheType.Q4_0, p.KvType);
        Assert.Equal(2, p.YarnFactor);
    }

    [Fact]
    public void SmallGpu_PartialOffload()
    {
        var p = ContextSizer.Pick(2 * GB);
        Assert.Equal(16_384, p.ContextTokens);
        Assert.False(p.FullOffload(new ModelSpec()));
        Assert.InRange(p.GpuLayers, 8, 36);
        Assert.False(p.UseYarn);
    }

    [Fact]
    public void NoVram_CpuOnly() => Assert.Equal(0, ContextSizer.Pick(512L * 1024 * 1024).GpuLayers);

    [Fact]
    public void PlanAlwaysFitsBudget()
    {
        for (long v = 2 * GB; v <= 24 * GB; v += GB / 4)
        {
            var p = ContextSizer.Pick(v);
            Assert.True(p.EstimatedVramBytes <= v - ContextSizer.DefaultReserveBytes, $"vram {v}");
        }
    }

    [Fact]
    public void MaxContextCap_Respected()
    {
        var p = ContextSizer.Pick(16 * GB, maxContext: 32_768);
        Assert.Equal(32_768, p.ContextTokens);
        Assert.False(p.UseYarn);
        Assert.DoesNotContain("--rope-scaling", p.ToArgs());
    }

    [Fact]
    public void Args_YarnAndQuantCache()
    {
        var a = ContextSizer.Pick(16 * GB).ToArgs();
        Assert.Equal("131072", a[a.ToList().IndexOf("-c") + 1]);
        Assert.Contains("yarn", a);
        Assert.Equal("32768", a[a.ToList().IndexOf("--yarn-orig-ctx") + 1]);
        Assert.Equal("q8_0", a[a.ToList().IndexOf("--cache-type-v") + 1]);
        Assert.Contains("-fa", a);
    }

    [Fact]
    public void Ladder_DescendsAndEndsWithF16()
    {
        var first = ContextSizer.Pick(16 * GB);
        var l = ContextSizer.Ladder(first).ToList();
        Assert.Equal(first, l[0]);
        for (int i = 2; i < 10; i++) Assert.True(l[i].ContextTokens < l[i - 1].ContextTokens);
        Assert.Equal(KvCacheType.F16, l[^1].KvType);
        Assert.DoesNotContain("-fa", l[^1].ToArgs());
    }

    [Fact]
    public void KvMath_3B()
    {
        var m = new ModelSpec();
        // 2 (K,V) * 2 heads * 128 dim * 2 bytes = 1024 B per layer per token at f16
        Assert.Equal(1024, m.KvBytesPerTokenPerLayer(KvCacheType.F16));
        Assert.Equal(544, m.KvBytesPerTokenPerLayer(KvCacheType.Q8_0));
    }
}
