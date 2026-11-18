using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class AiTrackHardwareDecodeTests
{
    [Fact]
    public void Prefill_asks_FFmpeg_to_auto_select_hardware_decode()
    {
        var args = AiTrackAnalysis.DecodeArguments("movie.mkv", "scale=256:144[cut]");

        Assert.Contains("-hwaccel", args);
        Assert.Contains("auto", args);
        Assert.Contains("-i", args);
        Assert.Contains("movie.mkv", args);
        Assert.Contains("-filter_complex", args);
        Assert.Contains("pipe:1", args);
    }
}
