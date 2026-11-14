using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenLightFX.Emby.Drivers.Govee;
using OpenLightFX.Emby.Drivers.Wiz;
using OpenLightFX.Emby.Models;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class UdpPowerTests
{
    [Fact]
    public async Task GoveeSetStateTurnsOnAndSendsAllFieldsWithoutWaitingForAck()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new GoveeDriver(new GoveeBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await driver.SetState(0, 255, 255, 100, 100).WaitAsync(timeout.Token);
        using var turn = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        using var color = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        using var brightness = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        Assert.Equal("turn", turn.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
        Assert.Equal(1, turn.RootElement.GetProperty("msg").GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal("colorwc", color.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
        Assert.Equal(255, color.RootElement.GetProperty("msg").GetProperty("data").GetProperty("color").GetProperty("g").GetInt32());
        Assert.Equal("brightness", brightness.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
        Assert.Equal(100, brightness.RootElement.GetProperty("msg").GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task WizEmptyChannelDoesNotTurnOnAtMinimumDimming()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new WizDriver(new WizBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await driver.SetState(0, 0, 0, 0, 100);
        using var off = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        Assert.False(off.RootElement.GetProperty("params").GetProperty("state").GetBoolean());
        await driver.SetState(255, 0, 0, 60, 100);
        using var on = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        Assert.True(on.RootElement.GetProperty("params").GetProperty("state").GetBoolean());
        Assert.Equal(64, on.RootElement.GetProperty("params").GetProperty("dimming").GetInt32());
    }
}
