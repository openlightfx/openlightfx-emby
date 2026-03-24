namespace OpenLightFX.Emby.Drivers.Hue;

using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Static helper for the Philips Hue bridge pairing flow.
/// The user must press the physical button on the bridge before calling RequestApiKey.
/// </summary>
public static class HueBridgePairing
{
    private const string DeviceType = "openlightfx#emby";

    /// <summary>
    /// Attempt to register with the Hue Bridge. User must press the bridge button first.
    /// Returns the API key (username) on success, null if the link button was not pressed
    /// or the bridge is unreachable.
    /// </summary>
    public static async Task<string?> RequestApiKey(string bridgeIp, string appName = "OpenLightFX")
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://{bridgeIp}/api";

            var body = new JsonObject { ["devicetype"] = DeviceType };
            var content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var arr = JsonNode.Parse(json) as JsonArray;
            if (arr == null || arr.Count == 0) return null;

            var first = arr[0]?.AsObject();
            if (first == null) return null;

            // Success: [{"success":{"username":"<api-key>"}}]
            var username = first["success"]?["username"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(username)) return username;

            // Link button not pressed: [{"error":{"type":101,...}}]
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if an existing API key is still valid by querying the bridge config.
    /// </summary>
    public static async Task<bool> ValidateApiKey(string bridgeIp, string apiKey)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://{bridgeIp}/api/{apiKey}/config";

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);

            // Invalid key returns an array with error objects
            if (node is JsonArray) return false;

            // Valid key returns a config object with "name", "bridgeid", etc.
            return node is JsonObject obj && obj["name"] != null;
        }
        catch
        {
            return false;
        }
    }
}
