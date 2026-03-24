namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

public class GoveeDiscovery : IDiscoveryModule
{
    private const string MulticastAddress = "239.255.255.250";
    private const int SendPort = 4001;
    private const int ListenPort = 4002;
    private const int ControlPort = 4003;
    public string Protocol => "Govee";

    public async Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct)
    {
        var bulbs = new List<DiscoveredBulb>();

        using var client = new UdpClient(ListenPort);
        client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
        client.Client.ReceiveTimeout = timeoutMs;

        var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), SendPort);
        var payload = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{}}}");
        await client.SendAsync(payload, payload.Length, target);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var receiveTask = client.ReceiveAsync();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var completed = await Task.WhenAny(receiveTask, Task.Delay(remaining, ct));
                if (completed != receiveTask) break;

                var result = await receiveTask;
                var json = Encoding.UTF8.GetString(result.Buffer);
                var obj = JsonNode.Parse(json)?.AsObject();
                var msg = obj?["msg"]?.AsObject();
                if (msg == null) continue;

                var cmd = msg["cmd"]?.GetValue<string>();
                if (cmd != "scan") continue;

                var data = msg["data"]?.AsObject();
                if (data == null) continue;

                var ip = data["ip"]?.GetValue<string>() ?? result.RemoteEndPoint.Address.ToString();
                var device = data["device"]?.GetValue<string>();
                var sku = data["sku"]?.GetValue<string>();

                bulbs.Add(new DiscoveredBulb
                {
                    IpAddress = ip,
                    Port = ControlPort,
                    Protocol = "Govee",
                    MacAddress = null,
                    Model = sku,
                    Name = device
                });
            }
            catch (SocketException) { break; }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed responses */ }
        }

        return bulbs;
    }
}
