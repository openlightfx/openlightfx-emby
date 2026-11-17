using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Studio;

namespace OpenLightFX.Emby.Api;

/// <summary>
/// Scene cuts for the Studio timeline.
///
/// GET /OpenLightFX/Studio/Items/{ItemId}/SceneCuts
/// 200 JSON:
/// { "itemId", "status": "ready"|"running"|"none"|"stale",
///   "detector": { "name": "ContentDetector", "threshold", "fps", "minSceneLen" },
///   "durationSeconds", "cuts": [seconds ascending], "updatedAt" }
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
    [DataMember(Name = "updatedAt"), JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
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
        if (saved == null)
        {
            return new StudioSceneCutsResponse
            {
                ItemId = itemId,
                Status = running ? "running" : "none",
                DurationSeconds = liveDurationSeconds,
                Detector = Defaults()
            };
        }

        return new StudioSceneCutsResponse
        {
            ItemId = itemId,
            Status = saved.Status(current, running),
            DurationSeconds = saved.DurationSeconds,
            Cuts = saved.Cuts.ToList(),
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

    private static StudioSceneCutDetectorResponse Defaults() => new()
    {
        Name = ContentDetector.Name,
        Threshold = ContentDetector.DefaultThreshold,
        Fps = AiTrackAnalysis.DetectionFps,
        MinSceneLen = ContentDetector.DefaultMinSceneLength
    };
}
