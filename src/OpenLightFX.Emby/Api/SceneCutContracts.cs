using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Studio;

namespace OpenLightFX.Emby.Api;

/// <summary>
/// Scene markers for the Studio timeline: the movie's one marker store (detected cuts plus
/// markers a person placed). Studio keeps no scene markers of its own.
///
/// GET /OpenLightFX/Studio/Items/{ItemId}/SceneCuts
/// 200 JSON:
/// { "itemId", "status": "ready"|"running"|"none"|"stale",
///   "detector": { "name": "ContentDetector", "threshold", "fps", "minSceneLen" },
///   "durationSeconds", "cuts": [seconds ascending],
///   "markers": [{ "id", "timeMs", "kind": "detected"|"manual", "label" }], "updatedAt" }
/// <c>cuts</c> is every marker time in seconds. Detected markers are withheld while stale.
/// <c>threshold</c> is PySceneDetect's content score (the 0.01–1 slider times 100).
/// <c>running</c> wins over a saved result. <c>stale</c> means the media file's path, size, or
/// mtime changed, or the detector version/fps/min scene length no longer matches. Saved cuts are
/// still returned. <c>none</c> means nothing is stored. <c>updatedAt</c> is UTC ISO-8601, or "".
///
/// POST /OpenLightFX/Studio/Items/{ItemId}/SceneCuts
/// Optional body { "sceneCutThreshold": 0.01–1 }. Omitted uses 0.27 (score 27).
/// 200 JSON: { "jobId", "itemId", "state": "processing"|"done"|"failed", "error", "progress", "throughMs" }
/// 409 when detection is already running for the item.
///
/// GET /OpenLightFX/Studio/SceneCuts/{JobId}
/// Same job JSON as the POST response.
///
/// Marker edits (Admin). Each returns the updated scene-cut JSON above.
/// POST   /OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers              { "timeMs", "label"? }  add a manual marker
/// PUT    /OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers/{MarkerId}   { "timeMs"?, "label"? } move or rename
/// DELETE /OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers/{MarkerId}                           delete
/// Times are whole milliseconds inside the movie. A moved detected marker stays detected.
/// Re-running detection replaces detected markers and keeps manual ones, so detected markers
/// cannot be edited while detection runs (409). 404 when the marker id is unknown.
/// </summary>
[Route("/OpenLightFX/Studio/Items/{ItemId}/SceneCuts", "GET", Description = "Scene cuts for the timeline")]
[Authenticated]
public class GetStudioSceneCuts : IReturn<StudioSceneCutsResponse>
{
    public string ItemId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/SceneCuts", "POST", Description = "Detect scene cuts for the timeline")]
[Authenticated(Roles = "Admin")]
[DataContract]
public class PostStudioSceneCuts : IReturn<StudioSceneCutJobResponse>
{
    public string ItemId { get; set; } = "";

    /// <summary>Optional 0.01–1 slider. Omitted uses <see cref="ContentDetector.StudioSettingDefault"/>.</summary>
    [DataMember(Name = "sceneCutThreshold"), JsonPropertyName("sceneCutThreshold")]
    public double? SceneCutThreshold { get; set; }
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers", "POST", Description = "Add a scene marker")]
[Authenticated(Roles = "Admin")]
[DataContract]
public class PostStudioSceneMarker : IReturn<StudioSceneCutsResponse>
{
    public string ItemId { get; set; } = "";

    [DataMember(Name = "timeMs"), JsonPropertyName("timeMs")]
    public long? TimeMs { get; set; }

    [DataMember(Name = "label"), JsonPropertyName("label")]
    public string? Label { get; set; }
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers/{MarkerId}", "PUT", Description = "Move or rename a scene marker")]
[Authenticated(Roles = "Admin")]
[DataContract]
public class PutStudioSceneMarker : IReturn<StudioSceneCutsResponse>
{
    public string ItemId { get; set; } = "";

    public string MarkerId { get; set; } = "";

    [DataMember(Name = "timeMs"), JsonPropertyName("timeMs")]
    public long? TimeMs { get; set; }

    [DataMember(Name = "label"), JsonPropertyName("label")]
    public string? Label { get; set; }
}

[Route("/OpenLightFX/Studio/Items/{ItemId}/SceneCuts/Markers/{MarkerId}", "DELETE", Description = "Delete a scene marker")]
[Authenticated(Roles = "Admin")]
public class DeleteStudioSceneMarker : IReturn<StudioSceneCutsResponse>
{
    public string ItemId { get; set; } = "";

    public string MarkerId { get; set; } = "";
}

[Route("/OpenLightFX/Studio/SceneCuts/{JobId}", "GET", Description = "Scene cut detection progress")]
[Authenticated(Roles = "Admin")]
public class GetStudioSceneCutJob : IReturn<StudioSceneCutJobResponse>
{
    public string JobId { get; set; } = "";
}

[DataContract]
public sealed class StudioSceneCutsResponse
{
    [DataMember(Name = "itemId"), JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [DataMember(Name = "status"), JsonPropertyName("status")] public string Status { get; set; } = "none";
    [DataMember(Name = "detector"), JsonPropertyName("detector")] public StudioSceneCutDetectorResponse Detector { get; set; } = new();
    [DataMember(Name = "durationSeconds"), JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
    [DataMember(Name = "cuts"), JsonPropertyName("cuts")] public List<double> Cuts { get; set; } = new();
    [DataMember(Name = "markers"), JsonPropertyName("markers")] public List<StudioSceneMarkerResponse> Markers { get; set; } = new();
    [DataMember(Name = "updatedAt"), JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
}

[DataContract]
public sealed class StudioSceneMarkerResponse
{
    [DataMember(Name = "id"), JsonPropertyName("id")] public string Id { get; set; } = "";
    [DataMember(Name = "timeMs"), JsonPropertyName("timeMs")] public long TimeMs { get; set; }
    [DataMember(Name = "kind"), JsonPropertyName("kind")] public string Kind { get; set; } = SceneMarkerEntry.Detected;
    [DataMember(Name = "label"), JsonPropertyName("label")] public string Label { get; set; } = "";
}

[DataContract]
public sealed class StudioSceneCutDetectorResponse
{
    [DataMember(Name = "name"), JsonPropertyName("name")] public string Name { get; set; } = ContentDetector.Name;
    [DataMember(Name = "threshold"), JsonPropertyName("threshold")] public double Threshold { get; set; } = ContentDetector.DefaultThreshold;
    [DataMember(Name = "fps"), JsonPropertyName("fps")] public double Fps { get; set; } = AiTrackAnalysis.DetectionFps;
    [DataMember(Name = "minSceneLen"), JsonPropertyName("minSceneLen")] public int MinSceneLen { get; set; } = ContentDetector.DefaultMinSceneLength;
}

[DataContract]
public sealed class StudioSceneCutJobResponse
{
    [DataMember(Name = "jobId"), JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [DataMember(Name = "itemId"), JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [DataMember(Name = "state"), JsonPropertyName("state")] public string State { get; set; } = "";
    [DataMember(Name = "error"), JsonPropertyName("error")] public string Error { get; set; } = "";
    [DataMember(Name = "progress"), JsonPropertyName("progress")] public double Progress { get; set; }
    [DataMember(Name = "throughMs"), JsonPropertyName("throughMs")] public long ThroughMs { get; set; }
}

public static class SceneCutApi
{
    public static double ResolveThreshold(double? sceneCutThreshold)
    {
        if (sceneCutThreshold is not double value)
            return ContentDetector.DefaultThreshold;
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(sceneCutThreshold), "Scene cut threshold must be a finite number");
        return ContentDetector.ThresholdFromStudioSetting(value);
    }

    public static StudioSceneCutsResponse Response(string itemId, SceneCutDocument? saved, SceneCutSource? current, bool running, double liveDurationSeconds)
    {
        if (saved == null || !saved.HasDetection)
        {
            // No detection yet; a manual-only document still carries the person's markers.
            var manual = saved?.VisibleMarkers(includeDetected: false) ?? Array.Empty<SceneMarkerEntry>();
            return new StudioSceneCutsResponse
            {
                ItemId = itemId,
                Status = running ? "running" : "none",
                DurationSeconds = saved is { DurationSeconds: > 0 } ? saved.DurationSeconds : liveDurationSeconds,
                Cuts = manual.Select(marker => marker.TimeMs / 1000.0).Distinct().ToList(),
                Markers = manual.Select(MarkerResponse).ToList(),
                UpdatedAt = saved?.UpdatedAt ?? "",
                Detector = Defaults()
            };
        }

        var status = saved.Status(current, running);
        var markers = saved.VisibleMarkers(includeDetected: status != "stale");
        return new StudioSceneCutsResponse
        {
            ItemId = itemId,
            Status = status,
            DurationSeconds = saved.DurationSeconds,
            Cuts = markers.Select(marker => marker.TimeMs / 1000.0).Distinct().ToList(),
            Markers = markers.Select(MarkerResponse).ToList(),
            UpdatedAt = saved.UpdatedAt,
            Detector = new StudioSceneCutDetectorResponse
            {
                Name = saved.Detector.Name,
                Threshold = saved.Detector.Threshold,
                Fps = saved.Detector.Fps,
                MinSceneLen = saved.Detector.MinSceneLen
            }
        };
    }

    public static StudioSceneCutJobResponse Job(SceneCutDetectionJob job)
    {
        var snapshot = job.Snapshot();
        return new StudioSceneCutJobResponse
        {
            JobId = job.Id,
            ItemId = job.ItemId,
            State = snapshot.State,
            Error = snapshot.Error,
            Progress = snapshot.Progress,
            ThroughMs = snapshot.ThroughMs
        };
    }

    /// <summary>Validate a marker time: whole milliseconds, after the start, inside the movie.</summary>
    public static long RequireMarkerTime(long? timeMs, double durationSeconds)
    {
        if (timeMs is not long value || value <= 0)
            throw new ArgumentException("timeMs must be a positive whole number of milliseconds");
        if (durationSeconds > 0 && value > Math.Round(durationSeconds * 1000))
            throw new ArgumentException("timeMs is past the end of the movie");
        return value;
    }

    public static string MarkerLabel(string? label)
    {
        var trimmed = (label ?? "").Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static StudioSceneMarkerResponse MarkerResponse(SceneMarkerEntry marker) => new()
    {
        Id = marker.Id,
        TimeMs = marker.TimeMs,
        Kind = marker.IsDetected ? SceneMarkerEntry.Detected : SceneMarkerEntry.Manual,
        Label = marker.Label
    };

    private static StudioSceneCutDetectorResponse Defaults() => new()
    {
        Name = ContentDetector.Name,
        Threshold = ContentDetector.DefaultThreshold,
        Fps = AiTrackAnalysis.DetectionFps,
        MinSceneLen = ContentDetector.DefaultMinSceneLength
    };
}
