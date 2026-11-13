namespace OpenLightFX.Emby;

using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Discovery;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Effects;
using OpenLightFX.Emby.Engine;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;
using OpenLightFX.Emby.Studio;
using OpenLightFX.Emby.Rooms;
using OpenLightFX.Emby.Utilities;
using Openlightfx;
using System.Collections.Concurrent;
using System.Timers;

/// <summary>
/// Main entry point for the OpenLightFX plugin. Subscribes to Emby playback events
/// and orchestrates lighting sessions for each active playback.
/// </summary>
public class ServerEntryPoint : IServerEntryPoint
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger _logger;

    private readonly TrackParser _trackParser;
    private readonly TrackDiscoveryService _trackDiscoveryService;
    private readonly TrackSelectionService _selectionService;
    private readonly ConfigurationService _configService;
    private readonly EffectRendererFactory _effectFactory;

    // Shared discovery infrastructure (accessed by API endpoints)
    private readonly DiscoveredBulbStore _discoveredBulbStore;
    private readonly DiscoveryCoordinator _discoveryCoordinator;
    private readonly IdentifyService _identifyService;

    // Active lighting sessions keyed by Emby session ID
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new();
    private int _playbackLightingSuspended;

    // Per-session lighting enabled toggle (EMB-152)
    private readonly ConcurrentDictionary<string, bool> _lightingEnabled = new();

    // Sessions where no track was found — avoid retrying every progress tick
    private readonly ConcurrentDictionary<string, bool> _noTrackSessions = new();

    // Playback position polling timer
    private System.Timers.Timer? _pollTimer;

    private readonly StudioPreviewHost _preview = new();
    private readonly StudioTrackIndex _studioTrackIndex;
    private readonly RoomStore _roomStore;

    public ServerEntryPoint(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IServerApplicationPaths appPaths,
        ILogManager logManager)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logManager.GetLogger("OpenLightFX");

        _trackParser = new TrackParser();
        _trackDiscoveryService = new TrackDiscoveryService(_trackParser, _logger);
        _selectionService = new TrackSelectionService(appPaths.DataPath);
        _configService = new ConfigurationService();
        _effectFactory = new EffectRendererFactory();

        _discoveredBulbStore = new DiscoveredBulbStore();
        _discoveryCoordinator = new DiscoveryCoordinator();
        _identifyService = new IdentifyService();
        _studioTrackIndex = new StudioTrackIndex(
            Path.Combine(appPaths.DataPath, "openlightfx-studio-tracks.json"));
        _roomStore = new RoomStore(appPaths.DataPath);

        Instance = this;
    }

    /// <summary>Singleton for API endpoint access.</summary>
    public static ServerEntryPoint? Instance { get; private set; }

    public void Run()
    {
        _logger.Info("OpenLightFX starting up");

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;

        var options = GetOptions();
        var intervalMs = Math.Max(options.PollIntervalMs, 100);
        _pollTimer = new System.Timers.Timer(intervalMs);
        _pollTimer.Elapsed += OnPollTick;
        _pollTimer.AutoReset = true;
        _pollTimer.Start();

        _logger.Info("OpenLightFX ready — polling every {0}ms", intervalMs);
    }

    public void Dispose()
    {
        _logger.Info("OpenLightFX shutting down");

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;

        _pollTimer?.Stop();
        _pollTimer?.Dispose();

        try
        {
            _preview.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn("Error stopping Studio preview: {0}", ex.Message);
        }

        foreach (var session in _sessions.Values)
            session.StopAsync().GetAwaiter().GetResult();
        _sessions.Clear();

        _logger.Info("OpenLightFX shut down complete");
    }

    // ─── Event handlers ────────────────────────────────────────────────

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var sessionId = e.Session?.Id;
            _logger.Debug("PlaybackStart fired: session={0}", sessionId ?? "(null)");

            if (sessionId == null) return;

            var item = e.MediaInfo ?? e.Session?.NowPlayingItem;
            if (item == null)
            {
                _logger.Debug("PlaybackStart: no item info available for session={0}", sessionId);
                return;
            }

            // A new Emby play is allowed to own the bulbs again after a Studio takeover.
            Interlocked.Exchange(ref _playbackLightingSuspended, 0);
            var deviceId = e.Session?.DeviceId;
            _ = TryStartLightingSession(sessionId, item, deviceId);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error in OnPlaybackStart", ex);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var sessionId = e.Session?.Id;
            if (sessionId == null) return;

            if (_sessions.TryRemove(sessionId, out var session))
            {
                _ = session.StopAsync();
                _logger.Info("Lighting session stopped: {0}", sessionId);
            }
            _noTrackSessions.TryRemove(sessionId, out _);
            _lightingEnabled.TryRemove(sessionId, out _); // Reset toggle on stop (EMB-152)
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error stopping lighting session", ex);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var sessionId = e.Session?.Id;
            if (sessionId == null) return;

            if (!_sessions.ContainsKey(sessionId))
            {
                if (_noTrackSessions.ContainsKey(sessionId)) return;

                var item = e.MediaInfo ?? e.Session?.NowPlayingItem;
                if (item != null)
                {
                    _logger.Debug("PlaybackProgress: no session for {0}, attempting late init", sessionId);
                    var deviceId = e.Session?.DeviceId;
                    _ = TryStartLightingSession(sessionId, item, deviceId);
                }
                return;
            }

            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            // Check lighting enabled toggle (EMB-152)
            if (_lightingEnabled.TryGetValue(sessionId, out var enabled) && !enabled)
                return;

            var playState = e.Session?.PlayState;
            if (playState == null) return;

            var positionTicks = playState.PositionTicks ?? 0;
            var positionMs = (ulong)(positionTicks / TimeSpan.TicksPerMillisecond);

            if (playState.IsPaused)
                session.Pause();
            else
                session.UpdatePosition(positionMs);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error processing playback progress", ex);
        }
    }

    private void OnPollTick(object? sender, ElapsedEventArgs e)
    {
        foreach (var (sessionId, session) in _sessions)
        {
            try
            {
                // Skip ticking if lighting is disabled for this session
                if (_lightingEnabled.TryGetValue(sessionId, out var enabled) && !enabled)
                    continue;

                session.Tick();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error in poll tick for session {0}", ex, sessionId);
            }
        }
    }

    internal PluginOptions GetOptions() =>
        Plugin.Instance?.GetPluginOptions() ?? new PluginOptions();

    private async Task TryStartLightingSession(
        string sessionId, MediaBrowser.Model.Dto.BaseItemDto item, string? deviceId)
    {
        try
        {
            if (Volatile.Read(ref _playbackLightingSuspended) != 0)
            {
                _logger.Info("Lighting stays off for session {0}; Studio preview took the bulbs", sessionId);
                return;
            }

            _logger.Info("Starting lighting session: session={0}, item={1}, device={2}",
                sessionId, item.Name, deviceId ?? "(global)");

            var moviePath = item.Path;
            if (string.IsNullOrEmpty(moviePath))
            {
                _logger.Debug("No file path for item '{0}' — skipping", item.Name);
                return;
            }

            var itemId = item.Id ?? string.Empty;

            string? imdbId = null;
            item.ProviderIds?.TryGetValue("Imdb", out imdbId);

            var options = GetOptions();
            var scanPaths = _configService.GetScanPaths(options.AdditionalScanPaths);

            var tracks = _trackDiscoveryService.DiscoverTracks(moviePath, imdbId, scanPaths);
            if (tracks.Count == 0)
            {
                _logger.Debug("No .lightfx tracks found for '{0}' at '{1}'", item.Name, moviePath);
                _noTrackSessions.TryAdd(sessionId, true);
                return;
            }

            _logger.Info("Found {0} track(s) for '{1}'", tracks.Count, item.Name);

            // Per-device track selection (EMB-038): check device scope first, then global
            var selectedPath = _selectionService.GetSelectedTrack(itemId, deviceId);
            if (selectedPath == null)
            {
                if (tracks.Count == 1)
                {
                    // Only one track available — use it automatically
                    _logger.Debug("Auto-selecting sole track '{0}' for '{1}'", tracks[0].FileName, item.Name);
                    selectedPath = tracks[0].FilePath;
                }
                else
                {
                    // Multiple tracks, none selected — proceed without lighting (EMB-011)
                    _logger.Debug("No track selected for '{0}' (device={1}) — skipping lighting",
                        item.Name, deviceId ?? "global");
                    _noTrackSessions.TryAdd(sessionId, true);
                    return;
                }
            }

            var trackInfo = tracks.FirstOrDefault(t => t.FilePath == selectedPath)
                ?? tracks.FirstOrDefault(t => t.IsValid);

            if (trackInfo == null || !trackInfo.IsValid)
            {
                _logger.Warn("No valid track for '{0}': {1}", item.Name,
                    string.Join("; ", tracks.Select(t => t.FileName + ": " + string.Join(", ", t.ValidationErrors))));
                return;
            }

            if (options.ShowFlashingWarnings && trackInfo.Track.SafetyInfo != null)
            {
                var warning = SafetyWarningHelper.GetWarningMessage(
                    trackInfo.Track.SafetyInfo, options.PhotosensitivityMode);
                if (warning != null)
                    _logger.Warn("Safety warning for '{0}': {1}", item.Name, warning);
            }

            var room = string.IsNullOrWhiteSpace(deviceId)
                ? null
                : _roomStore.Rooms.FirstOrDefault(candidate => candidate.Screen.DeviceId == deviceId);
            if (room == null)
            {
                _logger.Info("Device {0} is not assigned to a room; skipping lighting for '{1}'", deviceId ?? "(unknown)", item.Name);
                return;
            }

            var knownBulbs = _roomStore.Bulbs;
            var bulbs = room.Bulbs
                .Where(assignment => assignment.Position != null || assignment.Follows != null)
                .Select(assignment => knownBulbs.FirstOrDefault(bulb => bulb.Id == assignment.Id)?.Config)
                .Where(config => config != null)
                .Cast<BulbConfig>()
                .ToList();
            if (bulbs.Count == 0)
            {
                _logger.Warn("Room '{0}' has no placed bulbs — skipping lighting for '{1}'", room.Name, item.Name);
                return;
            }

            var profile = RoomPlaybackMapping.Create(room, knownBulbs, trackInfo.Track);
            if (profile.Mappings.Count == 0)
            {
                _logger.Warn("Room '{0}' has no bulbs matching the track channels — skipping lighting for '{1}'", room.Name, item.Name);
                return;
            }
            _logger.Debug("Room '{0}': {1} bulb(s), {2} channel mapping(s)", room.Name, bulbs.Count, profile.Mappings.Count);

            var session = new PlaybackSession(trackInfo, bulbs, profile, options, _effectFactory, _logger);

            await _preview.ExclusiveAsync(async () =>
            {
                // Preview and playback both own bulb drivers. Stop preview before taking them.
                await _preview.StopCoreAsync();
                if (!_sessions.TryAdd(sessionId, session))
                    return;

                try
                {
                    await session.StartAsync();
                    _logger.Info("Lighting session started for '{0}' (track: '{1}', {2} channels, {3} keyframes)",
                        item.Name, trackInfo.Title, trackInfo.ChannelCount, trackInfo.KeyframeCount);
                }
                catch
                {
                    _sessions.TryRemove(sessionId, out _);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error starting lighting session for session {0}", ex, sessionId);
        }
    }

    // ─── Public API (accessed by REST endpoints) ──────────────────────

    /// <summary>
    /// Stop playback lighting so Studio can drive the bulbs. Progress events do not
    /// start it again until Emby fires a new playback start.
    /// </summary>
    public async Task SuspendPlaybackLightingAsync()
    {
        Interlocked.Exchange(ref _playbackLightingSuspended, 1);
        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            if (!_sessions.TryRemove(sessionId, out var session))
                continue;
            try { await session.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn("Error stopping lighting for takeover: {0}", ex.Message); }
        }
    }

    public int ActiveSessionCount => _sessions.Count;
    public IEnumerable<string> ActiveSessionIds => _sessions.Keys;
    public ISessionManager SessionManager => _sessionManager;
    public TrackDiscoveryService TrackDiscoveryService => _trackDiscoveryService;
    public TrackSelectionService SelectionService => _selectionService;
    public DiscoveredBulbStore DiscoveredBulbStore => _discoveredBulbStore;
    public DiscoveryCoordinator DiscoveryCoordinator => _discoveryCoordinator;
    public IdentifyService IdentifyService => _identifyService;
    public ILibraryManager LibraryManager => _libraryManager;
    public StudioPreviewHost Preview => _preview;
    public StudioTrackIndex StudioTrackIndex => _studioTrackIndex;
    public RoomStore RoomStore => _roomStore;

    /// <summary>Get lighting enabled state for a session (EMB-152).</summary>
    public bool IsLightingEnabled(string sessionId) =>
        !_lightingEnabled.TryGetValue(sessionId, out var enabled) || enabled;

    /// <summary>Set lighting enabled state for a session (EMB-152).</summary>
    public void SetLightingEnabled(string sessionId, bool enabled) =>
        _lightingEnabled[sessionId] = enabled;

    /// <summary>Get active session info for status endpoint.</summary>
    public IEnumerable<(string SessionId, bool LightingEnabled)> GetActiveSessionInfo() =>
        _sessions.Keys.Select(id => (id, IsLightingEnabled(id)));
}
