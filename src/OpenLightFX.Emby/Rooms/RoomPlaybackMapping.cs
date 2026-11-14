namespace OpenLightFX.Emby.Rooms;

using OpenLightFX.Emby.Models;
using Openlightfx;

/// <summary>Builds playback's transient channel-to-bulb dispatch from the shared RoomRouter.</summary>
public static class RoomPlaybackMapping
{
    public static PlaybackMapping Create(
        RoomRecord room,
        IReadOnlyList<KnownBulbRecord> knownBulbs,
        LightFXTrack track)
    {
        var channels = track.Channels
            .Select(channel => new RoutingChannel(channel.Id, channel.SpatialHint, channel.DisplayName))
            .ToArray();
        var routing = RoomRouter.Route(channels, room, knownBulbs);
        var mappings = routing.Channels
            .Where(channel => channel.BulbIds.Count > 0)
            .Select(channel => new ChannelBulbMapping
            {
                ChannelId = channel.ChannelId,
                BulbIds = channel.BulbIds.ToList()
            })
            .ToList();
        var bulbSources = routing.Bulbs.Select(bulb => new BulbChannelMapping
        {
            BulbId = bulb.BulbId,
            Sources = bulb.Sources.Select(source => new BulbChannelSource
            {
                ChannelId = source.ChannelId,
                Kind = source.Kind
            }).ToList()
        }).ToList();
        return new PlaybackMapping { Name = room.Name, Mappings = mappings, BulbSources = bulbSources };
    }
}
