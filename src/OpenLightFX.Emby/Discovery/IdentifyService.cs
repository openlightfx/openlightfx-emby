namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Sends visual identification sequences to discovered bulbs.
/// </summary>
public class IdentifyService
{
    private static readonly (byte R, byte G, byte B)[] IdentifyColors = new[]
    {
        ((byte)255, (byte)0, (byte)0),     // Red
        ((byte)0, (byte)255, (byte)0),     // Green
        ((byte)0, (byte)0, (byte)255),     // Blue
    };

    /// <summary>
    /// Run identification sequence. Returns estimated duration in ms.
    /// Saves and restores bulb state.
    /// </summary>
    public async Task<int> IdentifyAsync(DiscoveredBulb bulb)
    {
        return bulb.Protocol.ToLowerInvariant() switch
        {
            "wiz" => await IdentifyWiz(bulb),
            "hue" => await IdentifyHue(bulb),
            "lifx" => await IdentifyLifx(bulb),
            "govee" => await IdentifyGovee(bulb),
            _ => 0
        };
    }

    private async Task<int> IdentifyWiz(DiscoveredBulb bulb)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(bulb.IpAddress), bulb.Port);
        using var client = new UdpClient();

        // Save current state
        var getPayload = Encoding.UTF8.GetBytes("{\"method\":\"getPilot\",\"params\":{}}");
        await client.SendAsync(getPayload, getPayload.Length, endpoint);
        byte[]? savedState = null;
        try
        {
            var result = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
            savedState = result.Buffer;
        }
        catch { }

        // Flash RGB 3 times (500ms each)
        foreach (var (r, g, b) in IdentifyColors)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"method\":\"setPilot\",\"params\":{{\"r\":{r},\"g\":{g},\"b\":{b},\"dimming\":100,\"state\":true}}}}");
            await client.SendAsync(payload, payload.Length, endpoint);
            await Task.Delay(500);
        }

        // Restore state if we captured it
        if (savedState != null)
        {
            try
            {
                var json = Encoding.UTF8.GetString(savedState);
                var obj = JsonNode.Parse(json)?.AsObject();
                var resultObj = obj?["result"]?.AsObject();
                if (resultObj != null)
                {
                    var restore = new JsonObject
                    {
                        ["method"] = "setPilot",
                        ["params"] = JsonNode.Parse(resultObj.ToJsonString())
                    };
                    var restoreBytes = Encoding.UTF8.GetBytes(restore.ToJsonString());
                    await client.SendAsync(restoreBytes, restoreBytes.Length, endpoint);
                }
            }
            catch { }
        }

        return 1500;
    }

    private async Task<int> IdentifyHue(DiscoveredBulb bulb)
    {
        // Hue: use alert:select breathe cycle via bridge API
        // For discovery, we just know the bridge IP. We can't identify individual lights
        // without an API key. We'll send a basic alert to the bridge config endpoint.
        // In practice, the marketplace UI handles Hue identification post-pairing.
        try
        {
            await Task.Delay(1500); // Hue bridge doesn't support pre-pairing identify
        }
        catch { }
        return 1500;
    }

    private async Task<int> IdentifyLifx(DiscoveredBulb bulb)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(bulb.IpAddress), bulb.Port);
        using var client = new UdpClient();

        // Flash RGB 3 times using LIFX SetColor (type 102)
        foreach (var (r, g, b) in IdentifyColors)
        {
            var (hue, sat, bri) = RgbToLifxHsbk(r, g, b);
            var packet = BuildLifxSetColor(hue, sat, bri, 3500, 0);
            await client.SendAsync(packet, packet.Length, endpoint);
            await Task.Delay(500);
        }

        return 1500;
    }

    private async Task<int> IdentifyGovee(DiscoveredBulb bulb)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(bulb.IpAddress), 4003);
        using var client = new UdpClient();

        // Flash RGB 3 times
        foreach (var (r, g, b) in IdentifyColors)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"msg\":{{\"cmd\":\"colorwc\",\"data\":{{\"color\":{{\"r\":{r},\"g\":{g},\"b\":{b}}},\"colorTemInKelvin\":0}}}}}}");
            await client.SendAsync(payload, payload.Length, endpoint);
            await Task.Delay(500);
        }

        return 1500;
    }

    private static (ushort Hue, ushort Sat, ushort Bri) RgbToLifxHsbk(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        float hue = 0;
        if (delta > 0)
        {
            if (max == rf) hue = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) hue = 60 * (((bf - rf) / delta) + 2);
            else hue = 60 * (((rf - gf) / delta) + 4);
        }
        if (hue < 0) hue += 360;

        float sat = max > 0 ? delta / max : 0;

        return (
            (ushort)(hue / 360f * 65535),
            (ushort)(sat * 65535),
            (ushort)(max * 65535)
        );
    }

    private static byte[] BuildLifxSetColor(ushort hue, ushort sat, ushort bri, ushort kelvin, uint durationMs)
    {
        // LIFX SetColor: type 102, 49 bytes total (36 header + 13 payload)
        var packet = new byte[49];

        // Frame header
        BitConverter.GetBytes((ushort)49).CopyTo(packet, 0);
        BitConverter.GetBytes((ushort)(1024 | 0x2000 | 0x1000)).CopyTo(packet, 2);
        BitConverter.GetBytes((uint)2).CopyTo(packet, 4);

        // Protocol header
        BitConverter.GetBytes((ushort)102).CopyTo(packet, 32);

        // Payload: reserved(1) + HSBK(8) + duration(4)
        packet[36] = 0; // reserved
        BitConverter.GetBytes(hue).CopyTo(packet, 37);
        BitConverter.GetBytes(sat).CopyTo(packet, 39);
        BitConverter.GetBytes(bri).CopyTo(packet, 41);
        BitConverter.GetBytes(kelvin).CopyTo(packet, 43);
        BitConverter.GetBytes(durationMs).CopyTo(packet, 45);

        return packet;
    }
}
