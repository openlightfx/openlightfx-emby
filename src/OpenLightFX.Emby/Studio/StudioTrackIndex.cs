using System.Text.Json;

namespace OpenLightFX.Emby.Studio;

public sealed class StudioTrackIndexEntry
{
    public string TrackId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string FilePath { get; set; } = "";
}

/// <summary>
/// Maps a Studio track id to the file discovery will play. Survives process restarts.
/// </summary>
public sealed class StudioTrackIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, StudioTrackIndexEntry> _entries;

    public StudioTrackIndex(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public bool TryGet(string trackId, out StudioTrackIndexEntry entry)
    {
        lock (_gate)
            return _entries.TryGetValue(trackId, out entry!);
    }

    public void Remember(string trackId, string itemId, string filePath)
    {
        lock (_gate)
        {
            _entries[trackId] = new StudioTrackIndexEntry
            {
                TrackId = trackId,
                ItemId = itemId,
                FilePath = filePath
            };
            Save();
        }
    }

    public void RememberAll(IEnumerable<StudioListedTrack> tracks)
    {
        lock (_gate)
        {
            foreach (var track in tracks)
            {
                _entries[track.TrackId] = new StudioTrackIndexEntry
                {
                    TrackId = track.TrackId,
                    ItemId = track.ItemId,
                    FilePath = track.FilePath
                };
            }
            Save();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_entries.Values.ToList(), JsonOptions);
        File.WriteAllText(_path, json);
    }

    private static Dictionary<string, StudioTrackIndexEntry> Load(string path)
    {
        var entries = new Dictionary<string, StudioTrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return entries;

        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<StudioTrackIndexEntry>>(json, JsonOptions);
            if (list == null)
                return entries;

            foreach (var entry in list)
            {
                if (string.IsNullOrEmpty(entry.TrackId) || string.IsNullOrEmpty(entry.FilePath))
                    continue;
                entries[entry.TrackId] = entry;
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, StudioTrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return entries;
        }

        return entries;
    }
}
