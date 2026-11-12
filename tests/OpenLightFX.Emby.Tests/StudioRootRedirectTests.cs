using System.Xml.Linq;
using OpenLightFX.Emby.Api;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class StudioRootRedirectTests
{
    [Theory]
    [InlineData("/emby/OpenLightFX/studio?api_key=abc", "/emby/OpenLightFX/studio/?api_key=abc")]
    [InlineData("/OpenLightFX/studio?api_key=abc", "/OpenLightFX/studio/?api_key=abc")]
    [InlineData("/emby/OpenLightFX/studio", "/emby/OpenLightFX/studio/")]
    [InlineData("/OpenLightFX/studio", "/OpenLightFX/studio/")]
    [InlineData(
        "https://jonflix.proietti.family/emby/OpenLightFX/studio?api_key=abc",
        "/emby/OpenLightFX/studio/?api_key=abc")]
    [InlineData(
        "http://127.0.0.1:8096/OpenLightFX/studio?api_key=a%2Fb&x=1",
        "/OpenLightFX/studio/?api_key=a%2Fb&x=1")]
    public void Redirects_the_studio_root_and_keeps_the_query(string request, string expected)
    {
        var target = StudioRootRedirect.Target(request);

        Assert.Equal(expected, target);
        Assert.StartsWith("/", target);
        Assert.DoesNotContain("://", target);
    }

    [Theory]
    [InlineData("/emby/OpenLightFX/studio/")]
    [InlineData("/emby/OpenLightFX/studio/?api_key=abc")]
    [InlineData("/OpenLightFX/studio/")]
    [InlineData("/OpenLightFX/studio/?api_key=abc")]
    [InlineData("/emby/OpenLightFX/studio/assets/app.js")]
    [InlineData("/emby/OpenLightFX/studio/index.html")]
    [InlineData("/OpenLightFX/Studio/Bulbs")]
    [InlineData("/emby/OpenLightFX/assets/app.js")]
    public void Leaves_slashed_roots_and_other_paths_alone(string request)
    {
        Assert.Null(StudioRootRedirect.Target(request));
    }

    [Fact]
    public void Keeps_the_emby_prefix_when_another_view_of_the_url_omits_it()
    {
        var target = StudioRootRedirect.Target(
            "/OpenLightFX/studio?api_key=abc",
            "http://127.0.0.1:8096/emby/OpenLightFX/studio?api_key=abc");

        Assert.Equal("/emby/OpenLightFX/studio/?api_key=abc", target);
    }

    [Fact]
    public void Does_not_loop_when_path_info_lost_the_trailing_slash()
    {
        // Emby trims PathInfo before the service runs. RawUrl still has the slash.
        var target = StudioRootRedirect.Target(
            "/emby/OpenLightFX/studio/?api_key=abc",
            "https://jonflix.proietti.family/emby/OpenLightFX/studio/?api_key=abc",
            "/emby/OpenLightFX/studio");

        Assert.Null(target);
    }

    [Fact]
    public void Preserves_the_callers_casing()
    {
        Assert.Equal(
            "/emby/openlightfx/Studio/",
            StudioRootRedirect.Target("/emby/openlightfx/Studio"));
    }
}

public class StudioLinkTests
{
    [Fact]
    public void Public_studio_url_has_a_trailing_slash()
    {
        Assert.Equal("/emby/OpenLightFX/studio/", StudioPaths.PublicUrl);
    }

    [Fact]
    public void Light_tracks_page_links_to_the_slashed_studio_url()
    {
        using var stream = new LightTracksPage().GetHtmlStream();
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        Assert.Contains("href=\"/emby/OpenLightFX/studio/\"", html);
        Assert.DoesNotContain(
            "href=\"/emby/OpenLightFX/studio\"",
            html.Replace("href=\"/emby/OpenLightFX/studio/\"", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_description_points_at_the_slashed_studio_url()
    {
        var description = new PluginOptions().EditorDescription;

        Assert.Contains("/emby/OpenLightFX/studio/", description);
        Assert.DoesNotContain(
            "/emby/OpenLightFX/studio",
            description.Replace("/emby/OpenLightFX/studio/", "", StringComparison.Ordinal));
    }
}

public class FetchStudioDistTests
{
    [Fact]
    public void Fetch_helper_rolls_forward_onto_a_newer_runtime()
    {
        var csproj = FindRepoFile("build/FetchStudioDist/FetchStudioDist.csproj");
        var doc = XDocument.Load(csproj);
        var rollForward = doc.Descendants().Single(element => element.Name.LocalName == "RollForward").Value;

        Assert.Equal("Major", rollForward);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
