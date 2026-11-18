using System.Security.Cryptography;
using System.Text;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Resolves durable OpenLightFX files outside media-library folders. A configured root
/// can live on a separate disk; the default is the plugin's own data directory.
/// </summary>
public static class StudioTrackStorage
{
    public static string ResolveRoot(string? configuredPath, string? pluginDataPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));

        var pluginRoot = string.IsNullOrWhiteSpace(pluginDataPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby", "OpenLightFX")
            : pluginDataPath;
        return Path.GetFullPath(Path.Combine(pluginRoot, "OpenLightFX"));
    }

    public static string MovieDirectory(string root, string movieFilePath, string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(movieFilePath))
            throw new ArgumentException("Movie file path is not available", nameof(movieFilePath));

        var key = MovieKey(movieFilePath, imdbId);
        return Path.Combine(Path.GetFullPath(root), "Tracks", key);
    }

    public static string TrackPath(string root, string movieFilePath, string? imdbId, string trackId)
    {
        if (!StudioTrackFiles.IsSafeTrackId(trackId))
            throw new ArgumentException("trackId contains unsupported characters", nameof(trackId));
        return Path.Combine(MovieDirectory(root, movieFilePath, imdbId), trackId + ".lightfx");
    }

    public static string ProjectPath(string root, string movieFilePath, string? imdbId, string trackId)
    {
        if (!StudioTrackFiles.IsSafeTrackId(trackId))
            throw new ArgumentException("trackId contains unsupported characters", nameof(trackId));
        return Path.Combine(MovieDirectory(root, movieFilePath, imdbId), trackId + ".studio.json");
    }

    public static string ProxyPath(string root, string? movieFilePath, string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(movieFilePath))
            throw new ArgumentException("Movie file path is not available", nameof(movieFilePath));
        var key = MovieKey(movieFilePath, imdbId);
        return Path.Combine(Path.GetFullPath(root), "Proxies", key + ".mp4");
    }

    private static string MovieKey(string movieFilePath, string? imdbId)
    {
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var providerId = new string(imdbId.Trim().ToLowerInvariant().Select(ch =>
                char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
            if (providerId.Length > 0)
                return "imdb-" + providerId;
        }

        var normalizedPath = Path.GetFullPath(movieFilePath);
        if (Path.DirectorySeparatorChar == '\\')
            normalizedPath = normalizedPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))
            .ToLowerInvariant();
        return "path-" + hash;
    }
}
