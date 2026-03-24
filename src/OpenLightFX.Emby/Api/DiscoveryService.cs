namespace OpenLightFX.Emby.Api;

using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Discovery;
using OpenLightFX.Emby.Models;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// ─── Request DTOs ────────────────────────────────────────────────────

[Route("/OpenLightFX/Discover", "POST", Description = "Run bulb discovery across all protocols")]
public class DiscoverRequest : IReturn<DiscoverResponse> { }

[Route("/OpenLightFX/Identify/{BulbId}", "POST", Description = "Identify a discovered bulb with a visual flash")]
public class IdentifyBulbRequest : IReturn<IdentifyBulbResponse>
{
    public string BulbId { get; set; } = "";
}

[Route("/OpenLightFX/Discover/Bulb/{BulbId}/State", "GET", Description = "Get current state of a discovered bulb")]
public class GetBulbStateRequest : IReturn<BulbStateResponse>
{
    public string BulbId { get; set; } = "";
}

[Route("/OpenLightFX/Discover/Bulb/{BulbId}/Test", "POST", Description = "Test a discovered bulb with a brief flash")]
public class TestDiscoveredBulbRequest : IReturn<TestDiscoveredBulbResponse>
{
    public string BulbId { get; set; } = "";
}

[Route("/OpenLightFX/Hue/Pair", "POST", Description = "Start Hue bridge pairing")]
public class HuePairRequest : IReturn<HuePairResponse>
{
    public string BridgeIp { get; set; } = "";
}

[Route("/OpenLightFX/Hue/Pair/Status", "GET", Description = "Check Hue pairing status")]
public class HuePairStatusRequest : IReturn<HuePairResponse> { }

[Route("/OpenLightFX/Hue/Pair/Poll", "POST", Description = "Poll for Hue pairing completion")]
public class HuePairPollRequest : IReturn<HuePairResponse> { }

[Route("/OpenLightFX/Hue/Lights", "GET", Description = "List lights on a Hue bridge")]
public class GetHueLightsRequest : IReturn<HueLightsResponse>
{
    public string BridgeIp { get; set; } = "";
    public string Username { get; set; } = "";
}

[Route("/OpenLightFX/Discover/SaveConfig", "POST", Description = "Save a discovered bulb as a configured bulb")]
public class SaveDiscoveredBulbConfigRequest : IReturn<SaveDiscoveredBulbConfigResponse>
{
    public string BulbId { get; set; } = "";
    public BulbConfig? Config { get; set; }
}

// ─── Response DTOs ───────────────────────────────────────────────────

public class DiscoverResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("discoveredBulbs")]
    public List<DiscoveredBulbDto> DiscoveredBulbs { get; set; } = new();
}

public class DiscoveredBulbDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = "";

    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("isReachable")]
    public bool IsReachable { get; set; } = true;

    [JsonPropertyName("discoveredAt")]
    public DateTime DiscoveredAt { get; set; }

    [JsonPropertyName("isConfigured")]
    public bool IsConfigured { get; set; }
}

public class IdentifyBulbResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class BulbStateResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("isReachable")]
    public bool IsReachable { get; set; } = true;

    [JsonPropertyName("discoveredAt")]
    public DateTime DiscoveredAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class TestDiscoveredBulbResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class HuePairResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public class HueLightsResponse
{
    [JsonPropertyName("lights")]
    public JsonObject? Lights { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class SaveDiscoveredBulbConfigResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("configuredBulbCount")]
    public int ConfiguredBulbCount { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// ─── Hue pairing state ──────────────────────────────────────────────

internal class HuePairingState
{
    public string BridgeIp { get; set; } = "";
    public string Status { get; set; } = "idle"; // idle, waiting, paired, failed
    public string? Username { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

// ─── Service ─────────────────────────────────────────────────────────

public class DiscoveryService : IService
{
    private static readonly ConcurrentDictionary<string, HuePairingState> HuePairingStates = new();
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── POST /OpenLightFX/Discover ───────────────────────────────────

    public object Post(DiscoverRequest request)
    {
        try
        {
            var entry = ServerEntryPoint.Instance;
            if (entry == null)
                return new DiscoverResponse { Status = "error" };

            var discovered = entry.DiscoveryCoordinator
                .DiscoverAsync()
                .GetAwaiter().GetResult();

            entry.DiscoveredBulbStore.Clear();
            entry.DiscoveredBulbStore.AddRange(discovered);

            var options = entry.GetOptions();
            var configuredBulbs = entry.ConfigService.ParseBulbConfig(options.BulbConfigJson);
            var configuredIps = new HashSet<string>(
                configuredBulbs.Select(b => b.IpAddress),
                StringComparer.OrdinalIgnoreCase);

            var dtos = discovered.Select(b => new DiscoveredBulbDto
            {
                Id = b.Id,
                Protocol = b.Protocol,
                IpAddress = b.IpAddress,
                MacAddress = b.MacAddress,
                Name = b.Name,
                Model = b.Model,
                IsReachable = true,
                DiscoveredAt = b.DiscoveredAt,
                IsConfigured = configuredIps.Contains(b.IpAddress)
            }).ToList();

            return new DiscoverResponse
            {
                Status = "scanning",
                DiscoveredBulbs = dtos
            };
        }
        catch (Exception)
        {
            return new DiscoverResponse { Status = "error" };
        }
    }

    // ── POST /OpenLightFX/Identify/{BulbId} ──────────────────────────

    public object Post(IdentifyBulbRequest request)
    {
        try
        {
            var entry = ServerEntryPoint.Instance;
            if (entry == null)
                return new IdentifyBulbResponse { Success = false, Error = "Plugin not initialized" };

            var bulb = entry.DiscoveredBulbStore.Get(request.BulbId);
            if (bulb == null)
                return new IdentifyBulbResponse { Success = false, Error = "Bulb not found" };

            entry.IdentifyService
                .IdentifyAsync(bulb)
                .GetAwaiter().GetResult();

            return new IdentifyBulbResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new IdentifyBulbResponse { Success = false, Error = ex.Message };
        }
    }

    // ── GET /OpenLightFX/Discover/Bulb/{BulbId}/State ────────────────

    public object Get(GetBulbStateRequest request)
    {
        var entry = ServerEntryPoint.Instance;
        if (entry == null)
            return new BulbStateResponse { Error = "Plugin not initialized" };

        var bulb = entry.DiscoveredBulbStore.Get(request.BulbId);
        if (bulb == null)
            return new BulbStateResponse { Error = "Bulb not found" };

        return new BulbStateResponse
        {
            Id = bulb.Id,
            Protocol = bulb.Protocol,
            IpAddress = bulb.IpAddress,
            Port = bulb.Port,
            MacAddress = bulb.MacAddress,
            Name = bulb.Name,
            Model = bulb.Model,
            IsReachable = true,
            DiscoveredAt = bulb.DiscoveredAt
        };
    }

    // ── POST /OpenLightFX/Discover/Bulb/{BulbId}/Test ────────────────

    public object Post(TestDiscoveredBulbRequest request)
    {
        try
        {
            var entry = ServerEntryPoint.Instance;
            if (entry == null)
                return new TestDiscoveredBulbResponse { Success = false, Error = "Plugin not initialized" };

            var bulb = entry.DiscoveredBulbStore.Get(request.BulbId);
            if (bulb == null)
                return new TestDiscoveredBulbResponse { Success = false, Error = "Bulb not found" };

            entry.IdentifyService
                .IdentifyAsync(bulb)
                .GetAwaiter().GetResult();

            return new TestDiscoveredBulbResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new TestDiscoveredBulbResponse { Success = false, Error = ex.Message };
        }
    }

    // ── POST /OpenLightFX/Hue/Pair ───────────────────────────────────

    public object Post(HuePairRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.BridgeIp))
                return new HuePairResponse { Status = "failed", Message = "bridgeIp is required" };

            var result = TryHuePairing(request.BridgeIp);

            HuePairingStates[request.BridgeIp] = new HuePairingState
            {
                BridgeIp = request.BridgeIp,
                Status = result.Status,
                Username = result.Username
            };

            return result;
        }
        catch (Exception ex)
        {
            return new HuePairResponse { Status = "failed", Message = ex.Message };
        }
    }

    // ── GET /OpenLightFX/Hue/Pair/Status ─────────────────────────────

    public object Get(HuePairStatusRequest request)
    {
        // Return the most recent pairing state
        var state = HuePairingStates.Values
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        if (state == null)
            return new HuePairResponse { Status = "idle" };

        return new HuePairResponse
        {
            Status = state.Status,
            Username = state.Username,
            Message = state.Status == "waiting"
                ? "Press the link button on your Hue bridge"
                : null
        };
    }

    // ── POST /OpenLightFX/Hue/Pair/Poll ──────────────────────────────

    public object Post(HuePairPollRequest request)
    {
        try
        {
            var state = HuePairingStates.Values
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefault();

            if (state == null || state.Status == "idle")
                return new HuePairResponse { Status = "idle", Message = "No pairing in progress" };

            if (state.Status == "paired")
                return new HuePairResponse { Status = "paired", Username = state.Username };

            var result = TryHuePairing(state.BridgeIp);

            state.Status = result.Status;
            state.Username = result.Username;

            return result;
        }
        catch (Exception ex)
        {
            return new HuePairResponse { Status = "failed", Message = ex.Message };
        }
    }

    // ── GET /OpenLightFX/Hue/Lights ──────────────────────────────────

    public object Get(GetHueLightsRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.BridgeIp) || string.IsNullOrEmpty(request.Username))
                return new HueLightsResponse { Error = "bridgeIp and username are required" };

            var url = $"http://{request.BridgeIp}/api/{request.Username}/lights";
            var json = SharedHttpClient.GetStringAsync(url).GetAwaiter().GetResult();
            var lights = JsonNode.Parse(json)?.AsObject();

            return new HueLightsResponse { Lights = lights };
        }
        catch (Exception ex)
        {
            return new HueLightsResponse { Error = ex.Message };
        }
    }

    // ── POST /OpenLightFX/Discover/SaveConfig ────────────────────────

    public object Post(SaveDiscoveredBulbConfigRequest request)
    {
        try
        {
            var entry = ServerEntryPoint.Instance;
            if (entry == null)
                return new SaveDiscoveredBulbConfigResponse { Success = false, Error = "Plugin not initialized" };

            if (request.Config == null)
                return new SaveDiscoveredBulbConfigResponse { Success = false, Error = "config is required" };

            var options = entry.GetOptions();
            var bulbs = entry.ConfigService.ParseBulbConfig(options.BulbConfigJson);

            // Replace existing bulb with same Id, or add new
            var existing = bulbs.FindIndex(b =>
                string.Equals(b.Id, request.Config.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                bulbs[existing] = request.Config;
            else
                bulbs.Add(request.Config);

            var updatedJson = JsonSerializer.Serialize(bulbs, JsonOptions);

            // Update plugin options
            var pluginOptions = Plugin.Instance?.GetPluginOptions();
            if (pluginOptions != null)
            {
                pluginOptions.BulbConfigJson = updatedJson;
                Plugin.Instance!.SavePluginOptions(pluginOptions);
            }

            return new SaveDiscoveredBulbConfigResponse
            {
                Success = true,
                ConfiguredBulbCount = bulbs.Count
            };
        }
        catch (Exception ex)
        {
            return new SaveDiscoveredBulbConfigResponse { Success = false, Error = ex.Message };
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static HuePairResponse TryHuePairing(string bridgeIp)
    {
        var url = $"http://{bridgeIp}/api";
        var body = new StringContent(
            "{\"devicetype\":\"openlightfx#emby\"}",
            Encoding.UTF8,
            "application/json");

        var response = SharedHttpClient.PostAsync(url, body).GetAwaiter().GetResult();
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        // Hue returns a JSON array: [{"success":{"username":"..."}}] or [{"error":{"type":101,...}}]
        var array = JsonNode.Parse(json)?.AsArray();
        if (array == null || array.Count == 0)
            return new HuePairResponse { Status = "failed", Message = "Unexpected response from bridge" };

        var first = array[0]?.AsObject();
        if (first == null)
            return new HuePairResponse { Status = "failed", Message = "Unexpected response from bridge" };

        var success = first["success"];
        if (success != null)
        {
            var username = success["username"]?.GetValue<string>();
            return new HuePairResponse { Status = "paired", Username = username };
        }

        var error = first["error"];
        if (error != null)
        {
            var errorType = error["type"]?.GetValue<int>() ?? 0;
            if (errorType == 101)
            {
                return new HuePairResponse
                {
                    Status = "waiting",
                    Message = "Press the link button on your Hue bridge"
                };
            }

            var description = error["description"]?.GetValue<string>() ?? "Unknown error";
            return new HuePairResponse { Status = "failed", Message = description };
        }

        return new HuePairResponse { Status = "failed", Message = "Unexpected response from bridge" };
    }
}
