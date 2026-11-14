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
