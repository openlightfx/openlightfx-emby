namespace OpenLightFX.Emby.Discovery;

using System.Text.Json.Nodes;

public class HueDiscovery : IDiscoveryModule
{
    public string Protocol => "Hue";

    public async Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct)
    {
        var bulbs = new List<DiscoveredBulb>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            // N-UPNP discovery
            var response = await http.GetStringAsync("https://discovery.meethue.com", ct);
            var bridges = JsonNode.Parse(response)?.AsArray();
            if (bridges == null) return bulbs;

            foreach (var bridge in bridges)
            {
                var ip = bridge?["internalipaddress"]?.GetValue<string>();
                if (string.IsNullOrEmpty(ip)) continue;

                string? bridgeName = null;
                string? modelId = null;
                string? mac = null;

                try
                {
                    var configJson = await http.GetStringAsync($"http://{ip}/api/config", ct);
                    var config = JsonNode.Parse(configJson)?.AsObject();
                    bridgeName = config?["name"]?.GetValue<string>();
                    modelId = config?["modelid"]?.GetValue<string>();
                    mac = config?["mac"]?.GetValue<string>();
                }
                catch { /* bridge may not respond */ }

                bulbs.Add(new DiscoveredBulb
                {
                    IpAddress = ip,
                    Port = 80,
                    Protocol = "Hue",
                    MacAddress = mac,
                    Model = modelId,
                    Name = bridgeName
                });
            }
        }
        catch (Exception) { /* N-UPNP or network failure */ }

        return bulbs;
    }
}
