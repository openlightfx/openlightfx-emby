namespace OpenLightFX.Emby.Models;

using System.Net;
using System.Text.Json.Serialization;
using OpenLightFX.Emby.Utilities;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "protocol")]
[JsonDerivedType(typeof(WizBulbConfig),   "Wiz")]
[JsonDerivedType(typeof(HueBulbConfig),   "Hue")]
[JsonDerivedType(typeof(LifxBulbConfig),  "Lifx")]
[JsonDerivedType(typeof(GoveeBulbConfig), "Govee")]
[JsonDerivedType(typeof(RestBulbConfig),  "Rest")]
public abstract class BulbConfig
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Resolved from the JSON type discriminator; never stored as a separate field.</summary>
    [JsonIgnore]
    public abstract BulbProtocol Protocol { get; }

    [JsonConverter(typeof(IPAddressJsonConverter))]
    public IPAddress IpAddress { get; set; } = IPAddress.None;

    public int Port { get; set; }
    public string? SpatialPosition { get; set; }
    public string? MacAddress { get; set; }
    public string? Model { get; set; }

    // Capability overrides (null = use protocol defaults)
    public CapabilityOverrides? CapabilityOverrides { get; set; }
}

public class CapabilityOverrides
{
    public bool? SupportsRgb { get; set; }
    public bool? SupportsColorTemp { get; set; }
    public uint? MinTransitionMs { get; set; }
    public uint? MaxCommandsPerSecond { get; set; }
}
