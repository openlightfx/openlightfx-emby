namespace OpenLightFX.Emby.Api;

using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Services;

// ─── Request DTOs ──────────────────────────────────────────────────────

[Route("/OpenLightFX/Settings", "GET", Description = "Get all plugin settings")]
public class GetSettings : IReturn<SettingsResponse> { }

[Route("/OpenLightFX/Tracks/ByItem", "GET", Description = "Get available .lightfx tracks for a media item")]
public class GetTracksByItem : IReturn<TrackListResponse>
{
    public string ItemId { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
}

// ─── Response DTOs ─────────────────────────────────────────────────────

[DataContract]
public class SettingsResponse
{
    [DataMember(Name = "globalTimeOffsetMs")]
    [JsonPropertyName("globalTimeOffsetMs")]
    public int GlobalTimeOffsetMs { get; set; }

    [DataMember(Name = "globalBrightnessCap")]
    [JsonPropertyName("globalBrightnessCap")]
    public int GlobalBrightnessCap { get; set; } = 100;

    [DataMember(Name = "lookaheadBufferMs")]
    [JsonPropertyName("lookaheadBufferMs")]
    public int LookaheadBufferMs { get; set; } = 2000;

    [DataMember(Name = "pollIntervalMs")]
    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMs { get; set; } = 500;

    [DataMember(Name = "startBehaviorOverride")]
    [JsonPropertyName("startBehaviorOverride")]
    public string StartBehaviorOverride { get; set; } = "UseTrackDefault";

    [DataMember(Name = "endBehaviorOverride")]
    [JsonPropertyName("endBehaviorOverride")]
    public string EndBehaviorOverride { get; set; } = "UseTrackDefault";

    [DataMember(Name = "creditsBehaviorOverride")]
    [JsonPropertyName("creditsBehaviorOverride")]
    public string CreditsBehaviorOverride { get; set; } = "UseTrackDefault";

    [DataMember(Name = "preShowEnabled")]
    [JsonPropertyName("preShowEnabled")]
    public bool PreShowEnabled { get; set; } = true;

    [DataMember(Name = "photosensitivityMode")]
    [JsonPropertyName("photosensitivityMode")]
    public bool PhotosensitivityMode { get; set; }

    [DataMember(Name = "showFlashingWarnings")]
    [JsonPropertyName("showFlashingWarnings")]
    public bool ShowFlashingWarnings { get; set; } = true;

    [DataMember(Name = "additionalScanPaths")]
    [JsonPropertyName("additionalScanPaths")]
    public string AdditionalScanPaths { get; set; } = "";

    [DataMember(Name = "pluginLogLevel")]
    [JsonPropertyName("pluginLogLevel")]
    public string PluginLogLevel { get; set; } = "Info";
}

[DataContract]
public class TrackListResponse
{
    [DataMember(Name = "itemId")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [DataMember(Name = "tracks")]
    [JsonPropertyName("tracks")]
    public List<TrackSummary> Tracks { get; set; } = new();

    [DataMember(Name = "globalSelectedTrack")]
    [JsonPropertyName("globalSelectedTrack")]
    public string? GlobalSelectedTrack { get; set; }

    [DataMember(Name = "deviceSelectedTrack")]
    [JsonPropertyName("deviceSelectedTrack")]
    public string? DeviceSelectedTrack { get; set; }
}

[DataContract]
public class TrackSummary
{
    [DataMember(Name = "trackPath")]
    [JsonPropertyName("trackPath")]
    public string TrackPath { get; set; } = string.Empty;

    [DataMember(Name = "title")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "durationMs")]
    [JsonPropertyName("durationMs")]
    public ulong DurationMs { get; set; }

    [DataMember(Name = "channelCount")]
    [JsonPropertyName("channelCount")]
    public int ChannelCount { get; set; }

    [DataMember(Name = "formatVersion")]
    [JsonPropertyName("formatVersion")]
    public string FormatVersion { get; set; } = "1.0";
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
        _discoveryService = new TrackDiscoveryService(_trackParser, _logger);
        _configService = new ConfigurationService();

        var dataPath = Plugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emby");
        _selectionService = new TrackSelectionService(dataPath);
    }

    // ── GET /OpenLightFX/Settings ──────────────────────────────────────

    public object Get(GetSettings request)
    {
        var options = Plugin.Instance?.GetPluginOptions() ?? new PluginOptions();
        return MapOptionsToResponse(options);
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

    private static SettingsResponse MapOptionsToResponse(PluginOptions options)
    {
        return new SettingsResponse
        {
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
