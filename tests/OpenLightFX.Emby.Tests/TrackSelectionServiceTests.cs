using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;
using Openlightfx;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class TrackSelectionServiceTests
{
    [Fact(DisplayName = "PLAY-001 @unit playback defaults to a stable discovered track and persists it globally")]
    public void Playback_defaults_to_first_valid_track_and_persists_global_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            var tracks = new[]
            {
                CreateTrack("/movie/lightfx/zeta.lightfx"),
                CreateTrack("/movie/lightfx/alpha.lightfx"),
                CreateTrack("/movie/lightfx/invalid.lightfx", isValid: false)
            };

            var selected = service.ResolveTrackForPlayback("movie", "living-room-tv", tracks);

            Assert.Equal("/movie/lightfx/alpha.lightfx", selected?.FilePath);
            Assert.Equal(selected?.FilePath, service.GetGlobalSelectedTrack("movie"));
            Assert.Equal(selected?.FilePath, service.GetSelectedTrack("movie", "theater-tv"));
            Assert.Null(service.GetDeviceSelectedTrack("movie", "living-room-tv"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(DisplayName = "PLAY-002 @unit playback honors a valid device-scoped track")]
    public void Playback_honors_device_scoped_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            service.SetSelectedTrack("movie", "/movie/global.lightfx");
            service.SetSelectedTrack("movie", "/movie/device.lightfx", "living-room-tv");

            var selected = service.ResolveTrackForPlayback("movie", "living-room-tv", new[]
            {
                CreateTrack("/movie/global.lightfx"),
                CreateTrack("/movie/device.lightfx")
            });

            Assert.Equal("/movie/device.lightfx", selected?.FilePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(DisplayName = "PLAY-003 @unit playback repairs a stale selection to a valid discovered track")]
    public void Playback_replaces_stale_device_selection_with_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            service.SetSelectedTrack("movie", "/movie/global.lightfx");
            service.SetSelectedTrack("movie", "/movie/missing.lightfx", "living-room-tv");

            var selected = service.ResolveTrackForPlayback("movie", "living-room-tv", new[]
            {
                CreateTrack("/movie/available.lightfx")
            });

            Assert.Equal("/movie/available.lightfx", selected?.FilePath);
            Assert.Equal("/movie/available.lightfx", service.GetDeviceSelectedTrack("movie", "living-room-tv"));
            Assert.Equal("/movie/global.lightfx", service.GetSelectedTrack("movie", "theater-tv"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(DisplayName = "PLAY-004 @unit playback does not select when no valid track exists")]
    public void Playback_does_not_create_selection_without_a_valid_track()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            var selected = service.ResolveTrackForPlayback("movie", "living-room-tv", new[]
            {
                CreateTrack("/movie/invalid.lightfx", isValid: false)
            });

            Assert.Null(selected);
            Assert.Null(service.GetGlobalSelectedTrack("movie"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(DisplayName = "WATCH-001 @unit deleting a track clears global and device selections for that path")]
    public void Deleting_track_clears_all_matching_saved_selections()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            var deletedPath = Path.Combine("movie", "lightfx", "deleted.lightfx");
            service.SetSelectedTrack("movie-a", deletedPath);
            service.SetSelectedTrack("movie-a", deletedPath, "screen-a");
            service.SetSelectedTrack("movie-b", Path.Combine("movie", "other.lightfx"));

            service.ClearTrackPath(deletedPath);
            var reloaded = new TrackSelectionService(root);

            Assert.Null(reloaded.GetGlobalSelectedTrack("movie-a"));
            Assert.Null(reloaded.GetDeviceSelectedTrack("movie-a", "screen-a"));
            Assert.Equal(Path.Combine("movie", "other.lightfx"), reloaded.GetGlobalSelectedTrack("movie-b"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static TrackInfo CreateTrack(string path, bool isValid = true) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Track = new LightFXTrack(),
        IsValid = isValid
    };

    [Fact(DisplayName = "WATCH-001 @unit global active track clears device overrides")]
    public void Global_selection_replaces_device_scoped_selections()
    {
        var root = Path.Combine(Path.GetTempPath(), "olfx-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TrackSelectionService(root);
            service.SetSelectedTrack("movie", "device-track-a", "device-a");
            service.SetSelectedTrack("movie", "device-track-b", "device-b");
            service.SetGlobalSelectionAndClearDeviceOverrides("movie", "movie-track");

            Assert.Equal("movie-track", service.GetSelectedTrack("movie"));
            Assert.Equal("movie-track", service.GetSelectedTrack("movie", "device-a"));
            Assert.Null(service.GetDeviceSelectedTrack("movie", "device-a"));
            Assert.Null(service.GetDeviceSelectedTrack("movie", "device-b"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
