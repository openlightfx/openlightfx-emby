namespace OpenLightFX.Emby.Rooms;

using OpenLightFX.Emby.Models;
using Openlightfx;

/// <summary>Builds playback's transient channel-to-bulb dispatch from room positions.</summary>
public static class RoomPlaybackMapping
{
    public static PlaybackMapping Create(RoomRecord room, IReadOnlyList<KnownBulbRecord> knownBulbs, LightFXTrack track)
    {
        var known = knownBulbs.ToDictionary(bulb => bulb.Id, StringComparer.Ordinal);
        var mappings = new List<ChannelBulbMapping>();
        foreach (var channel in track.Channels)
        {
            var position = ParsePosition(channel.SpatialHint)
                ?? ParsePosition(channel.Id)
                ?? ParsePosition(channel.DisplayName);
            if (position == null) continue;

            var bulbIds = room.Bulbs
                .Where(assignment => known.ContainsKey(assignment.Id))
                .Where(assignment => string.Equals(assignment.Follows ?? assignment.Position, position, StringComparison.Ordinal))
                .Select(assignment => assignment.Id)
                .ToList();
            if (bulbIds.Count > 0)
                mappings.Add(new ChannelBulbMapping { ChannelId = channel.Id, BulbIds = bulbIds });
        }
        return new PlaybackMapping { Name = room.Name, Mappings = mappings };
    }

    private static string? ParsePosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace("SPATIAL_", "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        return normalized switch
        {
            "behind-screen" => "center",
            "ceiling" or "floor" => "ambient",
            "left" or "center" or "right" or "surround-left" or "surround-right" or "ambient" => normalized,
            _ => null
        };
    }
}
