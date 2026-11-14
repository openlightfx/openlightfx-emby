namespace OpenLightFX.Emby.Rooms;

using OpenLightFX.Emby.Models;
using Openlightfx;

public sealed record RoutingChannel(string Id, string? SpatialHint, string? DisplayName = null);
public sealed record RoutingSource(string ChannelId, string Kind);
public sealed record RoutedChannel(string ChannelId, string? Position, IReadOnlyList<string> BulbIds, IReadOnlyList<string> FoldedInto);
public sealed record RoutedBulb(string BulbId, IReadOnlyList<RoutingSource> Sources);
public sealed record RoomRouting(IReadOnlyList<RoutedChannel> Channels, IReadOnlyList<RoutedBulb> Bulbs);

/// <summary>Single position-based routing implementation shared by playback and Studio preview.</summary>
public static class RoomRouter
{
    public static RoomRouting Route(
        IReadOnlyList<RoutingChannel> channels,
        RoomRecord room,
        IReadOnlyList<KnownBulbRecord> knownBulbs)
    {
        var known = knownBulbs.ToDictionary(bulb => bulb.Id, StringComparer.Ordinal);
        var roomBulbs = room.Bulbs
            .Where(assignment => known.ContainsKey(assignment.Id))
            .Select(assignment => new EffectiveBulb(
                assignment.Id,
                ParsePosition(assignment.Follows) ?? ParsePosition(assignment.Position)))
            .Where(bulb => bulb.Position != null)
            .ToList();
        var channelPositions = channels.ToDictionary(channel => channel.Id, ChannelPosition, StringComparer.Ordinal);
        var channelOrder = channels.Select((channel, index) => (channel.Id, index))
            .ToDictionary(entry => entry.Id, entry => entry.index, StringComparer.Ordinal);
        var sources = roomBulbs.ToDictionary(
            bulb => bulb.Id,
            _ => new List<RoutingSource>(),
            StringComparer.Ordinal);
        var foldedInto = channels.ToDictionary(channel => channel.Id, _ => new HashSet<string>(), StringComparer.Ordinal);

        void Add(string bulbId, string channelId, string kind)
        {
            var list = sources[bulbId];
            if (!list.Any(source => source.ChannelId == channelId))
                list.Add(new RoutingSource(channelId, kind));
        }

        foreach (var channel in channels)
        {
            var position = channelPositions[channel.Id];
            if (position == null)
            {
                // A legacy channel without a position is an Ambient-only source.
                var ambient = roomBulbs.Where(bulb => bulb.Position == "ambient").ToList();
                var destinations = ambient.Count > 0 ? ambient : roomBulbs;
                foreach (var bulb in destinations) Add(bulb.Id, channel.Id, "fold");
                if (ambient.Count == 0 && destinations.Count > 0) foldedInto[channel.Id].Add("all");
                else if (destinations.Count > 0) foldedInto[channel.Id].Add("ambient");
                continue;
            }

            var native = roomBulbs.Where(bulb => bulb.Position == position).ToList();
            if (position == "ambient")
            {
                foreach (var bulb in roomBulbs) Add(bulb.Id, channel.Id, "native");
                continue;
            }
            if (native.Count > 0)
            {
                foreach (var bulb in native) Add(bulb.Id, channel.Id, "native");
                continue;
            }

            var destinationsByPosition = FoldDestinations(position, roomBulbs);
            foreach (var destination in destinationsByPosition)
            {
                foldedInto[channel.Id].Add(destination.Position);
                foreach (var bulb in destination.Bulbs) Add(bulb.Id, channel.Id, "fold");
            }
        }

        // Fill only bulbs that otherwise receive no source. This keeps native and folded
        // routing authoritative while ensuring an unmatched room position is not dark.
        foreach (var bulb in roomBulbs)
        {
            if (sources[bulb.Id].Count > 0) continue;
            var fill = FillSources(bulb.Position!, channels, channelPositions);
            foreach (var channelId in fill) Add(bulb.Id, channelId, "fill");
        }

        // The all-bulbs route is the final fallback, after fill rules assign surround,
        // center, and Ambient bulbs their intended channels.
        foreach (var channel in channels)
        {
            if (sources.Values.Any(list => list.Any(source => source.ChannelId == channel.Id))) continue;
            foreach (var bulb in roomBulbs) Add(bulb.Id, channel.Id, "fold");
            if (roomBulbs.Count > 0) foldedInto[channel.Id].Add("all");
        }

        var routedChannels = channels.Select(channel =>
        {
            var bulbIds = sources
                .Where(entry => entry.Value.Any(source => source.ChannelId == channel.Id))
                .Select(entry => entry.Key)
                .ToArray();
            return new RoutedChannel(
                channel.Id,
                channelPositions[channel.Id],
                bulbIds,
                foldedInto[channel.Id].OrderBy(position => position, StringComparer.Ordinal).ToArray());
        }).ToArray();
        var routedBulbs = roomBulbs.Select(bulb => new RoutedBulb(
            bulb.Id,
            sources[bulb.Id].OrderBy(source => channelOrder[source.ChannelId])
                .ToArray())).ToArray();
        return new RoomRouting(routedChannels, routedBulbs);
    }

    public static IReadOnlyList<RoutingChannel> ForLayout(string layout) => layout switch
    {
        "LAYOUT_AMBIENT" => new[] { new RoutingChannel("ambient", "SPATIAL_AMBIENT") },
        "LAYOUT_2_0" => new[] { new RoutingChannel("left", "SPATIAL_LEFT"), new RoutingChannel("right", "SPATIAL_RIGHT") },
        "LAYOUT_3_0" => new[] { new RoutingChannel("left", "SPATIAL_LEFT"), new RoutingChannel("center", "SPATIAL_CENTER"), new RoutingChannel("right", "SPATIAL_RIGHT") },
        "LAYOUT_5_0" => new[] { new RoutingChannel("left", "SPATIAL_LEFT"), new RoutingChannel("center", "SPATIAL_CENTER"), new RoutingChannel("right", "SPATIAL_RIGHT"), new RoutingChannel("surround-left", "SPATIAL_SURROUND_LEFT"), new RoutingChannel("surround-right", "SPATIAL_SURROUND_RIGHT") },
        _ => throw new ArgumentException($"Unsupported channel layout '{layout}'", nameof(layout))
    };

    public static string InferLayout(IReadOnlyList<RoutingChannel> channels)
    {
        var positions = channels.Select(ChannelPosition).Where(position => position != null).ToHashSet(StringComparer.Ordinal);
        if (positions.Contains("surround-left") || positions.Contains("surround-right")) return "LAYOUT_5_0";
        if (positions.Contains("center")) return "LAYOUT_3_0";
        if (positions.Any(position => position != "ambient")) return "LAYOUT_2_0";
        return "LAYOUT_AMBIENT";
    }

    public static string? ChannelPosition(RoutingChannel channel) =>
        ParsePosition(channel.SpatialHint) ?? ParsePosition(channel.Id) ?? ParsePosition(channel.DisplayName);

    public static string? ParsePosition(string? value)
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

    private static IReadOnlyList<(string Position, IReadOnlyList<EffectiveBulb> Bulbs)> FoldDestinations(
        string source,
        IReadOnlyList<EffectiveBulb> bulbs)
    {
        if (source == "ambient")
            return Array.Empty<(string, IReadOnlyList<EffectiveBulb>)>();
        var priorities = source switch
        {
            "surround-left" => new[] { "left", "center", "ambient" },
            "surround-right" => new[] { "right", "center", "ambient" },
            "center" => new[] { "left", "right", "ambient" },
            "left" => new[] { "center", "ambient" },
            "right" => new[] { "center", "ambient" },
            _ => Array.Empty<string>()
        };
        foreach (var position in priorities)
        {
            var candidates = bulbs.Where(bulb => bulb.Position == position).ToList();
            if (candidates.Count == 0) continue;
            if (source == "center" && position is "left" or "right")
            {
                var bothFrontSides = priorities
                    .Where(side => side is "left" or "right")
                    .SelectMany(side => bulbs.Where(bulb => bulb.Position == side))
                    .DistinctBy(bulb => bulb.Id)
                    .ToList();
                return bothFrontSides.Select(group => (group.Position!, (IReadOnlyList<EffectiveBulb>)new[] { group })).ToArray();
            }
            return new[] { (position, (IReadOnlyList<EffectiveBulb>)candidates) };
        }
        return Array.Empty<(string, IReadOnlyList<EffectiveBulb>)>();
    }

    private static IReadOnlyList<string> FillSources(
        string bulbPosition,
        IReadOnlyList<RoutingChannel> channels,
        IReadOnlyDictionary<string, string?> positions)
    {
        var byPosition = channels.Where(channel => positions[channel.Id] != null)
            .GroupBy(channel => positions[channel.Id]!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(channel => channel.Id).ToArray(), StringComparer.Ordinal);
        if (bulbPosition == "ambient") return channels.Select(channel => channel.Id).ToArray();
        string? preferred = bulbPosition switch
        {
            "center" => null,
            "surround-left" => "left",
            "surround-right" => "right",
            "left" or "right" => "center",
            _ => null
        };
        if (bulbPosition == "center" && byPosition.TryGetValue("left", out var left) && byPosition.TryGetValue("right", out var right))
            return left.Concat(right).Distinct(StringComparer.Ordinal).ToArray();
        if (preferred != null && byPosition.TryGetValue(preferred, out var direct)) return direct;
        if (byPosition.TryGetValue("ambient", out var ambient)) return ambient;
        return channels.Select(channel => channel.Id).ToArray();
    }

    private sealed record EffectiveBulb(string Id, string? Position);
}
