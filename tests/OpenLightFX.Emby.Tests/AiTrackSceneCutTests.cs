using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class AiTrackSceneCutTests
{
    [Fact]
    public void ShouldEmitKeyframe_emits_initial_sample_as_channel_baseline()
    {
        Assert.True(AiTrackSceneCut.ShouldEmitKeyframe(false, default, (40, 50, 60), 0.35));
    }

    [Fact]
    public void ShouldEmitKeyframe_skips_a_sample_without_a_scene_cut()
    {
        Assert.False(AiTrackSceneCut.ShouldEmitKeyframe(true, (100, 100, 100), (110, 100, 100), 0.1));
    }

    [Fact]
    public void IsCut_returns_false_when_color_difference_is_below_threshold()
    {
        Assert.False(AiTrackSceneCut.IsCut((100, 100, 100), (110, 100, 100), 0.1));
    }

    [Fact]
    public void IsCut_returns_true_when_color_difference_reaches_threshold()
    {
        Assert.True(AiTrackSceneCut.IsCut((0, 0, 0), (255, 255, 255), 1));
    }

    [Fact]
    public void Lower_threshold_detects_smaller_color_changes()
    {
        var previous = ((byte)100, (byte)100, (byte)100);
        var current = ((byte)130, (byte)100, (byte)100);

        Assert.False(AiTrackSceneCut.IsCut(previous, current, 0.1));
        Assert.True(AiTrackSceneCut.IsCut(previous, current, 0.03));
    }
}
