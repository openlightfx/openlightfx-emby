using System.Net;
using Google.Protobuf;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;
using OpenLightFX.Emby.Studio;
using Openlightfx;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public class StudioBulbCapabilityTests
{
    [Fact]
    public void Overrides_replace_protocol_defaults()
    {
        var caps = StudioBulbCapabilities.ApplyOverrides(
            StudioBulbCapabilities.ForProtocol(BulbProtocol.Lifx),
            new CapabilityOverrides { SupportsRgb = false, MinTransitionMs = 400 });

        Assert.False(caps.SupportsRgb);
        Assert.Equal(400u, caps.MinTransitionMs);
        Assert.Equal(20u, caps.MaxCommandsPerSecond);
    }

    [Fact]
    public void Protocol_defaults_match_the_drivers()
    {
        Assert.Equal(100u, StudioBulbCapabilities.ForProtocol(BulbProtocol.Wiz).MinTransitionMs);
        Assert.Equal(50u, StudioBulbCapabilities.ForProtocol(BulbProtocol.Lifx).MinTransitionMs);
        Assert.Equal(200u, StudioBulbCapabilities.ForProtocol(BulbProtocol.Rest).MinTransitionMs);
        Assert.True(StudioBulbCapabilities.ForProtocol(BulbProtocol.Hue).SupportsRgb);
    }
}

public class StudioTrackFileTests
{
    [Fact]
    public void Saves_where_discovery_looks_and_rejects_invalid_bytes()
    {
        var root = Directory.CreateTempSubdirectory("olfx-tracks-");
        try
        {
            var movie = Path.Combine(root.FullName, "Blade Runner 2049.mkv");
            File.WriteAllBytes(movie, new byte[] { 0 });

            Assert.Throws<ArgumentException>(() =>
                StudioTrackFiles.ParseForSave(new TrackParser(), new byte[] { 1, 2, 3 }, "scene"));
            Assert.Throws<ArgumentException>(() => StudioTrackFiles.GetSavePath(movie, "../escape"));

            var bytes = SampleTrack("Neon Alley");
            var info = StudioTrackFiles.ParseForSave(new TrackParser(), bytes, "neon-alley");
            var path = StudioTrackFiles.GetSavePath(movie, "neon-alley");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("Neon Alley", info.Title);
            Assert.EndsWith(Path.Combine("lightfx", "neon-alley.lightfx"), path);

            var discovered = new TrackDiscoveryService(new TrackParser(), new NullLogger())
                .DiscoverTracks(movie, null);
            var listed = StudioTrackFiles.ToListItems("99", discovered);

            var match = Assert.Single(listed);
            Assert.Equal("neon-alley", match.TrackId);
            Assert.Equal("Neon Alley", match.Title);
            Assert.Equal("99", match.ItemId);
            Assert.Equal(Path.GetFullPath(path), match.FilePath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "LIB-001 @fixture empty movie discovery is briefly cached and invalidated on save")]
    public void Empty_discovery_cache_avoids_repeated_library_scans()
    {
        var root = Directory.CreateTempSubdirectory("olfx-discovery-cache-");
        try
        {
            var movie = Path.Combine(root.FullName, "Movie.mkv");
            File.WriteAllBytes(movie, new byte[] { 0 });
            var discovery = new TrackDiscoveryService(new TrackParser(), new NullLogger());
            Assert.Empty(discovery.DiscoverTracks(movie, null));

            var trackPath = Path.Combine(root.FullName, "lightfx", "new-track.lightfx");
            Directory.CreateDirectory(Path.GetDirectoryName(trackPath)!);
            File.WriteAllBytes(trackPath, SampleTrack("New Track"));
            Assert.Empty(discovery.DiscoverTracks(movie, null));

            discovery.InvalidateCache(movie);
            Assert.Single(discovery.DiscoverTracks(movie, null));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Lightfx_folder_wins_when_filenames_collide()
    {
        var root = Directory.CreateTempSubdirectory("olfx-collide-");
        try
        {
            var sidecar = Path.Combine(root.FullName, "scene.lightfx");
            var nestedDir = Path.Combine(root.FullName, "lightfx");
            Directory.CreateDirectory(nestedDir);
            var nested = Path.Combine(nestedDir, "scene.lightfx");
            File.WriteAllBytes(sidecar, SampleTrack("Sidecar"));
            File.WriteAllBytes(nested, SampleTrack("Nested"));

            var parser = new TrackParser();
            var listed = StudioTrackFiles.ToListItems("1", new[]
            {
                parser.Parse(sidecar),
                parser.Parse(nested)
            });

            Assert.Equal(2, listed.Count);
            Assert.Equal("scene", listed[0].TrackId);
            Assert.Equal("Nested", listed[0].Title);
            Assert.StartsWith("scene~", listed[1].TrackId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Index_round_trips_a_track_id()
    {
        var root = Directory.CreateTempSubdirectory("olfx-index-");
        try
        {
            var index = new StudioTrackIndex(Path.Combine(root.FullName, "studio-tracks.json"));
            index.Remember("neon-alley", "99", Path.Combine(root.FullName, "neon-alley.lightfx"));

            var reloaded = new StudioTrackIndex(Path.Combine(root.FullName, "studio-tracks.json"));
            Assert.True(reloaded.TryGet("NEON-ALLEY", out var entry));
            Assert.Equal("99", entry.ItemId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    internal static byte[] SampleTrack(string title)
    {
        var track = new LightFXTrack
        {
            Version = 1,
            Metadata = new TrackMetadata
            {
                Title = title,
                DurationMs = 5_000
            }
        };
        track.Channels.Add(new Channel { Id = "left", DisplayName = "Left" });
        return track.ToByteArray();
    }
}

public class StudioPreviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Coalesces_frames_and_respects_the_send_interval()
    {
        var driver = new FakeDriver
        {
            Capabilities = new BulbCapabilityProfile(true, true, 2000, 6500, MinTransitionMs: 0, MaxCommandsPerSecond: 2)
        };
        var session = await Session(driver, photosensitivity: false);

        await session.SubmitFrameAsync(new[] { Sample(1) }, T0);
        await session.SubmitFrameAsync(new[] { Sample(2) }, T0.AddMilliseconds(10));
        await session.SubmitFrameAsync(new[] { Sample(3) }, T0.AddMilliseconds(20));
        await session.PumpAsync(T0.AddMilliseconds(499));
        Assert.Single(driver.States);

        await session.PumpAsync(T0.AddMilliseconds(500));

        Assert.Equal(2, driver.States.Count);
        Assert.Equal(3u, driver.States[^1].Brightness);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Raises_transition_to_the_bulb_minimum()
    {
        var driver = new FakeDriver
        {
            Capabilities = new BulbCapabilityProfile(true, false, 2000, 6500, MinTransitionMs: 250, MaxCommandsPerSecond: 1000)
        };
        var session = await Session(driver, photosensitivity: false);

        await session.SubmitFrameAsync(new[] { Sample(40, transitionMs: 10) }, T0);

        Assert.Equal(250u, Assert.Single(driver.States).TransitionMs);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Applies_photosensitivity_filter_to_live_frames()
    {
        var driver = new FakeDriver
        {
            Capabilities = new BulbCapabilityProfile(true, true, 2000, 6500, MinTransitionMs: 0, MaxCommandsPerSecond: 1000)
        };
        var session = await Session(driver, photosensitivity: true);

        await session.SubmitFrameAsync(new[] { Sample(0) }, T0);
        await session.SubmitFrameAsync(new[] { Sample(100) }, T0.AddSeconds(1));

        Assert.Equal(2, driver.States.Count);
        Assert.Equal(50u, driver.States[^1].Brightness);
        Assert.True(driver.States[^1].TransitionMs >= 200);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Stops_and_restores_after_ten_seconds_without_frames()
    {
        var driver = new FakeDriver
        {
            Prior = new BulbState(9, 8, 7, 40, null, true),
            Capabilities = new BulbCapabilityProfile(true, true, 2000, 6500, 0, 1000)
        };
        var session = await Session(driver, photosensitivity: false);
        await session.SubmitFrameAsync(new[] { Sample(80, r: 255) }, T0);

        await session.PumpAsync(T0.AddSeconds(9));
        Assert.True(session.IsActive);

        await session.PumpAsync(T0.AddSeconds(10));

        Assert.False(session.IsActive);
        var restored = driver.States[^1];
        Assert.Equal((byte)9, restored.R);
        Assert.Equal((byte)8, restored.G);
        Assert.Equal((byte)7, restored.B);
        Assert.Equal(40u, restored.Brightness);
    }

    [Fact]
    public async Task Powers_off_when_the_previous_state_was_off()
    {
        var driver = new FakeDriver
        {
            Prior = new BulbState(0, 0, 0, 0, null, false)
        };
        var session = await Session(driver, photosensitivity: false);

        await session.PumpAsync(T0.AddSeconds(10));

        Assert.False(session.IsActive);
        Assert.Equal(new[] { false }, driver.Power);
    }

    [Fact(DisplayName = "PREV-006 @fixture a preview frame reaches the bulb")]
    public async Task Preview_frame_reaches_the_bulb()
    {
        var driver = new FakeDriver
        {
            Capabilities = new BulbCapabilityProfile(true, true, 2000, 6500, MinTransitionMs: 0, MaxCommandsPerSecond: 20)
        };
        var session = await Session(driver, photosensitivity: false);

        await session.SubmitFrameAsync(new[] { new StudioPreviewSample("b1", 255, 0, 0, 100, 0) }, T0);

        var sent = Assert.Single(driver.States);
        Assert.Equal((byte)255, sent.R);
        Assert.Equal((byte)0, sent.G);
        Assert.Equal((byte)0, sent.B);
        Assert.Equal(100u, sent.Brightness);
        Assert.Equal(0u, sent.TransitionMs);
        await session.DisposeAsync();
    }

    [Fact(DisplayName = "PLUG-005 @fixture takeover starts while playback is active")]
    public async Task Takeover_starts_while_playback_is_active()
    {
        var host = new StudioPreviewHost(startPump: false);
        var started = await host.TryStartAsync(
            playbackActive: () => true,
            takeover: true,
            mapping: new Dictionary<string, IReadOnlyList<string>> { ["left"] = new[] { "b1" } },
            bulbs: new[] { Bulb("b1") },
            photosensitivity: false,
            createDriver: _ => new FakeDriver(),
            now: T0);

        Assert.True(started);
        Assert.True(host.IsActive);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Refuses_to_start_while_playback_is_active()
    {
        var host = new StudioPreviewHost(startPump: false);
        var called = false;

        var started = await host.TryStartAsync(
            playbackActive: () => true,
            takeover: false,
            mapping: new Dictionary<string, IReadOnlyList<string>> { ["left"] = new[] { "b1" } },
            bulbs: new[] { Bulb("b1") },
            photosensitivity: false,
            createDriver: _ =>
            {
                called = true;
                return new FakeDriver();
            },
            now: T0);

        Assert.False(started);
        Assert.False(called);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_bulb_is_rejected()
    {
        var host = new StudioPreviewHost(startPump: false);
        await Assert.ThrowsAsync<ArgumentException>(() => host.TryStartAsync(
            () => false,
            takeover: false,
            new Dictionary<string, IReadOnlyList<string>> { ["left"] = new[] { "missing" } },
            new[] { Bulb("b1") },
            false,
            _ => new FakeDriver(),
            T0));
        await host.DisposeAsync();
    }

    private static async Task<StudioPreviewSession> Session(FakeDriver driver, bool photosensitivity)
    {
        return await StudioPreviewSession.CreateAsync(
            new Dictionary<string, IReadOnlyList<string>> { ["left"] = new[] { "b1" } },
            new[] { Bulb("b1") },
            photosensitivity,
            _ => driver,
            T0);
    }

    private static BulbConfig Bulb(string id) => new WizBulbConfig
    {
        Id = id,
        Name = id,
        IpAddress = IPAddress.Loopback
    };

    private static StudioPreviewSample Sample(int brightness, int transitionMs = 0, int r = 10) =>
        new("b1", r, 20, 30, brightness, transitionMs);

    private sealed class FakeDriver : IBulbDriver
    {
        public BulbCapabilityProfile Capabilities { get; init; } =
            new(true, true, 2000, 6500, 100, 20);

        public BulbState? Prior { get; init; }
        public List<(byte R, byte G, byte B, uint Brightness, uint TransitionMs)> States { get; } = new();
        public List<bool> Power { get; } = new();
        public string DriverName => "fake";
        public BulbCapabilityProfile GetCapabilities() => Capabilities;
        public Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
        {
            States.Add((r, g, b, brightness, transitionMs));
            return Task.CompletedTask;
        }
        public Task SetColor(byte r, byte g, byte b, uint transitionMs = 0) => Task.CompletedTask;
        public Task SetBrightness(uint brightness, uint transitionMs = 0) => Task.CompletedTask;
        public Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0) => Task.CompletedTask;
        public Task SetPower(bool on)
        {
            Power.Add(on);
            return Task.CompletedTask;
        }
        public Task<bool> TestConnection() => Task.FromResult(true);
        public Task<BulbState?> GetCurrentState() => Task.FromResult(Prior);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
