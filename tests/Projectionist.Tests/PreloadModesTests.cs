using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class PreloadModesTests
{
    [Fact]
    public void NullConfigIsOff() => Assert.Equal(FeaturePreloadMode.Off, PreloadModes.Effective(null));

    [Fact]
    public void LegacyBoolPromotesOffToWarm()
    {
        var cfg = new PluginConfiguration { EnableFeaturePreload = true, FeaturePreloadMode = FeaturePreloadMode.Off };
        Assert.Equal(FeaturePreloadMode.Warm, PreloadModes.Effective(cfg));
    }

    [Theory]
    [InlineData(FeaturePreloadMode.Off)]
    [InlineData(FeaturePreloadMode.Warm)]
    [InlineData(FeaturePreloadMode.Hot)]
    public void ExplicitModeWins(FeaturePreloadMode mode)
    {
        var cfg = new PluginConfiguration { EnableFeaturePreload = false, FeaturePreloadMode = mode };
        Assert.Equal(mode, PreloadModes.Effective(cfg));
    }
}
