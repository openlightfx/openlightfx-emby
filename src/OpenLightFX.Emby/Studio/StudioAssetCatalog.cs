using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace OpenLightFX.Emby.Studio;

public sealed record StudioAsset(string RelativePath, byte[] Content, string ContentType, bool IsSpaFallback);

/// <summary>
/// Studio static files extracted from the embedded dist zip, with SPA fallback.
/// </summary>
public sealed class StudioAssetCatalog
{
    public const string ResourceName = "OpenLightFX.Emby.Studio.dist.zip";

    public static string PlaceholderHtml { get; } = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>OpenLightFX Studio</title>
          <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; margin: 48px auto; max-width: 640px; color: #e8e8e8; background: #1b1b1b; }
            a { color: #52b54b; }
            code { color: #f0f0f0; }
          </style>
        </head>
        <body>
          <h1>OpenLightFX Studio</h1>
          <p>This plugin build does not include a Studio release yet. Publish <code>openlightfx-studio-dist.zip</code> on the openlightfx-studio GitHub Release, then rebuild the plugin (<code>-p:StudioVersion=&lt;tag&gt;</code>).</p>
          <p>When a build is embedded it is served at <code>/emby/OpenLightFX/studio/</code>.</p>
        </body>
        </html>
        """;

    private readonly Dictionary<string, byte[]> _files;

    public StudioAssetCatalog(IReadOnlyDictionary<string, byte[]> files)
    {
        _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in files)
        {
            var key = NormalizeKey(pair.Key);
            if (key == null || pair.Value == null)
                continue;
            _files[key] = pair.Value;
        }

        if (!_files.ContainsKey("index.html"))
            _files["index.html"] = Encoding.UTF8.GetBytes(PlaceholderHtml);
    }

    public static StudioAssetCatalog Shared { get; } = FromAssembly(typeof(StudioAssetCatalog).Assembly);

    public static StudioAssetCatalog FromAssembly(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
            return new StudioAssetCatalog(new Dictionary<string, byte[]>());
        return FromZip(stream);
    }

    public static StudioAssetCatalog FromZip(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var key = NormalizeKey(entry.FullName);
            if (key == null)
                continue;

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            files[key] = buffer.ToArray();
        }

        return new StudioAssetCatalog(files);
    }

    /// <summary>
    /// Resolve a request path. Extensionless paths that are not files fall back to index.html.
    /// Returns null for missing static assets and for paths that try to escape the bundle.
    /// </summary>
    public StudioAsset? Resolve(string? relativePath)
    {
        var path = NormalizeKey(relativePath);
        if (path == null)
            return null;

        if (path.Length == 0)
            return Html("index.html", spaFallback: false);

        if (_files.TryGetValue(path, out var exact))
            return new StudioAsset(path, exact, StudioContentTypes.ForPath(path), false);

        // Emby strips a trailing .json from some routes. Recover that asset when it exists.
        if (!Path.HasExtension(path) && _files.TryGetValue(path + ".json", out var json))
            return new StudioAsset(path + ".json", json, "application/json", false);

        if (!Path.HasExtension(path))
            return Html("index.html", spaFallback: true);

        return null;
    }

    private StudioAsset Html(string path, bool spaFallback)
    {
        var bytes = _files.TryGetValue(path, out var found)
            ? found
            : Encoding.UTF8.GetBytes(PlaceholderHtml);
        return new StudioAsset(path, bytes, StudioContentTypes.ForPath(path), spaFallback);
    }

    /// <summary>Returns "" for the SPA root, null when the path is unsafe, otherwise a zip-relative key.</summary>
    public static string? NormalizeKey(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return "";

        var path = relativePath.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        path = path.Trim('/');

        if (path.Length == 0)
            return "";
        if (path.Contains("..", StringComparison.Ordinal))
            return null;
        if (path.Contains('\0', StringComparison.Ordinal))
            return null;

        return path;
    }
}
