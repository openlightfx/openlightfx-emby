using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpenLightFX.Emby.Studio;

public sealed record AiTrackZone(string ChannelId, double X, double Y, double Width, double Height);

/// <param name="SampleRateFps">
/// Kept so existing Studio clients still send it. Colour is taken from the detection frames, not at this rate.
/// </param>
/// <param name="SceneCutThreshold">
/// PySceneDetect content score. Studio sends 0.01–1 and the service multiplies by 100, so 0.27 is the upstream default of 27.
/// </param>
public sealed record AiTrackGenerationOptions(
    double SampleRateFps,
    double SceneCutThreshold,
    double CenterExclusionPercent,
    IReadOnlyList<AiTrackZone> Zones);

/// <summary>
/// Frame sizes and the ffmpeg graph for keyframe generation. Colour is read from the
/// cut frames (24 fps, about <see cref="DetectionWidth"/> pixels wide). The 64×36
/// stream remains available for callers that still build the split graph.
/// </summary>
public static class AiTrackAnalysis
{
    /// <summary>
    /// ContentDetector's threshold of 27 and 15-frame minimum were tuned for ~24 fps film:
    /// a cut is one frame and ordinary motion per frame stays well below 27. Sampling the
    /// whole frame at 24 fps keeps that calibration. 60 fps sources are stepped down to the
    /// same interval, so motion scores stay on the film scale the threshold expects.
    /// </summary>
    public const double DetectionFps = 24;

    /// <summary>
    /// PySceneDetect downscales so the frame is at least 256 px wide (<c>DEFAULT_MIN_WIDTH</c>).
    /// Wider than the 64×36 colour grid, still far below a full movie frame.
    /// </summary>
    public const int DetectionWidth = 256;

    public const int ColorWidth = 64;
    public const int ColorHeight = 36;

    public static (int Width, int Height) DetectionSize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 2 || sourceHeight < 2)
            return (DetectionWidth, 144);
        if (sourceWidth <= DetectionWidth)
            return (Even(sourceWidth), Even(sourceHeight));
        var height = (int)Math.Round(DetectionWidth * (sourceHeight / (double)sourceWidth) / 2.0, MidpointRounding.AwayFromZero) * 2;
        return (DetectionWidth, Math.Max(2, height));
    }

    public static long FrameTimeMs(int frameIndex, double fps) =>
        (long)Math.Round(frameIndex * 1000.0 / fps);

    public static string ToneMap(bool hdr, string? colorTransfer, bool zscale)
    {
        if (!hdr) return "";
        if (!zscale) return "format=p010le,supertonemap=tonemap=hable:desat=0,format=rgb24,";
        var transfer = StudioFfprobe.IsHlg(colorTransfer) ? "arib-std-b67" : "smpte2084";
        return "zscale=tin=" + transfer +
            ":t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0," +
            "zscale=t=bt709:m=bt709:r=tv,";
    }

    /// <summary>The cut half of <see cref="Filter"/>, without the label. Detection-only jobs map this alone.</summary>
    public static string CutBranch(int detectWidth, int detectHeight, string toneMap)
    {
        var detect = DetectionFps.ToString("0.###", CultureInfo.InvariantCulture);
        return $"fps={detect},{toneMap}scale={detectWidth.ToString(CultureInfo.InvariantCulture)}:{detectHeight.ToString(CultureInfo.InvariantCulture)}:flags=bilinear,format=rgb24";
    }

    public static string Filter(int videoStreamIndex, double sampleRateFps, int detectWidth, int detectHeight, string toneMap)
    {
        var sample = sampleRateFps.ToString("0.###", CultureInfo.InvariantCulture);
        var input = videoStreamIndex.ToString(CultureInfo.InvariantCulture);
        return $"[0:{input}]split=2[color_in][cut_in];" +
            $"[color_in]fps={sample},{toneMap}scale={ColorWidth}:{ColorHeight},format=rgb24[color];" +
            $"[cut_in]{CutBranch(detectWidth, detectHeight, toneMap)}[cut]";
    }

    /// <summary>Cut stream only. Same fps, scale, and tonemap as the split graph's <c>[cut]</c> output.</summary>
    public static string CutFilter(int videoStreamIndex, int detectWidth, int detectHeight, string toneMap) =>
        $"[0:{videoStreamIndex.ToString(CultureInfo.InvariantCulture)}]{CutBranch(detectWidth, detectHeight, toneMap)}[cut]";

    public static StudioGeneratedKeyframe Keyframe(string id, string channelId, long timeMs, (byte R, byte G, byte B) color, bool sceneCut) =>
        new()
        {
            Id = id,
            ChannelId = channelId,
            TimestampMs = timeMs,
            R = color.R,
            G = color.G,
            B = color.B,
            TransitionMs = (uint)Math.Min(sceneCut ? 200 : 1500, Math.Max(0, timeMs)),
            PowerOn = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) >= 8
        };

    private static int Even(int value) => value < 2 ? 2 : value & ~1;
}

/// <summary>One non-cancelable Studio keyframe-generation job, owned by Emby.</summary>
public sealed class AiTrackGenerationJob
{
    private readonly object _gate = new();
    private readonly List<StudioGeneratedKeyframe> _keyframes = new();
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private string _state = "pending";
    private string _error = "";
    private double _progress;
    private long _throughMs;

    public AiTrackGenerationJob(string id, string itemId, string videoPath, long durationMs, AiTrackGenerationOptions options, int sourceWidth, int sourceHeight)
    {
        Id = id;
        ItemId = itemId;
        VideoPath = videoPath;
        DurationMs = durationMs;
        Options = options;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
    }

    public string Id { get; }
    public string ItemId { get; }
    private string VideoPath { get; }
    private long DurationMs { get; }
    private AiTrackGenerationOptions Options { get; }

    public string State { get { lock (_gate) return _state; } }

    public (string State, string Error, double Progress, long ThroughMs, int Count, List<StudioGeneratedKeyframe> NewKeyframes)
        Snapshot(int afterSequence)
    {
        lock (_gate)
        {
            var start = Math.Clamp(afterSequence, 0, _keyframes.Count);
            return (_state, _error, _progress, _throughMs, _keyframes.Count,
                _keyframes.Skip(start).ToList());
        }
    }

    public void Start(string ffmpegPath, bool hdr, string? colorTransfer, bool zscale, int videoStreamIndex)
    {
        lock (_gate)
        {
            if (_state != "pending") return;
            _state = "processing";
        }
        SceneCutActivity.Enter(ItemId);
        _ = Task.Run(() => RunAsync(ffmpegPath, hdr, colorTransfer, zscale, videoStreamIndex));
    }

    private async Task RunAsync(string ffmpegPath, bool hdr, string? colorTransfer, bool zscale, int videoStreamIndex)
    {
        var (detectWidth, detectHeight) = AiTrackAnalysis.DetectionSize(_sourceWidth, _sourceHeight);
        Process? process = null;
        try
        {
            var toneMap = AiTrackAnalysis.ToneMap(hdr, colorTransfer, zscale);
            var analyzedSource = SceneCutSource.Capture(VideoPath);
            var filter = AiTrackAnalysis.CutFilter(videoStreamIndex, detectWidth, detectHeight, toneMap);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", VideoPath,
                "-filter_complex", filter,
                "-map", "[cut]", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            };
            process = new Process { StartInfo = StudioProcess.StartInfo(ffmpegPath, args) };
            if (!process.Start()) throw new InvalidOperationException("ffmpeg failed to start");
            var stderrTask = process.StandardError.ReadToEndAsync();
            var detector = new ContentDetector(Options.SceneCutThreshold);
            var cutFrame = new byte[detectWidth * detectHeight * 3];
            var colors = new SceneColorAccumulator(Options.Zones, Options.CenterExclusionPercent, detectWidth, detectHeight);
            var seenCuts = new HashSet<int>();
            var detectedCuts = new List<int>();
            var cutIndex = 0;
            var sceneStart = 0;
            var opening = true;
            var idPrefix = Id.Length >= 8 ? Id[..8] : Id;

            while (await ReadFrameAsync(process.StandardOutput.BaseStream, cutFrame).ConfigureAwait(false))
            {
                colors.Observe(cutIndex, cutFrame);
                foreach (var cut in detector.ProcessFrame(cutFrame, detectWidth, detectHeight))
                {
                    if (!seenCuts.Add(cut) || cut <= sceneStart) continue;
                    detectedCuts.Add(cut);
                    EmitScene(colors, idPrefix, sceneStart, cut, opening);
                    opening = false;
                    sceneStart = cut;
                }

                var timeMs = AiTrackAnalysis.FrameTimeMs(cutIndex, AiTrackAnalysis.DetectionFps);
                lock (_gate)
                {
                    _throughMs = timeMs;
                    _progress = DurationMs > 0 ? Math.Clamp(timeMs / (double)DurationMs, 0, 1) : 0;
                }
                cutIndex++;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffmpeg could not analyze the movie" : "ffmpeg could not analyze the movie");
            if (cutIndex > sceneStart)
                EmitScene(colors, idPrefix, sceneStart, cutIndex, opening);
            SceneCutStore.Open().Save(SceneCutDocument.Create(
                ItemId, analyzedSource, Options.SceneCutThreshold, detectWidth, DurationMs / 1000.0, detectedCuts));
            lock (_gate)
            {
                _progress = 1;
                _throughMs = DurationMs;
                _state = "done";
            }
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _state = "failed";
                _error = "The movie could not be analyzed. Check the server log and try again.";
            }
        }
        finally
        {
            SceneCutActivity.Exit(ItemId);
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }

    private void EmitScene(SceneColorAccumulator colors, string idPrefix, int sceneStart, int sceneEnd, bool opening)
    {
        var timeMs = opening ? 0 : AiTrackAnalysis.FrameTimeMs(sceneStart, AiTrackAnalysis.DetectionFps);
        var token = opening ? "c0" : "d" + sceneStart.ToString(CultureInfo.InvariantCulture);
        AddKeyframes(idPrefix, token, timeMs, colors.Close(sceneEnd), sceneCut: !opening);
    }

    private void AddKeyframes(string idPrefix, string token, long timeMs, Dictionary<string, (byte R, byte G, byte B)> colors, bool sceneCut)
    {
        var generated = new List<StudioGeneratedKeyframe>(Options.Zones.Count);
        foreach (var zone in Options.Zones)
        {
            if (!colors.TryGetValue(zone.ChannelId, out var color))
                continue;
            generated.Add(AiTrackAnalysis.Keyframe(
                $"ai-gen-{idPrefix}-{zone.ChannelId}-{token}",
                zone.ChannelId,
                timeMs,
                color,
                sceneCut));
        }

        lock (_gate)
            _keyframes.AddRange(generated);
    }

    private static async Task<bool> ReadFrameAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

}

[System.Runtime.Serialization.DataContract]
public sealed class StudioGeneratedKeyframe
{
    [System.Runtime.Serialization.DataMember(Name = "id"), System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "channelId"), System.Text.Json.Serialization.JsonPropertyName("channelId")] public string ChannelId { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "timestampMs"), System.Text.Json.Serialization.JsonPropertyName("timestampMs")] public long TimestampMs { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "r"), System.Text.Json.Serialization.JsonPropertyName("r")] public byte R { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "g"), System.Text.Json.Serialization.JsonPropertyName("g")] public byte G { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "b"), System.Text.Json.Serialization.JsonPropertyName("b")] public byte B { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "transitionMs"), System.Text.Json.Serialization.JsonPropertyName("transitionMs")] public uint TransitionMs { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "powerOn"), System.Text.Json.Serialization.JsonPropertyName("powerOn")] public bool PowerOn { get; set; }
}

public static class AiTrackGenerationJobs
{
    private static readonly ConcurrentDictionary<string, AiTrackGenerationJob> Jobs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ActiveByItem = new(StringComparer.Ordinal);

    public static AiTrackGenerationJob Start(string itemId, string videoPath, long durationMs, AiTrackGenerationOptions options,
        string ffmpegPath, bool hdr, string? transfer, bool zscale, int videoStreamIndex, int sourceWidth, int sourceHeight)
    {
        if (ActiveByItem.TryGetValue(itemId, out var previousId)
            && Jobs.TryGetValue(previousId, out var previous)
            && (previous.State is "done" or "failed"))
            ActiveByItem.TryRemove(itemId, out _);

        var id = Guid.NewGuid().ToString("N");
        var job = new AiTrackGenerationJob(id, itemId, videoPath, durationMs, options, sourceWidth, sourceHeight);
        if (!ActiveByItem.TryAdd(itemId, id))
            throw new InvalidOperationException("Keyframe generation is already running for this movie");
        Jobs[id] = job;
        job.Start(ffmpegPath, hdr, transfer, zscale, videoStreamIndex);
        return job;
    }

    public static AiTrackGenerationJob? Find(string jobId) => Jobs.TryGetValue(jobId, out var job) ? job : null;
}
