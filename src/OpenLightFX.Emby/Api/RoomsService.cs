namespace OpenLightFX.Emby.Api;

using System.Text.Json.Serialization;
using System.Runtime.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Model.Devices;
using OpenLightFX.Emby.Discovery;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Rooms;

[Route("/OpenLightFX/Studio/Setup", "GET")]
[Authenticated]
public sealed class GetStudioSetup : IReturn<StudioSetupDto> { }
[Route("/OpenLightFX/Studio/CanManageRooms", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetStudioRoomAdminCapability : IReturn<StudioRoomAdminCapabilityDto> { }
[Route("/OpenLightFX/Studio/Rooms", "GET")]
[Authenticated]
public sealed class GetStudioRooms : IReturn<List<StudioRoomDto>> { }
[Route("/OpenLightFX/Studio/Rooms", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class CreateStudioRoom : StudioRoomWrite, IReturn<StudioRoomDto> { }
[Route("/OpenLightFX/Studio/Rooms/{RoomId}", "PUT")]
[Authenticated(Roles = "Admin")]
public sealed class UpdateStudioRoom : StudioRoomWrite, IReturn<StudioRoomDto> { public string RoomId { get; set; } = ""; }
[Route("/OpenLightFX/Studio/Rooms/{RoomId}", "DELETE")]
[Authenticated(Roles = "Admin")]
public sealed class DeleteStudioRoom { public string RoomId { get; set; } = ""; }
public class StudioRoomWrite
{
    public string Name { get; set; } = "";
    public string ScreenDeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? AppName { get; set; }
    public List<RoomBulbAssignment> Bulbs { get; set; } = new();
    public bool MoveFromOtherRooms { get; set; }
}
[Route("/OpenLightFX/Studio/Devices", "GET")]
[Authenticated]
public sealed class GetStudioDevices : IReturn<List<StudioDeviceDto>> { }
[Route("/OpenLightFX/Studio/Discover", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class DiscoverStudioBulbs : IReturn<StudioDiscoveryDto> { }
[Route("/OpenLightFX/Studio/Bulbs/{BulbId}", "PUT")]
[Authenticated(Roles = "Admin")]
public sealed class RenameStudioBulb : IReturn<StudioBulbDto> { public string BulbId { get; set; } = ""; public string Name { get; set; } = ""; }
[Route("/OpenLightFX/Studio/Bulbs/{BulbId}/Identify", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class IdentifyStudioBulb { public string BulbId { get; set; } = ""; }

[DataContract]
public sealed class StudioSetupDto
{
    [DataMember(Name = "roomCount"), JsonPropertyName("roomCount")] public int RoomCount { get; set; }
}
[DataContract]
public sealed class StudioRoomAdminCapabilityDto
{
    [DataMember(Name = "canManageRooms"), JsonPropertyName("canManageRooms")] public bool CanManageRooms { get; set; }
}
[DataContract]
public sealed class StudioRoomDto
{
    [DataMember(Name = "id"), JsonPropertyName("id")] public string Id { get; set; } = "";
    [DataMember(Name = "name"), JsonPropertyName("name")] public string Name { get; set; } = "";
    [DataMember(Name = "screen"), JsonPropertyName("screen")] public StudioScreenDto Screen { get; set; } = new();
    [DataMember(Name = "bulbs"), JsonPropertyName("bulbs")] public List<StudioRoomBulbDto> Bulbs { get; set; } = new();
    [DataMember(Name = "updatedAt"), JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [DataMember(Name = "suggestedLayout"), JsonPropertyName("suggestedLayout")] public string SuggestedLayout { get; set; } = "LAYOUT_AMBIENT";
}
[DataContract]
public sealed class StudioScreenDto
{
    [DataMember(Name = "deviceId"), JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [DataMember(Name = "deviceName"), JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [DataMember(Name = "appName"), JsonPropertyName("appName")] public string AppName { get; set; } = "";
}
[DataContract]
public sealed class StudioRoomBulbDto
{
    [DataMember(Name = "id"), JsonPropertyName("id")] public string Id { get; set; } = "";
    [DataMember(Name = "position"), JsonPropertyName("position")] public string? Position { get; set; }
    [DataMember(Name = "follows"), JsonPropertyName("follows")] public string? Follows { get; set; }
}
[DataContract]
public sealed class StudioBulbDto
{
    [DataMember(Name = "id"), JsonPropertyName("id")] public string Id { get; set; } = "";
    [DataMember(Name = "name"), JsonPropertyName("name")] public string Name { get; set; } = "";
    [DataMember(Name = "protocol"), JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [DataMember(Name = "model"), JsonPropertyName("model")] public string? Model { get; set; }
    [DataMember(Name = "ipAddress"), JsonPropertyName("ipAddress")] public string? IpAddress { get; set; }
    [DataMember(Name = "roomId"), JsonPropertyName("roomId")] public string? RoomId { get; set; }
    [DataMember(Name = "position"), JsonPropertyName("position")] public string? Position { get; set; }
    [DataMember(Name = "follows"), JsonPropertyName("follows")] public string? Follows { get; set; }
    [DataMember(Name = "lastSeenAt"), JsonPropertyName("lastSeenAt")] public string? LastSeenAt { get; set; }
}
[DataContract]
public sealed class StudioDeviceDto
{
    [DataMember(Name = "deviceId"), JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [DataMember(Name = "name"), JsonPropertyName("name")] public string Name { get; set; } = "";
    [DataMember(Name = "appName"), JsonPropertyName("appName")] public string AppName { get; set; } = "";
    [DataMember(Name = "roomId"), JsonPropertyName("roomId")] public string? RoomId { get; set; }
    [DataMember(Name = "roomName"), JsonPropertyName("roomName")] public string? RoomName { get; set; }
}
[DataContract]
public sealed class StudioDiscoveryDto
{
    [DataMember(Name = "bulbs"), JsonPropertyName("bulbs")] public List<StudioBulbDto> Bulbs { get; set; } = new();
    [DataMember(Name = "hueBridges"), JsonPropertyName("hueBridges")] public List<object> HueBridges { get; set; } = new();
    [DataMember(Name = "foundCount"), JsonPropertyName("foundCount")] public int FoundCount { get; set; }
}

public sealed class RoomsService : IService, IRequiresRequest
{
    private readonly IDeviceManager _deviceManager;
    public IRequest Request { get; set; } = null!;

    public RoomsService(IDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }
    private static ServerEntryPoint Entry => ServerEntryPoint.Instance ?? throw new InvalidOperationException("OpenLightFX is not ready.");
    private static Exception Error(RoomStoreException exception) => new ArgumentException($"{exception.Code}: {exception.Message}");

    public object Get(GetStudioSetup request)
    {
        var entry = Entry;
        return new StudioSetupDto { RoomCount = entry.RoomStore.Rooms.Count };
    }

    public object Get(GetStudioRoomAdminCapability request) =>
        new StudioRoomAdminCapabilityDto { CanManageRooms = true };

    public object Get(GetStudioRooms request) => Entry.RoomStore.Rooms.Select(MapRoom).ToList();

    public object Post(CreateStudioRoom request) => Save(null, request, null);

    public object Put(UpdateStudioRoom request)
    {
        var match = Request?.Headers?["If-Match"];
        if (string.IsNullOrWhiteSpace(match)) throw new ArgumentException("If-Match is required. Reload the room before saving your changes.");
        return Save(request.RoomId, request, match.Trim('"'));
    }

    public object Delete(DeleteStudioRoom request) { Entry.RoomStore.DeleteRoom(request.RoomId); return new { success = true }; }

    public object Get(GetStudioDevices request)
    {
        var entry = Entry;
        var rooms = entry.RoomStore.Rooms;
        var devices = entry.SessionManager.Sessions.Where(x => !string.IsNullOrWhiteSpace(x.DeviceId)).GroupBy(x => x.DeviceId!).Select(g =>
        {
            var session = g.OrderByDescending(x => x.LastActivityDate).First();
            var room = rooms.FirstOrDefault(r => r.Screen.DeviceId == session.DeviceId);
            return new StudioDeviceDto { DeviceId = session.DeviceId!, Name = session.DeviceName ?? "Unknown device", AppName = session.Client ?? "Emby", RoomId = room?.Id, RoomName = room?.Name };
        }).ToList();
        foreach (var registered in _deviceManager.GetDevices(new DeviceQuery { Limit = 500 }).Items ?? Array.Empty<DeviceInfo>())
        {
            var deviceId = registered.ReportedDeviceId;
            if (string.IsNullOrWhiteSpace(deviceId) || devices.Any(d => d.DeviceId == deviceId)) continue;
            var room = rooms.FirstOrDefault(r => r.Screen.DeviceId == deviceId);
            devices.Add(new StudioDeviceDto { DeviceId = deviceId, Name = registered.Name ?? "Emby device", AppName = registered.AppName ?? "Emby", RoomId = room?.Id, RoomName = room?.Name });
        }
        foreach (var room in rooms)
            if (!devices.Any(d => d.DeviceId == room.Screen.DeviceId))
                devices.Add(new StudioDeviceDto { DeviceId = room.Screen.DeviceId, Name = room.Screen.DeviceName, AppName = room.Screen.AppName, RoomId = room.Id, RoomName = room.Name });
        return devices;
    }

    public object Post(DiscoverStudioBulbs request)
    {
        var entry = Entry;
        var discovered = entry.DiscoveryCoordinator.DiscoverAsync().GetAwaiter().GetResult();
        var hueBridges = discovered.Where(b => b.Protocol == BulbProtocol.Hue).Select(b => new
        {
            bridgeId = "hue:" + new string((b.MacAddress ?? b.IpAddress.ToString()).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(),
            ip = b.IpAddress.ToString(),
            paired = false
        }).Cast<object>().ToList();
        foreach (var bulb in discovered.Where(b => b.Protocol != BulbProtocol.Hue))
        {
            BulbConfig config = bulb.Protocol switch
            {
                BulbProtocol.Wiz => new WizBulbConfig(),
                BulbProtocol.Lifx => new LifxBulbConfig(),
                BulbProtocol.Govee => new GoveeBulbConfig(),
                _ => new RestBulbConfig()
            };
            config.Id = bulb.Id; config.Name = bulb.Name ?? bulb.Protocol.ToString(); config.IpAddress = bulb.IpAddress;
            config.Port = bulb.Port; config.MacAddress = bulb.MacAddress; config.Model = bulb.Model;
            entry.RoomStore.UpsertBulb(config);
        }
        return new StudioDiscoveryDto
        {
            Bulbs = entry.RoomStore.Bulbs.Select(MapBulb).ToList(),
            HueBridges = hueBridges,
            FoundCount = discovered.Count
        };
    }

    public object Put(RenameStudioBulb request)
    {
        try { return MapBulb(Entry.RoomStore.RenameBulb(request.BulbId, request.Name)); }
        catch (RoomStoreException ex) { throw Error(ex); }
    }

    public object Post(IdentifyStudioBulb request)
    {
        var record = Entry.RoomStore.FindBulb(request.BulbId) ?? throw new ArgumentException("bulb_not_found: Bulb not found.");
        var config = record.Config;
        var discovered = new DiscoveredBulb { Id = request.BulbId, IpAddress = config.IpAddress, Port = config.Port, Protocol = config.Protocol, MacAddress = config.MacAddress, Model = config.Model, Name = record.Name };
        try { Entry.IdentifyService.IdentifyAsync(discovered).GetAwaiter().GetResult(); return new { durationMs = 1500 }; }
        catch { throw new InvalidOperationException("no_answer: The bulb did not respond to Identify."); }
    }

    private object Save(string? roomId, StudioRoomWrite request, string? match)
    {
        var entry = Entry;
        var session = entry.SessionManager.Sessions.Where(x => x.DeviceId == request.ScreenDeviceId).OrderByDescending(x => x.LastActivityDate).FirstOrDefault();
        var registered = _deviceManager.GetDevices(new DeviceQuery { Limit = 500 }).Items?.FirstOrDefault(x => x.ReportedDeviceId == request.ScreenDeviceId);
        var existing = roomId == null ? null : entry.RoomStore.FindRoom(roomId);
        var existingScreen = existing?.Screen.DeviceId == request.ScreenDeviceId ? existing.Screen : null;
        if (session == null && registered == null && existingScreen == null) throw new ArgumentException("Choose a screen known to Emby.");
        var deviceName = session?.DeviceName ?? registered?.Name ?? existingScreen?.DeviceName ?? "Emby device";
        var appName = session?.Client ?? registered?.AppName ?? existingScreen?.AppName ?? "Emby";
        try
        {
            var room = entry.RoomStore.SaveRoom(roomId, request.Name, request.ScreenDeviceId, deviceName, appName, request.Bulbs, match, request.MoveFromOtherRooms);
            return MapRoom(room);
        }
        catch (RoomStoreException ex) { throw Error(ex); }
    }

    private static StudioRoomDto MapRoom(RoomRecord room)
    {
        var placements = room.Bulbs.Where(x => x.Position != null).Select(x => x.Position!).ToHashSet(StringComparer.Ordinal);
        var layout = placements.Contains("surround-left") || placements.Contains("surround-right") ? "LAYOUT_5_0" : placements.Contains("center") ? "LAYOUT_3_0" : placements.Contains("left") || placements.Contains("right") ? "LAYOUT_2_0" : "LAYOUT_AMBIENT";
        return new StudioRoomDto { Id = room.Id, Name = room.Name, Screen = new StudioScreenDto { DeviceId = room.Screen.DeviceId, DeviceName = room.Screen.DeviceName, AppName = room.Screen.AppName }, Bulbs = room.Bulbs.Select(x => new StudioRoomBulbDto { Id = x.Id, Position = x.Position, Follows = x.Follows }).ToList(), UpdatedAt = room.UpdatedAt, SuggestedLayout = layout };
    }
    private static StudioBulbDto MapBulb(KnownBulbRecord bulb) => new() { Id = bulb.Id, Name = bulb.Name, Protocol = bulb.Config.Protocol.ToString().ToLowerInvariant(), Model = bulb.Config.Model, IpAddress = bulb.Config.IpAddress?.ToString(), RoomId = bulb.RoomId, Position = bulb.Position, Follows = bulb.Follows, LastSeenAt = bulb.LastSeenAt };
}
