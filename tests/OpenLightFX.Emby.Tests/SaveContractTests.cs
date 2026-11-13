using System.Text;
using System.Text.Json;
using Google.Protobuf;
using OpenLightFX.Emby.Api;
using OpenLightFX.Emby.Services;
using OpenLightFX.Emby.Studio;
using Openlightfx;
using Xunit;
using SsJson = ServiceStack.Text.JsonSerializer;

namespace OpenLightFX.Emby.Tests;

public class StudioJsonNameTests
{
    [Fact(DisplayName = "MEDIA-001 @fixture item JSON uses camelCase itemId and runtimeMs")]
    public void Items_are_camel_case_under_the_emby_serializer()
    {
        var json = SsJson.SerializeToString(new StudioItemResponse
        {
            ItemId = "42",
            Name = "Proxy Fixture",
            Year = 2020,
            RuntimeMs = 101000,
            PrimaryImageTag = "abc"
        });

        Assert.Contains("\"itemId\":\"42\"", json);
        Assert.Contains("\"runtimeMs\":101000", json);
        Assert.Contains("\"primaryImageTag\":\"abc\"", json);
        Assert.DoesNotContain("ItemId", json);
        Assert.DoesNotContain("RuntimeMs", json);
        Assert.DoesNotContain("PrimaryImageTag", json);
    }

    [Fact(DisplayName = "PLUG-006 @fixture settings JSON includes photosensitivityMode")]
    public void Settings_include_photosensitivity_mode()
    {
        var json = SsJson.SerializeToString(new SettingsResponse
        {
            PhotosensitivityMode = true,
            MappingProfilesJson = "[{\"name\":\"Default\",\"mappings\":[{\"channelId\":\"left\",\"bulbIds\":[\"b1\"]}]}]"
        });

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("photosensitivityMode").GetBoolean());
        Assert.Contains("left", doc.RootElement.GetProperty("mappingProfilesJson").GetString());
        Assert.Contains("trackPath", SsJson.SerializeToString(new TrackSummary { TrackPath = "lightfx/proxy-fixture.lightfx" }));
    }
}

public class SaveContractTests
{
    [Fact(DisplayName = "SAVE-004 @fixture an unreadable track reports a V- code")]
    public void Unreadable_bytes_use_a_v_code()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            StudioTrackFiles.ParseForSave(new TrackParser(), Encoding.UTF8.GetBytes("not a track"), "bad"));
        Assert.Contains("V-", error.Message);

        var parsed = new TrackParser().ParseBytes(Encoding.UTF8.GetBytes("not a track"), "bad.lightfx");
        Assert.Contains(parsed.ValidationErrors, message => message.StartsWith("V-001", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SAVE-004 @fixture duration 0 is V-004")]
    public void Zero_duration_is_v004()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot(), "roundtrip.lightfx"));
        var track = LightFXTrack.Parser.ParseFrom(bytes);
        track.Metadata.DurationMs = 0;
        var parsed = new TrackParser().ParseBytes(track.ToByteArray(), "zero.lightfx");
        Assert.Contains(parsed.ValidationErrors, message => message.StartsWith("V-004", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ERR-003 @fixture a replace with a missing or stale If-Match is a conflict")]
    public void Replace_without_a_current_if_match_conflicts()
    {
        var root = Directory.CreateTempSubdirectory("olfx-conflict-");
        try
        {
            var movie = Path.Combine(root.FullName, "Clip.mkv");
            File.WriteAllBytes(movie, new byte[] { 0 });
            var path = StudioTrackFiles.GetSavePath(movie, "conflict");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            StudioTrackFiles.EnsureNoConflict(path, ifMatch: null);

            File.WriteAllBytes(path, new byte[] { 1 });
            var token = StudioTrackFiles.UpdatedAtToken(path);

            var missing = Assert.Throws<TrackSaveConflictException>(() =>
                StudioTrackFiles.EnsureNoConflict(path, ifMatch: null));
            Assert.Contains("conflict", missing.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(token, missing.Message);

            var stale = Assert.Throws<TrackSaveConflictException>(() =>
                StudioTrackFiles.EnsureNoConflict(path, "2000-01-01T00:00:00.0000000Z"));
            Assert.Contains(token, stale.Message);

            StudioTrackFiles.EnsureNoConflict(path, token);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "SAVE-003 @fixture the sidecar drops undo and is not a playback file")]
    public void Sidecar_round_trip_is_not_a_track()
    {
        var root = Directory.CreateTempSubdirectory("olfx-sidecar-");
        try
        {
            var movie = Path.Combine(root.FullName, "Clip.mkv");
            File.WriteAllBytes(movie, new byte[] { 0 });
            var body = """
                {"version":1,"itemId":"42","trackId":"proxy-fixture","undo":[{"id":"old"}],"duration":99,
                 "sceneMarkers":[{"id":"m1","timeMs":1000,"label":"Intro","kind":"manual"}],
                 "channelGroups":[{"id":"g1","name":"Walls","mode":"mirror","channelIds":["left","right"],"spreadMs":150}]}
                """;
            var stored = StudioSidecar.Normalize(Encoding.UTF8.GetBytes(body));
            var path = StudioSidecar.GetPath(movie, "proxy-fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, stored);

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("undo", out _));
            Assert.False(doc.RootElement.TryGetProperty("duration", out _));
            Assert.Equal("Intro", doc.RootElement.GetProperty("sceneMarkers")[0].GetProperty("label").GetString());

            Assert.Throws<ArgumentException>(() => StudioSidecar.Normalize("{\"version\":2}"u8.ToArray()));
            Assert.Throws<ArgumentException>(() => StudioSidecar.Normalize("not-json"u8.ToArray()));

            var discovered = new TrackDiscoveryService(new TrackParser(), new NullLogger())
                .DiscoverTracks(movie, null);
            Assert.Empty(discovered);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string FixtureRoot()
    {
        var env = Environment.GetEnvironmentVariable("OLFX_FIXTURES");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return Path.Combine(AppContext.BaseDirectory, "fixtures");
    }
}
