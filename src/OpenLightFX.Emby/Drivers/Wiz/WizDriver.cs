namespace OpenLightFX.Emby.Drivers.Wiz;

using OpenLightFX.Emby.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public class WizDriver : IBulbDriver
{
    private const int WizPort = 38899;
    private const int ResponseTimeoutMs = 1000;
    private const int CriticalRetryCount = 3;
    private const int CriticalRetryDelayMs = 100;
    private const uint WizDimmingMin = 10;
    private const uint WizDimmingMax = 100;
    private const uint WizColorTempMin = 2200;
    private const uint WizColorTempMax = 6500;

    private readonly BulbConfig _config;
    private readonly IPEndPoint _endpoint;
    private readonly UdpClient _udpClient;
    private readonly BulbCapabilityProfile _capabilities;
    private BulbState? _lastKnownState;

    public string DriverName => "Wiz";

    public WizDriver(BulbConfig config)
    {
        _config = config;

        var port = config.Port > 0 ? config.Port : WizPort;
        _endpoint = new IPEndPoint(IPAddress.Parse(config.IpAddress), port);

        _udpClient = new UdpClient();
        _udpClient.Client.ReceiveTimeout = ResponseTimeoutMs;

        _capabilities = new BulbCapabilityProfile(
            SupportsRgb: true,
            SupportsColorTemp: true,
            ColorTempMin: WizColorTempMin,
            ColorTempMax: WizColorTempMax,
            MinTransitionMs: 100,
            MaxCommandsPerSecond: 5
        );
    }

    public BulbCapabilityProfile GetCapabilities() => _capabilities;

    public async Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
    {
        var dimming = MapBrightness(brightness);
        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = r,
                ["g"] = g,
                ["b"] = b,
                ["dimming"] = dimming,
                ["state"] = true
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var brightness = _lastKnownState?.Brightness ?? 100;
        var dimming = MapBrightness(brightness);
        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = r,
                ["g"] = g,
                ["b"] = b,
                ["dimming"] = dimming,
                ["state"] = true
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var dimming = MapBrightness(brightness);
        var state = _lastKnownState;
        byte r = state?.R ?? 255, g = state?.G ?? 255, bVal = state?.B ?? 255;

        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = r,
                ["g"] = g,
                ["b"] = bVal,
                ["dimming"] = dimming,
                ["state"] = true
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, bVal, brightness, null, true);
    }

    public async Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0)
    {
        var dimming = MapBrightness(brightness);
        var clampedKelvin = Math.Clamp(kelvin, WizColorTempMin, WizColorTempMax);

        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["temp"] = clampedKelvin,
                ["dimming"] = dimming,
                ["state"] = true
            }
        };

        await SendFireAndForget(payload);
        var state = _lastKnownState;
        _lastKnownState = new BulbState(
            state?.R ?? 0, state?.G ?? 0, state?.B ?? 0,
            brightness, clampedKelvin, true);
    }

    public async Task SetPower(bool on)
    {
        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["state"] = on
            }
        };

        // Power changes are critical — use retry logic
        await SendWithRetry(payload);
        _lastKnownState = _lastKnownState != null
            ? _lastKnownState with { IsOn = on }
            : new BulbState(0, 0, 0, 0, null, on);
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            var response = await SendAndReceive(new JsonObject
            {
                ["method"] = "getPilot",
                ["params"] = new JsonObject()
            });
            return response != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<BulbState?> GetCurrentState()
    {
        try
        {
            var response = await SendAndReceive(new JsonObject
            {
                ["method"] = "getPilot",
                ["params"] = new JsonObject()
            });

            if (response == null) return _lastKnownState;

            var result = response["result"]?.AsObject();
            if (result == null) return _lastKnownState;

            var r = (byte)(result["r"]?.GetValue<int>() ?? 0);
            var g = (byte)(result["g"]?.GetValue<int>() ?? 0);
            var b = (byte)(result["b"]?.GetValue<int>() ?? 0);
            var dimming = (uint)(result["dimming"]?.GetValue<int>() ?? 100);
            var isOn = result["state"]?.GetValue<bool>() ?? false;

            uint? temp = result["temp"] != null
                ? (uint)result["temp"].GetValue<int>()
                : null;

            var brightness = UnmapBrightness(dimming);
            _lastKnownState = new BulbState(r, g, b, brightness, temp, isOn);
            return _lastKnownState;
        }
        catch
        {
            return _lastKnownState;
        }
    }

    /// <summary>
    /// Sends a registration message for persistent association with the bulb.
    /// </summary>
    public async Task<bool> Register(string homeId, string? roomId = null)
    {
        var regParams = new JsonObject
        {
            ["phoneMac"] = "AAAAAAAAAAAA",
            ["register"] = false,
            ["homeId"] = homeId,
            ["id"] = _config.Id
        };
        if (roomId != null)
            regParams["roomId"] = roomId;

        var payload = new JsonObject
        {
            ["method"] = "registration",
            ["params"] = regParams
        };

        var response = await SendAndReceive(payload);
        return response?["result"]?["success"]?.GetValue<bool>() == true;
    }

    /// <summary>
    /// Broadcasts getSystemConfig for discovery. Returns the raw JSON response or null.
    /// </summary>
    public static async Task<JsonObject?> BroadcastDiscovery(int timeoutMs = ResponseTimeoutMs)
    {
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        client.Client.ReceiveTimeout = timeoutMs;

        var broadcast = new IPEndPoint(IPAddress.Broadcast, WizPort);
        var payload = new JsonObject
        {
            ["method"] = "getSystemConfig",
            ["params"] = new JsonObject()
        };

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await client.SendAsync(bytes, bytes.Length, broadcast);

        try
        {
            var result = await client.ReceiveAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var json = Encoding.UTF8.GetString(result.Buffer);
            return JsonNode.Parse(json)?.AsObject();
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // Maps track brightness (0–100) → Wiz dimming (10–100)
    private static uint MapBrightness(uint brightness)
    {
        if (brightness == 0) return WizDimmingMin;
        return (uint)Math.Clamp(
            WizDimmingMin + (brightness * (WizDimmingMax - WizDimmingMin) / 100.0),
            WizDimmingMin, WizDimmingMax);
    }

    // Maps Wiz dimming (10–100) → track brightness (0–100)
    private static uint UnmapBrightness(uint dimming)
    {
        if (dimming <= WizDimmingMin) return 0;
        return (uint)Math.Clamp(
            (dimming - WizDimmingMin) * 100.0 / (WizDimmingMax - WizDimmingMin),
            0, 100);
    }

    private async Task SendFireAndForget(JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _udpClient.SendAsync(bytes, bytes.Length, _endpoint);
    }

    private async Task<JsonObject?> SendAndReceive(JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _udpClient.SendAsync(bytes, bytes.Length, _endpoint);

        try
        {
            var result = await _udpClient.ReceiveAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(ResponseTimeoutMs));
            var json = Encoding.UTF8.GetString(result.Buffer);
            return JsonNode.Parse(json)?.AsObject();
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task SendWithRetry(JsonObject payload)
    {
        for (int attempt = 0; attempt < CriticalRetryCount; attempt++)
        {
            try
            {
                var response = await SendAndReceive(payload);
                if (response != null) return;
            }
            catch
            {
                // Fall through to retry
            }

            if (attempt < CriticalRetryCount - 1)
                await Task.Delay(CriticalRetryDelayMs);
        }

        // Final fire-and-forget attempt so we don't throw during playback
        await SendFireAndForget(payload);
    }

    public async ValueTask DisposeAsync()
    {
        _udpClient.Dispose();
        await ValueTask.CompletedTask;
    }
}
