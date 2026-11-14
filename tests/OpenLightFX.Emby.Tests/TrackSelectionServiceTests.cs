using OpenLightFX.Emby.Services;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class TrackSelectionServiceTests
{
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
