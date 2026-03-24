namespace OpenLightFX.Emby.Drivers.Govee;

using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public class GoveeDriver : IBulbDriver
{
    private const int GoveeControlPort = 4003;
    private const int ResponseTimeoutMs = 1000;
    private const int CriticalRetryCount = 3;
    private const int CriticalRetryDelayMs = 100;
    private const uint GoveeColorTempMin = 2000;
    private const uint GoveeColorTempMax = 9000;

    private readonly BulbConfig _config;
    private readonly IPEndPoint _endpoint;
    private readonly UdpClient _udpClient;
    private readonly BulbCapabilityProfile _capabilities;
    private BulbState? _lastKnownState;

    public string DriverName => "Govee";

    public GoveeDriver(BulbConfig config)
    {
        _config = config;

        _endpoint = new IPEndPoint(IPAddress.Parse(config.IpAddress), GoveeControlPort);

        _udpClient = new UdpClient();
        _udpClient.Client.ReceiveTimeout = ResponseTimeoutMs;

        _capabilities = new BulbCapabilityProfile(
            SupportsRgb: true,
            SupportsColorTemp: true,
            ColorTempMin: GoveeColorTempMin,
            ColorTempMax: GoveeColorTempMax,
            MinTransitionMs: 100,
            MaxCommandsPerSecond: 5
        );
    }

    public BulbCapabilityProfile GetCapabilities() => _capabilities;

    public async Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
    {
        var colorPayload = BuildCommand("colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b },
            ["colorTemInKelvin"] = 0
        });
        await SendFireAndForget(colorPayload);

        if (brightness != (_lastKnownState?.Brightness ?? 100))
        {
            var brightnessPayload = BuildCommand("brightness", new JsonObject
            {
                ["value"] = (int)Math.Clamp(brightness, 0, 100)
            });
            await SendFireAndForget(brightnessPayload);
        }

        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var payload = BuildCommand("colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b },
            ["colorTemInKelvin"] = 0
        });

        await SendFireAndForget(payload);
        var brightness = _lastKnownState?.Brightness ?? 100;
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var payload = BuildCommand("brightness", new JsonObject
        {
            ["value"] = (int)Math.Clamp(brightness, 0, 100)
        });

        await SendFireAndForget(payload);
        var state = _lastKnownState;
        _lastKnownState = new BulbState(
            state?.R ?? 255, state?.G ?? 255, state?.B ?? 255,
            brightness, state?.ColorTemperature, state?.IsOn ?? true);
    }

    public async Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0)
    {
        var clampedKelvin = Math.Clamp(kelvin, GoveeColorTempMin, GoveeColorTempMax);
        var (r, g, b) = ColorConverter.KelvinToRgb(clampedKelvin);

        var colorPayload = BuildCommand("colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b },
            ["colorTemInKelvin"] = clampedKelvin
        });
        await SendFireAndForget(colorPayload);

        var brightnessPayload = BuildCommand("brightness", new JsonObject
        {
            ["value"] = (int)Math.Clamp(brightness, 0, 100)
        });
        await SendFireAndForget(brightnessPayload);

        _lastKnownState = new BulbState(r, g, b, brightness, clampedKelvin, true);
    }

    public async Task SetPower(bool on)
    {
        var payload = BuildCommand("turn", new JsonObject
        {
            ["value"] = on ? 1 : 0
        });

        await SendWithRetry(payload);
        _lastKnownState = _lastKnownState != null
            ? _lastKnownState with { IsOn = on }
            : new BulbState(0, 0, 0, 0, null, on);
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            var payload = BuildCommand("devStatus", new JsonObject());
            var response = await SendAndReceive(payload);
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
            var payload = BuildCommand("devStatus", new JsonObject());
            var response = await SendAndReceive(payload);

            if (response == null) return _lastKnownState;

            var msg = response["msg"]?.AsObject();
            var data = msg?["data"]?.AsObject();
            if (data == null) return _lastKnownState;

            var isOn = data["onOff"]?.GetValue<int>() == 1;
            var brightness = (uint)(data["brightness"]?.GetValue<int>() ?? 100);
            var colorObj = data["color"]?.AsObject();
            var r = (byte)(colorObj?["r"]?.GetValue<int>() ?? 0);
            var g = (byte)(colorObj?["g"]?.GetValue<int>() ?? 0);
            var b = (byte)(colorObj?["b"]?.GetValue<int>() ?? 0);
            uint? colorTemp = data["colorTemInKelvin"]?.GetValue<int>() is > 0 and int k
                ? (uint)k
                : null;

            _lastKnownState = new BulbState(r, g, b, brightness, colorTemp, isOn);
            return _lastKnownState;
        }
        catch
        {
            return _lastKnownState;
        }
    }

    private static JsonObject BuildCommand(string cmd, JsonObject data)
    {
        return new JsonObject
        {
            ["msg"] = new JsonObject
            {
                ["cmd"] = cmd,
                ["data"] = data
            }
        };
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
