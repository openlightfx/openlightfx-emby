namespace OpenLightFX.Emby.Rooms;

using System.Text.Json;
using OpenLightFX.Emby.Models;

public sealed class RoomStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly StoreDocument _document;

    public RoomStore(string dataPath)
    {
        var directory = Path.Combine(dataPath, "OpenLightFX");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "rooms.json");
        _document = File.Exists(_path)
            ? JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_path), JsonOptions) ?? new StoreDocument()
            : new StoreDocument();
        Normalize();
    }

    public IReadOnlyList<RoomRecord> Rooms { get { lock (_gate) return _document.Rooms.ToArray(); } }
    public IReadOnlyList<KnownBulbRecord> Bulbs { get { lock (_gate) return _document.Bulbs.ToArray(); } }
    public RoomRecord? FindRoom(string id) { lock (_gate) return _document.Rooms.FirstOrDefault(x => x.Id == id); }
    public KnownBulbRecord? FindBulb(string id) { lock (_gate) return _document.Bulbs.FirstOrDefault(x => x.Id == id); }

    public RoomRecord SaveRoom(string? roomId, string name, string deviceId, string deviceName, string appName,
        IReadOnlyList<RoomBulbAssignment> assignments, string? expectedUpdatedAt = null, bool moveFromOtherRooms = false)
    {
        lock (_gate)
        {
            name = (name ?? "").Trim();
            if (name.Length is < 1 or > 40) throw new RoomStoreException("name_required", "Room name must be 1–40 characters.");
            if (string.IsNullOrWhiteSpace(deviceId)) throw new RoomStoreException("screen_required", "Choose a screen for this room.");
            if (_document.Rooms.Any(x => x.Id != roomId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new RoomStoreException("name_taken", "A room already has that name.");
            if (assignments.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != assignments.Count)
                throw new RoomStoreException("duplicate_bulb", "A bulb can only be placed once in a room.");
            var existing = roomId == null ? null : _document.Rooms.FirstOrDefault(x => x.Id == roomId);
            if (roomId != null && existing == null) throw new RoomStoreException("room_not_found", "Room not found.");
            if (existing != null && expectedUpdatedAt != null && existing.UpdatedAt != expectedUpdatedAt)
                throw new RoomStoreException("stale", "This room changed in another tab.");
            var otherScreen = _document.Rooms.FirstOrDefault(x => x.Id != roomId && x.Screen.DeviceId == deviceId);
            if (otherScreen != null) throw new RoomStoreException("screen_in_other_room", "That screen is already assigned to a different room.");
            foreach (var assignment in assignments)
            {
                var bulb = _document.Bulbs.FirstOrDefault(x => x.Id == assignment.Id);
                if (bulb == null) throw new RoomStoreException("unknown_bulb", "A selected bulb is no longer known.");
                if (bulb.RoomId != null && bulb.RoomId != roomId && !moveFromOtherRooms)
                    throw new RoomStoreException("in_other_room", "A selected bulb belongs to another room.");
                if (assignment.Position != null && !RoomPositions.All.Contains(assignment.Position))
                    throw new RoomStoreException("invalid_position", "Choose a valid bulb position.");
                if (assignment.Follows != null && !RoomPositions.All.Contains(assignment.Follows))
                    throw new RoomStoreException("invalid_follows", "Choose a valid Follows position.");
            }
            if (moveFromOtherRooms)
            {
                foreach (var assignment in assignments)
                    foreach (var other in _document.Rooms.Where(x => x.Id != roomId && x.Bulbs.Any(b => b.Id == assignment.Id)))
                    { other.Bulbs.RemoveAll(b => b.Id == assignment.Id); other.UpdatedAt = Now(); }
            }
            foreach (var bulb in _document.Bulbs.Where(x => x.RoomId == roomId)) bulb.RoomId = null;
            var updated = Now();
            var result = existing ?? new RoomRecord { Id = "r_" + Guid.NewGuid().ToString("N")[..10] };
            result.Name = name;
            result.Screen = new RoomScreen { DeviceId = deviceId, DeviceName = deviceName, AppName = appName };
            result.UpdatedAt = updated;
            result.Bulbs = assignments.Select(a =>
            {
                var bulb = _document.Bulbs.Single(x => x.Id == a.Id);
                bulb.RoomId = result.Id; bulb.Position = a.Position; bulb.Follows = a.Follows;
                return new RoomBulbAssignment { Id = a.Id, Position = a.Position, Follows = a.Follows };
            }).ToList();
            if (existing == null) _document.Rooms.Add(result);
            Persist();
            return result;
        }
    }

    public void DeleteRoom(string id)
    {
        lock (_gate)
        {
            var room = _document.Rooms.FirstOrDefault(x => x.Id == id);
            if (room == null) return;
            foreach (var bulb in _document.Bulbs.Where(x => x.RoomId == id)) { bulb.RoomId = null; bulb.Position = null; bulb.Follows = null; }
            _document.Rooms.Remove(room); Persist();
        }
    }

    public KnownBulbRecord UpsertBulb(BulbConfig config, string? stableId = null)
    {
        lock (_gate)
        {
            var id = stableId ?? StableBulbId.From(config);
            var bulb = _document.Bulbs.FirstOrDefault(x => x.Id == id)
                ?? _document.Bulbs.FirstOrDefault(x => x.Config.Protocol == config.Protocol &&
                    ((!string.IsNullOrWhiteSpace(config.MacAddress) && NormalizeMac(x.Config.MacAddress) == NormalizeMac(config.MacAddress)) ||
                     x.Config.IpAddress.Equals(config.IpAddress)));
            if (bulb == null) { bulb = new KnownBulbRecord { Id = id, Name = config.Name }; _document.Bulbs.Add(bulb); }
            bulb.Id = id; config.Id = id; bulb.Config = config; bulb.Name = string.IsNullOrWhiteSpace(bulb.Name) ? config.Name : bulb.Name;
            bulb.LastSeenAt = Now(); Persist(); return bulb;
        }
    }

    public KnownBulbRecord RenameBulb(string id, string name)
    {
        lock (_gate)
        {
            name = (name ?? "").Trim();
            if (name.Length is < 1 or > 40) throw new RoomStoreException("name_required", "Bulb name must be 1–40 characters.");
            var bulb = _document.Bulbs.FirstOrDefault(x => x.Id == id) ?? throw new RoomStoreException("bulb_not_found", "Bulb not found.");
            bulb.Name = name; Persist(); return bulb;
        }
    }

    private void Normalize()
    {
        _document.Rooms ??= new(); _document.Bulbs ??= new();
        foreach (var room in _document.Rooms)
        {
            room.Bulbs ??= new();
            foreach (var assignment in room.Bulbs)
                if (_document.Bulbs.FirstOrDefault(b => b.Id == assignment.Id) is { } bulb) { bulb.RoomId = room.Id; bulb.Position = assignment.Position; bulb.Follows = assignment.Follows; }
        }
    }
    private void Persist()
    {
        _document.UpdatedAt = Now();
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_document, JsonOptions));
        File.Move(temp, _path, true);
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
    private static string NormalizeMac(string? mac) => new string((mac ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

public sealed class StoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string UpdatedAt { get; set; } = "";
    public List<RoomRecord> Rooms { get; set; } = new();
    public List<KnownBulbRecord> Bulbs { get; set; } = new();
}
public sealed class RoomRecord
{
    public string Id { get; set; } = ""; public string Name { get; set; } = ""; public RoomScreen Screen { get; set; } = new();
    public List<RoomBulbAssignment> Bulbs { get; set; } = new(); public string UpdatedAt { get; set; } = "";
}
public sealed class RoomScreen { public string DeviceId { get; set; } = ""; public string DeviceName { get; set; } = ""; public string AppName { get; set; } = ""; }
public sealed class RoomBulbAssignment { public string Id { get; set; } = ""; public string? Position { get; set; } public string? Follows { get; set; } }
public sealed class KnownBulbRecord
{
    public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? RoomId { get; set; }
    public string? Position { get; set; } public string? Follows { get; set; } public BulbConfig Config { get; set; } = null!;
    public string LastSeenAt { get; set; } = "";
}
public static class RoomPositions { public static readonly string[] All = { "left", "center", "right", "surround-left", "surround-right", "ambient" }; }
public static class StableBulbId
{
    public static string From(BulbConfig config)
    {
        var protocol = config.Protocol.ToString().ToLowerInvariant();
        var mac = new string((config.MacAddress ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return mac.Length > 0 ? $"{protocol}:{mac}" : $"{protocol}:ip:{config.IpAddress}";
    }
}
public sealed class RoomStoreException : Exception
{
    public string Code { get; }
    public RoomStoreException(string code, string message) : base(message) => Code = code;
}
