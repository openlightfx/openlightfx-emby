namespace OpenLightFX.Emby.Models;

using System.Text.Json.Serialization;

public class HueBulbConfig : BulbConfig
{
    public override BulbProtocol Protocol => BulbProtocol.Hue;

    [JsonPropertyName("hueBridgeIp")]
    public string HueBridgeIp { get; set; } = string.Empty;

    [JsonPropertyName("hueApiKey")]
    public string HueApiKey { get; set; } = string.Empty;

    [JsonPropertyName("hueLightId")]
    public string? HueLightId { get; set; }
}
