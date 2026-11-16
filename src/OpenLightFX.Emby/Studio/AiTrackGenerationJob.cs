using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpenLightFX.Emby.Studio;

public sealed record AiTrackZone(string ChannelId, double X, double Y, double Width, double Height);

public sealed record AiTrackGenerationOptions(
    double SampleRateFps,
    double SceneCutThreshold,
    double CenterExclusionPercent,
    IReadOnlyList<AiTrackZone> Zones);

/// <summary>One non-cancelable Studio keyframe-generation job, owned by Emby.</summary>
public static class AiTrackSceneCut
{
    public static bool ShouldEmitKeyframe(
        bool hasPrevious,
        (byte R, byte G, byte B) previous,
        (byte R, byte G, byte B) current,
        double threshold) => !hasPrevious || IsCut(previous, current, threshold);

    public static bool IsCut((byte R, byte G, byte B) previous, (byte R, byte G, byte B) current, double threshold)
    {
        var dr = previous.R - current.R;
        var dg = previous.G - current.G;
        var db = previous.B - current.B;
        var difference = Math.Sqrt(dr * dr + dg * dg + db * db) / Math.Sqrt(3 * 255.0 * 255.0);
        return difference >= threshold;
    }
}

public sealed class AiTrackGenerationJob
{
    private const int FrameWidth = 64;
    private const int FrameHeight = 36;
    private const int BytesPerFrame = FrameWidth * FrameHeight * 3;
    private readonly object _gate = new();
    private readonly List<StudioGeneratedKeyframe> _keyframes = new();
    private string _state = "pending";
    private string _error = "";
    private double _progress;
    private long _throughMs;

    public AiTrackGenerationJob(string id, string itemId, string videoPath, long durationMs, AiTrackGenerationOptions options)
    {
        Id = id;
        ItemId = itemId;
        VideoPath = videoPath;
        DurationMs = durationMs;
        Options = options;
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
        _ = Task.Run(() => RunAsync(ffmpegPath, hdr, colorTransfer, zscale, videoStreamIndex));
    }

    private async Task RunAsync(string ffmpegPath, bool hdr, string? colorTransfer, bool zscale, int videoStreamIndex)
    {
        try
        {
            var fps = Math.Clamp(Options.SampleRateFps, 0.1, 5.0);
            var toneMap = !hdr
                ? ""
                : zscale
                    ? "zscale=tin=" + (StudioFfprobe.IsHlg(colorTransfer) ? "arib-std-b67" : "smpte2084") +
                      ":t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0," +
                      "zscale=t=bt709:m=bt709:r=tv,"
                    : "format=p010le,supertonemap=tonemap=hable:desat=0,format=rgb24,";
            var filter = $"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)}," + toneMap +
                $"scale={FrameWidth}:{FrameHeight},format=rgb24";
            var args = new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", VideoPath,
                "-map", $"0:{videoStreamIndex}", "-an", "-vf", filter,
                "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            };
            using var process = new Process { StartInfo = StudioProcess.StartInfo(ffmpegPath, args) };
            if (!process.Start()) throw new InvalidOperationException("ffmpeg failed to start");
            var stderrTask = process.StandardError.ReadToEndAsync();
            var frame = new byte[BytesPerFrame];
            var frameIndex = 0L;
            var previous = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.Ordinal);
            while (await ReadFrameAsync(process.StandardOutput.BaseStream, frame).ConfigureAwait(false))
            {
                var timeMs = (long)Math.Round(frameIndex * 1000.0 / fps);
                var generated = new List<StudioGeneratedKeyframe>(Options.Zones.Count);
                foreach (var zone in Options.Zones)
                {
                    var color = AverageZone(frame, zone, Options.CenterExclusionPercent);
                    var hasPrevious = previous.TryGetValue(zone.ChannelId, out var old);
                    var emitKeyframe = AiTrackSceneCut.ShouldEmitKeyframe(
                        hasPrevious, old, color, Options.SceneCutThreshold);
                    var sceneCut = hasPrevious && emitKeyframe;
                    previous[zone.ChannelId] = color;
                    // Emit one initial state per channel, then only emit when the sampled zone
                    // changes enough to cross the configured scene-cut threshold.
                    if (!emitKeyframe) continue;
                    generated.Add(new StudioGeneratedKeyframe
                    {
                        Id = $"ai-gen-{Id[..8]}-{zone.ChannelId}-{frameIndex}",
                        ChannelId = zone.ChannelId,
                        TimestampMs = timeMs,
                        R = color.R, G = color.G, B = color.B,
                        TransitionMs = (uint)Math.Min(sceneCut ? 200 : 1500, timeMs),
                        PowerOn = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) >= 8
                    });
                }
                lock (_gate)
                {
                    _keyframes.AddRange(generated);
                    _throughMs = timeMs;
                    _progress = DurationMs > 0 ? Math.Clamp(timeMs / (double)DurationMs, 0, 1) : 0;
                }
                frameIndex++;
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffmpeg could not analyze the movie" : "ffmpeg could not analyze the movie");
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

    private static (byte R, byte G, byte B) AverageZone(byte[] rgb, AiTrackZone zone, double centerExclusionPercent)
    {
        var left = Math.Clamp((int)Math.Floor(zone.X * FrameWidth), 0, FrameWidth - 1);
        var top = Math.Clamp((int)Math.Floor(zone.Y * FrameHeight), 0, FrameHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((zone.X + zone.Width) * FrameWidth), left + 1, FrameWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((zone.Y + zone.Height) * FrameHeight), top + 1, FrameHeight);
        var cx = (left + right) / 2.0;
        var cy = (top + bottom) / 2.0;
        var rx = (right - left) * centerExclusionPercent / 200.0;
        var ry = (bottom - top) * centerExclusionPercent / 200.0;
        long r = 0, g = 0, b = 0, count = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (rx > 0 && ry > 0)
            {
                var dx = (x - cx) / rx;
                var dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1) continue;
            }
            var offset = (y * FrameWidth + x) * 3;
            r += rgb[offset]; g += rgb[offset + 1]; b += rgb[offset + 2]; count++;
        }
        return count == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(r / count), (byte)(g / count), (byte)(b / count));
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
        string ffmpegPath, bool hdr, string? transfer, bool zscale, int videoStreamIndex)
    {
        if (ActiveByItem.TryGetValue(itemId, out var previousId)
            && Jobs.TryGetValue(previousId, out var previous)
            && (previous.State is "done" or "failed"))
            ActiveByItem.TryRemove(itemId, out _);

        var id = Guid.NewGuid().ToString("N");
        var job = new AiTrackGenerationJob(id, itemId, videoPath, durationMs, options);
        if (!ActiveByItem.TryAdd(itemId, id))
            throw new InvalidOperationException("Keyframe generation is already running for this movie");
        Jobs[id] = job;
        job.Start(ffmpegPath, hdr, transfer, zscale, videoStreamIndex);
        return job;
    }

    public static AiTrackGenerationJob? Find(string jobId) => Jobs.TryGetValue(jobId, out var job) ? job : null;
}
