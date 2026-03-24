namespace OpenLightFX.Emby.Api;

using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;

// ─── Request DTOs ──────────────────────────────────────────────────────

[Route("/OpenLightFX/Status", "GET", Description = "Get current OpenLightFX plugin status")]
public class GetStatus : IReturn<PluginStatusResponse> { }

[Route("/OpenLightFX/Settings", "GET", Description = "Get all plugin settings")]
public class GetSettings : IReturn<SettingsResponse> { }

[Route("/OpenLightFX/Settings", "PUT", Description = "Partial update plugin settings")]
public class UpdateSettings : IReturn<SettingsResponse>
{
    public string? BulbConfigJson { get; set; }
    public string? MappingProfilesJson { get; set; }
    public string? ActiveProfileName { get; set; }
    public string? DeviceProfileOverridesJson { get; set; }
    public int? GlobalTimeOffsetMs { get; set; }
    public int? GlobalBrightnessCap { get; set; }
    public int? LookaheadBufferMs { get; set; }
    public int? PollIntervalMs { get; set; }
    public string? StartBehaviorOverride { get; set; }
    public string? EndBehaviorOverride { get; set; }
    public string? CreditsBehaviorOverride { get; set; }
    public bool? PreShowEnabled { get; set; }
    public bool? PhotosensitivityMode { get; set; }
    public bool? ShowFlashingWarnings { get; set; }
    public string? AdditionalScanPaths { get; set; }
    public string? PluginLogLevel { get; set; }
}

[Route("/OpenLightFX/Tracks/ByItem", "GET", Description = "Get available .lightfx tracks for a media item")]
public class GetTracksByItem : IReturn<TrackListResponse>
{
    public string ItemId { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
}

[Route("/OpenLightFX/Tracks/Select", "POST", Description = "Set or clear the selected track for a media item")]
public class SelectTrack : IReturn<SelectTrackResponse>
{
    public string ItemId { get; set; } = string.Empty;
    public string? TrackPath { get; set; }
    public string? DeviceId { get; set; }
}

[Route("/OpenLightFX/Devices", "GET", Description = "List Emby devices/sessions")]
public class GetDevices : IReturn<DeviceListResponse> { }

[Route("/OpenLightFX/Bulbs/Test", "GET", Description = "Test connectivity to a configured bulb")]
public class TestBulb : IReturn<BulbTestResponse>
{
    public string BulbId { get; set; } = string.Empty;
}

[Route("/OpenLightFX/Playback/LightingEnabled", "POST", Description = "Enable or disable lighting for the active session")]
public class SetLightingEnabled : IReturn<LightingEnabledResponse>
{
    public bool Enabled { get; set; } = true;
}

// ─── Response DTOs ─────────────────────────────────────────────────────

public class PluginStatusResponse
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("activeSessions")]
    public int ActiveSessions { get; set; }

    [JsonPropertyName("configuredBulbCount")]
    public int ConfiguredBulbCount { get; set; }

    [JsonPropertyName("activeProfileName")]
    public string ActiveProfileName { get; set; } = "Default";

    [JsonPropertyName("sessions")]
    public List<SessionStatusEntry> Sessions { get; set; } = new();
}

public class SessionStatusEntry
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("lightingEnabled")]
    public bool LightingEnabled { get; set; }
}

public class SettingsResponse
{
    [JsonPropertyName("bulbConfigJson")]
    public string BulbConfigJson { get; set; } = "[]";

    [JsonPropertyName("mappingProfilesJson")]
    public string MappingProfilesJson { get; set; } = "[]";

    [JsonPropertyName("activeProfileName")]
    public string ActiveProfileName { get; set; } = "Default";

    [JsonPropertyName("deviceProfileOverridesJson")]
    public string DeviceProfileOverridesJson { get; set; } = "{}";

    [JsonPropertyName("globalTimeOffsetMs")]
    public int GlobalTimeOffsetMs { get; set; }

    [JsonPropertyName("globalBrightnessCap")]
    public int GlobalBrightnessCap { get; set; } = 100;

    [JsonPropertyName("lookaheadBufferMs")]
    public int LookaheadBufferMs { get; set; } = 2000;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMs { get; set; } = 500;

    [JsonPropertyName("startBehaviorOverride")]
    public string StartBehaviorOverride { get; set; } = "UseTrackDefault";

    [JsonPropertyName("endBehaviorOverride")]
    public string EndBehaviorOverride { get; set; } = "UseTrackDefault";

    [JsonPropertyName("creditsBehaviorOverride")]
    public string CreditsBehaviorOverride { get; set; } = "UseTrackDefault";

    [JsonPropertyName("preShowEnabled")]
    public bool PreShowEnabled { get; set; } = true;

    [JsonPropertyName("photosensitivityMode")]
    public bool PhotosensitivityMode { get; set; }

    [JsonPropertyName("showFlashingWarnings")]
    public bool ShowFlashingWarnings { get; set; } = true;

    [JsonPropertyName("additionalScanPaths")]
    public string AdditionalScanPaths { get; set; } = "";

    [JsonPropertyName("pluginLogLevel")]
    public string PluginLogLevel { get; set; } = "Info";
}

public class TrackListResponse
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("tracks")]
    public List<TrackSummary> Tracks { get; set; } = new();

    [JsonPropertyName("globalSelectedTrack")]
    public string? GlobalSelectedTrack { get; set; }

    [JsonPropertyName("deviceSelectedTrack")]
    public string? DeviceSelectedTrack { get; set; }
}

public class TrackSummary
{
    [JsonPropertyName("trackPath")]
    public string TrackPath { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public ulong DurationMs { get; set; }

    [JsonPropertyName("channelCount")]
    public int ChannelCount { get; set; }

    [JsonPropertyName("formatVersion")]
    public string FormatVersion { get; set; } = "1.0";
}

public class SelectTrackResponse
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("trackPath")]
    public string? TrackPath { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}

public class DeviceListResponse
{
    [JsonPropertyName("devices")]
    public List<DeviceEntry> Devices { get; set; } = new();
}

public class DeviceEntry
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("client")]
    public string Client { get; set; } = string.Empty;

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; }
}

public class BulbTestResponse
{
    [JsonPropertyName("bulbId")]
    public string BulbId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class LightingEnabledResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

// ─── Service ───────────────────────────────────────────────────────────

public class TrackService : IService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly TrackParser _trackParser;
    private readonly TrackDiscoveryService _discoveryService;
    private readonly TrackSelectionService _selectionService;
    private readonly ConfigurationService _configService;

    public TrackService(
        ILibraryManager libraryManager,
        ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _logger = logManager.GetLogger("OpenLightFX.Api");
        _trackParser = new TrackParser();
        _discoveryService = new TrackDiscoveryService(_trackParser);
        _configService = new ConfigurationService();

        var dataPath = Plugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emby");
        _selectionService = new TrackSelectionService(dataPath);
    }

    // ── GET /OpenLightFX/Status ────────────────────────────────────────

    public object Get(GetStatus request)
    {
        var options = Plugin.Instance?.GetPluginOptions();
        var configService = ServerEntryPoint.Instance?.ConfigService ?? _configService;

        var bulbCount = options != null
            ? configService.ParseBulbConfig(options.BulbConfigJson).Count
            : 0;

        var sessions = ServerEntryPoint.Instance?.GetActiveSessionInfo()
            .Select(s => new SessionStatusEntry
            {
                SessionId = s.SessionId,
                LightingEnabled = s.LightingEnabled
            }).ToList() ?? new List<SessionStatusEntry>();

        return new PluginStatusResponse
        {
            Version = Plugin.Instance?.GetPluginInfo().Version?.ToString() ?? "unknown",
            ActiveSessions = sessions.Count,
            ConfiguredBulbCount = bulbCount,
            ActiveProfileName = options?.ActiveProfileName ?? "Default",
            Sessions = sessions
        };
    }

    // ── GET /OpenLightFX/Settings ──────────────────────────────────────

    public object Get(GetSettings request)
    {
        var options = Plugin.Instance?.GetPluginOptions() ?? new PluginOptions();
        return MapOptionsToResponse(options);
    }

    // ── PUT /OpenLightFX/Settings ──────────────────────────────────────

    public object Put(UpdateSettings request)
    {
        try
        {
            Plugin.Instance!.UpdateOptions(options =>
            {
                if (request.BulbConfigJson != null)
                    options.BulbConfigJson = request.BulbConfigJson;

                if (request.MappingProfilesJson != null)
                    options.MappingProfilesJson = request.MappingProfilesJson;

                if (request.ActiveProfileName != null)
                    options.ActiveProfileName = request.ActiveProfileName;

                if (request.DeviceProfileOverridesJson != null)
                    options.DeviceProfileOverridesJson = request.DeviceProfileOverridesJson;

                if (request.GlobalTimeOffsetMs.HasValue)
                    options.GlobalTimeOffsetMs = request.GlobalTimeOffsetMs.Value;

                if (request.GlobalBrightnessCap.HasValue)
                {
                    var cap = request.GlobalBrightnessCap.Value;
                    if (cap < 0 || cap > 100)
                        throw new ArgumentOutOfRangeException(nameof(options.GlobalBrightnessCap),
                            "globalBrightnessCap must be between 0 and 100");
                    options.GlobalBrightnessCap = cap;
                }

                if (request.LookaheadBufferMs.HasValue)
                    options.LookaheadBufferMs = request.LookaheadBufferMs.Value;

                if (request.PollIntervalMs.HasValue)
                    options.PollIntervalMs = request.PollIntervalMs.Value;

                if (request.StartBehaviorOverride != null)
                    options.StartBehaviorOverride = Enum.Parse<BehaviorOverride>(request.StartBehaviorOverride, true);

                if (request.EndBehaviorOverride != null)
                    options.EndBehaviorOverride = Enum.Parse<BehaviorOverride>(request.EndBehaviorOverride, true);

                if (request.CreditsBehaviorOverride != null)
                    options.CreditsBehaviorOverride = Enum.Parse<CreditsBehaviorOverride>(request.CreditsBehaviorOverride, true);

                if (request.PreShowEnabled.HasValue)
                    options.PreShowEnabled = request.PreShowEnabled.Value;

                if (request.PhotosensitivityMode.HasValue)
                    options.PhotosensitivityMode = request.PhotosensitivityMode.Value;

                if (request.ShowFlashingWarnings.HasValue)
                    options.ShowFlashingWarnings = request.ShowFlashingWarnings.Value;

                if (request.AdditionalScanPaths != null)
                    options.AdditionalScanPaths = request.AdditionalScanPaths;

                if (request.PluginLogLevel != null)
                    options.PluginLogLevel = Enum.Parse<PluginLogLevel>(request.PluginLogLevel, true);
            });

            var updated = Plugin.Instance.GetPluginOptions();
            return MapOptionsToResponse(updated);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error updating settings", ex);
            throw;
        }
    }

    // ── GET /OpenLightFX/Tracks/ByItem ─────────────────────────────────

    public object Get(GetTracksByItem request)
    {
        try
        {
            var item = _libraryManager.GetItemById(long.Parse(request.ItemId));
            if (item == null)
                return new TrackListResponse { ItemId = request.ItemId };

            string? imdbId = null;
            if (item.ProviderIds?.TryGetValue("Imdb", out var id) == true)
                imdbId = id;

            var options = Plugin.Instance?.GetPluginOptions();
            var scanPaths = options != null
                ? _configService.GetScanPaths(options.AdditionalScanPaths)
                : Enumerable.Empty<string>();

            var tracks = _discoveryService.DiscoverTracks(item.Path, imdbId, scanPaths);

            var selectionService = ServerEntryPoint.Instance?.SelectionService ?? _selectionService;
            var globalSelected = selectionService.GetGlobalSelectedTrack(request.ItemId);
            string? deviceSelected = null;
            if (!string.IsNullOrEmpty(request.DeviceId))
                deviceSelected = selectionService.GetDeviceSelectedTrack(request.ItemId, request.DeviceId);

            return new TrackListResponse
            {
                ItemId = request.ItemId,
                GlobalSelectedTrack = globalSelected,
                DeviceSelectedTrack = deviceSelected,
                Tracks = tracks.Select(t => new TrackSummary
                {
                    TrackPath = t.FilePath,
                    Title = t.Title,
                    DurationMs = t.DurationMs,
                    ChannelCount = t.ChannelCount,
                    FormatVersion = t.Track.Metadata?.TrackVersion is { Length: > 0 } tv
                        ? tv
                        : (t.Track.Version > 0 ? t.Track.Version.ToString() : "1.0")
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error getting tracks for item {0}", ex, request.ItemId);
            return new TrackListResponse { ItemId = request.ItemId };
        }
    }

    // ── POST /OpenLightFX/Tracks/Select ────────────────────────────────

    public object Post(SelectTrack request)
    {
        try
        {
            var selectionService = ServerEntryPoint.Instance?.SelectionService ?? _selectionService;

            if (string.IsNullOrEmpty(request.TrackPath))
            {
                selectionService.ClearSelectedTrack(request.ItemId, request.DeviceId);
            }
            else
            {
                if (!File.Exists(request.TrackPath))
                    throw new FileNotFoundException($"Track file not found: {request.TrackPath}");

                selectionService.SetSelectedTrack(request.ItemId, request.TrackPath, request.DeviceId);
            }

            return new SelectTrackResponse
            {
                ItemId = request.ItemId,
                TrackPath = request.TrackPath,
                DeviceId = request.DeviceId
            };
        }
        catch (FileNotFoundException ex)
        {
            _logger.ErrorException("Track file not found for item {0}", ex, request.ItemId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error selecting track for item {0}", ex, request.ItemId);
            throw;
        }
    }

    // ── GET /OpenLightFX/Devices ───────────────────────────────────────

    public object Get(GetDevices request)
    {
        try
        {
            var sessionManager = ServerEntryPoint.Instance?.SessionManager;
            if (sessionManager == null)
                return new DeviceListResponse();

            var sessions = sessionManager.Sessions;
            var devices = new List<DeviceEntry>();
            var seen = new HashSet<string>();

            foreach (var session in sessions)
            {
                var deviceId = session.DeviceId;
                if (string.IsNullOrEmpty(deviceId) || !seen.Add(deviceId))
                    continue;

                devices.Add(new DeviceEntry
                {
                    DeviceId = deviceId,
                    DeviceName = session.DeviceName ?? "",
                    Client = session.Client ?? "",
                    LastSeen = session.LastActivityDate.UtcDateTime
                });
            }

            return new DeviceListResponse { Devices = devices };
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error listing devices", ex);
            return new DeviceListResponse();
        }
    }

    // ── GET /OpenLightFX/Bulbs/Test ────────────────────────────────────

    public async Task<object> Get(TestBulb request)
    {
        try
        {
            var options = Plugin.Instance?.GetPluginOptions();
            if (options == null)
                return new BulbTestResponse { BulbId = request.BulbId, Success = false, Error = "Plugin not configured" };

            var bulbs = _configService.ParseBulbConfig(options.BulbConfigJson);
            var bulbConfig = bulbs.FirstOrDefault(b => b.Id == request.BulbId);

            if (bulbConfig == null)
                return new BulbTestResponse { BulbId = request.BulbId, Success = false, Error = "Bulb not found" };

            await using var driver = BulbDriverFactory.Create(bulbConfig);
            var success = await driver.TestConnection();

            return new BulbTestResponse { BulbId = request.BulbId, Success = success };
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error testing bulb {0}", ex, request.BulbId);
            return new BulbTestResponse { BulbId = request.BulbId, Success = false, Error = ex.Message };
        }
    }

    // ── POST /OpenLightFX/Playback/LightingEnabled ─────────────────────

    public object Post(SetLightingEnabled request)
    {
        var entry = ServerEntryPoint.Instance;
        if (entry != null)
        {
            foreach (var sessionId in entry.ActiveSessionIds)
            {
                entry.SetLightingEnabled(sessionId, request.Enabled);
            }
        }

        return new LightingEnabledResponse { Enabled = request.Enabled };
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static SettingsResponse MapOptionsToResponse(PluginOptions options)
    {
        return new SettingsResponse
        {
            BulbConfigJson = options.BulbConfigJson,
            MappingProfilesJson = options.MappingProfilesJson,
            ActiveProfileName = options.ActiveProfileName,
            DeviceProfileOverridesJson = options.DeviceProfileOverridesJson,
            GlobalTimeOffsetMs = options.GlobalTimeOffsetMs,
            GlobalBrightnessCap = options.GlobalBrightnessCap,
            LookaheadBufferMs = options.LookaheadBufferMs,
            PollIntervalMs = options.PollIntervalMs,
            StartBehaviorOverride = options.StartBehaviorOverride.ToString(),
            EndBehaviorOverride = options.EndBehaviorOverride.ToString(),
            CreditsBehaviorOverride = options.CreditsBehaviorOverride.ToString(),
            PreShowEnabled = options.PreShowEnabled,
            PhotosensitivityMode = options.PhotosensitivityMode,
            ShowFlashingWarnings = options.ShowFlashingWarnings,
            AdditionalScanPaths = options.AdditionalScanPaths,
            PluginLogLevel = options.PluginLogLevel.ToString()
        };
    }
}
