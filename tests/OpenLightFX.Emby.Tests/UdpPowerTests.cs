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
    public async Task WizRgbOutputSplitsWhiteBetweenColdAndWarmChannels()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new WizDriver(new WizBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await driver.SetState(255, 190, 70, 100, 100);
        using var gold = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var goldParams = gold.RootElement.GetProperty("params");
        var goldCold = goldParams.GetProperty("c").GetInt32();
        var goldWarm = goldParams.GetProperty("w").GetInt32();
        Assert.True(goldWarm > goldCold);
        Assert.True(goldCold > 0);
        Assert.True(goldParams.GetProperty("r").GetInt32() > goldParams.GetProperty("g").GetInt32());

        await driver.SetState(255, 0, 0, 100, 100);
        using var red = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var redParams = red.RootElement.GetProperty("params");
        Assert.Equal(0, redParams.GetProperty("c").GetInt32());
        Assert.Equal(0, redParams.GetProperty("w").GetInt32());

        await driver.SetState(255, 255, 255, 100, 100);
        using var white = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var whiteParams = white.RootElement.GetProperty("params");
        Assert.Equal(255, whiteParams.GetProperty("c").GetInt32());
        Assert.Equal(255, whiteParams.GetProperty("w").GetInt32());
        Assert.Equal(
            510,
            whiteParams.GetProperty("c").GetInt32() + whiteParams.GetProperty("w").GetInt32());
        Assert.Equal(0, whiteParams.GetProperty("r").GetInt32());

        await driver.SetState(180, 220, 255, 100, 100);
        using var cool = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var coolParams = cool.RootElement.GetProperty("params");
        Assert.True(coolParams.GetProperty("c").GetInt32() > coolParams.GetProperty("w").GetInt32());

        await driver.SetState(1, 1, 1, 100, 100);
        using var dimNeutral = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var dimNeutralParams = dimNeutral.RootElement.GetProperty("params");
        Assert.True(dimNeutralParams.GetProperty("state").GetBoolean());
        Assert.Equal(
            1,
            dimNeutralParams.GetProperty("c").GetInt32() + dimNeutralParams.GetProperty("w").GetInt32());

        await driver.SetState(1, 0, 0, 100, 100);
        using var dimRed = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var dimRedParams = dimRed.RootElement.GetProperty("params");
        Assert.True(dimRedParams.GetProperty("state").GetBoolean());
        Assert.Equal(1, dimRedParams.GetProperty("r").GetInt32());
        Assert.Equal(0, dimRedParams.GetProperty("c").GetInt32());
        Assert.Equal(0, dimRedParams.GetProperty("w").GetInt32());
    }

    [Fact]
    public async Task WizBlackRgbAtFullBrightnessStaysOffWhenPoweredOnAgain()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var driver = new WizDriver(new WizBulbConfig
        {
            IpAddress = IPAddress.Loopback, Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await driver.SetState(255, 190, 70, 100, 100);
        _ = await socket.ReceiveAsync(timeout.Token);

        await driver.SetState(0, 0, 0, 100, 100);
        using var black = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var parameters = black.RootElement.GetProperty("params");
        Assert.False(parameters.GetProperty("state").GetBoolean());
        Assert.Equal(0, parameters.GetProperty("r").GetInt32());
        Assert.Equal(0, parameters.GetProperty("g").GetInt32());
        Assert.Equal(0, parameters.GetProperty("b").GetInt32());
        Assert.Equal(0, parameters.GetProperty("c").GetInt32());
        Assert.Equal(0, parameters.GetProperty("w").GetInt32());

        await driver.SetPower(true);
        using var poweredBlack = JsonDocument.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer);
        var poweredBlackParams = poweredBlack.RootElement.GetProperty("params");
        Assert.False(poweredBlackParams.GetProperty("state").GetBoolean());
        Assert.Equal(0, poweredBlackParams.GetProperty("r").GetInt32());
        Assert.Equal(0, poweredBlackParams.GetProperty("g").GetInt32());
        Assert.Equal(0, poweredBlackParams.GetProperty("b").GetInt32());
        Assert.Equal(0, poweredBlackParams.GetProperty("c").GetInt32());
        Assert.Equal(0, poweredBlackParams.GetProperty("w").GetInt32());
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
