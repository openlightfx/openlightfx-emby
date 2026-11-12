namespace OpenLightFX.Emby.Studio;

public static class StudioContentTypes
{
    public static string ForPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json",
            ".webmanifest" => "application/manifest+json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".wasm" => "application/wasm",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}
