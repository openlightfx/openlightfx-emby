using System.IO.Compression;
using System.Text;
using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class StudioAssetAuthTests
{
    [Fact]
    public void Reads_the_studio_cookie()
    {
        var token = StudioAssetAuth.ReadCookie(
            "other=1; olfx_studio_auth=abc%2F123; theme=dark",
            StudioAssetAuth.CookieName);

        Assert.Equal("abc/123", token);
    }

    [Fact]
    public void Cookie_is_http_only_and_covers_asset_requests()
    {
        var header = StudioAssetAuth.SetCookieHeader("tok en", secure: true);

        Assert.Contains("olfx_studio_auth=tok%20en", header);
        Assert.Contains("Path=/", header);
        Assert.Contains("HttpOnly", header);
        Assert.Contains("SameSite=Lax", header);
        Assert.Contains("Secure", header);
    }
}

public class StudioContentTypeTests
{
    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("assets/app.js", "text/javascript; charset=utf-8")]
    [InlineData("assets/app.mjs", "text/javascript; charset=utf-8")]
    [InlineData("assets/app.css", "text/css; charset=utf-8")]
    [InlineData("manifest.json", "application/json")]
    [InlineData("assets/app.js.map", "application/json")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("hero.webp", "image/webp")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("app.wasm", "application/wasm")]
    [InlineData("unknown.bin", "application/octet-stream")]
    public void Maps_extensions(string path, string contentType)
    {
        Assert.Equal(contentType, StudioContentTypes.ForPath(path));
    }
}

public class StudioAssetCatalogTests
{
    [Fact]
    public void Serves_index_and_nested_files()
    {
        var catalog = CatalogWith(
            ("index.html", "<html>studio</html>"),
            ("assets/app.js", "console.log(1)"));

        var index = catalog.Resolve(null);
        var nested = catalog.Resolve("assets/app.js");
        var slash = catalog.Resolve("/assets/app.js");

        Assert.NotNull(index);
        Assert.Equal("text/html; charset=utf-8", index!.ContentType);
        Assert.Contains("studio", Encoding.UTF8.GetString(index.Content));
        Assert.False(index.IsSpaFallback);

        Assert.NotNull(nested);
        Assert.Equal("text/javascript; charset=utf-8", nested!.ContentType);
        Assert.Equal(nested.Content, slash!.Content);
    }

    [Fact]
    public void Falls_back_to_index_for_client_routes()
    {
        var catalog = CatalogWith(("index.html", "<html>studio</html>"));

        var route = catalog.Resolve("editor/movie/42");

        Assert.NotNull(route);
        Assert.True(route!.IsSpaFallback);
        Assert.Equal("text/html; charset=utf-8", route.ContentType);
        Assert.Contains("studio", Encoding.UTF8.GetString(route.Content));
    }

    [Fact]
    public void Missing_static_asset_is_not_the_spa_shell()
    {
        var catalog = CatalogWith(("index.html", "<html>studio</html>"));

        Assert.Null(catalog.Resolve("assets/missing.js"));
        Assert.Null(catalog.Resolve("../secret.txt"));
        Assert.Null(catalog.Resolve("assets/../../etc/passwd"));
    }

    [Fact]
    public void Embedded_bundle_contains_an_index_page()
    {
        var resources = typeof(StudioAssetCatalog).Assembly.GetManifestResourceNames();
        Assert.Contains(StudioAssetCatalog.ResourceName, resources);

        var index = StudioAssetCatalog.Shared.Resolve("");

        Assert.NotNull(index);
        Assert.Equal("text/html; charset=utf-8", index!.ContentType);
        Assert.NotEmpty(index.Content);
        Assert.Contains("<html", Encoding.UTF8.GetString(index.Content), StringComparison.OrdinalIgnoreCase);
    }

    private static StudioAssetCatalog CatalogWith(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        buffer.Position = 0;
        return StudioAssetCatalog.FromZip(buffer);
    }
}

public class StudioStreamUrlTests
{
    [Fact]
    public void Transcodes_mkv_through_emby_video_endpoint()
    {
        var url = StudioStreamUrls.Build(
            new StudioRequestOrigin("http://host:8096", "/emby"),
            new StudioMediaInfo("42", "mkv", "hevc", "ac3", "ms-1"),
            "token value");

        Assert.StartsWith("http://host:8096/emby/Videos/42/stream.mp4?", url);
        Assert.Contains("VideoCodec=h264", url);
        Assert.Contains("AudioCodec=aac", url);
        Assert.Contains("MediaSourceId=ms-1", url);
        Assert.Contains("api_key=token%20value", url);
        Assert.DoesNotContain("Static=true", url);
    }

    [Fact]
    public void Direct_plays_browser_mp4()
    {
        var url = StudioStreamUrls.Build(
            new StudioRequestOrigin("http://host:8096", ""),
            new StudioMediaInfo("42", "mp4", "h264", "aac", null),
            null);

        Assert.StartsWith("http://host:8096/Videos/42/stream.mp4?", url);
        Assert.Contains("Static=true", url);
        Assert.DoesNotContain("api_key=", url);
        Assert.DoesNotContain("VideoCodec=", url);
    }

    [Fact]
    public void Direct_plays_browser_webm()
    {
        var playback = StudioStreamUrls.ChoosePlayback(new StudioMediaInfo("1", "webm", "vp9", "opus", null));
        Assert.Equal(("webm", true), playback);
    }

    [Theory]
    [InlineData("http://host:8096/emby/OpenLightFX/Studio/Items/5/StreamUrl", "http://host:8096", "/emby")]
    [InlineData("http://host:8096/OpenLightFX/Studio/Items/5/StreamUrl", "http://host:8096", "")]
    public void Reads_the_prefix_the_request_used(string absoluteUri, string origin, string pathBase)
    {
        var parsed = StudioStreamUrls.FromAbsoluteUri(absoluteUri);
        Assert.Equal(origin, parsed.Origin);
        Assert.Equal(pathBase, parsed.PathBase);
    }

    [Fact]
    public void Poster_url_uses_the_emby_image_route_and_the_page_token()
    {
        var url = StudioPosters.PosterUrl(
            "http://host:8096/emby/OpenLightFX/Studio/Items?search=clip",
            "9001",
            "harbor tag");

        Assert.Equal(
            "http://host:8096/emby/Items/9001/Images/Primary?tag=harbor%20tag&maxHeight=240",
            url);
        Assert.DoesNotContain("api_key", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poster_url_uses_the_emby_prefix_when_the_items_call_is_at_the_root()
    {
        var url = StudioPosters.PosterUrl(
            "http://host:8096/OpenLightFX/Studio/Items",
            "9001",
            "abc");

        Assert.Equal("http://host:8096/emby/Items/9001/Images/Primary?tag=abc&maxHeight=240", url);
        Assert.DoesNotContain("api_key", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poster_url_is_absent_without_a_primary_tag()
    {
        Assert.Null(StudioPosters.PosterUrl("http://host:8096/emby/OpenLightFX/Studio/Items", "9001", ""));
        Assert.Null(StudioPosters.PosterUrl("http://host:8096/emby/OpenLightFX/Studio/Items", "9001", null));
    }

    [Fact]
    public void Playback_uses_the_studio_cookie_when_the_url_has_no_token()
    {
        Assert.Equal(
            "token value",
            StudioSessionAuth.PresentedToken(null, "olfx_studio_auth=token%20value"));
        Assert.Equal(
            "header-token",
            StudioSessionAuth.PresentedToken("header-token", "olfx_studio_auth=cookie-token"));
        Assert.Null(StudioSessionAuth.PresentedToken(null, null));
    }

    [Fact]
    public void Reads_token_from_authorization_header()
    {
        var token = StudioStreamUrls.TokenFromAuthorization(
            "MediaBrowser Client=\"Studio\", Device=\"Browser\", Token=\"abc-123\", Version=\"1.0\"");
        Assert.Equal("abc-123", token);
    }
}
