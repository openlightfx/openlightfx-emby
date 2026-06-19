namespace OpenLightFX.Emby.Models;

using System.Text.Json.Serialization;

public class RestBulbConfig : BulbConfig
{
    public override BulbProtocol Protocol => BulbProtocol.Rest;

    [JsonPropertyName("restUrlTemplate")]
    public string? RestUrlTemplate { get; set; }

    [JsonPropertyName("restHttpMethod")]
    public string? RestHttpMethod { get; set; }

    [JsonPropertyName("restBodyTemplate")]
    public string? RestBodyTemplate { get; set; }

    [JsonPropertyName("restHeaders")]
    public Dictionary<string, string>? RestHeaders { get; set; }
}
