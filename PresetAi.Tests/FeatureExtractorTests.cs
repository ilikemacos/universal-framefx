using UniversalFrameFX.PresetAi;
using Xunit;

namespace UniversalFrameFX.PresetAi.Tests;

public class FeatureExtractorTests
{
    [Fact]
    public void Extracts_cyberpunk_and_fps()
    {
        var f = FeatureExtractor.Extract("I want to play Cyberpunk 2077, getting about 45 fps and want 120");
        Assert.NotNull(f.Game);
        Assert.Equal("Cyberpunk2077.exe", f.Game?.Exe);
        Assert.Equal(45, f.Fps);
        Assert.Equal(120, f.Target);
    }
}
