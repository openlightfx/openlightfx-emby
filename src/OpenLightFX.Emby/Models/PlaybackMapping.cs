namespace OpenLightFX.Emby.Models;

/// <summary>Transient playback mapping derived from a room; never stored as user configuration.</summary>
public sealed class PlaybackMapping
{
    public string Name { get; set; } = string.Empty;
    public List<ChannelBulbMapping> Mappings { get; set; } = new();
}

public sealed class ChannelBulbMapping
{
    public string ChannelId { get; set; } = string.Empty;
    public List<string> BulbIds { get; set; } = new();
}
