namespace OpenLightFX.Emby.Api;

using MediaBrowser.Model.Services;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// ─── Request DTOs ────────────────────────────────────────────────────

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

// ─── Response DTOs ───────────────────────────────────────────────────

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
