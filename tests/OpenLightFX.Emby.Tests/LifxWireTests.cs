using System.Net;
using System.Net.Sockets;
using OpenLightFX.Emby.Drivers.Lifx;
using OpenLightFX.Emby.Models;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class LifxWireTests
{
    [Fact]
    public async Task SendsStandard36ByteHeadersAndPayloads()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new LifxDriver(new LifxBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.SetState(255, 0, 0, 80, 500);
        var startupPower = (await socket.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(42, startupPower.Length);
        Assert.Equal(117, BitConverter.ToUInt16(startupPower, 32));
        Assert.Equal(65535, BitConverter.ToUInt16(startupPower, 36));
        var color = (await socket.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(49, color.Length);
        Assert.Equal(49, BitConverter.ToUInt16(color, 0));
        Assert.Equal(0x1400, BitConverter.ToUInt16(color, 2));
        Assert.Equal(102, BitConverter.ToUInt16(color, 32));
        Assert.Equal(1, color[23]);
        Assert.All(color[24..32], value => Assert.Equal(0, value));
        Assert.Equal(0, color[36]);
        Assert.Equal(0, BitConverter.ToUInt16(color, 37));
        Assert.Equal(65535, BitConverter.ToUInt16(color, 39));
        Assert.Equal(52428, BitConverter.ToUInt16(color, 41));
        Assert.Equal(500u, BitConverter.ToUInt32(color, 45));
        await driver.SetPower(true);
        var power = (await socket.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(42, power.Length);
        Assert.Equal(117, BitConverter.ToUInt16(power, 32));
        Assert.Equal(2, power[23]);
        Assert.Equal(2, power[22]);
        Assert.Equal(65535, BitConverter.ToUInt16(power, 36));
    }

    [Fact]
    public async Task ReadsStandardStateWithoutDoubleDimmingOrLosingPower()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new LifxDriver(new LifxBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stateTask = driver.GetCurrentState();
        var request = await socket.ReceiveAsync(timeout.Token);
        Assert.Equal(36, request.Buffer.Length);
        Assert.Equal(101, BitConverter.ToUInt16(request.Buffer, 32));
        var response = new byte[88]; // LAN header (36) + LightState (52)
        BitConverter.TryWriteBytes(response.AsSpan(0), (ushort)88);
        BitConverter.TryWriteBytes(response.AsSpan(32), (ushort)107);
        BitConverter.TryWriteBytes(response.AsSpan(38), ushort.MaxValue); // saturation
        BitConverter.TryWriteBytes(response.AsSpan(40), (ushort)16384); // quarter intensity
        BitConverter.TryWriteBytes(response.AsSpan(42), (ushort)3500);
        // power at payload + 10 remains zero
        await socket.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        var state = await stateTask;
        Assert.NotNull(state);
        Assert.Equal(255, state.R);
        Assert.Equal(0, state.G);
        Assert.Equal(0, state.B);
        Assert.Equal(25u, state.Brightness);
        Assert.False(state.IsOn);
    }
}
