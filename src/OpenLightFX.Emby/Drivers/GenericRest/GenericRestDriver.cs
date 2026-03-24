namespace OpenLightFX.Emby.Drivers.GenericRest;

using OpenLightFX.Emby.Models;
using System.Net.Http;
using System.Text;

public class GenericRestDriver : IBulbDriver
{
    private readonly BulbConfig _config;
    private readonly HttpClient _httpClient;
    private readonly BulbCapabilityProfile _capabilities;
    private BulbState? _lastKnownState;

    public string DriverName => "Generic REST";

    public GenericRestDriver(BulbConfig config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        if (config.RestHeaders != null)
        {
            foreach (var header in config.RestHeaders)
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var overrides = config.CapabilityOverrides;
        _capabilities = new BulbCapabilityProfile(
            SupportsRgb: overrides?.SupportsRgb ?? true,
            SupportsColorTemp: overrides?.SupportsColorTemp ?? false,
            ColorTempMin: 2000,
            ColorTempMax: 6500,
            MinTransitionMs: overrides?.MinTransitionMs ?? 200,
            MaxCommandsPerSecond: overrides?.MaxCommandsPerSecond ?? 5
        );
    }

    public BulbCapabilityProfile GetCapabilities() => _capabilities;

    public async Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
    {
        await SendCommand(r, g, b, brightness, null, true, transitionMs);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var brightness = _lastKnownState?.Brightness ?? 100;
        await SendCommand(r, g, b, brightness, null, true, transitionMs);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var state = _lastKnownState;
        byte r = state?.R ?? 255, g = state?.G ?? 255, bVal = state?.B ?? 255;
        await SendCommand(r, g, bVal, brightness, null, true, transitionMs);
        _lastKnownState = new BulbState(r, g, bVal, brightness, null, true);
    }

    public async Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0)
    {
        var state = _lastKnownState;
        byte r = state?.R ?? 255, g = state?.G ?? 255, bVal = state?.B ?? 255;
        await SendCommand(r, g, bVal, brightness, kelvin, true, transitionMs);
        _lastKnownState = new BulbState(r, g, bVal, brightness, kelvin, true);
    }

    public async Task SetPower(bool on)
    {
        var state = _lastKnownState;
        await SendCommand(state?.R ?? 0, state?.G ?? 0, state?.B ?? 0,
            state?.Brightness ?? 0, null, on, 0);
        _lastKnownState = _lastKnownState != null
            ? _lastKnownState with { IsOn = on }
            : new BulbState(0, 0, 0, 0, null, on);
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            await SendCommand(255, 255, 255, 50, null, true, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<BulbState?> GetCurrentState() => Task.FromResult(_lastKnownState);

    private async Task SendCommand(byte r, byte g, byte b, uint brightness,
        uint? kelvin, bool power, uint transitionMs)
    {
        var url = ExpandTemplate(_config.RestUrlTemplate ?? "", r, g, b, brightness, kelvin, power, transitionMs);
        var method = (_config.RestHttpMethod?.ToUpperInvariant()) switch
        {
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => HttpMethod.Get
        };

        var request = new HttpRequestMessage(method, url);

        if (method != HttpMethod.Get && !string.IsNullOrEmpty(_config.RestBodyTemplate))
        {
            var body = ExpandTemplate(_config.RestBodyTemplate, r, g, b, brightness, kelvin, power, transitionMs);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        await _httpClient.SendAsync(request);
    }

    private static string ExpandTemplate(string template, byte r, byte g, byte b,
        uint brightness, uint? kelvin, bool power, uint transitionMs)
    {
        return template
            .Replace("{r}", r.ToString())
            .Replace("{g}", g.ToString())
            .Replace("{b}", b.ToString())
            .Replace("{brightness}", brightness.ToString())
            .Replace("{kelvin}", (kelvin ?? 4000).ToString())
            .Replace("{power}", power ? "true" : "false")
            .Replace("{power_int}", power ? "1" : "0")
            .Replace("{transition_ms}", transitionMs.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await ValueTask.CompletedTask;
    }
}
