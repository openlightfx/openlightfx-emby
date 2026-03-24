namespace OpenLightFX.Emby.Models;

using Openlightfx;

public class TrackInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required LightFXTrack Track { get; init; }
    public bool IsValid { get; init; }
    public List<string> ValidationErrors { get; init; } = new();

    // Convenience accessors
    public string Title => Track.Metadata?.Title ?? FileName;
    public string? ImdbId => Track.Metadata?.MovieReference?.ImdbId;
    public ulong DurationMs => Track.Metadata?.DurationMs ?? 0;
    public string Author => Track.Metadata?.Author ?? "Unknown";
    public int ChannelCount => Track.Channels.Count;
    public int KeyframeCount => Track.Keyframes.Count;
    public int EffectKeyframeCount => Track.EffectKeyframes.Count;
}
