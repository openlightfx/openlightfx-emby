using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

// Downloads openlightfx-studio-dist.zip from a GitHub Release and writes a
// normalized zip the plugin embeds. Always exits 0: a missing release becomes
// a placeholder page so the plugin build still succeeds.
//
//   FetchStudioDist <StudioVersion> <output.zip>
//   StudioVersion: "latest" or a release tag such as v1.2.3

const string Repo = "openlightfx/openlightfx-studio";
const string AssetName = "openlightfx-studio-dist.zip";

var version = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0].Trim() : "latest";
if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
{
    Console.Error.WriteLine("Usage: FetchStudioDist <version> <output.zip>");
    WritePlaceholder(Path.GetFullPath("openlightfx-studio-dist.zip"));
    return 0;
}

var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var stampPath = output + ".version";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenLightFX-Emby");
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

try
{
    var resolved = await ResolveAsync(http, version).ConfigureAwait(false);
    if (resolved != null
        && File.Exists(output)
        && File.Exists(stampPath)
        && string.Equals(File.ReadAllText(stampPath).Trim(), resolved.Identity, StringComparison.Ordinal)
        && ZipHasIndex(output))
    {
        Console.Error.WriteLine($"Studio dist already current ({resolved.Identity}).");
        return 0;
    }

    if (resolved != null && await TryWriteReleaseAsync(http, resolved, output).ConfigureAwait(false))
    {
        await File.WriteAllTextAsync(stampPath, resolved.Identity).ConfigureAwait(false);
        Console.Error.WriteLine($"Embedded Studio dist {resolved.Identity}.");
        return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Studio dist download failed: {ex.Message}");
}

if (File.Exists(output) && ZipHasIndex(output))
{
    Console.Error.WriteLine("Keeping the previously downloaded Studio dist.");
    return 0;
}

WritePlaceholder(output);
await File.WriteAllTextAsync(stampPath, "placeholder").ConfigureAwait(false);
Console.Error.WriteLine("No Studio release found; embedded a placeholder page.");
return 0;

static async Task<ReleaseAsset?> ResolveAsync(HttpClient http, string version)
{
    if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var tag = doc.RootElement.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrEmpty(url))
                return null;
            return new ReleaseAsset(string.IsNullOrEmpty(tag) ? "latest" : tag, url);
        }

        return null;
    }

    var tagName = version;
    return new ReleaseAsset(tagName, $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tagName)}/{AssetName}");
}

static async Task<bool> TryWriteReleaseAsync(HttpClient http, ReleaseAsset release, string output)
{
    var urls = new List<string> { release.Url };
    if (!release.Identity.Equals("latest", StringComparison.OrdinalIgnoreCase)
        && !release.Identity.StartsWith('v'))
    {
        urls.Add($"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString("v" + release.Identity)}/{AssetName}");
    }

    foreach (var url in urls)
    {
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            continue;

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!TryNormalizeZip(bytes, output))
            continue;
        return true;
    }

    return false;
}

static bool TryNormalizeZip(byte[] bytes, string output)
{
    try
    {
        using var input = new MemoryStream(bytes);
        using var source = new ZipArchive(input, ZipArchiveMode.Read);
        var files = source.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => (Entry: entry, Name: entry.FullName.Replace('\\', '/').TrimStart('/')))
            .Where(pair => !pair.Name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            .Where(pair => !pair.Name.Contains("..", StringComparison.Ordinal))
            .ToList();

        var index = files
            .Select(pair => pair.Name)
            .Where(name => name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name.Length)
            .FirstOrDefault();
        if (index == null)
            return false;

        var strip = index.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            ? ""
            : index[..^"index.html".Length];

        var temp = output + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);

        using (var outputStream = File.Create(temp))
        using (var dest = new ZipArchive(outputStream, ZipArchiveMode.Create))
        {
            foreach (var (entry, name) in files)
            {
                if (!name.StartsWith(strip, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = name[strip.Length..];
                if (string.IsNullOrEmpty(relative))
                    continue;

                var created = dest.CreateEntry(relative, CompressionLevel.Optimal);
                using var sourceStream = entry.Open();
                using var targetStream = created.Open();
                sourceStream.CopyTo(targetStream);
            }
        }

        File.Move(temp, output, overwrite: true);
        return ZipHasIndex(output);
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException)
    {
        Console.Error.WriteLine($"Studio dist zip was not usable: {ex.Message}");
        return false;
    }
}

static bool ZipHasIndex(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').TrimStart('/').Equals("index.html", StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        return false;
    }
}

static void WritePlaceholder(string output)
{
    var temp = output + ".tmp";
    if (File.Exists(temp))
        File.Delete(temp);

    using (var stream = File.Create(temp))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("index.html", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write("""
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
            """);
    }

    File.Move(temp, output, overwrite: true);
}

sealed record ReleaseAsset(string Identity, string Url);
