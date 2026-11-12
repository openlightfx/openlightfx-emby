namespace OpenLightFX.Emby.Api;

using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Services;
using OpenLightFX.Emby.Studio;

// ─── Requests ──────────────────────────────────────────────────────────

[Route("/OpenLightFX/Studio/Bulbs", "GET", Description = "Configured bulbs and capability profiles for Studio")]
[Authenticated]
public class GetStudioBulbs : IReturn<List<StudioBulbResponse>>
{
}

[Route("/OpenLightFX/Studio/Items", "GET", Description = "Search Emby movies for Studio")]
[Authenticated]
public class GetStudioItems : IReturn<List<StudioItemResponse>>
{
    public string? Search { get; set; }
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/StreamUrl", "GET", Description = "Browser-playable video URL for a movie")]
[Authenticated]
public class GetStudioStreamUrl : IReturn<StudioStreamUrlResponse>
{
    public string ItemId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Tracks", "GET", Description = "List .lightfx tracks Studio can edit for a movie")]
[Authenticated]
public class GetStudioTracks : IReturn<List<StudioTrackResponse>>
{
    public string? ItemId { get; set; }
}

[Route("/OpenLightFX/Studio/Tracks/{TrackId}", "GET", Description = "Download a .lightfx track")]
[Authenticated]
public class GetStudioTrack : IReturnVoid
{
    public string TrackId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Tracks/{TrackId}", "PUT", Description = "Validate and save a .lightfx track")]
[Authenticated(Roles = "Admin")]
public class PutStudioTrack : IRequiresRequestStream, IReturn<StudioTrackResponse>
{
    public string TrackId { get; set; } = "";
    public string? ItemId { get; set; }
    public Stream? RequestStream { get; set; }
}

[Route("/OpenLightFX/Studio/Projects/{TrackId}", "GET", Description = "Read the Studio project sidecar")]
[Authenticated]
public class GetStudioProject
{
    public string TrackId { get; set; } = "";
    public string? ItemId { get; set; }
}

[Route("/OpenLightFX/Studio/Projects/{TrackId}", "PUT", Description = "Store the Studio project sidecar")]
[Authenticated(Roles = "Admin")]
public class PutStudioProject : IRequiresRequestStream
{
    public string TrackId { get; set; } = "";
    public string? ItemId { get; set; }
    public Stream? RequestStream { get; set; }
}

[Route("/OpenLightFX/Studio/Preview/Start", "POST", Description = "Start a live bulb preview")]
[Authenticated(Roles = "Admin")]
public class StartStudioPreview : IReturn<StudioPreviewResponse>
{
    public Dictionary<string, List<string>>? Mapping { get; set; }
    public bool? Takeover { get; set; }
}

[Route("/OpenLightFX/Studio/Preview/Frame", "POST", Description = "Send one coalesced preview frame")]
[Authenticated(Roles = "Admin")]
public class PostStudioPreviewFrame : IReturn<StudioPreviewResponse>
{
    public List<StudioPreviewStateDto>? States { get; set; }
}

[Route("/OpenLightFX/Studio/Preview/Stop", "POST", Description = "Stop preview and restore bulbs")]
[Authenticated(Roles = "Admin")]
public class StopStudioPreview : IReturn<StudioPreviewResponse>
{
}

// ─── Responses ─────────────────────────────────────────────────────────

[DataContract]
public class StudioBulbResponse
{
    [DataMember(Name = "id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [DataMember(Name = "name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [DataMember(Name = "protocol")]
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [DataMember(Name = "capabilities")]
    [JsonPropertyName("capabilities")]
    public StudioBulbCapabilitiesResponse Capabilities { get; set; } = new();
}

[DataContract]
public class StudioBulbCapabilitiesResponse
{
    [DataMember(Name = "rgb")]
    [JsonPropertyName("rgb")]
    public bool Rgb { get; set; }

    [DataMember(Name = "minTransitionMs")]
    [JsonPropertyName("minTransitionMs")]
    public int MinTransitionMs { get; set; }
}

[DataContract]
public class StudioItemResponse
{
    [DataMember(Name = "itemId")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = "";

    [DataMember(Name = "name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [DataMember(Name = "year")]
    [JsonPropertyName("year")]
    public int Year { get; set; }

    [DataMember(Name = "runtimeMs")]
    [JsonPropertyName("runtimeMs")]
    public long RuntimeMs { get; set; }
}

[DataContract]
public class StudioStreamUrlResponse
{
    [DataMember(Name = "url")]
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

[DataContract]
public class StudioTrackResponse
{
    [DataMember(Name = "trackId")]
    [JsonPropertyName("trackId")]
    public string TrackId { get; set; } = "";

    [DataMember(Name = "title")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [DataMember(Name = "itemId")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = "";

    [DataMember(Name = "updatedAt")]
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}

[DataContract]
public class StudioPreviewStateDto
{
    [DataMember(Name = "bulbId")]
    [JsonPropertyName("bulbId")]
    public string BulbId { get; set; } = "";

    [DataMember(Name = "r")]
    [JsonPropertyName("r")]
    public int R { get; set; }

    [DataMember(Name = "g")]
    [JsonPropertyName("g")]
    public int G { get; set; }

    [DataMember(Name = "b")]
    [JsonPropertyName("b")]
    public int B { get; set; }

    [DataMember(Name = "brightness")]
    [JsonPropertyName("brightness")]
    public int Brightness { get; set; }

    [DataMember(Name = "transitionMs")]
    [JsonPropertyName("transitionMs")]
    public int TransitionMs { get; set; }
}

[DataContract]
public class StudioPreviewResponse
{
    [DataMember(Name = "status")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>False after an idle restore so the next frame is told preview is not active.</summary>
    [DataMember(Name = "active")]
    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;
}

// ─── Service ───────────────────────────────────────────────────────────

public class StudioService : IService, IRequiresRequest
{
    private const int ItemSearchLimit = 200;

    private readonly ILibraryManager _library;
    private readonly ILogger _logger;
    private readonly IHttpResultFactory _results;
    private readonly TrackParser _parser = new();
    private readonly ConfigurationService _config = new();

    public IRequest Request { get; set; } = null!;

    public StudioService(ILibraryManager library, ILogManager logManager, IHttpResultFactory results)
    {
        _library = library;
        _logger = logManager.GetLogger("OpenLightFX.Studio");
        _results = results;
    }

    public async Task<object> Get(GetStudioBulbs request)
    {
        var bulbs = LoadBulbs();
        var response = new List<StudioBulbResponse>(bulbs.Count);
        foreach (var bulb in bulbs)
        {
            var caps = await StudioBulbCapabilities.ReadAsync(bulb).ConfigureAwait(false);
            response.Add(new StudioBulbResponse
            {
                Id = bulb.Id,
                Name = string.IsNullOrEmpty(bulb.Name) ? bulb.Id : bulb.Name,
                Protocol = bulb.Protocol.ToString(),
                Capabilities = new StudioBulbCapabilitiesResponse
                {
                    Rgb = caps.SupportsRgb,
                    MinTransitionMs = (int)caps.MinTransitionMs
                }
            });
        }

        return response;
    }

    public object Get(GetStudioItems request)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie" },
            Recursive = true,
            IsVirtualItem = false,
            Limit = ItemSearchLimit
        };

        if (!string.IsNullOrWhiteSpace(request.Search))
            query.SearchTerm = request.Search.Trim();

        var items = _library.GetItemList(query);
        return items
            .Where(item => !string.IsNullOrEmpty(item.Path))
            .Select(item => new StudioItemResponse
            {
                ItemId = PublicItemId(item),
                Name = item.Name ?? "",
                Year = item.ProductionYear ?? 0,
                RuntimeMs = item.RunTimeTicks is long ticks
                    ? ticks / TimeSpan.TicksPerMillisecond
                    : 0
            })
            .ToList();
    }

    public object Get(GetStudioStreamUrl request)
    {
        var item = RequireItem(request.ItemId);
        string? mediaSourceId = null;
        try
        {
            mediaSourceId = item.GetDefaultMediaSourceId();
        }
        catch (Exception ex)
        {
            _logger.Debug("No media source id for item {0}: {1}", request.ItemId, ex.Message);
        }

        var origin = StudioStreamUrls.FromAbsoluteUri(Request?.AbsoluteUri);
        var url = StudioStreamUrls.Build(
            origin,
            new StudioMediaInfo(
                PublicItemId(item),
                item.Container,
                item.VideoCodec,
                item.AudioCodec,
                mediaSourceId),
            StudioStreamUrls.ReadAccessToken(Request));

        return new StudioStreamUrlResponse { Url = url };
    }

    public object Get(GetStudioTracks request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
            throw new ArgumentException("itemId is required");

        var item = RequireItem(request.ItemId);
        if (string.IsNullOrEmpty(item.Path))
            return new List<StudioTrackResponse>();

        var listed = Discover(item);
        IndexOrNull()?.RememberAll(listed);
        return listed.Select(ToResponse).ToList();
    }

    private static StudioTrackResponse ToResponse(StudioListedTrack track) => new()
    {
        TrackId = track.TrackId,
        Title = track.Title,
        ItemId = track.ItemId,
        UpdatedAt = track.UpdatedAt.UtcDateTime.ToString("o")
    };

    public object Get(GetStudioTrack request)
    {
        if (!StudioTrackFiles.IsSafeTrackId(request.TrackId))
            throw new ArgumentException("trackId contains unsupported characters");

        var bytes = ReadTrackBytes(request.TrackId);
        if (bytes == null)
            throw new ResourceNotFoundException("Track not found");

        return _results.GetResult(
            Request,
            new ReadOnlyMemory<byte>(bytes),
            "application/octet-stream",
            new Dictionary<string, string> { ["Cache-Control"] = "no-cache" });
    }

    public async Task<object> Put(PutStudioTrack request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
            throw new ArgumentException("itemId is required");
        if (!StudioTrackFiles.IsSafeTrackId(request.TrackId))
            throw new ArgumentException("trackId contains unsupported characters");

        var item = RequireItem(request.ItemId);
        if (string.IsNullOrEmpty(item.Path))
            throw new ArgumentException("Item has no file path to store a light track beside");

        var bytes = await ReadBodyAsync(request.RequestStream).ConfigureAwait(false);
        var info = StudioTrackFiles.ParseForSave(_parser, bytes, request.TrackId);
        var path = StudioTrackFiles.GetSavePath(item.Path, request.TrackId);
        try
        {
            StudioTrackFiles.EnsureNoConflict(path, request.TrackId, Request?.Headers?["If-Match"]);
        }
        catch (TrackSaveConflictException ex)
        {
            throw new ConflictException(ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Could not save track: {ex.Message}");
        }

        ServerEntryPoint.Instance?.TrackDiscoveryService.InvalidateCache(item.Path);
        var itemId = PublicItemId(item);
        var updated = StudioTrackFiles.UpdatedAtToken(path);
        IndexOrNull()?.Remember(request.TrackId, itemId, path);

        _logger.Info("Saved Studio track '{0}' for item {1} at '{2}'", request.TrackId, itemId, path);
        return new StudioTrackResponse
        {
            TrackId = request.TrackId,
            Title = info.Title,
            ItemId = itemId,
            UpdatedAt = updated
        };
    }

    public object Get(GetStudioProject request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
            throw new ArgumentException("itemId is required");
        if (!StudioTrackFiles.IsSafeTrackId(request.TrackId))
            throw new ArgumentException("trackId contains unsupported characters");

        var item = RequireItem(request.ItemId);
        if (string.IsNullOrEmpty(item.Path))
            throw new ResourceNotFoundException("Item has no file path");

        var path = StudioSidecar.GetPath(item.Path, request.TrackId);
        if (!File.Exists(path))
            throw new ResourceNotFoundException("Project sidecar not found");

        var bytes = File.ReadAllBytes(path);
        return JsonResult(bytes);
    }

    public async Task<object> Put(PutStudioProject request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
            throw new ArgumentException("itemId is required");
        if (!StudioTrackFiles.IsSafeTrackId(request.TrackId))
            throw new ArgumentException("trackId contains unsupported characters");

        var item = RequireItem(request.ItemId);
        if (string.IsNullOrEmpty(item.Path))
            throw new ArgumentException("Item has no file path to store a project sidecar beside");

        var raw = await ReadLimitedAsync(request.RequestStream, StudioSidecar.MaxBytes, "Project body exceeds 4 MB")
            .ConfigureAwait(false);
        var bytes = StudioSidecar.Normalize(raw);
        var path = StudioSidecar.GetPath(item.Path, request.TrackId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Could not save project: {ex.Message}");
        }

        _logger.Info("Saved Studio sidecar '{0}' for item {1}", request.TrackId, PublicItemId(item));
        return JsonResult(bytes);
    }

    private object JsonResult(byte[] json) =>
        _results.GetResult(
            Request,
            new ReadOnlyMemory<byte>(json),
            "application/json; charset=utf-8",
            new Dictionary<string, string> { ["Cache-Control"] = "no-cache" });

    public async Task<object> Post(StartStudioPreview request)
    {
        var entry = RequireEntry();
        var mapping = (IReadOnlyDictionary<string, IReadOnlyList<string>>)(request.Mapping ?? new Dictionary<string, List<string>>())
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);

        if (request.Takeover == true)
            await entry.SuspendPlaybackLightingAsync().ConfigureAwait(false);

        var started = await entry.Preview.TryStartAsync(
            () => entry.ActiveSessionCount > 0,
            request.Takeover == true,
            mapping,
            LoadBulbs(),
            PhotosensitivityEnabled(),
            BulbDriverFactory.Create,
            DateTimeOffset.UtcNow).ConfigureAwait(false);

        if (!started)
            throw new ConflictException("playback_active");

        RememberMapping(mapping);
        _logger.Info("Studio preview started");
        return new StudioPreviewResponse { Status = "started", Active = true };
    }

    public async Task<object> Post(PostStudioPreviewFrame request)
    {
        var entry = RequireEntry();
        var samples = (request.States ?? new List<StudioPreviewStateDto>())
            .Select(state => new StudioPreviewSample(
                state.BulbId, state.R, state.G, state.B, state.Brightness, state.TransitionMs))
            .ToList();

        try
        {
            await entry.Preview.SubmitFrameAsync(samples, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (StudioPreviewNotActiveException)
        {
            throw new ConflictException("Preview is not active");
        }

        return new StudioPreviewResponse { Status = "ok" };
    }

    public async Task<object> Post(StopStudioPreview request)
    {
        var entry = RequireEntry();
        await entry.Preview.StopAsync().ConfigureAwait(false);
        _logger.Info("Studio preview stopped");
        return new StudioPreviewResponse { Status = "stopped" };
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private ServerEntryPoint RequireEntry() =>
        ServerEntryPoint.Instance ?? throw new ServiceUnavailableException("OpenLightFX is not running");

    private StudioTrackIndex? IndexOrNull() => ServerEntryPoint.Instance?.StudioTrackIndex;

    private BaseItem RequireItem(string itemId)
    {
        var item = FindItem(itemId);
        if (item == null)
            throw new ResourceNotFoundException("Item not found");
        return item;
    }

    private BaseItem? FindItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        if (long.TryParse(itemId, out var internalId))
        {
            var byInternal = _library.GetItemById(internalId);
            if (byInternal != null)
                return byInternal;
        }

        if (Guid.TryParse(itemId, out var guid))
            return _library.GetItemById(guid);

        return null;
    }

    private static string PublicItemId(BaseItem item) =>
        item.InternalId > 0 ? item.InternalId.ToString() : item.Id.ToString("N");

    private List<Models.BulbConfig> LoadBulbs()
    {
        var options = Plugin.Instance?.GetPluginOptions();
        if (options == null)
            return new List<Models.BulbConfig>();
        return _config.ParseBulbConfig(options.BulbConfigJson);
    }

    private bool PhotosensitivityEnabled() =>
        Plugin.Instance?.GetPluginOptions().PhotosensitivityMode == true;

    private static void RememberMapping(IReadOnlyDictionary<string, IReadOnlyList<string>> mapping)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || mapping.Count == 0)
            return;

        plugin.UpdateOptions(options =>
        {
            options.MappingProfilesJson = StudioMappingProfiles.WithChannels(
                options.MappingProfilesJson,
                options.ActiveProfileName,
                mapping);
        });
    }

    private List<StudioListedTrack> Discover(BaseItem item)
    {
        string? imdbId = null;
        item.ProviderIds?.TryGetValue("Imdb", out imdbId);

        var options = Plugin.Instance?.GetPluginOptions();
        var scanPaths = options != null
            ? _config.GetScanPaths(options.AdditionalScanPaths)
            : Enumerable.Empty<string>();

        var discovery = ServerEntryPoint.Instance?.TrackDiscoveryService
            ?? new TrackDiscoveryService(_parser, _logger);
        discovery.InvalidateCache(item.Path);
        var tracks = discovery.DiscoverTracks(item.Path, imdbId, scanPaths);
        return StudioTrackFiles.ToListItems(PublicItemId(item), tracks).ToList();
    }

    private byte[]? ReadTrackBytes(string trackId)
    {
        var index = IndexOrNull();
        if (index != null && index.TryGet(trackId, out var entry) && File.Exists(entry.FilePath))
            return File.ReadAllBytes(entry.FilePath);

        var library = ServerEntryPoint.Instance?.LibraryManager ?? _library;
        var movies = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie" },
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var movie in movies)
        {
            if (string.IsNullOrEmpty(movie.Path))
                continue;

            var found = StudioTrackFiles.FindFile(movie.Path, trackId);
            if (found == null)
                continue;

            index?.Remember(trackId, PublicItemId(movie), found);
            return File.ReadAllBytes(found);
        }

        return null;
    }

    private static Task<byte[]> ReadBodyAsync(Stream? stream) =>
        ReadLimitedAsync(stream, StudioTrackFiles.MaxTrackBytes, "V-001: Track body exceeds 32 MB");

    private static async Task<byte[]> ReadLimitedAsync(Stream? stream, int maxBytes, string tooLarge)
    {
        if (stream == null)
            throw new ArgumentException("V-001: Track body is empty");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maxBytes)
                throw new ArgumentException(tooLarge);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
