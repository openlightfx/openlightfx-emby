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
            "other=1; olfx_studio_media=abc%2F123; theme=dark",
            "olfx_studio_media");

        Assert.Equal("abc/123", token);
    }

    [Fact]
    public void Cookie_is_http_only_strict_secure_on_https_and_expires()
    {
        var header = StudioAssetAuth.SetCookieHeader("olfx_studio_media", "tick et", secure: true, TimeSpan.FromHours(8));

        Assert.Contains("olfx_studio_media=tick%20et", header);
        Assert.Contains("Path=/", header);
        Assert.Contains("Max-Age=28800", header);
        Assert.Contains("HttpOnly", header);
        Assert.Contains("SameSite=Strict", header);
        Assert.Contains("; Secure", header);
        Assert.DoesNotContain("Secure", StudioAssetAuth.SetCookieHeader("n", "v", secure: false, TimeSpan.FromHours(1)));
        Assert.Contains("Max-Age=0", StudioAssetAuth.ClearCookieHeader(StudioAssetAuth.LegacyCookieName, secure: true));
    }
}

public class StudioCookieTicketTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ticket_is_opaque_and_redeems_only_for_its_scope()
    {
        var ticket = StudioCookieTickets.Issue("emby-token", StudioTicketScope.ProxyMedia, Now);

        Assert.DoesNotContain("emby-token", ticket);
        Assert.True(ticket.Length >= 40);
        Assert.Equal("emby-token", StudioCookieTickets.Redeem(ticket, StudioTicketScope.ProxyMedia, Now));
        Assert.Null(StudioCookieTickets.Redeem(ticket, StudioTicketScope.Assets, Now));
        Assert.Null(StudioCookieTickets.Redeem("emby-token", StudioTicketScope.ProxyMedia, Now));
    }

    [Fact]
    public void Ticket_expires()
    {
        var ticket = StudioCookieTickets.Issue("emby-token", StudioTicketScope.ProxyMedia, Now);

        Assert.NotNull(StudioCookieTickets.Redeem(ticket, StudioTicketScope.ProxyMedia, Now + StudioCookieTickets.Lifetime - TimeSpan.FromSeconds(1)));
        Assert.Null(StudioCookieTickets.Redeem(ticket, StudioTicketScope.ProxyMedia, Now + StudioCookieTickets.Lifetime));
    }

    [Fact]
    public void Old_ticket_rotates_and_keeps_only_a_short_grace()
    {
        var first = StudioCookieTickets.Issue("emby-token", StudioTicketScope.ProxyMedia, Now);
        Assert.Equal(first, StudioCookieTickets.Rotate(first, "emby-token", StudioTicketScope.ProxyMedia, Now + TimeSpan.FromHours(1)));

        var later = Now + TimeSpan.FromHours(5);
        var second = StudioCookieTickets.Rotate(first, "emby-token", StudioTicketScope.ProxyMedia, later);
        Assert.NotEqual(first, second);
        Assert.NotNull(StudioCookieTickets.Redeem(first, StudioTicketScope.ProxyMedia, later + TimeSpan.FromSeconds(30)));
        Assert.Null(StudioCookieTickets.Redeem(first, StudioTicketScope.ProxyMedia, later + StudioCookieTickets.RotationGrace));
        Assert.Equal("emby-token", StudioCookieTickets.Redeem(second, StudioTicketScope.ProxyMedia, later + TimeSpan.FromHours(7)));
    }

    [Fact]
    public void A_different_token_gets_a_new_ticket()
    {
        var first = StudioCookieTickets.Issue("token-a", StudioTicketScope.ProxyMedia, Now);
        var second = StudioCookieTickets.Rotate(first, "token-b", StudioTicketScope.ProxyMedia, Now);
        Assert.NotEqual(first, second);
        Assert.Equal("token-b", StudioCookieTickets.Redeem(second, StudioTicketScope.ProxyMedia, Now));
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
    public void Playback_uses_the_media_ticket_when_the_url_has_no_token()
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = StudioCookieTickets.Issue("token value", StudioTicketScope.ProxyMedia, now);
        Assert.Equal(
            "token value",
            StudioSessionAuth.PresentedToken(null, "olfx_studio_media=" + ticket, StudioTicketScope.ProxyMedia, now));
        Assert.Equal(
            "header-token",
            StudioSessionAuth.PresentedToken("header-token", "olfx_studio_media=" + ticket, StudioTicketScope.ProxyMedia, now));
        Assert.Null(StudioSessionAuth.PresentedToken(null, null, StudioTicketScope.ProxyMedia, now));
        // The legacy cookie held a raw access token. It is never accepted.
        Assert.Null(StudioSessionAuth.PresentedToken(null, "olfx_studio_auth=token%20value", StudioTicketScope.ProxyMedia, now));
        // A page ticket does not open the edit proxy.
        var page = StudioCookieTickets.Issue("token value", StudioTicketScope.Assets, now);
        Assert.Null(StudioSessionAuth.PresentedToken(null, "olfx_studio_media=" + page, StudioTicketScope.ProxyMedia, now));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    [InlineData(null, false)]
    public void Cookie_tickets_are_only_for_reads(string? method, bool allowed)
    {
        Assert.Equal(allowed, StudioSessionAuth.CookieAllowedFor(method));
    }

    [Fact]
    public void Reads_token_from_authorization_header()
    {
        var token = StudioStreamUrls.TokenFromAuthorization(
            "MediaBrowser Client=\"Studio\", Device=\"Browser\", Token=\"abc-123\", Version=\"1.0\"");
        Assert.Equal("abc-123", token);
    }
}
