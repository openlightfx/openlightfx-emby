using System.Reflection;
using System.Text.Json;
using OpenLightFX.Emby.Api;
using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class SceneCutStoreTests
{
    [Fact]
    public void Saved_cuts_round_trip_as_ascending_seconds_with_detector_settings()
    {
        using var dir = new TempDir();
        var store = new SceneCutStore(dir.Path);
        var media = dir.File("movie.mkv", "abcdefghij");
        var source = SceneCutSource.Capture(media);
        store.Save(SceneCutDocument.Create("42", source, 35, 256, 90.5, new[] { 48, 15, 48 }));

        var loaded = store.TryLoad("42");
        Assert.NotNull(loaded);
        Assert.Equal("42", loaded!.ItemId);
        Assert.Equal(new[] { 15 / 24.0, 48 / 24.0 }, loaded.Cuts);
        Assert.Equal(90.5, loaded.DurationSeconds);
        Assert.Equal(media, loaded.Source.Path);
        Assert.Equal(new FileInfo(media).Length, loaded.Source.Size);
        Assert.False(string.IsNullOrEmpty(loaded.UpdatedAt));
        Assert.Equal(ContentDetector.Name, loaded.Detector.Name);
        Assert.Equal(ContentDetector.PersistenceVersion, loaded.Detector.Version);
        Assert.Equal(35, loaded.Detector.Threshold);
        Assert.Equal(24, loaded.Detector.Fps);
        Assert.Equal(256, loaded.Detector.Width);
        Assert.Equal(15, loaded.Detector.MinSceneLen);
        Assert.Equal("ready", loaded.Status(SceneCutSource.Capture(media), running: false));
        Assert.True(File.Exists(Path.Combine(dir.Path, "OpenLightFX", "scene-cuts", "42.json")));

        store.Save(SceneCutDocument.Create("99", source, 27, 256, 1, new[] { 24 }));
        Assert.Equal(2, loaded.Cuts.Count);
        Assert.Single(store.TryLoad("99")!.Cuts);
    }

    [Fact]
    public void A_changed_file_or_detector_makes_the_saved_cuts_stale()
    {
        using var dir = new TempDir();
        var store = new SceneCutStore(dir.Path);
        var media = dir.File("movie.mkv", "abcdefghij");
        var source = SceneCutSource.Capture(media);
        store.Save(SceneCutDocument.Create("42", source, 27, 256, 10, new[] { 24 }));

        var moved = store.TryLoad("42")!;
        moved.Source.Path += ".moved";
        Assert.Equal("stale", moved.Status(source, running: false));

        File.AppendAllText(media, "!");
        Assert.Equal("stale", store.TryLoad("42")!.Status(SceneCutSource.Capture(media), running: false));

        var current = SceneCutSource.Capture(media);
        store.Save(SceneCutDocument.Create("42", current, 27, 256, 10, new[] { 24 }));
        var version = store.TryLoad("42")!;
        version.Detector.Version = "0";
        Assert.Equal("stale", version.Status(current, running: false));
        version.Detector.Version = ContentDetector.PersistenceVersion;
        version.Detector.Fps = 1;
        Assert.Equal("stale", version.Status(current, running: false));
        version.Detector.Fps = AiTrackAnalysis.DetectionFps;
        version.Detector.MinSceneLen = 2;
        Assert.Equal("stale", version.Status(current, running: false));

        File.WriteAllText(Path.Combine(dir.Path, "OpenLightFX", "scene-cuts", "42.json"), "{");
        Assert.Null(store.TryLoad("42"));
    }

    [Fact]
    public void Mtime_change_is_stale_and_a_running_job_wins()
    {
        using var dir = new TempDir();
        var store = new SceneCutStore(dir.Path);
        var media = dir.File("movie.mkv", "abcdefghij");
        store.Save(SceneCutDocument.Create("42", SceneCutSource.Capture(media), 27, 256, 10, new[] { 24 }));
        File.SetLastWriteTimeUtc(media, DateTime.UtcNow.AddHours(-3));
        var saved = store.TryLoad("42")!;
        Assert.Equal("stale", saved.Status(SceneCutSource.Capture(media), running: false));
        Assert.Equal("running", saved.Status(SceneCutSource.Capture(media), running: true));
    }

    [Fact]
    public void Response_json_uses_the_studio_scene_cut_shape()
    {
        using var dir = new TempDir();
        var store = new SceneCutStore(dir.Path);
        var media = dir.File("movie.mkv", "abcdefghij");
        var source = SceneCutSource.Capture(media);
        store.Save(SceneCutDocument.Create("42", source, 27, 256, 90.5, new[] { 15, 48 }));
        var ready = SceneCutApi.Response("42", store.TryLoad("42"), source, running: false, liveDurationSeconds: 1);
        var json = JsonSerializer.Serialize(ready);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("42", root.GetProperty("itemId").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal(90.5, root.GetProperty("durationSeconds").GetDouble());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("updatedAt").GetString()));
        var detector = root.GetProperty("detector");
        Assert.Equal("ContentDetector", detector.GetProperty("name").GetString());
        Assert.Equal(27, detector.GetProperty("threshold").GetDouble());
        Assert.Equal(24, detector.GetProperty("fps").GetDouble());
        Assert.Equal(15, detector.GetProperty("minSceneLen").GetInt32());
        Assert.False(detector.TryGetProperty("width", out _));
        Assert.False(detector.TryGetProperty("version", out _));
        var cuts = root.GetProperty("cuts").EnumerateArray().Select(item => item.GetDouble()).ToArray();
        Assert.Equal(new[] { 15 / 24.0, 2.0 }, cuts);

        var job = JsonSerializer.Serialize(new StudioSceneCutJobResponse
        {
            JobId = "abc", ItemId = "42", State = "processing", Error = "", Progress = 0.5, ThroughMs = 1000
        });
        using var jobDoc = JsonDocument.Parse(job);
        Assert.Equal("abc", jobDoc.RootElement.GetProperty("jobId").GetString());
        Assert.Equal("processing", jobDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(0.5, jobDoc.RootElement.GetProperty("progress").GetDouble());
        Assert.Equal(1000, jobDoc.RootElement.GetProperty("throughMs").GetInt64());

        var none = SceneCutApi.Response("7", null, null, running: false, liveDurationSeconds: 12);
        Assert.Equal("none", none.Status);
        Assert.Empty(none.Cuts);
        Assert.Equal("", none.UpdatedAt);
        Assert.Equal(12, none.DurationSeconds);
        Assert.Equal(27, none.Detector.Threshold);

        var running = SceneCutApi.Response("42", store.TryLoad("42"), source, running: true, liveDurationSeconds: 1);
        Assert.Equal("running", running.Status);
        Assert.Equal(2, running.Cuts.Count);
    }

    [Fact]
    public void Threshold_omitted_is_27_and_the_slider_still_scales_by_100()
    {
        Assert.Equal(27, SceneCutApi.ResolveThreshold(null));
        Assert.Equal(27, SceneCutApi.ResolveThreshold(0.27));
        Assert.Equal(35, SceneCutApi.ResolveThreshold(0.35));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneCutApi.ResolveThreshold(double.NaN));
    }

    [Fact]
    public void Scene_cut_routes_match_the_studio_contract()
    {
        AssertRoute(typeof(GetStudioSceneCuts), "/OpenLightFX/Studio/Items/{ItemId}/SceneCuts", "GET", admin: false);
        AssertRoute(typeof(PostStudioSceneCuts), "/OpenLightFX/Studio/Items/{ItemId}/SceneCuts", "POST", admin: true);
        AssertRoute(typeof(GetStudioSceneCutJob), "/OpenLightFX/Studio/SceneCuts/{JobId}", "GET", admin: true);
    }

    [Fact]
    public void Detection_filter_is_the_cut_branch_of_the_generation_split()
    {
        var split = AiTrackAnalysis.Filter(2, 1, 256, 144, "");
        Assert.Contains("[cut_in]fps=24,scale=256:144:flags=bilinear,format=rgb24[cut]", split, StringComparison.Ordinal);
        Assert.Equal("[0:0]fps=24,scale=256:144:flags=bilinear,format=rgb24[cut]", AiTrackAnalysis.CutFilter(0, 256, 144, ""));
        var hdr = "zscale=tin=smpte2084:t=linear:npl=100,";
        Assert.Equal(
            "[0:1]fps=24,zscale=tin=smpte2084:t=linear:npl=100,scale=256:106:flags=bilinear,format=rgb24[cut]",
            AiTrackAnalysis.CutFilter(1, 256, 106, hdr));
    }

    [Fact]
    public void Activity_tracks_overlapping_jobs_for_an_item()
    {
        SceneCutActivity.Enter("item-a");
        SceneCutActivity.Enter("item-a");
        Assert.True(SceneCutActivity.IsRunning("item-a"));
        SceneCutActivity.Exit("item-a");
        Assert.True(SceneCutActivity.IsRunning("item-a"));
        SceneCutActivity.Exit("item-a");
        Assert.False(SceneCutActivity.IsRunning("item-a"));
    }

    private static void AssertRoute(Type type, string path, string verbs, bool admin)
    {
        var route = Assert.Single(type.GetCustomAttributes(false), attribute => attribute.GetType().Name == "RouteAttribute");
        Assert.Equal(path, route.GetType().GetProperty("Path")!.GetValue(route));
        Assert.Equal(verbs, route.GetType().GetProperty("Verbs")!.GetValue(route));
        var auth = Assert.Single(type.GetCustomAttributes(false), attribute => attribute.GetType().Name == "AuthenticatedAttribute");
        var roles = auth.GetType().GetProperty("Roles")?.GetValue(auth) as string;
        if (admin) Assert.Equal("Admin", roles);
        else Assert.True(string.IsNullOrEmpty(roles));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "olfx-cuts-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public string File(string name, string contents)
        {
            Directory.CreateDirectory(Path);
            var full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(full, contents);
            return full;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
