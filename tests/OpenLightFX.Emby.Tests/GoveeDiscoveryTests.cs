using System.Net;
using OpenLightFX.Emby.Discovery;
using OpenLightFX.Emby.Rooms;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class GoveeDiscoveryTests
{
    [Fact]
    public void ScanDeviceFieldIsKeptAsHardwareIdAndNotMistakenForMacOrDisplayName()
    {
        const string response = """
            {"msg":{"cmd":"scan","data":{"ip":"192.0.2.50","device":"AA:BB:CC:DD:EE:FF","sku":"H6008","wifiVersionSoft":"1.0"}}}
            """;
        var bulb = GoveeDiscovery.ParseScanResponse(response, IPAddress.Parse("192.0.2.1"));

        Assert.NotNull(bulb);
        Assert.Equal(IPAddress.Parse("192.0.2.50"), bulb.IpAddress);
        Assert.Equal("AA:BB:CC:DD:EE:FF", bulb.HardwareId);
        Assert.Null(bulb.MacAddress);
        Assert.Equal("H6008", bulb.Model);
        Assert.Equal("Govee H6008", bulb.Name);
        Assert.Equal("govee:aabbccddeeff", StableBulbId.From(new OpenLightFX.Emby.Models.GoveeBulbConfig
        {
            HardwareId = bulb.HardwareId,
            IpAddress = bulb.IpAddress
        }));
    }

    [Fact]
    public void ScanResponseWithoutDeviceIdentityDoesNotInventOne()
    {
        const string response = """
            {"msg":{"cmd":"scan","data":{"ip":"192.0.2.51","sku":"H6008"}}}
            """;
        var bulb = GoveeDiscovery.ParseScanResponse(response, IPAddress.Parse("192.0.2.1"));

        Assert.NotNull(bulb);
        Assert.Null(bulb.HardwareId);
        Assert.Null(bulb.MacAddress);
    }

    [Fact]
    public void NonScanAndMalformedPacketsAreIgnored()
    {
        Assert.Null(GoveeDiscovery.ParseScanResponse("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}", IPAddress.Loopback));
        Assert.Null(GoveeDiscovery.ParseScanResponse("not json", IPAddress.Loopback));
    }
}
