namespace OpenLightFX.Emby.Services;

using OpenLightFX.Emby.Models;
using System.Text.Json;

public class TrackSelectionService
{
    private readonly string _selectionsFilePath;
    private readonly object _gate = new();
    private Dictionary<string, TrackSelection> _selections;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TrackSelectionService(string pluginDataPath)
    {
        var dataDir = Path.Combine(pluginDataPath, "OpenLightFX");
        Directory.CreateDirectory(dataDir);
        _selectionsFilePath = Path.Combine(dataDir, "track-selections.json");
        _selections = Load();
    }

    /// <summary>
    /// Get the selected track path for a media item with optional device scope (EMB-038).
    /// Checks device-scoped selection first ("itemId:deviceId"), falls back to global ("itemId").
    /// </summary>
    public string? GetSelectedTrack(string itemId, string? deviceId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(deviceId) &&
                _selections.TryGetValue($"{itemId}:{deviceId}", out var deviceSelection))
                return deviceSelection.TrackPath;
            return _selections.TryGetValue(itemId, out var selection) ? selection.TrackPath : null;
        }
    }

    /// <summary>
    /// Resolve the active playback track, defaulting to the first valid discovered track.
    /// The default becomes the movie-wide selection unless a stale device override needs repair.
    /// </summary>
    public TrackInfo? ResolveTrackForPlayback(
        string itemId, string? deviceId, IEnumerable<TrackInfo> tracks)
    {
        var available = tracks
            .Where(track => track.IsValid)
            .OrderBy(track => track.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.FilePath, StringComparer.Ordinal)
            .ToList();
        if (available.Count == 0)
            return null;

        lock (_gate)
        {
            var deviceKey = string.IsNullOrEmpty(deviceId) ? null : $"{itemId}:{deviceId}";
            TrackSelection? deviceSelection = null;
            var hasDeviceSelection = deviceKey != null &&
                _selections.TryGetValue(deviceKey, out deviceSelection);
            _selections.TryGetValue(itemId, out var globalSelection);
            var selected = hasDeviceSelection ? deviceSelection : globalSelection;

            var selectedTrack = selected == null
                ? null
                : available.FirstOrDefault(track => string.Equals(
                    track.FilePath, selected.TrackPath, StringComparison.Ordinal));
            if (selectedTrack != null)
                return selectedTrack;

            var defaultTrack = available[0];
            var selectionKey = hasDeviceSelection ? deviceKey! : itemId;
            _selections[selectionKey] = new TrackSelection
            {
                ItemId = itemId,
                TrackPath = defaultTrack.FilePath,
                SelectedAt = DateTime.UtcNow
            };
            Save();
            return defaultTrack;
        }
    }

    /// <summary>Get the global selected track path (no device scope).</summary>
    public string? GetGlobalSelectedTrack(string itemId)
    {
        lock (_gate)
            return _selections.TryGetValue(itemId, out var selection) ? selection.TrackPath : null;
    }

    /// <summary>Get the device-scoped selected track path, or null.</summary>
    public string? GetDeviceSelectedTrack(string itemId, string deviceId)
    {
        lock (_gate)
            return _selections.TryGetValue($"{itemId}:{deviceId}", out var selection) ? selection.TrackPath : null;
    }

    /// <summary>
    /// Set the selected track for a media item.
    /// If deviceId is provided, the selection is device-scoped (EMB-038).
    /// </summary>
    public void SetSelectedTrack(string itemId, string trackPath, string? deviceId = null)
    {
        lock (_gate)
        {
            var key = string.IsNullOrEmpty(deviceId) ? itemId : $"{itemId}:{deviceId}";
            _selections[key] = new TrackSelection
            {
                ItemId = itemId,
                TrackPath = trackPath,
                SelectedAt = DateTime.UtcNow
            };
            Save();
        }
    }

    /// <summary>Set the movie-wide selection and clear every legacy device override for that item.</summary>
    public void SetGlobalSelectionAndClearDeviceOverrides(string itemId, string trackPath)
    {
        lock (_gate)
        {
            foreach (var key in _selections.Keys
                .Where(key => key.StartsWith(itemId + ":", StringComparison.Ordinal))
                .ToArray())
                _selections.Remove(key);
            _selections[itemId] = new TrackSelection
            {
                ItemId = itemId,
                TrackPath = trackPath,
                SelectedAt = DateTime.UtcNow
            };
            Save();
        }
    }

    /// <summary>
    /// Clear the selected track for a media item.
    /// If deviceId is provided, only the device-scoped selection is cleared.
    /// </summary>
    public void ClearSelectedTrack(string itemId, string? deviceId = null)
    {
        lock (_gate)
        {
            var key = string.IsNullOrEmpty(deviceId) ? itemId : $"{itemId}:{deviceId}";
            if (_selections.Remove(key)) Save();
        }
    }

    /// <summary>Update selections when a legacy in-library track is saved into plugin storage.</summary>
    public void ReplaceTrackPath(string oldTrackPath, string newTrackPath)
    {
        lock (_gate)
        {
            var matches = _selections.Values
                .Where(selection => string.Equals(
                    selection.TrackPath, oldTrackPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) return;

            foreach (var selection in matches)
                selection.TrackPath = newTrackPath;
            Save();
        }
    }

    /// <summary>Clear every selection that points at a deleted track file.</summary>
    public void ClearTrackPath(string trackPath)
    {
        lock (_gate)
        {
            var keys = _selections
                .Where(entry => string.Equals(entry.Value.TrackPath, trackPath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToArray();
            if (keys.Length == 0)
                return;

            foreach (var key in keys)
                _selections.Remove(key);
            Save();
        }
    }

    /// <summary>Get all track selections.</summary>
    public IReadOnlyDictionary<string, TrackSelection> GetAllSelections()
    {
        lock (_gate)
            return new Dictionary<string, TrackSelection>(_selections);
    }

    private Dictionary<string, TrackSelection> Load()
    {
        if (!File.Exists(_selectionsFilePath))
            return new Dictionary<string, TrackSelection>();
        try
        {
            var json = File.ReadAllText(_selectionsFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, TrackSelection>>(json, JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_selections, JsonOptions);
        File.WriteAllText(_selectionsFilePath, json);
    }
}
