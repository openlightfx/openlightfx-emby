namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Text.Json.Serialization;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;

public class DiscoveredBulb
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonConverter(typeof(IPAddressJsonConverter))]
    public IPAddress IpAddress { get; set; } = IPAddress.None;

    public int Port { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BulbProtocol Protocol { get; set; } = BulbProtocol.Unknown;

    public string? MacAddress { get; set; }
    public string? Model { get; set; }
    public string? Name { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
