namespace OpenLightFX.Emby.Discovery;

public class DiscoveredBulb
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = string.Empty; // "Wiz", "Hue", "Lifx", "Govee"
    public string? MacAddress { get; set; }
    public string? Model { get; set; }
    public string? Name { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
