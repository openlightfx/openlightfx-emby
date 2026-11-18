using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// ContentDetector over the cut stream only (no zone colour sampling). On success the cuts are
/// written by <see cref="SceneCutStore"/>.
/// </summary>
public sealed class SceneCutDetectionJob
{
    private readonly object _gate = new();
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly string _sourceIdentityPath;
    private readonly double _threshold;
    private string _state = "pending";
    private string _error = "";
    private double _progress;
    private long _throughMs;

    public SceneCutDetectionJob(string id, string itemId, string videoPath, string sourceIdentityPath, long durationMs, double threshold, int sourceWidth, int sourceHeight)
    {
        Id = id;
        ItemId = itemId;
        VideoPath = videoPath;
        _sourceIdentityPath = sourceIdentityPath;
        DurationMs = durationMs;
        _threshold = threshold;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
    }

    public string Id { get; }
    public string ItemId { get; }
    private string VideoPath { get; }
    private long DurationMs { get; }

    public (string State, string Error, double Progress, long ThroughMs) Snapshot()
    {
        lock (_gate) return (_state, _error, _progress, _throughMs);
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
        var analyzed = SceneCutSource.Capture(_sourceIdentityPath);
        Process? process = null;
        try
        {
            var toneMap = AiTrackAnalysis.ToneMap(hdr, colorTransfer, zscale);
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
            var detector = new ContentDetector(_threshold);
            var frame = new byte[detectWidth * detectHeight * 3];
            var cuts = new List<int>();
            var seen = new HashSet<int>();
            var index = 0;
            while (await ReadFrameAsync(process.StandardOutput.BaseStream, frame).ConfigureAwait(false))
            {
                foreach (var cut in detector.ProcessFrame(frame, detectWidth, detectHeight))
                {
                    if (seen.Add(cut))
                        cuts.Add(cut);
                }

                var timeMs = AiTrackAnalysis.FrameTimeMs(index, AiTrackAnalysis.DetectionFps);
                lock (_gate)
                {
                    _throughMs = timeMs;
                    _progress = DurationMs > 0 ? Math.Clamp(timeMs / (double)DurationMs, 0, 1) : 0;
                }
                index++;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffmpeg could not analyze the movie" : "ffmpeg could not analyze the movie");
            SceneCutStore.Open().Save(SceneCutDocument.Create(
                ItemId, analyzed, _threshold, detectWidth, DurationMs / 1000.0, cuts));
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

public static class SceneCutDetectionJobs
{
    private static readonly ConcurrentDictionary<string, SceneCutDetectionJob> Jobs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ActiveByItem = new(StringComparer.Ordinal);

    public static SceneCutDetectionJob Start(string itemId, string videoPath, long durationMs, double threshold,
        string ffmpegPath, bool hdr, string? transfer, bool zscale, int videoStreamIndex, int sourceWidth, int sourceHeight,
        string? sourceIdentityPath = null)
    {
        if (ActiveByItem.TryGetValue(itemId, out var previousId)
            && Jobs.TryGetValue(previousId, out var previous)
            && previous.Snapshot().State is "done" or "failed")
            ActiveByItem.TryRemove(itemId, out _);

        var id = Guid.NewGuid().ToString("N");
        var job = new SceneCutDetectionJob(id, itemId, videoPath, sourceIdentityPath ?? videoPath, durationMs, threshold, sourceWidth, sourceHeight);
        if (!ActiveByItem.TryAdd(itemId, id))
            throw new InvalidOperationException("Scene cut detection is already running for this movie");
        Jobs[id] = job;
        job.Start(ffmpegPath, hdr, transfer, zscale, videoStreamIndex);
        return job;
    }

    public static SceneCutDetectionJob? Find(string jobId) => Jobs.TryGetValue(jobId, out var job) ? job : null;
}
