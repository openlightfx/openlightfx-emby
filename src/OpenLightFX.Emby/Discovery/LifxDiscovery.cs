namespace OpenLightFX.Emby.Discovery;

using System.Net;
using System.Net.Sockets;

public class LifxDiscovery : IDiscoveryModule
{
    private const int LifxPort = 56700;
    private const ushort GetServiceType = 2;
    private const ushort StateServiceType = 3;
    public string Protocol => "Lifx";

    public async Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct)
    {
        var bulbs = new List<DiscoveredBulb>();
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        client.Client.ReceiveTimeout = timeoutMs;

        var broadcast = new IPEndPoint(IPAddress.Broadcast, LifxPort);

        // Build LIFX GetService packet (type 2)
        var packet = BuildGetServicePacket();
        await client.SendAsync(packet, packet.Length, broadcast);

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
                if (result.Buffer.Length < 36) continue;

                // Parse header to get message type
                var msgType = BitConverter.ToUInt16(result.Buffer, 32);
                if (msgType != StateServiceType) continue;

                // Extract MAC from frame address (bytes 8-13)
                var macBytes = new byte[6];
                Array.Copy(result.Buffer, 8, macBytes, 0, 6);
                var mac = BitConverter.ToString(macBytes).Replace("-", ":");

                bulbs.Add(new DiscoveredBulb
                {
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    Port = LifxPort,
                    Protocol = "Lifx",
                    MacAddress = mac,
                    Model = null,
                    Name = null
                });
            }
            catch (SocketException) { break; }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed responses */ }
        }

        return bulbs;
    }

    private static byte[] BuildGetServicePacket()
    {
        // LIFX LAN protocol header: 36 bytes for GetService
        var packet = new byte[36];

        // Frame (8 bytes)
        // Size (2 bytes) - total packet size
        BitConverter.GetBytes((ushort)36).CopyTo(packet, 0);
        // Protocol (2 bytes) - 1024 | tagged(1) | addressable(1)
        BitConverter.GetBytes((ushort)(1024 | 0x2000 | 0x1000)).CopyTo(packet, 2);
        // Source (4 bytes)
        BitConverter.GetBytes((uint)2).CopyTo(packet, 4);

        // Frame address (16 bytes) - all zeros for broadcast (target=all)
        // Already zero-initialized

        // Protocol header (4 bytes at offset 32)
        // Type (2 bytes)
        BitConverter.GetBytes(GetServiceType).CopyTo(packet, 32);

        return packet;
    }
}
