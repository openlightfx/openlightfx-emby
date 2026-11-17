using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpenLightFX.Emby.Studio;

public sealed record AiTrackZone(string ChannelId, double X, double Y, double Width, double Height);

/// <param name="SceneCutThreshold">
/// PySceneDetect content score. Studio sends 0.01–1 and the service multiplies by 100, so 0.27 is the upstream default of 27.
/// </param>
public sealed record AiTrackGenerationOptions(
    double SampleRateFps,
    double SceneCutThreshold,
    double CenterExclusionPercent,
    IReadOnlyList<AiTrackZone> Zones);

/// <summary>
/// Frame sizes and the ffmpeg graph for keyframe generation. Colour stays on the caller's
/// sample rate. Cuts are a second output of the same decode, at
/// <see cref="DetectionFps"/> and about <see cref="DetectionWidth"/> pixels wide.
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

    /// <summary>
    /// Colour sample to paint a cut with: the first sample at or after the cut, else the last.
    /// Returns -1 when there are no samples.
    /// </summary>
    public static int ColorSampleIndex(IReadOnlyList<long> sampleTimesMs, long cutMs)
    {
        if (sampleTimesMs.Count == 0) return -1;
        for (var i = 0; i < sampleTimesMs.Count; i++)
        {
            if (sampleTimesMs[i] >= cutMs)
                return i;
        }

        return sampleTimesMs.Count - 1;
    }

    public static string Filter(int videoStreamIndex, double sampleRateFps, int detectWidth, int detectHeight, string toneMap)
    {
        var sample = sampleRateFps.ToString("0.###", CultureInfo.InvariantCulture);
        var detect = DetectionFps.ToString("0.###", CultureInfo.InvariantCulture);
        return $"[0:{videoStreamIndex.ToString(CultureInfo.InvariantCulture)}]split=2[color_in][cut_in];" +
            $"[color_in]fps={sample},{toneMap}scale={ColorWidth}:{ColorHeight},format=rgb24[color];" +
            $"[cut_in]fps={detect},{toneMap}scale={detectWidth.ToString(CultureInfo.InvariantCulture)}:{detectHeight.ToString(CultureInfo.InvariantCulture)}:flags=bilinear,format=rgb24[cut]";
    }

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
        _ = Task.Run(() => RunAsync(ffmpegPath, hdr, colorTransfer, zscale, videoStreamIndex));
    }

    private async Task RunAsync(string ffmpegPath, bool hdr, string? colorTransfer, bool zscale, int videoStreamIndex)
    {
        var (detectWidth, detectHeight) = AiTrackAnalysis.DetectionSize(_sourceWidth, _sourceHeight);
        var colorPath = Path.Combine(Path.GetTempPath(), "openlightfx-ai-" + Id + ".raw");
        Process? process = null;
        GrowingFrameReader? colorReader = null;
        try
        {
            var sampleFps = Math.Clamp(Options.SampleRateFps, 0.1, 5.0);
            var toneMap = !hdr
                ? ""
                : zscale
                    ? "zscale=tin=" + (StudioFfprobe.IsHlg(colorTransfer) ? "arib-std-b67" : "smpte2084") +
                      ":t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0," +
                      "zscale=t=bt709:m=bt709:r=tv,"
                    : "format=p010le,supertonemap=tonemap=hable:desat=0,format=rgb24,";
            var filter = AiTrackAnalysis.Filter(videoStreamIndex, sampleFps, detectWidth, detectHeight, toneMap);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", VideoPath,
                "-filter_complex", filter,
                "-map", "[color]", "-flush_packets", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", colorPath,
                "-map", "[cut]", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            };
            process = new Process { StartInfo = StudioProcess.StartInfo(ffmpegPath, args) };
            if (!process.Start()) throw new InvalidOperationException("ffmpeg failed to start");
            var stderrTask = process.StandardError.ReadToEndAsync();
            colorReader = new GrowingFrameReader(colorPath, AiTrackAnalysis.ColorWidth * AiTrackAnalysis.ColorHeight * 3);
            var detector = new ContentDetector(Options.SceneCutThreshold);
            var cutFrame = new byte[detectWidth * detectHeight * 3];
            var samples = new List<ColorSample>();
            var pendingCuts = new List<int>();
            var seenCuts = new HashSet<int>();
            var colorIndex = 0;
            var cutIndex = 0;
            var baselineEmitted = false;
            var idPrefix = Id.Length >= 8 ? Id[..8] : Id;

            while (await ReadFrameAsync(process.StandardOutput.BaseStream, cutFrame).ConfigureAwait(false))
            {
                foreach (var cut in detector.ProcessFrame(cutFrame, detectWidth, detectHeight))
                {
                    if (seenCuts.Add(cut))
                        pendingCuts.Add(cut);
                }

                colorIndex = DrainColors(colorReader, sampleFps, samples, colorIndex);
                baselineEmitted = Publish(samples, pendingCuts, idPrefix, baselineEmitted, detectionComplete: false);
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
            DrainColors(colorReader, sampleFps, samples, colorIndex);
            if (cutIndex > 0 && samples.Count == 0)
                throw new InvalidOperationException("ffmpeg could not analyze the movie");
            Publish(samples, pendingCuts, idPrefix, baselineEmitted, detectionComplete: true);
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
            colorReader?.Dispose();
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            process?.Dispose();
            try { File.Delete(colorPath); } catch (IOException) { }
        }
    }

    private int DrainColors(GrowingFrameReader reader, double sampleFps, List<ColorSample> samples, int colorIndex)
    {
        while (reader.TryRead())
        {
            var timeMs = AiTrackAnalysis.FrameTimeMs(colorIndex, sampleFps);
            var colors = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.Ordinal);
            foreach (var zone in Options.Zones)
                colors[zone.ChannelId] = AverageZone(reader.Current, zone, Options.CenterExclusionPercent);
            samples.Add(new ColorSample(timeMs, colors));
            colorIndex++;
        }

        return colorIndex;
    }

    private bool Publish(List<ColorSample> samples, List<int> pendingCuts, string idPrefix, bool baselineEmitted, bool detectionComplete)
    {
        var times = new long[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            times[i] = samples[i].TimeMs;
        if (!baselineEmitted && samples.Count > 0)
        {
            AddKeyframes(idPrefix, "c0", samples[0].TimeMs, samples[0].Colors, sceneCut: false);
            baselineEmitted = true;
        }

        var index = 0;
        while (index < pendingCuts.Count)
        {
            var cutFrame = pendingCuts[index];
            var cutMs = AiTrackAnalysis.FrameTimeMs(cutFrame, AiTrackAnalysis.DetectionFps);
            var sampleIndex = AiTrackAnalysis.ColorSampleIndex(times, cutMs);
            if (sampleIndex < 0 || (samples[sampleIndex].TimeMs < cutMs && !detectionComplete))
                break;
            var sample = samples[sampleIndex];
            AddKeyframes(idPrefix, "d" + cutFrame.ToString(CultureInfo.InvariantCulture), cutMs, sample.Colors, sceneCut: true);
            pendingCuts.RemoveAt(index);
        }

        return baselineEmitted;
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

    private static (byte R, byte G, byte B) AverageZone(ReadOnlySpan<byte> rgb, AiTrackZone zone, double centerExclusionPercent)
    {
        var left = Math.Clamp((int)Math.Floor(zone.X * AiTrackAnalysis.ColorWidth), 0, AiTrackAnalysis.ColorWidth - 1);
        var top = Math.Clamp((int)Math.Floor(zone.Y * AiTrackAnalysis.ColorHeight), 0, AiTrackAnalysis.ColorHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((zone.X + zone.Width) * AiTrackAnalysis.ColorWidth), left + 1, AiTrackAnalysis.ColorWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((zone.Y + zone.Height) * AiTrackAnalysis.ColorHeight), top + 1, AiTrackAnalysis.ColorHeight);
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
            var offset = (y * AiTrackAnalysis.ColorWidth + x) * 3;
            r += rgb[offset]; g += rgb[offset + 1]; b += rgb[offset + 2]; count++;
        }
        return count == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private readonly record struct ColorSample(long TimeMs, Dictionary<string, (byte R, byte G, byte B)> Colors);

    private sealed class GrowingFrameReader : IDisposable
    {
        private readonly string _path;
        private readonly int _frameBytes;
        private readonly byte[] _buffer;
        private FileStream? _stream;

        public GrowingFrameReader(string path, int frameBytes)
        {
            _path = path;
            _frameBytes = frameBytes;
            _buffer = new byte[frameBytes];
        }

        public ReadOnlySpan<byte> Current => _buffer;

        public bool TryRead()
        {
            _stream ??= TryOpen();
            if (_stream == null) return false;
            var total = 0;
            while (total < _frameBytes)
            {
                var read = _stream.Read(_buffer, total, _frameBytes - total);
                if (read == 0)
                {
                    if (total > 0)
                        _stream.Position -= total;
                    return false;
                }
                total += read;
            }

            return true;
        }

        public void Dispose() => _stream?.Dispose();

        private FileStream? TryOpen()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
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
