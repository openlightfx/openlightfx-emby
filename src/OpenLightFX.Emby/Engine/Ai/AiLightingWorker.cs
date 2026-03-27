namespace OpenLightFX.Emby.Engine.Ai;

using Google.Protobuf;
using MediaBrowser.Model.Logging;
using OpenLightFX.Emby.Configuration;
using Openlightfx;

/// <summary>
/// Background worker that runs during a playback session with AI lighting enabled.
/// Continuously analyzes frames ahead of the current playback position and injects
/// generated keyframes into the live PlaybackSession via AppendKeyframes().
/// Also manages the .ailfx sidecar cache: loads it on start, writes it on stop.
/// </summary>
public class AiLightingWorker : IDisposable
{
    private const string AiChannelId = "ai-ambient";
    private const ulong MinBufferMs = 2000;

    private readonly PlaybackSession _session;
    private readonly string _videoPath;
    private readonly PluginOptions _options;
    private readonly FrameAnalysisPipeline _pipeline;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;

    // Cursor: next timestamp (ms) to analyze from
    private ulong _nextUnanalyzedMs;

    // Tracks generated timestamps to avoid duplicates across seeks
    private readonly HashSet<ulong> _generatedTimestamps = new();

    // All keyframes generated this session — accumulated for cache write on stop
    private readonly List<Keyframe> _allKeyframes = new();
    private readonly object _allKeyframesLock = new();

    private bool _disposed;

    public AiLightingWorker(
        PlaybackSession session,
        string videoPath,
        PluginOptions options,
        ILogger logger)
    {
        _session = session;
        _videoPath = videoPath;
        _options = options;
        _logger = logger;
        _pipeline = new FrameAnalysisPipeline(options, logger);
    }

    /// <summary>
    /// Attempts to load the .ailfx cache. If found and valid, feeds all keyframes
    /// to the session immediately and returns true (no analysis loop needed).
    /// Otherwise returns false — caller should invoke Start().
    /// </summary>
    public bool TryLoadCache()
    {
        if (!_options.AiCacheEnabled) return false;

        var cachePath = GetCachePath();
        if (!File.Exists(cachePath)) return false;

        try
        {
            var bytes = File.ReadAllBytes(cachePath);
            var track = LightFXTrack.Parser.ParseFrom(bytes);

            // Verify cache was built from the current version of the video file
            var expectedMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(_videoPath))
                .ToUnixTimeSeconds().ToString();
            var mtimeTag = track.Metadata?.Tags.FirstOrDefault(t => t.StartsWith("source-mtime:"));
            if (mtimeTag == null || mtimeTag != $"source-mtime:{expectedMtime}")
            {
                _logger.Debug("[AI] Cache mtime mismatch for '{0}' — re-analyzing", _videoPath);
                TryDeleteFile(cachePath);
                return false;
            }

            _logger.Info("[AI] Loaded {0} keyframe(s) from cache for '{1}'",
                track.Keyframes.Count, Path.GetFileName(_videoPath));
            _session.AppendKeyframes(track.Keyframes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to load cache for '{0}': {1}", _videoPath, ex.Message);
            TryDeleteFile(cachePath);
            return false;
        }
    }

    /// <summary>
    /// Starts the background analysis loop. Call only when TryLoadCache() returned false.
    /// Wire session.OnSeek = worker.NotifySeek before calling this.
    /// </summary>
    public void Start()
    {
        // Initialize cursor from current session position (handles late attach)
        _nextUnanalyzedMs = _session.CurrentPositionMs + MinBufferMs;
        _workerTask = Task.Run(RunLoop, _cts.Token);
        _logger.Info("[AI] Analysis worker started for '{0}'", Path.GetFileName(_videoPath));
    }

    /// <summary>Called by PlaybackSession.OnSeek. Resets the analysis cursor.</summary>
    public void NotifySeek(ulong seekPositionMs)
    {
        if (seekPositionMs > _nextUnanalyzedMs)
        {
            // Forward seek: jump cursor ahead and clear duplicate-tracking (new territory)
            Interlocked.Exchange(ref _nextUnanalyzedMs, seekPositionMs + MinBufferMs);
            lock (_allKeyframesLock)
                _generatedTimestamps.Clear();
            _logger.Debug("[AI] Forward seek to {0}ms — resetting cursor", seekPositionMs);
        }
        // Backward seek: existing keyframes still valid; cursor stays where it is
        _pipeline.ResetOnSeek();
    }

    // ── Private analysis loop ────────────────────────────────────────

    private async Task RunLoop()
    {
        var token = _cts.Token;
        _logger.Debug("[AI] Worker loop entered");

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollIntervalMs, token);

                if (IsCpuThrottled())
                {
                    _logger.Debug("[AI] CPU above {0}% — skipping batch", _options.AiMaxCpuPercent);
                    continue;
                }

                var currentPos = _session.CurrentPositionMs;
                var windowEnd = currentPos + (ulong)_options.AiLookaheadMs;

                if (_nextUnanalyzedMs >= windowEnd) continue;

                // Analyze one 1000ms batch per loop iteration
                var batchEnd = Math.Min(_nextUnanalyzedMs + 1000, windowEnd);
                _logger.Debug("[AI] Analyzing {0}ms–{1}ms", _nextUnanalyzedMs, batchEnd);

                var results = _pipeline.AnalyzeWindow(_videoPath, _nextUnanalyzedMs, batchEnd);

                if (results.Count > 0)
                {
                    var keyframes = new List<Keyframe>(results.Count);
                    lock (_allKeyframesLock)
                    {
                        foreach (var result in results)
                        {
                            if (_generatedTimestamps.Contains(result.TimestampMs)) continue;
                            _generatedTimestamps.Add(result.TimestampMs);
                            var kf = ToKeyframe(result);
                            keyframes.Add(kf);
                            _allKeyframes.Add(kf);
                        }
                    }

                    if (keyframes.Count > 0)
                    {
                        _session.AppendKeyframes(keyframes);
                        _logger.Debug("[AI] Appended {0} keyframe(s)", keyframes.Count);
                    }
                }

                _nextUnanalyzedMs = batchEnd;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[AI] Error in analysis loop", ex);
            }
        }

        _logger.Debug("[AI] Worker loop exiting");
    }

    private static Keyframe ToKeyframe(AnalysisResult result)
    {
        return new Keyframe
        {
            Id = $"ai-{result.TimestampMs}",
            ChannelId = AiChannelId,
            TimestampMs = result.TimestampMs,
            ColorMode = ColorMode.Rgb,
            Color = new RGBColor { R = result.R, G = result.G, B = result.B },
            Brightness = 100,
            TransitionMs = result.TransitionMs,
            Interpolation = InterpolationMode.Linear,
            PowerOn = true
        };
    }

    private bool IsCpuThrottled()
    {
        try
        {
            // Linux: sample /proc/stat over 100ms to estimate CPU usage
            var lines1 = File.ReadAllLines("/proc/stat");
            var cpu1 = ParseCpuLine(lines1[0]);
            Thread.Sleep(100);
            var lines2 = File.ReadAllLines("/proc/stat");
            var cpu2 = ParseCpuLine(lines2[0]);

            long idle = cpu2.idle - cpu1.idle;
            long total = cpu2.total - cpu1.total;
            if (total <= 0) return false;

            double usagePercent = 100.0 * (1.0 - (double)idle / total);
            return usagePercent > _options.AiMaxCpuPercent;
        }
        catch
        {
            return false; // non-Linux or /proc unavailable — don't throttle
        }
    }

    private static (long idle, long total) ParseCpuLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Format: "cpu user nice system idle iowait irq softirq ..."
        long idle = long.Parse(parts[4]);
        long total = parts.Skip(1).Sum(p => long.TryParse(p, out var v) ? v : 0L);
        return (idle, total);
    }

    // ── Cache write ──────────────────────────────────────────────────

    private void WriteCache()
    {
        if (!_options.AiCacheEnabled) return;

        List<Keyframe> keyframes;
        lock (_allKeyframesLock)
            keyframes = _allKeyframes.OrderBy(k => k.TimestampMs).ToList();

        if (keyframes.Count == 0) return;

        try
        {
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(_videoPath))
                .ToUnixTimeSeconds();

            var track = new LightFXTrack { Version = 1 };
            track.Metadata = new TrackMetadata
            {
                Title = Path.GetFileNameWithoutExtension(_videoPath) + " (AI Generated)",
                DurationMs = keyframes[^1].TimestampMs + 1000,
                Tags =
                {
                    "ai-generated",
                    $"source-mtime:{mtime}",
                    $"center-exclusion:{_options.AiCenterExclusionPercent}",
                    $"analysis-rate:{_options.AiAnalysisRateFps:F1}"
                }
            };
            track.Channels.Add(new Channel
            {
                Id = AiChannelId,
                DisplayName = "AI Ambient",
                SpatialHint = "SPATIAL_AMBIENT",
                Optional = false
            });
            track.Keyframes.AddRange(keyframes);

            var tmpPath = GetCachePath() + ".tmp";
            var finalPath = GetCachePath();

            File.WriteAllBytes(tmpPath, track.ToByteArray());

            File.Move(tmpPath, finalPath, overwrite: true);
            _logger.Info("[AI] Wrote {0} keyframe(s) to cache '{1}'",
                keyframes.Count, Path.GetFileName(finalPath));
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to write cache: {0}", ex.Message);
        }
    }

    private string GetCachePath() => Path.ChangeExtension(_videoPath, ".ailfx");

    private void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ── IDisposable ──────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _workerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();

        WriteCache();
        _logger.Info("[AI] Worker disposed");
    }
}
