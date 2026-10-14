using System.Diagnostics;
using UniversalFrameFX.Agent;
using Xunit;

namespace FrameFX.Agent.Tests;

public class SetupAndHostTests
{
    static string SetupScript()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git")) && !File.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        Assert.NotNull(d);
        var f = Directory.EnumerateFiles(d!.FullName, "Setup-FrameFXAgent.ps1", SearchOption.AllDirectories)
            .First(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        return File.ReadAllText(f);
    }

    [Fact]
    public void SetupPinsTestedLlamaBuildByDefault() =>
        Assert.Contains("[string]$LlamaTag = 'b11609'", SetupScript());

    [Fact]
    public void SetupLatestScansRecentReleasesForVulkanZip()
    {
        var s = SetupScript();
        Assert.Contains("releases?per_page=", s);
        Assert.Contains("bin-win-vulkan-x64", s);
        Assert.DoesNotContain("releases/latest", s);
    }

    [Fact]
    public void SetupDisablesRtssBeforeListDevices()
    {
        var s = SetupScript();
        int env = s.IndexOf("$env:DISABLE_RTSS_LAYER = '1'", StringComparison.Ordinal);
        int list = s.IndexOf("--list-devices", StringComparison.Ordinal);
        Assert.True(env >= 0 && list > env, "DISABLE_RTSS_LAYER must be set before --list-devices");
    }

    [Fact]
    public void ServerProcessDisablesRtssLayer()
    {
        var psi = new ProcessStartInfo("llama-server");
        LlamaServerHost.ConfigureEnvironment(psi);
        Assert.Equal("1", psi.Environment["DISABLE_RTSS_LAYER"]);
    }

    [Theory]
    [InlineData(512L << 20, 16L << 30, (512L << 20) + (8L << 30))]  // iGPU: 512 MB dedicated + half of 16 GB shared
    [InlineData(8L << 30, 16L << 30, 8L << 30)]                      // dGPU: dedicated only
    [InlineData(1L << 30, 0L, 1L << 30)]                             // no shared memory reported
    public void EffectiveVramCountsSharedOnlyForIgpu(long dedicated, long shared, long expected) =>
        Assert.Equal(expected, ContextSizer.EffectiveVram(dedicated, shared));
}
