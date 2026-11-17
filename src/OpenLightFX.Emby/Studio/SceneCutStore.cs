using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// One movie's saved ContentDetector cuts. The file is
/// <c>{plugin data}/OpenLightFX/scene-cuts/{itemId}.json</c>.
/// <c>source</c> is the media file's path, size, and UTC mtime so a later open can tell the
/// result is stale. <c>detector.version</c> is <see cref="ContentDetector.PersistenceVersion"/>.
/// </summary>
public sealed class SceneCutDocument
{
    public string ItemId { get; set; } = "";
    public SceneCutSource Source { get; set; } = new();
    public SceneCutDetectorSettings Detector { get; set; } = new();
    public double DurationSeconds { get; set; }
    public List<double> Cuts { get; set; } = new();
    public string UpdatedAt { get; set; } = "";

    public static SceneCutDocument Create(string itemId, SceneCutSource source, double threshold, int width, double durationSeconds, IReadOnlyList<int> cutFrames)
    {
        var cuts = cutFrames
            .Distinct()
            .OrderBy(frame => frame)
            .Select(frame => frame / AiTrackAnalysis.DetectionFps)
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
            Cuts = cuts,
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
        document.Cuts = document.Cuts.Distinct().OrderBy(cut => cut).ToList();
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
                document.Cuts = document.Cuts?.Distinct().OrderBy(cut => cut).ToList() ?? new List<double>();
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
