namespace OpenLightFX.Emby.Rooms;

using Openlightfx;

/// <summary>Canonical channel layouts and legacy-track layout inference.</summary>
public static class TrackLayout
{
    public static readonly IReadOnlyDictionary<string, string[]> Positions = new Dictionary<string, string[]>
    {
        ["LAYOUT_AMBIENT"] = ["SPATIAL_AMBIENT"],
        ["LAYOUT_2_0"] = ["SPATIAL_LEFT", "SPATIAL_RIGHT"],
        ["LAYOUT_3_0"] = ["SPATIAL_LEFT", "SPATIAL_CENTER", "SPATIAL_RIGHT"],
        ["LAYOUT_5_0"] = ["SPATIAL_LEFT", "SPATIAL_CENTER", "SPATIAL_RIGHT", "SPATIAL_SURROUND_LEFT", "SPATIAL_SURROUND_RIGHT"]
    };

    public static string CanonicalPosition(string spatialHint)
    {
        if (spatialHint == "SPATIAL_BEHIND_SCREEN") return "SPATIAL_CENTER";
        if (spatialHint is "SPATIAL_CEILING" or "SPATIAL_FLOOR") return "SPATIAL_AMBIENT";
        return spatialHint;
    }

    public static string Infer(IEnumerable<Channel> channels)
    {
        var positions = channels.Select(channel =>
        {
            var hint = CanonicalPosition(channel.SpatialHint);
            if (!string.IsNullOrWhiteSpace(hint) && hint != "SPATIAL_UNSPECIFIED") return hint;
            var name = $"{channel.Id} {channel.DisplayName}".ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            if (name.Contains("surround left")) return "SPATIAL_SURROUND_LEFT";
            if (name.Contains("surround right")) return "SPATIAL_SURROUND_RIGHT";
            if (name.Contains("center") || name.Contains("centre")) return "SPATIAL_CENTER";
            if (name.Contains("left")) return "SPATIAL_LEFT";
            if (name.Contains("right")) return "SPATIAL_RIGHT";
            if (name.Contains("ambient")) return "SPATIAL_AMBIENT";
            return string.Empty;
        }).ToHashSet(StringComparer.Ordinal);

        if (positions.Contains("SPATIAL_SURROUND_LEFT") || positions.Contains("SPATIAL_SURROUND_RIGHT")) return "LAYOUT_5_0";
        if (positions.Contains("SPATIAL_CENTER")) return "LAYOUT_3_0";
        if (positions.Any(position => position is "SPATIAL_LEFT" or "SPATIAL_RIGHT")) return "LAYOUT_2_0";
        return "LAYOUT_AMBIENT";
    }

    public static string? Validate(string layout, IEnumerable<Channel> channels)
    {
        if (!Positions.TryGetValue(layout, out var expected))
            return $"V-022: Unsupported channel_layout {layout}";

        var actual = channels.Select(channel => CanonicalPosition(channel.SpatialHint)).ToArray();
        if (actual.Length != expected.Length ||
            expected.Any(position => actual.Count(candidate => candidate == position) != 1) ||
            actual.Any(position => !expected.Contains(position, StringComparer.Ordinal)))
            return $"V-023: channel_layout {layout} must contain exactly one channel for each layout position";
        return null;
    }
}
