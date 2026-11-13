using Google.Protobuf;
using OpenLightFX.Emby.Rooms;
using OpenLightFX.Emby.Services;
using Openlightfx;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class TrackLayoutTests
{
    [Fact]
    public void Legacy_uuid_channels_infer_the_smallest_layout_from_position_hints()
    {
        var channels = new[]
        {
            new Channel { Id = "a1", SpatialHint = "SPATIAL_LEFT" },
            new Channel { Id = "a2", SpatialHint = "SPATIAL_RIGHT" },
            new Channel { Id = "a3", SpatialHint = "SPATIAL_SURROUND_LEFT" },
            new Channel { Id = "a4", SpatialHint = "SPATIAL_SURROUND_RIGHT" }
        };

        Assert.Equal("LAYOUT_5_0", TrackLayout.Infer(channels));
    }

    [Fact]
    public void Parser_accepts_a_valid_declared_layout_and_rejects_bad_layout_codes()
    {
        var valid = Track("LAYOUT_3_0", "SPATIAL_LEFT", "SPATIAL_CENTER", "SPATIAL_RIGHT");
        Assert.True(new TrackParser().ParseBytes(valid.ToByteArray(), "valid.lightfx").IsValid);

        var invalidLayout = Track("LAYOUT_7_0", "SPATIAL_LEFT", "SPATIAL_RIGHT");
        var layoutError = Assert.Single(new TrackParser().ParseBytes(invalidLayout.ToByteArray(), "invalid.lightfx").ValidationErrors);
        Assert.StartsWith("V-022:", layoutError);

        var invalidChannels = Track("LAYOUT_3_0", "SPATIAL_LEFT", "SPATIAL_RIGHT");
        var channelError = Assert.Single(new TrackParser().ParseBytes(invalidChannels.ToByteArray(), "missing-center.lightfx").ValidationErrors);
        Assert.StartsWith("V-023:", channelError);
    }

    [Fact]
    public void Additive_layout_field_round_trips_through_the_plugin_parser()
    {
        var track = Track("LAYOUT_2_0", "SPATIAL_LEFT", "SPATIAL_RIGHT");
        var parsed = new TrackParser().ParseBytes(track.ToByteArray(), "stereo.lightfx");
        Assert.True(parsed.IsValid);
        Assert.Equal("LAYOUT_2_0", parsed.Track.Metadata.ChannelLayout);
    }

    private static LightFXTrack Track(string layout, params string[] hints)
    {
        var track = new LightFXTrack
        {
            Version = 1,
            Metadata = new TrackMetadata { Title = "Layout test", DurationMs = 10000, ChannelLayout = layout }
        };
        for (var i = 0; i < hints.Length; i++)
            track.Channels.Add(new Channel { Id = $"channel-{i}", SpatialHint = hints[i] });
        return track;
    }
}
