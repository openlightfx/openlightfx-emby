using System.Collections.Concurrent;
using MediaBrowser.Controller.Session;

namespace OpenLightFX.Emby.Studio;

/// <summary>Short-lived room reservations. A lost editor must not disable normal lighting forever.</summary>
public sealed class StudioScreenHost
{
    public sealed class Lease
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public required string RoomId { get; init; }
        public required string DeviceId { get; init; }
        public required string SessionId { get; init; }
        public required string ItemId { get; init; }
        public required long UserId { get; init; }
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddSeconds(20);
    }

    public sealed record Clock(long PositionMs, bool Paused, DateTimeOffset ReportedAt);
    private readonly ConcurrentDictionary<string, Lease> _rooms = new();
    private readonly ConcurrentDictionary<string, Clock> _clocks = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);

    // Expired reservations remain exclusive until cleanup has restored preview bulbs.
    public bool OwnsDevice(string? deviceId) => _rooms.Values.Any(x => x.DeviceId == deviceId);
    public Lease? Find(string roomId) => _rooms.GetValueOrDefault(roomId);
    public void Add(Lease lease) => _rooms[lease.RoomId] = lease;
    public void Remove(Lease lease) => _rooms.TryRemove(new KeyValuePair<string, Lease>(lease.RoomId, lease));
    public Lease[] Expired() => _rooms.Values.Where(x => x.ExpiresAt <= DateTimeOffset.UtcNow).ToArray();
    public void Report(SessionInfo session)
    {
        if (OwnsDevice(session.DeviceId))
            _clocks[session.Id] = new Clock(Math.Max(0, (session.PlayState.PositionTicks ?? 0) / TimeSpan.TicksPerMillisecond), session.PlayState.IsPaused, DateTimeOffset.UtcNow);
    }
    public void Forget(string sessionId) => _clocks.TryRemove(sessionId, out _);
    public Clock? GetClock(string sessionId) => _clocks.GetValueOrDefault(sessionId);
}
