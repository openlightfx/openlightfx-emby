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

    private readonly WizBulbConfig _config;
    private readonly IPEndPoint _endpoint;
    private readonly UdpClient _udpClient;
    private readonly BulbCapabilityProfile _capabilities;
    private BulbState? _lastKnownState;

    public string DriverName => "Wiz";

    public WizDriver(WizBulbConfig config)
    {
        _config = config;

        var port = config.Port > 0 ? config.Port : WizPort;
        _endpoint = new IPEndPoint(config.IpAddress, port);

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
        var (outR, outG, outB, coldWhite, warmWhite) = RgbToRgbColdWarmWhite(r, g, b);
        // WiZ's minimum dimming is nonzero, and all-zero output can fall back to white.
        var powered = brightness > 0 &&
            (outR != 0 || outG != 0 || outB != 0 || coldWhite != 0 || warmWhite != 0);
        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = outR,
                ["g"] = outG,
                ["b"] = outB,
                ["c"] = coldWhite,
                ["w"] = warmWhite,
                ["dimming"] = dimming,
                ["state"] = powered
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, b, brightness, null, powered);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var brightness = _lastKnownState?.Brightness ?? 100;
        var dimming = MapBrightness(brightness);
        var (outR, outG, outB, coldWhite, warmWhite) = RgbToRgbColdWarmWhite(r, g, b);
        var powered = brightness > 0 &&
            (outR != 0 || outG != 0 || outB != 0 || coldWhite != 0 || warmWhite != 0);
        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = outR,
                ["g"] = outG,
                ["b"] = outB,
                ["c"] = coldWhite,
                ["w"] = warmWhite,
                ["dimming"] = dimming,
                ["state"] = powered
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, b, brightness, null, powered);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var dimming = MapBrightness(brightness);
        var state = _lastKnownState;
        byte r = state?.R ?? 255, g = state?.G ?? 255, bVal = state?.B ?? 255;
        var (outR, outG, outB, coldWhite, warmWhite) = RgbToRgbColdWarmWhite(r, g, bVal);
        var powered = brightness > 0 &&
            (outR != 0 || outG != 0 || outB != 0 || coldWhite != 0 || warmWhite != 0);

        var payload = new JsonObject
        {
            ["method"] = "setPilot",
            ["params"] = new JsonObject
            {
                ["r"] = outR,
                ["g"] = outG,
                ["b"] = outB,
                ["c"] = coldWhite,
                ["w"] = warmWhite,
                ["dimming"] = dimming,
                ["state"] = powered
            }
        };

        await SendFireAndForget(payload);
        _lastKnownState = new BulbState(r, g, bVal, brightness, null, powered);
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
        // A state-only ON command revives the bulb's previously stored color.
        // Reapply our latest known mode and color so C/W cannot remain stale.
        if (on && _lastKnownState is { } knownState)
        {
            if (knownState.Brightness == 0)
                on = false;
            else if (knownState.ColorTemperature is uint kelvin && kelvin > 0)
            {
                await SetColorTemperature(kelvin, knownState.Brightness).ConfigureAwait(false);
                return;
            }
            else
            {
                await SetState(knownState.R, knownState.G, knownState.B, knownState.Brightness, 0)
                    .ConfigureAwait(false);
                return;
            }
        }

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

    // Extract the shared white contribution with pywizlight's RGB→RGBCW mapping,
    // then split it between WiZ's cold (c) and warm (w) white LEDs. The blue/red
    // balance is a tint heuristic; neutral inputs divide the white output evenly.
    // Reference: https://github.com/sbidy/pywizlight/blob/master/pywizlight/rgbcw.py
    private static (byte R, byte G, byte B, byte ColdWhite, byte WarmWhite) RgbToRgbColdWarmWhite(
        byte red, byte green, byte blue)
    {
        const double epsilon = 1e-12;
        const int cwMax = 128;
        var maxChannel = Math.Max(red, Math.Max(green, blue));
        if (maxChannel == 0)
            return (0, 0, 0, 0, 0);

        // Separate color intensity from chromaticity. The previous mapping treated
        // every low-level RGB value as unsaturated white at full output.
        var intensity = maxChannel / 255.0;
        var r = red / (double)maxChannel;
        var g = green / (double)maxChannel;
        var b = blue / (double)maxChannel;
        var x = r - 0.5 * g - 0.5 * b;
        var y = Math.Sqrt(3) * 0.5 * (g - b);
        var saturation = Math.Sqrt(x * x + y * y);
        var rgb = new double[3];
        if (saturation > epsilon)
        {
            x /= saturation;
            y /= saturation;
            var basis = new (double X, double Y)[]
            {
                (1, 0), (-0.5, Math.Sqrt(3) * 0.5), (-0.5, -Math.Sqrt(3) * 0.5)
            };
            var active = Enumerable.Range(0, basis.Length)
                .Where(i => x * basis[i].X + y * basis[i].Y > Math.Cos(2 * Math.PI / 3 - epsilon))
                .ToArray();
            if (active.Length == 1)
            {
                rgb[active[0]] = 1;
            }
            else if (active.Length == 2)
            {
                var first = basis[active[0]];
                var second = basis[active[1]];
                var perpendicular = (X: second.Y, Y: -second.X);
                var coeff0 = (x * perpendicular.X + y * perpendicular.Y) /
                    (first.X * perpendicular.X + first.Y * perpendicular.Y);
                var ix = x - first.X * coeff0;
                var iy = y - first.Y * coeff0;
                var coeff1 = ix * second.X + iy * second.Y;
                var maxCoeff = Math.Max(coeff0, coeff1);
                if (maxCoeff > epsilon)
                {
                    rgb[active[0]] = Math.Clamp(coeff0 / maxCoeff, 0, 1);
                    rgb[active[1]] = Math.Clamp(coeff1 / maxCoeff, 0, 1);
                }
            }
        }

        var cw = saturation >= 0.5 ? 1 - ((saturation - 0.5) * 2) : 1;
        if (saturation < 0.5)
            for (var i = 0; i < rgb.Length; i++) rgb[i] *= saturation * 2;

        var totalWhite = (byte)Math.Round(
            Math.Clamp(cw, 0, 1) * cwMax * intensity, MidpointRounding.AwayFromZero);
        var redBlueSum = (int)red + blue;
        var coldShare = redBlueSum == 0 ? 0.5 : blue / (double)redBlueSum;
        var coldWhite = (byte)Math.Round(
            totalWhite * coldShare, MidpointRounding.AwayFromZero);
        var warmWhite = (byte)(totalWhite - coldWhite);

        return (
            (byte)(Math.Clamp(rgb[0], 0, 1) * 255 * intensity),
            (byte)(Math.Clamp(rgb[1], 0, 1) * 255 * intensity),
            (byte)(Math.Clamp(rgb[2], 0, 1) * 255 * intensity),
            coldWhite,
            warmWhite);
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
