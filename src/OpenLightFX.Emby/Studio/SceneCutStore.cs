using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// One scene marker on a movie's timeline. <c>kind</c> is <c>detected</c> (from ContentDetector;
/// a re-run replaces these) or <c>manual</c> (placed by a person; kept across re-runs).
/// Times are whole milliseconds. Detected ids are <c>d{originalMs}</c>, manual ids <c>m{random}</c>.
/// </summary>
public sealed class SceneMarkerEntry
{
    public const string Detected = "detected";
    public const string Manual = "manual";

    public string Id { get; set; } = "";
    public long TimeMs { get; set; }
    public string Kind { get; set; } = Detected;
    public string Label { get; set; } = "";

    public bool IsDetected => !string.Equals(Kind, Manual, StringComparison.Ordinal);

    public static SceneMarkerEntry FromDetection(long timeMs) =>
        new() { Id = "d" + timeMs.ToString(CultureInfo.InvariantCulture), TimeMs = timeMs, Kind = Detected };

    public static SceneMarkerEntry CreateManual(long timeMs, string label) =>
        new() { Id = "m" + Guid.NewGuid().ToString("N")[..12], TimeMs = timeMs, Kind = Manual, Label = label };
}

/// <summary>
/// The one store of a movie's scene markers: ContentDetector cuts plus markers a person added,
/// moved, renamed, or deleted. The file is <c>{plugin data}/OpenLightFX/scene-cuts/{itemId}.json</c>.
/// <c>source</c> is the completed edit proxy's path, size, and UTC mtime so a later open can tell
/// the detection is stale. <c>detector.version</c> is <see cref="ContentDetector.PersistenceVersion"/>.
/// Re-running detection replaces only <c>detected</c> markers (see <see cref="WithManualFrom"/>).
/// </summary>
public sealed class SceneCutDocument
{
    public string ItemId { get; set; } = "";
    public SceneCutSource Source { get; set; } = new();
    public SceneCutDetectorSettings Detector { get; set; } = new();
    public double DurationSeconds { get; set; }
    public List<SceneMarkerEntry> Markers { get; set; } = new();
    public string UpdatedAt { get; set; } = "";

    /// <summary>Pre-marker files stored detected cuts as seconds. Read once, then written as markers.</summary>
    [JsonPropertyName("cuts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double>? LegacyCuts { get; set; }

    /// <summary>False for a document that only holds manual markers (detection never finished).</summary>
    [JsonIgnore]
    public bool HasDetection => !string.IsNullOrEmpty(Source.Path);

    /// <summary>Markers a consumer should see: detected ones only while the detection is current.</summary>
    public IReadOnlyList<SceneMarkerEntry> VisibleMarkers(bool includeDetected) =>
        Markers.Where(marker => includeDetected || !marker.IsDetected)
            .OrderBy(marker => marker.TimeMs)
            .ThenBy(marker => marker.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>All current scene marker times in seconds, ascending and distinct.</summary>
    public IReadOnlyList<double> CutSeconds(bool includeDetected = true) =>
        VisibleMarkers(includeDetected).Select(marker => marker.TimeMs / 1000.0).Distinct().ToList();

    /// <summary>This detection result plus <paramref name="existing"/>'s manual markers.</summary>
    public SceneCutDocument WithManualFrom(SceneCutDocument? existing)
    {
        if (existing == null) return this;
        Markers = Markers.Where(marker => marker.IsDetected)
            .Concat(existing.Markers.Where(marker => !marker.IsDetected))
            .ToList();
        if (string.CompareOrdinal(existing.UpdatedAt, UpdatedAt) > 0)
            UpdatedAt = existing.UpdatedAt;
        return this;
    }

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    public static SceneCutDocument Create(string itemId, SceneCutSource source, double threshold, int width, double durationSeconds, IReadOnlyList<int> cutFrames)
    {
        var markers = cutFrames
            .Distinct()
            .OrderBy(frame => frame)
            .Select(frame => SceneMarkerEntry.FromDetection((long)Math.Round(frame * 1000 / AiTrackAnalysis.DetectionFps)))
            .ToList();
        return new SceneCutDocument
        {
            ItemId = itemId,
            Source = source,
            Detector = new SceneCutDetectorSettings
            {
                Name = ContentDetector.Name,
                Version = ContentDetector.PersistenceVersion,
                Threshold = threshold,
                Fps = AiTrackAnalysis.DetectionFps,
                Width = width,
                MinSceneLen = ContentDetector.DefaultMinSceneLength
            },
            DurationSeconds = durationSeconds,
            Markers = markers,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// <c>running</c> while a job is in progress, otherwise <c>ready</c> when this document still
    /// describes <paramref name="current"/>, otherwise <c>stale</c>. A missing document is <c>none</c>
    /// and is decided by the caller.
    /// </summary>
    public string Status(SceneCutSource? current, bool running)
    {
        if (running) return "running";
        if (current == null || !Source.Matches(current)) return "stale";
        if (!string.Equals(Detector.Name, ContentDetector.Name, StringComparison.Ordinal)) return "stale";
        if (!string.Equals(Detector.Version, ContentDetector.PersistenceVersion, StringComparison.Ordinal)) return "stale";
        if (Math.Abs(Detector.Fps - AiTrackAnalysis.DetectionFps) > 0.001) return "stale";
        if (Detector.MinSceneLen != ContentDetector.DefaultMinSceneLength) return "stale";
        return "ready";
    }
}

public sealed class SceneCutSource
{
    public string Path { get; set; } = "";
    public long Size { get; set; } = -1;
    public string MtimeUtc { get; set; } = "";

    public static SceneCutSource Capture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return new SceneCutSource { Path = path, Size = -1, MtimeUtc = "" };
        return new SceneCutSource
        {
            Path = path,
            Size = info.Length,
            MtimeUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)
        };
    }

    public bool Matches(SceneCutSource? current) =>
        current != null
        && Size >= 0
        && current.Size == Size
        && string.Equals(Path, current.Path, StringComparison.Ordinal)
        && string.Equals(MtimeUtc, current.MtimeUtc, StringComparison.Ordinal);
}

public sealed class SceneCutDetectorSettings
{
    public string Name { get; set; } = ContentDetector.Name;
    public string Version { get; set; } = ContentDetector.PersistenceVersion;
    public double Threshold { get; set; } = ContentDetector.DefaultThreshold;
    public double Fps { get; set; } = AiTrackAnalysis.DetectionFps;
    public int Width { get; set; }
    public int MinSceneLen { get; set; } = ContentDetector.DefaultMinSceneLength;
}

/// <summary>JSON files keyed by Emby item id under the plugin data directory.</summary>
public sealed class SceneCutStore
{
    private static readonly object IoGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _directory;

    public SceneCutStore(string pluginDataPath)
    {
        _directory = Path.Combine(pluginDataPath, "OpenLightFX", "scene-cuts");
        Directory.CreateDirectory(_directory);
    }

    public static SceneCutStore Open()
    {
        var data = OpenLightFX.Emby.Plugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emby");
        return new SceneCutStore(data);
    }

    public void Save(SceneCutDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.ItemId))
            throw new ArgumentException("Scene cuts need an item id");
        document.LegacyCuts = null;
        document.Markers = document.Markers
            .GroupBy(marker => marker.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(marker => marker.TimeMs)
            .ThenBy(marker => marker.Id, StringComparer.Ordinal)
            .ToList();
        var path = PathFor(document.ItemId);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        lock (IoGate)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
    }

    public SceneCutDocument? TryLoad(string itemId)
    {
        var path = PathFor(itemId);
        lock (IoGate)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var document = JsonSerializer.Deserialize<SceneCutDocument>(File.ReadAllText(path), JsonOptions);
                if (document == null) return null;
                document.ItemId = string.IsNullOrWhiteSpace(document.ItemId) ? itemId : document.ItemId;
                document.Markers ??= new List<SceneMarkerEntry>();
                if (document.Markers.Count == 0 && document.LegacyCuts is { Count: > 0 } legacy)
                {
                    document.Markers = legacy
                        .Where(seconds => double.IsFinite(seconds) && seconds > 0)
                        .Select(seconds => SceneMarkerEntry.FromDetection((long)Math.Round(seconds * 1000)))
                        .GroupBy(marker => marker.Id, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                }
                document.LegacyCuts = null;
                foreach (var marker in document.Markers)
                {
                    marker.Kind = marker.IsDetected ? SceneMarkerEntry.Detected : SceneMarkerEntry.Manual;
                    marker.Label ??= "";
                    if (string.IsNullOrWhiteSpace(marker.Id))
                        marker.Id = marker.IsDetected
                            ? "d" + marker.TimeMs.ToString(CultureInfo.InvariantCulture)
                            : "m" + Guid.NewGuid().ToString("N")[..12];
                }
                document.Source ??= new SceneCutSource();
                document.Detector ??= new SceneCutDetectorSettings();
                return document;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Save a detection result, keeping the movie's manual markers.</summary>
    public void SaveDetection(SceneCutDocument detected)
    {
        lock (IoGate) Save(detected.WithManualFrom(TryLoad(detected.ItemId)));
    }

    /// <summary>
    /// Read-modify-write one movie's markers under the store lock. <paramref name="edit"/> receives
    /// the saved document (a manual-only one is created when none exists) and returns false to skip
    /// the write.
    /// </summary>
    public SceneCutDocument Update(string itemId, double durationSeconds, Func<SceneCutDocument, bool> edit)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Scene cuts need an item id");
        lock (IoGate)
        {
            var document = TryLoad(itemId) ?? new SceneCutDocument
            {
                ItemId = itemId,
                Source = new SceneCutSource(),
                DurationSeconds = durationSeconds
            };
            if (!edit(document)) return document;
            document.Touch();
            Save(document);
            return document;
        }
    }

    private string PathFor(string itemId) => Path.Combine(_directory, FileToken(itemId) + ".json");

    private static string FileToken(string itemId)
    {
        var chars = itemId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var token = new string(chars);
        if (token.Length == 0) return "item";
        return token.Length <= 120 ? token : token[..120];
    }
}

/// <summary>Counts in-flight generation and detection-only jobs per item.</summary>
public static class SceneCutActivity
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Depth = new(StringComparer.Ordinal);

    public static void Enter(string itemId)
    {
        lock (Gate)
            Depth[itemId] = Depth.GetValueOrDefault(itemId) + 1;
    }

    public static void Exit(string itemId)
    {
        lock (Gate)
        {
            if (!Depth.TryGetValue(itemId, out var count)) return;
            if (count <= 1) Depth.Remove(itemId);
            else Depth[itemId] = count - 1;
        }
    }

    public static bool IsRunning(string itemId)
    {
        lock (Gate) return Depth.ContainsKey(itemId);
    }
}
