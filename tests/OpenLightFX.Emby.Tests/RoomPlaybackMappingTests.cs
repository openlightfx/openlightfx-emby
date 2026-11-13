using OpenLightFX.Emby.Rooms;
using Openlightfx;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class RoomPlaybackMappingTests
{
    [Fact]
    public void Maps_track_spatial_hints_to_room_positions_and_follows_overrides()
    {
        var track = new LightFXTrack();
        track.Channels.Add(new Channel { Id = "channel-a", SpatialHint = "SPATIAL_LEFT" });
        track.Channels.Add(new Channel { Id = "channel-b", DisplayName = "Surround Right" });
        var room = new RoomRecord
        {
            Name = "Living Room",
            Bulbs = new()
            {
                new RoomBulbAssignment { Id = "lamp-left", Position = "left" },
                new RoomBulbAssignment { Id = "lamp-follow", Position = "ambient", Follows = "surround-right" },
                new RoomBulbAssignment { Id = "unplaced", Position = null }
            }
        };
        var known = room.Bulbs.Select(assignment => new KnownBulbRecord { Id = assignment.Id }).ToList();

        var mapping = RoomPlaybackMapping.Create(room, known, track);

        Assert.Equal(2, mapping.Mappings.Count);
        Assert.Equal(new[] { "lamp-left" }, mapping.Mappings[0].BulbIds);
        Assert.Equal("channel-a", mapping.Mappings[0].ChannelId);
        Assert.Equal(new[] { "lamp-follow" }, mapping.Mappings[1].BulbIds);
        Assert.Equal("channel-b", mapping.Mappings[1].ChannelId);
    }
}
