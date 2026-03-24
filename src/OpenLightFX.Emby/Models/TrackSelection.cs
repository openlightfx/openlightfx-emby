namespace OpenLightFX.Emby.Models;

public class TrackSelection
{
    public string ItemId { get; set; } = string.Empty;
    public string TrackPath { get; set; } = string.Empty;
    public DateTime SelectedAt { get; set; } = DateTime.UtcNow;
}
