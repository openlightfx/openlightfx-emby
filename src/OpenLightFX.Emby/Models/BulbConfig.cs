namespace OpenLightFX.Emby.Models;

using System.Text.Json.Serialization;

public class BulbConfig
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty; // "Wiz", "Hue", "Lifx", "Govee", "Rest"
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? SpatialPosition { get; set; }
    public string? MacAddress { get; set; }
    public string? Model { get; set; }

    // Hue-specific
    [JsonPropertyName("hueBridgeIp")]
    public string? HueBridgeIp { get; set; }

    [JsonPropertyName("hueApiKey")]
    public string? HueApiKey { get; set; }

    [JsonPropertyName("hueLightId")]
    public string? HueLightId { get; set; }

    // Generic REST-specific
    [JsonPropertyName("restUrlTemplate")]
    public string? RestUrlTemplate { get; set; }

    [JsonPropertyName("restHttpMethod")]
    public string? RestHttpMethod { get; set; }

    [JsonPropertyName("restBodyTemplate")]
    public string? RestBodyTemplate { get; set; }

    [JsonPropertyName("restHeaders")]
    public Dictionary<string, string>? RestHeaders { get; set; }

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
