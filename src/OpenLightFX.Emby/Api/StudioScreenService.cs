using System.Globalization;
using System.Runtime.Serialization;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Session;
using OpenLightFX.Emby.Studio;

namespace OpenLightFX.Emby.Api;

[Route("/OpenLightFX/Studio/Rooms/{RoomId}/Screen", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetStudioScreen : IReturn<StudioScreenState>
{
    public string RoomId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Rooms/{RoomId}/Screen/Start", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class StartStudioScreen : IReturn<StudioScreenState>
{
    public string RoomId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public long PositionMs { get; set; }
    public bool ReplacePlayback { get; set; }
}

[Route("/OpenLightFX/Studio/Rooms/{RoomId}/Screen/Control", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ControlStudioScreen : IReturn<StudioScreenState>
{
    public string RoomId { get; set; } = "";
    public string LeaseId { get; set; } = "";
    // state renews the reservation; disconnect pauses only our movie before releasing it.
    public string Command { get; set; } = "state";
    public long PositionMs { get; set; }
}

[DataContract]
public sealed class StudioScreenState
{
    [DataMember(Name = "leaseId")] public string? LeaseId { get; set; }
    [DataMember(Name = "available")] public bool Available { get; set; }
    [DataMember(Name = "canSeek")] public bool CanSeek { get; set; }
    [DataMember(Name = "itemId")] public string? ItemId { get; set; }
    [DataMember(Name = "itemName")] public string? ItemName { get; set; }
    [DataMember(Name = "positionMs")] public long PositionMs { get; set; }
    [DataMember(Name = "paused")] public bool Paused { get; set; }
    [DataMember(Name = "ready")] public bool Ready { get; set; }
    [DataMember(Name = "reason")] public string? Reason { get; set; }
}

/// <summary>Room-scoped Emby remote playback. Does not save or activate the draft track.</summary>
public sealed class StudioScreenService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authorization;
    public IRequest Request { get; set; } = null!;
    public StudioScreenService(IAuthorizationContext authorization) => _authorization = authorization;
    private static ServerEntryPoint Entry => ServerEntryPoint.Instance ?? throw new InvalidOperationException("Plugin is starting.");

    private SessionInfo? Resolve(string roomId)
    {
        var room = Entry.RoomStore.FindRoom(roomId) ?? throw new ResourceNotFoundException("Room not found.");
        return Entry.SessionManager.Sessions
            .Where(s => s.DeviceId == room.Screen.DeviceId && s.IsActive && s.SupportsRemoteControl)
            .OrderByDescending(s => s.LastActivityDate).FirstOrDefault();
    }

    private SessionInfo RequireSession(string roomId)
    {
        var session = Resolve(roomId) ?? throw new ConflictException("screen_unavailable: Open Emby on this room's screen and enable remote control.");
        var user = _authorization.GetAuthorizationInfo(Request).User;
        if (user == null || !session.IsControllableByUser(user, true))
            throw new ConflictException("screen_forbidden: This Emby user cannot control the room's screen.");
        return session;
    }

    public object Get(GetStudioScreen request)
    {
        Request.Response.AddHeader("Cache-Control", "no-store");
        var session = Resolve(request.RoomId);
        return Snapshot(session, null);
    }

    public async Task<object> Post(StartStudioScreen request)
    {
        Request.Response.AddHeader("Cache-Control", "no-store");
        var entry = Entry;
        await entry.Screen.Gate.WaitAsync().ConfigureAwait(false);
        StudioScreenHost.Lease? lease = null;
        try
        {
            var session = RequireSession(request.RoomId);
            if (entry.Screen.Find(request.RoomId) != null)
                throw new ConflictException("screen_busy: Another editor is connected to this room. Disconnect it or wait for its reservation to expire.");
            if (!long.TryParse(request.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
                throw new ArgumentException("A valid Emby movie id is required.");
            var movie = entry.LibraryManager.GetItemById(itemId);
            if (movie is not Movie) throw new ArgumentException("Choose a movie available in Emby.");
            if (session.NowPlayingItem != null && !request.ReplacePlayback)
                throw new ConflictException("screen_playing: The screen is already playing. Confirm before replacing its playback.");
            var position = ClampPosition(request.PositionMs, movie.RunTimeTicks);
            var auth = _authorization.GetAuthorizationInfo(Request);
            lease = new StudioScreenHost.Lease
            {
                RoomId = request.RoomId, DeviceId = session.DeviceId, SessionId = session.Id,
                ItemId = itemId.ToString(CultureInfo.InvariantCulture), UserId = auth.UserId
            };
            // Reserve before sending Play, so PlaybackStart cannot start the saved lighting track.
            await entry.Preview.ExclusiveAsync(async () =>
            {
                entry.Screen.Add(lease);
                await entry.StopRoomLightingAsync(session.DeviceId).ConfigureAwait(false);
            }).ConfigureAwait(false);
            entry.Screen.Forget(session.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await entry.SessionManager.SendPlayCommand(ControllerId(), session.Id, new PlayRequest
            {
                ItemIds = new[] { itemId }, PlayCommand = PlayCommand.PlayNow,
                StartPositionTicks = position * TimeSpan.TicksPerMillisecond,
                ControllingUserId = auth.User.Id.ToString("N")
            }, timeout.Token).ConfigureAwait(false);
            // Do not mistake the previous playback's position for the new movie clock.
            return new StudioScreenState { Available = true, LeaseId = lease.Id, Paused = true, Reason = "Starting movie on screen…" };
        }
        catch
        {
            if (lease != null) entry.Screen.Remove(lease);
            throw;
        }
        finally { entry.Screen.Gate.Release(); }
    }

    public async Task<object> Post(ControlStudioScreen request)
    {
        Request.Response.AddHeader("Cache-Control", "no-store");
        var entry = Entry;
        await entry.Screen.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = entry.Screen.Find(request.RoomId);
            if (lease == null || lease.Id != request.LeaseId || lease.UserId != _authorization.GetAuthorizationInfo(Request).UserId)
                throw new ConflictException("screen_disconnected: The room editing session has ended.");
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow && request.Command != "disconnect")
                throw new ConflictException("screen_disconnected: The room editing session expired. Reconnect the screen.");
            var session = entry.SessionManager.Sessions.FirstOrDefault(s => s.Id == lease.SessionId && s.IsActive);
            if (request.Command == "disconnect")
            {
                try
                {
                    if (session?.NowPlayingItem?.Id == lease.ItemId)
                        await Send(session, PlaystateCommand.Pause).ConfigureAwait(false);
                }
                finally { await Release(lease).ConfigureAwait(false); }
                return new StudioScreenState { Paused = true };
            }
            var resolved = RequireSession(request.RoomId);
            if (session == null || resolved.Id != lease.SessionId)
                throw new ConflictException("screen_disconnected: The screen reconnected. Connect it again from Studio.");
            lease.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20);
            if (request.Command == "state") return Snapshot(session, lease);
            if (session.NowPlayingItem?.Id != lease.ItemId || entry.Screen.GetClock(session.Id) == null)
                throw new ConflictException("screen_not_ready: Wait for this movie to start on the screen.");
            var command = request.Command switch
            {
                "play" => PlaystateCommand.Unpause,
                "pause" => PlaystateCommand.Pause,
                "seek" => PlaystateCommand.Seek,
                _ => throw new ArgumentException("Unknown screen command.")
            };
            if (command == PlaystateCommand.Seek && !session.PlayState.CanSeek)
                throw new ConflictException("screen_cannot_seek: This Emby player does not currently support seeking.");
            var position = ClampPosition(request.PositionMs, session.NowPlayingItem.RunTimeTicks);
            await Send(session, command, position).ConfigureAwait(false);
            return Snapshot(session, lease);
        }
        finally { entry.Screen.Gate.Release(); }
    }

    private string? ControllerId()
    {
        var auth = _authorization.GetAuthorizationInfo(Request);
        return Entry.SessionManager.Sessions.FirstOrDefault(s => s.DeviceId == auth.ReportedDeviceId && s.UserInternalId == auth.UserId)?.Id;
    }

    private async Task Send(SessionInfo session, PlaystateCommand command, long positionMs = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Entry.SessionManager.SendPlaystateCommand(ControllerId(), session.Id, new PlaystateRequest
        {
            Command = command,
            SeekPositionTicks = command == PlaystateCommand.Seek ? positionMs * TimeSpan.TicksPerMillisecond : null,
            ControllingUserId = _authorization.GetAuthorizationInfo(Request).User.Id.ToString("N")
        }, timeout.Token).ConfigureAwait(false);
    }

    private static long ClampPosition(long position, long? durationTicks) => Math.Clamp(position, 0,
        durationTicks is > 0 ? Math.Max(0, durationTicks.Value / TimeSpan.TicksPerMillisecond - 1) : TimeSpan.FromDays(7).Ticks / TimeSpan.TicksPerMillisecond);

    private static async Task Release(StudioScreenHost.Lease lease)
    {
        await Entry.Preview.ExclusiveAsync(async () =>
        {
            if (Entry.Preview.RoomId == lease.RoomId) await Entry.Preview.StopCoreAsync().ConfigureAwait(false);
            Entry.Screen.Remove(lease);
            Entry.Screen.Forget(lease.SessionId);
        }).ConfigureAwait(false);
    }

    private static StudioScreenState Snapshot(SessionInfo? session, StudioScreenHost.Lease? lease)
    {
        var clock = session == null ? null : Entry.Screen.GetClock(session.Id);
        var age = clock == null ? double.MaxValue : (DateTimeOffset.UtcNow - clock.ReportedAt).TotalMilliseconds;
        var ready = lease != null && session?.NowPlayingItem?.Id == lease.ItemId && clock != null && age < 35000;
        var position = clock?.PositionMs ?? Math.Max(0, (session?.PlayState.PositionTicks ?? 0) / TimeSpan.TicksPerMillisecond);
        if (ready && !clock!.Paused) position += (long)Math.Clamp(age, 0, 30000);
        return new StudioScreenState
        {
            LeaseId = lease?.Id, Available = session != null, CanSeek = session?.PlayState.CanSeek ?? false,
            ItemId = session?.NowPlayingItem?.Id, ItemName = session?.NowPlayingItem?.Name,
            PositionMs = ClampPosition(position, session?.NowPlayingItem?.RunTimeTicks),
            Paused = clock?.Paused ?? session?.PlayState.IsPaused ?? true, Ready = ready,
            Reason = ready ? null : session == null ? "Open Emby on the room's screen and enable remote control."
                : lease == null ? null : "Waiting for the screen to report this movie's playback…"
        };
    }
}
