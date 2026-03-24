namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

public class WizDiscovery : IDiscoveryModule
{
    private const int WizPort = 38899;
    public string Protocol => "Wiz";

    public async Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct)
    {
        var bulbs = new List<DiscoveredBulb>();
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        client.Client.ReceiveTimeout = timeoutMs;

        var broadcast = new IPEndPoint(IPAddress.Broadcast, WizPort);
        var payload = Encoding.UTF8.GetBytes("{\"method\":\"getSystemConfig\",\"params\":{}}");
        await client.SendAsync(payload, payload.Length, broadcast);

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
                var resultObj = obj?["result"]?.AsObject();
                if (resultObj == null) continue;

                var mac = resultObj["mac"]?.GetValue<string>();
                var moduleName = resultObj["moduleName"]?.GetValue<string>();

                bulbs.Add(new DiscoveredBulb
                {
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    Port = WizPort,
                    Protocol = "Wiz",
                    MacAddress = mac,
                    Model = moduleName,
                    Name = null
                });
            }
            catch (SocketException) { break; }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed responses */ }
        }

        return bulbs;
    }
}
