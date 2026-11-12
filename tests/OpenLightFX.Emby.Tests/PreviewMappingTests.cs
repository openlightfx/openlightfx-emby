using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class PreviewMappingTests
{
    [Fact(DisplayName = "PREV-005 @fixture confirming a mapping updates the active profile")]
    public void Active_profile_records_the_confirmed_channels()
    {
        var json = StudioMappingProfiles.WithChannels(
            "[]",
            "Default",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["left"] = new[] { "b1" },
                ["right"] = new[] { "b2", "b3" }
            });

        Assert.Contains("\"channelId\":\"left\"", json);
        Assert.Contains("\"bulbIds\":[\"b1\"]", json);
        Assert.Contains("right", json);

        var replaced = StudioMappingProfiles.WithChannels(
            json,
            "Default",
            new Dictionary<string, IReadOnlyList<string>> { ["left"] = new[] { "left" } });
        Assert.Contains("\"bulbIds\":[\"left\"]", replaced);
        Assert.DoesNotContain("b1", replaced);
    }
}
