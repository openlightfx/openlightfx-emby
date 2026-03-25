namespace OpenLightFX.Emby.Services;

using MediaBrowser.Model.Logging;
using OpenLightFX.Emby.Models;

public class TrackDiscoveryService
{
    private readonly TrackParser _parser;
    private readonly ILogger _logger;
    private readonly Dictionary<string, List<TrackInfo>> _cache = new();

    public TrackDiscoveryService(TrackParser parser, ILogger logger)
    {
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// Discover all .lightfx tracks for a movie given its file path and optional IMDB ID.
    /// Uses three strategies in priority order per Track Format Specification §5.2:
    /// sidecar file, lightfx/ subfolder, then IMDB match across additional scan paths.
    /// </summary>
    /// <param name="movieFilePath">Full path to the movie file.</param>
    /// <param name="imdbId">Optional IMDB ID (e.g. tt1234567) for metadata matching.</param>
    /// <param name="additionalScanPaths">User-configured library paths for centralized .lightfx storage.</param>
    public List<TrackInfo> DiscoverTracks(string movieFilePath, string? imdbId, IEnumerable<string>? additionalScanPaths = null)
    {
        var cacheKey = movieFilePath + "|" + (imdbId ?? "");
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var tracks = new List<TrackInfo>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesFound = 0;
        var filesRejected = 0;

        // Strategy 1: Sidecar — .lightfx file alongside the movie with matching base name
        var dir = Path.GetDirectoryName(movieFilePath);
        var baseName = Path.GetFileNameWithoutExtension(movieFilePath);
        if (dir != null)
        {
            var sidecar = Path.Combine(dir, baseName + ".lightfx");
            _logger.Debug("Discovery: checking sidecar '{0}'", sidecar);
            if (File.Exists(sidecar))
            {
                filesFound++;
                if (!AddTrack(tracks, seenPaths, sidecar))
                    filesRejected++;
            }
        }

        // Strategy 1.5: Any .lightfx file in the same directory (catches Untitled_Track.lightfx etc.)
        if (dir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.lightfx"))
                {
                    if (seenPaths.Contains(file)) continue;
                    filesFound++;
                    _logger.Debug("Discovery: found '{0}' in movie directory", Path.GetFileName(file));
                    if (!AddTrack(tracks, seenPaths, file))
                        filesRejected++;
                }
            }
            catch (DirectoryNotFoundException) { }
        }

        // Strategy 2: Subfolder — all .lightfx files in a lightfx/ subdirectory
        if (dir != null)
        {
            var lightfxDir = Path.Combine(dir, "lightfx");
            if (Directory.Exists(lightfxDir))
            {
                foreach (var file in Directory.GetFiles(lightfxDir, "*.lightfx"))
                {
                    filesFound++;
                    _logger.Debug("Discovery: found '{0}' in lightfx subfolder", Path.GetFileName(file));
                    if (!AddTrack(tracks, seenPaths, file))
                        filesRejected++;
                }
            }
        }

        // Strategy 3: IMDB match — search configured library paths for tracks whose
        // metadata.movie_reference.imdb_id matches the given IMDB ID
        if (!string.IsNullOrEmpty(imdbId) && additionalScanPaths != null)
        {
            foreach (var scanPath in additionalScanPaths)
            {
                if (!Directory.Exists(scanPath)) continue;

                foreach (var file in Directory.EnumerateFiles(scanPath, "*.lightfx", SearchOption.AllDirectories))
                {
                    if (seenPaths.Contains(file)) continue;

                    var info = _parser.Parse(file);
                    if (info.ImdbId == imdbId && info.IsValid)
                    {
                        seenPaths.Add(file);
                        tracks.Add(info);
                    }
                }
            }
        }

        if (filesFound > 0 && tracks.Count == 0)
            _logger.Warn("Discovery: found {0} .lightfx file(s) but all failed validation ({1} rejected)", filesFound, filesRejected);
        else
            _logger.Debug("Discovery: {0} file(s) found, {1} valid track(s), {2} rejected", filesFound, tracks.Count, filesRejected);

        _cache[cacheKey] = tracks;
        return tracks;
    }

    /// <summary>
    /// Clear the entire discovery cache (e.g. when scan paths change).
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Remove cache entries for a specific movie file path.
    /// </summary>
    public void InvalidateCache(string movieFilePath)
    {
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith(movieFilePath)).ToList();
        foreach (var key in keysToRemove)
            _cache.Remove(key);
    }

    private bool AddTrack(List<TrackInfo> tracks, HashSet<string> seenPaths, string filePath)
    {
        if (!seenPaths.Add(filePath)) return true; // already added

        var info = _parser.Parse(filePath);
        if (info.IsValid)
        {
            tracks.Add(info);
            return true;
        }

        _logger.Warn("Discovery: '{0}' failed validation: {1}",
            Path.GetFileName(filePath), string.Join("; ", info.ValidationErrors));
        return false;
    }
}
