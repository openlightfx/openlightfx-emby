namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenLightFX.Emby.Models;

/// <summary>
/// Identifies discovered bulbs by turning them off and on twice.
/// </summary>
public class IdentifyService
{
    private const int PowerPhaseMs = 500;
    private const int IdentifyDurationMs = PowerPhaseMs * 4;
    private static readonly bool[] IdentifyPowerStates = [false, true, false, true];

    /// <summary>
    /// Run the identification sequence. Returns its estimated duration in ms.
    /// </summary>
    public async Task<int> IdentifyAsync(DiscoveredBulb bulb)
    {
        return bulb.Protocol switch
        {
            BulbProtocol.Wiz   => await IdentifyWiz(bulb),
            BulbProtocol.Hue   => await IdentifyHue(bulb),
            BulbProtocol.Lifx  => await IdentifyLifx(bulb),
            BulbProtocol.Govee => await IdentifyGovee(bulb),
            _ => 0
        };
    }

    private async Task<int> IdentifyWiz(DiscoveredBulb bulb)
    {
        var endpoint = new IPEndPoint(bulb.IpAddress, bulb.Port);
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

        // Turn off and on twice without changing the saved color or brightness.
        foreach (var isOn in IdentifyPowerStates)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"method\":\"setPilot\",\"params\":{{\"state\":{isOn.ToString().ToLowerInvariant()}}}}}");
            await client.SendAsync(payload, payload.Length, endpoint);
            await Task.Delay(PowerPhaseMs);
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

        return IdentifyDurationMs;
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
        var endpoint = new IPEndPoint(bulb.IpAddress, bulb.Port);
        using var client = new UdpClient();

        // Turn off and on twice using LIFX SetPower (type 117).
        foreach (var isOn in IdentifyPowerStates)
        {
            var packet = BuildLifxSetPower(isOn);
            await client.SendAsync(packet, packet.Length, endpoint);
            await Task.Delay(PowerPhaseMs);
        }

        return IdentifyDurationMs;
    }

    private async Task<int> IdentifyGovee(DiscoveredBulb bulb)
    {
        var endpoint = new IPEndPoint(bulb.IpAddress, 4003);
        using var client = new UdpClient();

        // Turn off and on twice without changing the saved color or brightness.
        foreach (var isOn in IdentifyPowerStates)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"msg\":{{\"cmd\":\"turn\",\"data\":{{\"value\":{(isOn ? 1 : 0)}}}}}}}");
            await client.SendAsync(payload, payload.Length, endpoint);
            await Task.Delay(PowerPhaseMs);
        }

        return IdentifyDurationMs;
    }

    private static byte[] BuildLifxSetPower(bool on)
    {
        // LIFX SetPower: type 117, 42 bytes total (36-byte header + 6-byte payload).
        var packet = new byte[42];

        // Frame header
        BitConverter.GetBytes((ushort)42).CopyTo(packet, 0);
        BitConverter.GetBytes((ushort)(1024 | (1 << 12))).CopyTo(packet, 2);
        BitConverter.GetBytes((uint)2).CopyTo(packet, 4);

        // Protocol header
        BitConverter.GetBytes((ushort)117).CopyTo(packet, 32);

        // Payload: power level (0 or 65535) + duration (0 ms).
        BitConverter.GetBytes(on ? ushort.MaxValue : (ushort)0).CopyTo(packet, 36);
        BitConverter.GetBytes(0u).CopyTo(packet, 38);

        return packet;
    }
}
