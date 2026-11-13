namespace OpenLightFX.Emby.Api;

using System.Globalization;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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

[Route("/OpenLightFX/Studio/Items/{ItemId}/MediaInfo", "GET", Description = "Movie timing, HDR, and edit-proxy progress")]
[Authenticated]
public class GetStudioMediaInfo
{
    public string ItemId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/Frame", "GET", Description = "One sRGB PNG frame for the eyedropper")]
[Authenticated(Roles = "Admin")]
public class GetStudioFrame
{
    public string ItemId { get; set; } = "";
    public long? TimeMs { get; set; }
    public int? MaxWidth { get; set; }
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/Proxy", "POST", Description = "Start the H.264 edit proxy")]
[Authenticated(Roles = "Admin")]
public class PostStudioProxy
{
    public string ItemId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/Proxy/Media", "GET", Description = "Direct-play the edit proxy")]
[Authenticated]
public class GetStudioProxyMedia
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
public class StudioMediaInfoResponse
{
    [DataMember(Name = "runtimeMs")]
    public long RuntimeMs { get; set; }

    [DataMember(Name = "width")]
    public int Width { get; set; }

    [DataMember(Name = "height")]
    public int Height { get; set; }

    [DataMember(Name = "videoCodec")]
    public string VideoCodec { get; set; } = "";

    [DataMember(Name = "hdr")]
    public bool Hdr { get; set; }

    [DataMember(Name = "colorTransfer")]
    public string ColorTransfer { get; set; } = "";

    [DataMember(Name = "audioCodec")]
    public string AudioCodec { get; set; } = "";

    [DataMember(Name = "proxy")]
    public StudioProxyStatusResponse Proxy { get; set; } = new();
}

[DataContract]
public class StudioProxyStatusResponse
{
    [DataMember(Name = "state")]
    public string State { get; set; } = "missing";

    [DataMember(Name = "encodedThroughMs")]
    public long EncodedThroughMs { get; set; }

    [DataMember(Name = "fps")]
    public double Fps { get; set; }

    [DataMember(Name = "keyframeIntervalMs")]
    public int KeyframeIntervalMs { get; set; }

    [DataMember(Name = "width")]
    public int Width { get; set; }

    [DataMember(Name = "height")]
    public int Height { get; set; }

    [DataMember(Name = "editUrl")]
    public string? EditUrl { get; set; }
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
    private readonly IFfmpegManager _ffmpeg;
    private readonly TrackParser _parser = new();
    private readonly ConfigurationService _config = new();

    public IRequest Request { get; set; } = null!;

    public StudioService(ILibraryManager library, ILogManager logManager, IHttpResultFactory results, IFfmpegManager ffmpeg)
    {
        _library = library;
        _logger = logManager.GetLogger("OpenLightFX.Studio");
        _results = results;
        _ffmpeg = ffmpeg;
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

    public async Task<object> Get(GetStudioMediaInfo request)
    {
        var item = RequireItem(request.ItemId);
        var source = await ProbeAsync(item).ConfigureAwait(false);
        var job = EnsureProxy(item, source);
        return ToMediaInfo(item, source, job);
    }

    public async Task<object> Post(PostStudioProxy request)
    {
        var item = RequireItem(request.ItemId);
        var source = await ProbeAsync(item).ConfigureAwait(false);
        var job = EnsureProxy(item, source);
        return new StudioProxyStatusResponse
        {
            State = job.State,
            EncodedThroughMs = job.EncodedThroughMs,
            Fps = job.Fps,
            KeyframeIntervalMs = job.KeyframeIntervalMs,
            Width = job.Width,
            Height = job.Height,
            EditUrl = EditUrl(PublicItemId(item))
        };
    }

    public async Task<object> Get(GetStudioFrame request)
    {
        if (request.TimeMs is not long timeMs)
            throw new ArgumentException("timeMs is required");
        if (timeMs < 0)
            throw new ArgumentException("timeMs is past the runtime");

        var item = RequireItem(request.ItemId);
        if (string.IsNullOrEmpty(item.Path))
            throw new ArgumentException("Item has no media file");

        var source = await ProbeAsync(item).ConfigureAwait(false);
        if (source.RuntimeMs > 0 && timeMs > source.RuntimeMs)
            throw new ArgumentException("timeMs is past the runtime");

        var ffmpeg = RequireTool(FfmpegPath(), "ffmpeg");
        var zscale = source.Hdr && await HasZscaleAsync(ffmpeg).ConfigureAwait(false);
        var arguments = StudioFramePlan.Arguments(item.Path, timeMs, source.Hdr, source.ColorTransfer, request.MaxWidth, zscale);
        var (code, png, log) = await StudioProcess.RunAsync(ffmpeg, arguments, CancellationToken.None).ConfigureAwait(false);
        if (code != 0 || png.Length < 8 || png[0] != 0x89)
            throw new ServiceUnavailableException("Frame extraction failed");

        var actual = StudioFramePlan.SourceTimeMs(log, timeMs);
        return _results.GetResult(
            Request,
            new ReadOnlyMemory<byte>(png),
            "image/png",
            new Dictionary<string, string>
            {
                ["X-OpenLightFX-Tone-Map"] = StudioFramePlan.ToneMap(source.Hdr),
                ["X-OpenLightFX-Source-Time-Ms"] = actual.ToString(CultureInfo.InvariantCulture),
                ["Cache-Control"] = "no-cache"
            });
    }

    public async Task<object> Get(GetStudioProxyMedia request)
    {
        var item = RequireItem(request.ItemId);
        var source = await ProbeAsync(item).ConfigureAwait(false);
        var job = EnsureProxy(item, source);
        var range = Request?.Headers?["Range"];
        var length = await WaitForRangeAsync(job, range).ConfigureAwait(false);
        var slice = StudioByteRange.Resolve(range, length);
        if (slice.Status == 416)
        {
            var text = Encoding.UTF8.GetBytes($"edit copy has reached {job.EncodedThroughMs} ms");
            var denied = _results.GetResult(Request, new ReadOnlyMemory<byte>(text), "text/plain", new Dictionary<string, string>
            {
                ["Accept-Ranges"] = "bytes"
            });
            return WithStatus(denied, HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var count = length == 0 ? 0 : (int)(slice.End - slice.Start + 1);
        var bytes = new byte[Math.Max(count, 0)];
        if (count > 0)
        {
            using var stream = new FileStream(job.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(slice.Start, SeekOrigin.Begin);
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            if (read < bytes.Length)
                Array.Resize(ref bytes, read);
        }

        var headers = new Dictionary<string, string>
        {
            ["Accept-Ranges"] = "bytes",
            ["Cache-Control"] = "no-cache"
        };
        if (slice.Status == 206 && bytes.Length > 0)
            headers["Content-Range"] = $"bytes {slice.Start}-{slice.Start + bytes.Length - 1}/{length}";

        var result = _results.GetResult(Request, new ReadOnlyMemory<byte>(bytes), "video/mp4", headers);
        return slice.Status == 206 ? WithStatus(result, HttpStatusCode.PartialContent) : result;
    }

    /// <summary>
    /// GetResult over a byte buffer is not an IHttpResult, so setting the status on it
    /// was skipped and ranged reads went out as 200. Set it on the response as well.
    /// </summary>
    private object WithStatus(object result, HttpStatusCode status)
    {
        if (result is IHttpResult http)
        {
            http.StatusCode = status;
            http.Status = (int)status;
        }
        if (Request?.Response != null)
            Request.Response.StatusCode = (int)status;
        return result;
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
            StudioTrackFiles.EnsureNoConflict(path, Request?.Headers?["If-Match"]);
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

    private async Task<StudioSourceInfo> ProbeAsync(BaseItem item)
    {
        var itemRuntime = item.RunTimeTicks is long ticks && ticks > 0
            ? ticks / TimeSpan.TicksPerMillisecond
            : 0;
        var fallback = new StudioSourceInfo(
            itemRuntime,
            item.Width,
            item.Height,
            item.VideoCodec ?? "",
            item.AudioCodec ?? "",
            false,
            "",
            24);

        if (string.IsNullOrEmpty(item.Path))
            return fallback;

        var ffprobe = FfprobePath();
        if (string.IsNullOrEmpty(ffprobe) || !File.Exists(ffprobe))
            return fallback;

        var (code, stdout, _) = await StudioProcess.RunAsync(
            ffprobe,
            new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", item.Path },
            CancellationToken.None).ConfigureAwait(false);
        if (code != 0 || stdout.Length == 0)
            return fallback;

        var parsed = StudioFfprobe.Parse(Encoding.UTF8.GetString(stdout), itemRuntime);
        return parsed with
        {
            Width = parsed.Width > 0 ? parsed.Width : item.Width,
            Height = parsed.Height > 0 ? parsed.Height : item.Height,
            VideoCodec = parsed.VideoCodec.Length > 0 ? parsed.VideoCodec : item.VideoCodec ?? "",
            AudioCodec = parsed.AudioCodec.Length > 0 ? parsed.AudioCodec : item.AudioCodec ?? ""
        };
    }

    private StudioProxyJob EnsureProxy(BaseItem item, StudioSourceInfo source)
    {
        var itemId = PublicItemId(item);
        return StudioProxyCache.GetOrAdd(itemId, () =>
        {
            var directory = Path.Combine(
                Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(),
                "studio-proxy");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, SafeFileToken(itemId) + ".mp4");
            var (width, height) = OutputSize(source.Width, source.Height);
            var job = new StudioProxyJob(itemId, output, source.Fps, width, height);
            if (File.Exists(output) && new FileInfo(output).Length > 0)
            {
                job.MarkReady(source.RuntimeMs);
                return job;
            }

            if (string.IsNullOrEmpty(item.Path))
            {
                job.MarkFailed();
                return job;
            }

            try
            {
                job.Start(RequireTool(FfmpegPath(), "ffmpeg"), StudioProxyPlan.Arguments(item.Path, output, source.Fps));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Edit proxy failed to start for {0}", ex, itemId);
                job.MarkFailed();
            }
            return job;
        });
    }

    private StudioMediaInfoResponse ToMediaInfo(BaseItem item, StudioSourceInfo source, StudioProxyJob job) => new()
    {
        RuntimeMs = source.RuntimeMs,
        Width = source.Width,
        Height = source.Height,
        VideoCodec = source.VideoCodec,
        Hdr = source.Hdr,
        ColorTransfer = source.ColorTransfer,
        AudioCodec = source.AudioCodec,
        Proxy = new StudioProxyStatusResponse
        {
            State = job.State == "failed" ? "building" : job.State,
            EncodedThroughMs = job.EncodedThroughMs,
            Fps = job.Fps,
            KeyframeIntervalMs = job.KeyframeIntervalMs,
            Width = job.Width,
            Height = job.Height,
            EditUrl = EditUrl(PublicItemId(item))
        }
    };

    private string EditUrl(string itemId) =>
        StudioEditUrls.ProxyMedia(Request?.AbsoluteUri, itemId, StudioStreamUrls.ReadAccessToken(Request));

    private string? FfmpegPath() => _ffmpeg.FfmpegConfiguration?.EncoderPath;

    private string? FfprobePath()
    {
        // DetectPath is Emby's ffdetect, not ffprobe; running it with ffprobe arguments
        // fails and every probe silently fell back to Emby's item fields.
        var probe = _ffmpeg.FfmpegConfiguration?.ProbePath;
        if (!string.IsNullOrEmpty(probe) && File.Exists(probe))
            return probe;
        var ffmpeg = FfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg))
            return probe;
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe");
        return File.Exists(sibling) ? sibling : probe;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ZscaleByFfmpeg = new();

    private static async Task<bool> HasZscaleAsync(string ffmpeg)
    {
        if (ZscaleByFfmpeg.TryGetValue(ffmpeg, out var known))
            return known;
        var (code, stdout, _) = await StudioProcess.RunAsync(
            ffmpeg, new[] { "-hide_banner", "-filters" }, CancellationToken.None).ConfigureAwait(false);
        var has = code == 0 && StudioFramePlan.HasZscale(Encoding.UTF8.GetString(stdout));
        ZscaleByFfmpeg[ffmpeg] = has;
        return has;
    }

    private static string RequireTool(string? path, string name)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new ServiceUnavailableException($"Emby {name} is not available");
        return path;
    }

    private static async Task<long> WaitForRangeAsync(StudioProxyJob job, string? range)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        long length = 0;
        while (true)
        {
            length = File.Exists(job.OutputPath) ? new FileInfo(job.OutputPath).Length : 0;
            var slice = StudioByteRange.Resolve(range, length);
            // A malformed range never becomes satisfiable. Otherwise a 416 only means the
            // encode has not reached the start yet (length is 0 before ffmpeg creates the
            // file), so keep waiting while the job is still building.
            if (StudioByteRange.Resolve(range, long.MaxValue).Status == 416)
                return length;
            var building = job.State == "building" || job.State == "missing";
            if (slice.Status != 416 && length > slice.End)
                return length;
            if (slice.Status == 416 && !building)
                return length;
            if (DateTime.UtcNow >= deadline)
                return length;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static (int Width, int Height) OutputSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (1280, 720);
        var scale = Math.Max(width, height) > StudioProxyPlan.MaxLongSide
            ? StudioProxyPlan.MaxLongSide / (double)Math.Max(width, height)
            : 1;
        var outWidth = Even(Math.Max(2, (int)Math.Round(width * scale)));
        var outHeight = Even(Math.Max(2, (int)Math.Round(height * scale)));
        return (outWidth, outHeight);
    }

    private static int Even(int value) => value % 2 == 0 ? value : value - 1;

    private static string SafeFileToken(string itemId)
    {
        var chars = itemId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var token = new string(chars);
        return token.Length == 0 ? "item" : token;
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
