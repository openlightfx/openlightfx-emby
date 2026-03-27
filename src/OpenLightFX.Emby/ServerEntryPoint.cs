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
using OpenLightFX.Emby.Engine.Ai;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;
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

    // AI lighting workers, keyed by Emby session ID (parallel to _sessions)
    private readonly ConcurrentDictionary<string, AiLightingWorker> _aiWorkers = new();

    // Per-session lighting enabled toggle (EMB-152)
    private readonly ConcurrentDictionary<string, bool> _lightingEnabled = new();

    // Sessions where no track was found — avoid retrying every progress tick
    private readonly ConcurrentDictionary<string, bool> _noTrackSessions = new();

    // Playback position polling timer
    private System.Timers.Timer? _pollTimer;

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

        foreach (var session in _sessions.Values)
            session.StopAsync().GetAwaiter().GetResult();
        _sessions.Clear();

        foreach (var worker in _aiWorkers.Values)
            worker.Dispose();
        _aiWorkers.Clear();

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

            // Dispose AI worker if one is active (writes the .ailfx cache on dispose)
            if (_aiWorkers.TryRemove(sessionId, out var worker))
                worker.Dispose();

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

            // AI lighting mode: skip track discovery, use frame analysis instead
            if (options.AiLightingEnabled)
            {
                await TryStartAiLightingSession(sessionId, moviePath, item.Name ?? "Unknown", options);
                return;
            }

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

            var bulbs = _configService.ParseBulbConfig(options.BulbConfigJson);
            var profiles = _configService.ParseMappingProfiles(options.MappingProfilesJson);

            // Per-device mapping profile overrides (EMB-037)
            var profileName = options.ActiveProfileName;
            if (!string.IsNullOrEmpty(deviceId))
            {
                var overrides = _configService.ParseDeviceProfileOverrides(options.DeviceProfileOverridesJson);
                if (overrides.TryGetValue(deviceId, out var overrideName))
                {
                    // Verify the override profile exists; fall back to ActiveProfileName if not
                    if (profiles.Any(p => string.Equals(p.Name, overrideName, StringComparison.OrdinalIgnoreCase)))
                    {
                        profileName = overrideName;
                        _logger.Debug("Using device-specific profile '{0}' for device {1}", overrideName, deviceId);
                    }
                    else
                    {
                        _logger.Warn("Device override profile '{0}' not found for device {1} — falling back to '{2}'",
                            overrideName, deviceId, options.ActiveProfileName);
                    }
                }
            }

            var profile = profiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault();

            _logger.Debug("Config: {0} bulb(s), {1} profile(s), active='{2}'",
                bulbs.Count, profiles.Count, profileName);

            if (bulbs.Count == 0)
            {
                _logger.Warn("No bulbs configured — skipping lighting for '{0}'", item.Name);
                return;
            }

            if (profile == null)
            {
                _logger.Warn("No mapping profile '{0}' found — skipping lighting for '{1}'",
                    profileName, item.Name);
                return;
            }

            _logger.Debug("Profile '{0}': {1} channel mapping(s)", profile.Name, profile.Mappings.Count);

            var session = new PlaybackSession(trackInfo, bulbs, profile, options, _effectFactory, _logger);

            if (_sessions.TryAdd(sessionId, session))
            {
                await session.StartAsync();
                _logger.Info("Lighting session started for '{0}' (track: '{1}', {2} channels, {3} keyframes)",
                    item.Name, trackInfo.Title, trackInfo.ChannelCount, trackInfo.KeyframeCount);
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error starting lighting session for session {0}", ex, sessionId);
        }
    }

    // ─── Public API (accessed by REST endpoints) ──────────────────────

    public int ActiveSessionCount => _sessions.Count;
    public IEnumerable<string> ActiveSessionIds => _sessions.Keys;
    public ISessionManager SessionManager => _sessionManager;
    public TrackDiscoveryService TrackDiscoveryService => _trackDiscoveryService;
    public TrackSelectionService SelectionService => _selectionService;
    public ConfigurationService ConfigService => _configService;
    public DiscoveredBulbStore DiscoveredBulbStore => _discoveredBulbStore;
    public DiscoveryCoordinator DiscoveryCoordinator => _discoveryCoordinator;
    public IdentifyService IdentifyService => _identifyService;
    public ILibraryManager LibraryManager => _libraryManager;

    /// <summary>Get lighting enabled state for a session (EMB-152).</summary>
    public bool IsLightingEnabled(string sessionId) =>
        !_lightingEnabled.TryGetValue(sessionId, out var enabled) || enabled;

    /// <summary>Set lighting enabled state for a session (EMB-152).</summary>
    public void SetLightingEnabled(string sessionId, bool enabled) =>
        _lightingEnabled[sessionId] = enabled;

    /// <summary>Get active session info for status endpoint.</summary>
    public IEnumerable<(string SessionId, bool LightingEnabled)> GetActiveSessionInfo() =>
        _sessions.Keys.Select(id => (id, IsLightingEnabled(id)));

    private async Task TryStartAiLightingSession(
        string sessionId, string moviePath, string itemName, PluginOptions options)
    {
        _logger.Info("[AI] Starting AI lighting session: session={0}, item='{1}'", sessionId, itemName);

        var bulbs = _configService.ParseBulbConfig(options.BulbConfigJson);
        if (bulbs.Count == 0)
        {
            _logger.Warn("[AI] No bulbs configured — skipping AI lighting for '{0}'", itemName);
            _noTrackSessions.TryAdd(sessionId, true);
            return;
        }

        // Build a synthetic LightFXTrack with a single ai-ambient channel
        var syntheticTrack = new LightFXTrack { Version = 1 };
        syntheticTrack.Metadata = new TrackMetadata
        {
            Title = $"{itemName} (AI Lighting)",
            DurationMs = 1, // placeholder — V-004 requires > 0; not used for live playback
            Tags = { "ai-generated" }
        };
        syntheticTrack.Channels.Add(new Channel
        {
            Id = "ai-ambient",
            DisplayName = "AI Ambient",
            SpatialHint = "SPATIAL_AMBIENT",
            Optional = false
        });

        var trackInfo = new TrackInfo
        {
            FilePath = moviePath + ".ailfx",
            FileName = Path.GetFileNameWithoutExtension(moviePath) + ".ailfx",
            Track = syntheticTrack,
            IsValid = true
        };

        // Map all bulbs to the ai-ambient channel
        var aiProfile = new MappingProfile
        {
            Name = "AI Ambient",
            Mappings = new List<ChannelMapping>
            {
                new ChannelMapping
                {
                    ChannelId = "ai-ambient",
                    BulbIds = bulbs.Select(b => b.Id).ToList()
                }
            }
        };

        var session = new PlaybackSession(trackInfo, bulbs, aiProfile, options, _effectFactory, _logger);

        var worker = new AiLightingWorker(session, moviePath, options, _logger);
        session.OnSeek = worker.NotifySeek;

        if (_sessions.TryAdd(sessionId, session))
        {
            await session.StartAsync();

            // Try to load from cache first; only start analysis loop if cache miss
            if (!worker.TryLoadCache())
                worker.Start();

            _aiWorkers.TryAdd(sessionId, worker);
            _logger.Info("[AI] Session started for '{0}'", itemName);
        }
        else
        {
            worker.Dispose();
        }
    }
}
