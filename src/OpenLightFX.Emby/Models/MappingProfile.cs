namespace OpenLightFX.Emby.Models;

public class MappingProfile
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<ChannelMapping> Mappings { get; set; } = new();
}

public class ChannelMapping
{
    public string ChannelId { get; set; } = string.Empty;
    public List<string> BulbIds { get; set; } = new();
}
