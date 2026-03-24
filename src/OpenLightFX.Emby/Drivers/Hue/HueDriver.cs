namespace OpenLightFX.Emby.Drivers.Hue;

using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public class HueDriver : IBulbDriver
{
    private const uint HueBriMin = 1;
    private const uint HueBriMax = 254;
    private const uint HueColorTempMin = 2000;
    private const uint HueColorTempMax = 6535;
    private const uint MinTransitionMs = 100;
    private const uint MaxCommandsPerSecond = 10;
    private const int RateLimitIntervalMs = 1000 / (int)MaxCommandsPerSecond; // 100ms

    private readonly BulbConfig _config;
    private readonly HttpClient _httpClient;
    private readonly BulbCapabilityProfile _capabilities;
    private readonly string _baseUrl;
    private readonly string _stateUrl;
    private readonly string _lightUrl;

    private BulbState? _lastKnownState;
    private long _lastCommandTicks;
    private readonly object _rateLock = new();

    public string DriverName => "Philips Hue";

    public HueDriver(BulbConfig config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var bridgeIp = config.HueBridgeIp
            ?? throw new ArgumentException("HueBridgeIp is required for Hue driver");
        var apiKey = config.HueApiKey
            ?? throw new ArgumentException("HueApiKey is required for Hue driver");
        var lightId = config.HueLightId ?? config.IpAddress; // prefer HueLightId, fall back to IpAddress

        _baseUrl = $"http://{bridgeIp}/api/{apiKey}";
        _lightUrl = $"{_baseUrl}/lights/{lightId}";
        _stateUrl = $"{_lightUrl}/state";

        _capabilities = new BulbCapabilityProfile(
            SupportsRgb: true,
            SupportsColorTemp: true,
            ColorTempMin: HueColorTempMin,
            ColorTempMax: HueColorTempMax,
            MinTransitionMs: MinTransitionMs,
            MaxCommandsPerSecond: MaxCommandsPerSecond
        );
    }

    public BulbCapabilityProfile GetCapabilities() => _capabilities;

    public async Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
    {
        var (x, y, _) = ColorConverter.RgbToCieXy(r, g, b);
        var bri = MapBrightness(brightness);
        var tt = ToTransitionTime(transitionMs);

        var body = new JsonObject
        {
            ["on"] = true,
            ["xy"] = new JsonArray(x, y),
            ["bri"] = bri,
            ["transitiontime"] = tt
        };

        await SendStateCommand(body);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var (x, y, _) = ColorConverter.RgbToCieXy(r, g, b);
        var brightness = _lastKnownState?.Brightness ?? 100;
        var bri = MapBrightness(brightness);
        var tt = ToTransitionTime(transitionMs);

        var body = new JsonObject
        {
            ["on"] = true,
            ["xy"] = new JsonArray(x, y),
            ["bri"] = bri,
            ["transitiontime"] = tt
        };

        await SendStateCommand(body);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var bri = MapBrightness(brightness);
        var tt = ToTransitionTime(transitionMs);

        var body = new JsonObject
        {
            ["on"] = true,
            ["bri"] = bri,
            ["transitiontime"] = tt
        };

        await SendStateCommand(body);
        var state = _lastKnownState;
        _lastKnownState = new BulbState(
            state?.R ?? 255, state?.G ?? 255, state?.B ?? 255,
            brightness, state?.ColorTemperature, true);
    }

    public async Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0)
    {
        var clampedKelvin = Math.Clamp(kelvin, HueColorTempMin, HueColorTempMax);
        var mired = KelvinToMired(clampedKelvin);
        var bri = MapBrightness(brightness);
        var tt = ToTransitionTime(transitionMs);

        var body = new JsonObject
        {
            ["on"] = true,
            ["ct"] = mired,
            ["bri"] = bri,
            ["transitiontime"] = tt
        };

        await SendStateCommand(body);
        var state = _lastKnownState;
        _lastKnownState = new BulbState(
            state?.R ?? 0, state?.G ?? 0, state?.B ?? 0,
            brightness, clampedKelvin, true);
    }

    public async Task SetPower(bool on)
    {
        var body = new JsonObject { ["on"] = on };
        await SendStateCommand(body);
        _lastKnownState = _lastKnownState != null
            ? _lastKnownState with { IsOn = on }
            : new BulbState(0, 0, 0, 0, null, on);
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            var response = await _httpClient.GetAsync(_lightUrl);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);

            // Hue returns an array with error objects for invalid API keys
            if (node is JsonArray arr && arr.Count > 0
                && arr[0]?["error"] != null)
                return false;

            return node is JsonObject obj && obj["state"] != null;
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
            var response = await _httpClient.GetAsync(_lightUrl);
            if (!response.IsSuccessStatusCode) return _lastKnownState;

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return _lastKnownState;

            var state = obj["state"]?.AsObject();
            if (state == null) return _lastKnownState;

            var isOn = state["on"]?.GetValue<bool>() ?? false;
            var bri = (uint)(state["bri"]?.GetValue<int>() ?? 254);
            var brightness = UnmapBrightness(bri);

            byte r = 255, g = 255, b = 255;
            if (state["xy"] is JsonArray xyArr && xyArr.Count >= 2)
            {
                var x = xyArr[0]!.GetValue<double>();
                var y = xyArr[1]!.GetValue<double>();
                (r, g, b) = ColorConverter.CieXyToRgb(x, y, bri / (double)HueBriMax);
            }

            uint? colorTemp = null;
            if (state["ct"] != null)
            {
                var mired = (uint)state["ct"]!.GetValue<int>();
                colorTemp = MiredToKelvin(mired);
            }

            _lastKnownState = new BulbState(r, g, b, brightness, colorTemp, isOn);
            return _lastKnownState;
        }
        catch
        {
            return _lastKnownState;
        }
    }

    // Maps track brightness (0–100) → Hue brightness (1–254)
    private static uint MapBrightness(uint brightness)
    {
        if (brightness == 0) return HueBriMin;
        return (uint)Math.Clamp(
            HueBriMin + (brightness * (HueBriMax - HueBriMin) / 100.0),
            HueBriMin, HueBriMax);
    }

    // Maps Hue brightness (1–254) → track brightness (0–100)
    private static uint UnmapBrightness(uint bri)
    {
        if (bri <= HueBriMin) return 0;
        return (uint)Math.Clamp(
            (bri - HueBriMin) * 100.0 / (HueBriMax - HueBriMin),
            0, 100);
    }

    // Hue transitiontime is in deciseconds (100ms units)
    private static uint ToTransitionTime(uint transitionMs)
    {
        return transitionMs / 100;
    }

    private static uint KelvinToMired(uint kelvin)
    {
        return (uint)Math.Clamp(1_000_000.0 / kelvin, 153, 500);
    }

    private static uint MiredToKelvin(uint mired)
    {
        if (mired == 0) return HueColorTempMax;
        return (uint)Math.Clamp(1_000_000.0 / mired, HueColorTempMin, HueColorTempMax);
    }

    private async Task SendStateCommand(JsonObject body)
    {
        await EnforceRateLimit();

        var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        await _httpClient.PutAsync(_stateUrl, content);
    }

    private async Task EnforceRateLimit()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsedMs;

        lock (_rateLock)
        {
            elapsedMs = (now - _lastCommandTicks) * 1000 / Stopwatch.Frequency;
            _lastCommandTicks = now;
        }

        if (elapsedMs < RateLimitIntervalMs && elapsedMs >= 0)
        {
            await Task.Delay(RateLimitIntervalMs - (int)elapsedMs);
            lock (_rateLock)
            {
                _lastCommandTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await ValueTask.CompletedTask;
    }
}
