using System.Net;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Rooms;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class RoomStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olfx-rooms-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RoomAndBulbMembershipSurviveRestartAndDeleteFreesBulb()
    {
        var store = new RoomStore(_directory);
        var bulb = store.UpsertBulb(new WizBulbConfig { Id = "test-bulb", Name = "Lamp", IpAddress = IPAddress.Parse("192.0.2.1"), MacAddress = "AA:BB:CC:DD:EE:FF" });
        bulb = store.RenameBulb(bulb.Id, "Lamp");
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby", new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "left" } });
        var reopened = new RoomStore(_directory);
        Assert.Equal(room.Id, Assert.Single(reopened.Rooms).Id);
        Assert.Equal(room.Id, Assert.Single(reopened.Bulbs).RoomId);
        reopened.DeleteRoom(room.Id);
        Assert.Null(new RoomStore(_directory).Bulbs.Single().RoomId);
    }

    [Fact]
    public void RescanWithSameMacRefreshesIpAndPreservesRoomMembershipAndName()
    {
        var store = new RoomStore(_directory);
        var first = store.UpsertBulb(new WizBulbConfig { Name = "Original lamp", IpAddress = IPAddress.Parse("192.0.2.3"), MacAddress = "AA:BB:CC:DD:EE:FF" });
        first = store.RenameBulb(first.Id, "Original lamp");
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby", new[] { new RoomBulbAssignment { Id = first.Id, Position = "left" } });
        var rescanned = store.UpsertBulb(new WizBulbConfig { Name = "Discovered name", IpAddress = IPAddress.Parse("192.0.2.4"), MacAddress = "aa-bb-cc-dd-ee-ff" });
        Assert.Equal(first.Id, rescanned.Id);
        Assert.Equal("Original lamp", rescanned.Name);
        Assert.Equal("192.0.2.4", rescanned.Config.IpAddress.ToString());
        Assert.Equal(room.Id, rescanned.RoomId);
        Assert.Equal("left", rescanned.Position);
    }

    [Fact]
    public void GoveeHardwareIdKeepsIdentityAndRoomPlacementAcrossAnIpChangeAndRestart()
    {
        var store = new RoomStore(_directory);
        var first = store.UpsertBulb(new GoveeBulbConfig
        {
            Name = "Govee H6008", HardwareId = "AA:BB:CC:DD:EE:FF", IpAddress = IPAddress.Parse("192.0.2.10")
        });
        Assert.False(first.IsNamed);
        var named = store.RenameBulb(first.Id, "Reading lamp");
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby",
            new[] { new RoomBulbAssignment { Id = named.Id, Position = "ambient", Follows = "left" } });

        var rescanned = store.UpsertBulb(new GoveeBulbConfig
        {
            Name = "Govee H6008", HardwareId = "aa-bb-cc-dd-ee-ff", IpAddress = IPAddress.Parse("192.0.2.11")
        });
        var reopened = new RoomStore(_directory);
        var persisted = Assert.Single(reopened.Bulbs);
        Assert.Equal(first.Id, rescanned.Id);
        Assert.Equal("govee:aabbccddeeff", persisted.Id);
        Assert.Equal("Reading lamp", persisted.Name);
        Assert.True(persisted.IsNamed);
        Assert.Equal("192.0.2.11", persisted.Config.IpAddress.ToString());
        Assert.Equal(room.Id, persisted.RoomId);
        Assert.Equal("ambient", persisted.Position);
        Assert.Equal("left", persisted.Follows);
        Assert.Equal(persisted.Id, Assert.Single(Assert.Single(reopened.Rooms).Bulbs).Id);
    }

    [Fact]
    public void GoveeLegacyIpRecordUpgradesFromItsOldDeviceNameAndRewritesRoomReference()
    {
        var store = new RoomStore(_directory);
        const string deviceId = "AA:BB:CC:DD:EE:FF";
        var legacy = store.UpsertBulb(new GoveeBulbConfig
        {
            Name = deviceId, IpAddress = IPAddress.Parse("192.0.2.20")
        });
        var legacyId = legacy.Id;
        var named = store.RenameBulb(legacy.Id, "Floor lamp");
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby",
            new[] { new RoomBulbAssignment { Id = named.Id, Position = "right" } });

        var upgraded = store.UpsertBulb(new GoveeBulbConfig
        {
            Name = "Govee H6008", HardwareId = deviceId, IpAddress = IPAddress.Parse("192.0.2.21")
        });
        var reopened = new RoomStore(_directory);
        var persisted = Assert.Single(reopened.Bulbs);
        Assert.NotEqual(legacyId, upgraded.Id);
        Assert.Equal("Floor lamp", persisted.Name);
        Assert.Equal(room.Id, persisted.RoomId);
        Assert.Equal(upgraded.Id, Assert.Single(Assert.Single(reopened.Rooms).Bulbs).Id);
        Assert.Equal("192.0.2.21", persisted.Config.IpAddress.ToString());
    }

    [Fact]
    public void UnnamedBulbsCannotBePlacedAndInRoomBulbsCannotBeForgotten()
    {
        var store = new RoomStore(_directory);
        var bulb = store.UpsertBulb(new WizBulbConfig { IpAddress = IPAddress.Parse("192.0.2.30") });
        Assert.Equal("bulb_not_named", Assert.Throws<RoomStoreException>(() => store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby",
            new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "left" } })).Code);

        store.RenameBulb(bulb.Id, "Wall lamp");
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby",
            new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "left" } });
        Assert.Equal("in_room", Assert.Throws<RoomStoreException>(() => store.ForgetBulb(bulb.Id)).Code);
        store.DeleteRoom(room.Id);
        store.ForgetBulb(bulb.Id);
        Assert.Empty(store.Bulbs);
    }

    [Fact]
    public void StableBulbIdUsesProtocolScopedNormalizedHardwareIdentity()
    {
        var govee = new GoveeBulbConfig { HardwareId = "AA:BB:CC:DD:EE:FF", IpAddress = IPAddress.Parse("192.0.2.40") };
        Assert.Equal("govee:aabbccddeeff", StableBulbId.From(govee));
        govee.HardwareId = "aa-bb-cc-dd-ee-ff";
        govee.IpAddress = IPAddress.Parse("192.0.2.41");
        Assert.Equal("govee:aabbccddeeff", StableBulbId.From(govee));
    }

    [Fact]
    public void RejectsDuplicateRoomNameAndScreenWithoutMutatingStore()
    {
        var store = new RoomStore(_directory);
        store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby", Array.Empty<RoomBulbAssignment>());
        Assert.Equal("name_taken", Assert.Throws<RoomStoreException>(() => store.SaveRoom(null, "living room", "device-2", "TV 2", "Emby", Array.Empty<RoomBulbAssignment>())).Code);
        Assert.Equal("screen_in_other_room", Assert.Throws<RoomStoreException>(() => store.SaveRoom(null, "Bedroom", "device-1", "TV", "Emby", Array.Empty<RoomBulbAssignment>())).Code);
        Assert.Single(store.Rooms);
    }

    [Fact]
    public void RejectsBulbsAssignedToAnotherRoomWithoutMovingThem()
    {
        var store = new RoomStore(_directory);
        var bulb = store.UpsertBulb(new WizBulbConfig { Name = "Lamp", IpAddress = IPAddress.Parse("192.0.2.5") });
        bulb = store.RenameBulb(bulb.Id, "Lamp");
        var bedroom = store.SaveRoom(null, "Bedroom", "device-1", "TV", "Emby", new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "left" } });

        Assert.Equal("in_other_room", Assert.Throws<RoomStoreException>(() =>
            store.SaveRoom(null, "Living Room", "device-2", "TV 2", "Emby", new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "right" } })).Code);
        Assert.Single(store.Rooms);
        Assert.Equal(bedroom.Id, store.Bulbs.Single().RoomId);
        Assert.Equal("left", store.Rooms.Single().Bulbs.Single().Position);
    }

    [Fact]
    public void RejectsDuplicateBulbAssignments()
    {
        var store = new RoomStore(_directory);
        var bulb = store.UpsertBulb(new WizBulbConfig { Name = "Lamp", IpAddress = IPAddress.Parse("192.0.2.2") });
        bulb = store.RenameBulb(bulb.Id, "Lamp");
        var duplicate = new[] { new RoomBulbAssignment { Id = bulb.Id, Position = "left" }, new RoomBulbAssignment { Id = bulb.Id, Position = "right" } };
        Assert.Equal("duplicate_bulb", Assert.Throws<RoomStoreException>(() => store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby", duplicate)).Code);
        Assert.Empty(store.Rooms);
    }

    [Fact]
    public void RejectsStaleUpdate()
    {
        var store = new RoomStore(_directory);
        var room = store.SaveRoom(null, "Living Room", "device-1", "TV", "Emby", Array.Empty<RoomBulbAssignment>());
        Assert.Equal("stale", Assert.Throws<RoomStoreException>(() => store.SaveRoom(room.Id, "Living Room", "device-1", "TV", "Emby", Array.Empty<RoomBulbAssignment>(), "old-version")).Code);
    }
}
