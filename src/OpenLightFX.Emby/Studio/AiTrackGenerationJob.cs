using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpenLightFX.Emby.Studio;

public sealed record AiTrackZone(string ChannelId, double X, double Y, double Width, double Height);

/// <param name="SampleRateFps">
/// Kept so existing Studio clients still send it. Colour is taken from the detection frames, not at this rate.
/// </param>
/// <param name="SceneCutThreshold">
/// PySceneDetect content score from the plugin-wide Studio threshold setting (0.27 maps to 27).
/// </param>
/// <param name="Fades">Ramp zone keyframes through detected fades to or from black or white.</param>
/// <param name="LightningChannels">Track channels that receive a lightning effect. Empty skips lightning.</param>
/// <param name="SirenChannels">Track channels that receive a siren. Two channels swap red and blue.</param>
public sealed record AiTrackGenerationOptions(
    double SampleRateFps,
    double SceneCutThreshold,
    double CenterExclusionPercent,
    IReadOnlyList<AiTrackZone> Zones,
    bool Fades = false,
    IReadOnlyList<string>? LightningChannels = null,
    IReadOnlyList<string>? SirenChannels = null);

/// <summary>
/// Frame sizes and the ffmpeg graph for keyframe generation. Colour is read from
/// 24-fps frames and precomputed scene cuts are loaded from the durable scene-cut document.
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

    public static IReadOnlyList<string> DecodeArguments(string videoPath, string filter) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-hwaccel", "auto", "-i", videoPath,
        "-filter_complex", filter, "-map", "[cut]", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
    };

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
            Brightness = 100,
            PowerOn = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) >= 8
        };

    private static int Even(int value) => value < 2 ? 2 : value & ~1;
}

/// <summary>One non-cancelable Studio keyframe-generation job, owned by Emby.</summary>
public sealed class AiTrackGenerationJob
{
    private readonly object _gate = new();
    private readonly List<StudioGeneratedKeyframe> _keyframes = new();
    private readonly List<StudioGeneratedEffect> _effects = new();
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly string _sourceIdentityPath;
    private string _state = "pending";
    private string _error = "";
    private double _progress;
    private long _throughMs;

    public AiTrackGenerationJob(string id, string itemId, string videoPath, string sourceIdentityPath, long durationMs, AiTrackGenerationOptions options, int sourceWidth, int sourceHeight)
    {
        Id = id;
        ItemId = itemId;
        VideoPath = videoPath;
        _sourceIdentityPath = sourceIdentityPath;
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

    public (string State, string Error, double Progress, long ThroughMs, int Count, List<StudioGeneratedKeyframe> NewKeyframes, List<StudioGeneratedEffect> Effects)
        Snapshot(int afterSequence)
    {
        lock (_gate)
        {
            var start = Math.Clamp(afterSequence, 0, _keyframes.Count);
            return (_state, _error, _progress, _throughMs, _keyframes.Count,
                _keyframes.Skip(start).ToList(), _effects.ToList());
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
            var sceneDocument = SceneCutStore.Open().TryLoad(ItemId);
            if (sceneDocument == null
                || sceneDocument.Status(SceneCutSource.Capture(_sourceIdentityPath), false) != "ready"
                || Math.Abs(sceneDocument.Detector.Threshold - Options.SceneCutThreshold) >= 0.0001)
                throw new InvalidOperationException("Scene cuts are not ready for this movie");
            // Every scene marker for the movie (detected, moved, and manual) bounds pre-fill scenes.
            var storedCuts = sceneDocument.CutSeconds()
                .Select(seconds => (int)Math.Round(seconds * AiTrackAnalysis.DetectionFps))
                .ToHashSet();
            var filter = AiTrackAnalysis.CutFilter(videoStreamIndex, detectWidth, detectHeight, toneMap);
            var args = AiTrackAnalysis.DecodeArguments(VideoPath, filter);
            process = new Process { StartInfo = StudioProcess.StartInfo(ffmpegPath, args) };
            if (!process.Start()) throw new InvalidOperationException("ffmpeg failed to start");
            var stderrTask = process.StandardError.ReadToEndAsync();
            var cutFrame = new byte[detectWidth * detectHeight * 3];
            var prefill = new EffectPrefill();
            var detectLightning = Targets(Options.LightningChannels).Count > 0;
            var detectSiren = Targets(Options.SirenChannels).Count > 0;
            var analyzeFrames = Options.Fades || detectLightning || detectSiren;
            var cutIndex = 0;
            var sceneStart = 0;
            var opening = true;
            var idPrefix = Id.Length >= 8 ? Id[..8] : Id;
            var lastFrame = new byte[cutFrame.Length];
            var hasLastFrame = false;
            byte[]? sceneColorFrame = null;

            while (await ReadFrameAsync(process.StandardOutput.BaseStream, cutFrame).ConfigureAwait(false))
            {
                if (analyzeFrames)
                    prefill.Observe(cutFrame, detectWidth, detectHeight, detectLightning, detectSiren);
                if (storedCuts.Contains(cutIndex) && cutIndex > sceneStart)
                {
                    var colors = SceneColors(sceneColorFrame ?? (hasLastFrame ? lastFrame : cutFrame), detectWidth, detectHeight);
                    EmitScene(colors, prefill, idPrefix, sceneStart, cutIndex, opening);
                    opening = false;
                    sceneStart = cutIndex;
                    sceneColorFrame = null;
                }
                else if (cutIndex == sceneStart + SceneSampling.SkipFrames)
                {
                    sceneColorFrame = cutFrame.ToArray();
                }

                Buffer.BlockCopy(cutFrame, 0, lastFrame, 0, cutFrame.Length);
                hasLastFrame = true;
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
            if (cutIndex > sceneStart && hasLastFrame)
            {
                var colors = SceneColors(sceneColorFrame ?? lastFrame, detectWidth, detectHeight);
                EmitScene(colors, prefill, idPrefix, sceneStart, cutIndex, opening);
            }
            var cutFrames = storedCuts.Order().ToList();
            AddPrefillEffects(prefill, cutFrames, cutIndex, idPrefix);
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

    private Dictionary<string, (byte R, byte G, byte B)> SceneColors(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var colorFrame = ResizeRgb24(rgb, width, height, AiTrackAnalysis.ColorWidth, AiTrackAnalysis.ColorHeight);
        var colors = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.Ordinal);
        foreach (var zone in Options.Zones)
            colors[zone.ChannelId] = ZoneColor.Dominant(colorFrame, AiTrackAnalysis.ColorWidth, AiTrackAnalysis.ColorHeight, zone, Options.CenterExclusionPercent);
        return colors;
    }

    private static byte[] ResizeRgb24(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var resized = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((y + 0.5) * sourceHeight / height - 0.5, 0, sourceHeight - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var wy = sourceY - y0;
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((x + 0.5) * sourceWidth / width - 0.5, 0, sourceWidth - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var wx = sourceX - x0;
                var destination = (y * width + x) * 3;
                var topLeft = (y0 * sourceWidth + x0) * 3;
                var topRight = (y0 * sourceWidth + x1) * 3;
                var bottomLeft = (y1 * sourceWidth + x0) * 3;
                var bottomRight = (y1 * sourceWidth + x1) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source[topLeft + channel] * (1 - wx) + source[topRight + channel] * wx;
                    var bottom = source[bottomLeft + channel] * (1 - wx) + source[bottomRight + channel] * wx;
                    resized[destination + channel] = (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bottom * wy), 0, 255);
                }
            }
        }
        return resized;
    }

    private void EmitScene(Dictionary<string, (byte R, byte G, byte B)> sceneColors, EffectPrefill prefill, string idPrefix, int sceneStart, int sceneEnd, bool opening)
    {
        var startMs = opening ? 0 : AiTrackAnalysis.FrameTimeMs(sceneStart, AiTrackAnalysis.DetectionFps);
        var colorMs = startMs;
        uint transition = 0;
        var shaped = false;
        if (Options.Fades && prefill.TryFadeIn(sceneStart, sceneEnd, out var fadeEnd, out var fromWhite))
        {
            colorMs = AiTrackAnalysis.FrameTimeMs(fadeEnd, AiTrackAnalysis.DetectionFps);
            transition = (uint)Math.Max(0, colorMs - startMs);
            AddHold(idPrefix, sceneStart, startMs, fromWhite);
            shaped = true;
        }

        var token = opening ? "c0" : "d" + sceneStart.ToString(CultureInfo.InvariantCulture);
        if (!shaped)
            AddKeyframes(idPrefix, token, startMs, sceneColors, sceneCut: !opening);
        else
            AddKeyframes(idPrefix, token, colorMs, sceneColors, sceneCut: false, transition);

        if (Options.Fades && prefill.TryFadeOut(sceneStart, sceneEnd, out var fadeStart, out var toWhite))
        {
            var endMs = AiTrackAnalysis.FrameTimeMs(Math.Max(sceneStart, sceneEnd - 1), AiTrackAnalysis.DetectionFps);
            var fadeMs = AiTrackAnalysis.FrameTimeMs(fadeStart, AiTrackAnalysis.DetectionFps);
            AddFadeEnd(idPrefix, sceneEnd, endMs, (uint)Math.Max(0, endMs - fadeMs), toWhite);
        }
    }

    private void AddPrefillEffects(EffectPrefill prefill, IReadOnlyList<int> cuts, int frameCount, string idPrefix)
    {
        var lightning = Targets(Options.LightningChannels);
        var siren = Targets(Options.SirenChannels);
        if (lightning.Count == 0 && siren.Count == 0) return;
        var generated = new List<StudioGeneratedEffect>();
        if (lightning.Count > 0)
        {
            foreach (var hit in prefill.Lightning(cuts))
            {
                var timeMs = AiTrackAnalysis.FrameTimeMs(hit.Frame, AiTrackAnalysis.DetectionFps);
                var intensity = (int)Math.Clamp(Math.Round(hit.Confidence * 100), 40, 100);
                foreach (var channel in lightning)
                {
                    generated.Add(new StudioGeneratedEffect
                    {
                        Id = $"ai-gen-{idPrefix}-lightning-{channel}-{hit.Frame}",
                        ChannelId = channel,
                        TimestampMs = timeMs,
                        DurationMs = 800,
                        EffectType = "LIGHTNING",
                        Intensity = intensity,
                        R = 255, G = 255, B = 255,
                        SecondaryR = 255, SecondaryG = 180, SecondaryB = 80,
                        FlashCount = 2,
                        DecayMs = 500
                    });
                }
            }
        }

        if (siren.Count > 0)
        {
            foreach (var run in prefill.Sirens())
            {
                var timeMs = AiTrackAnalysis.FrameTimeMs(run.StartFrame, AiTrackAnalysis.DetectionFps);
                var duration = Math.Max(1, AiTrackAnalysis.FrameTimeMs(run.EndFrame, AiTrackAnalysis.DetectionFps) - timeMs);
                for (var i = 0; i < siren.Count; i++)
                {
                    var redFirst = siren.Count == 1 || i % 2 == 0;
                    generated.Add(new StudioGeneratedEffect
                    {
                        Id = $"ai-gen-{idPrefix}-siren-{siren[i]}-{run.StartFrame}",
                        ChannelId = siren[i],
                        TimestampMs = timeMs,
                        DurationMs = duration,
                        EffectType = "SIREN",
                        Intensity = 80,
                        R = (byte)(redFirst ? 255 : 0),
                        G = 0,
                        B = (byte)(redFirst ? 0 : 255),
                        SecondaryR = (byte)(redFirst ? 0 : 255),
                        SecondaryG = 0,
                        SecondaryB = (byte)(redFirst ? 255 : 0),
                        PulseRateHz = run.RateHz
                    });
                }
            }
        }

        if (generated.Count == 0 || frameCount < 0) return;
        lock (_gate) _effects.AddRange(generated);
    }

    private List<string> Targets(IReadOnlyList<string>? requested)
    {
        if (requested == null || requested.Count == 0) return new List<string>();
        var zones = new HashSet<string>(Options.Zones.Select(zone => zone.ChannelId), StringComparer.Ordinal);
        return requested.Where(zones.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    private void AddHold(string idPrefix, int sceneStart, long timeMs, bool white)
    {
        var generated = new List<StudioGeneratedKeyframe>();
        foreach (var zone in Options.Zones)
        {
            if (HasKeyframeNear(zone.ChannelId, timeMs, 80)) continue;
            var color = white ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
            var frame = AiTrackAnalysis.Keyframe(
                $"ai-gen-{idPrefix}-fade-{zone.ChannelId}-in-{sceneStart}",
                zone.ChannelId, timeMs, color, sceneCut: false);
            frame.TransitionMs = 0;
            frame.Brightness = white ? 100 : 0;
            frame.PowerOn = white;
            generated.Add(frame);
        }

        lock (_gate) _keyframes.AddRange(generated);
    }

    private void AddFadeEnd(string idPrefix, int sceneEnd, long timeMs, uint transitionMs, bool white)
    {
        var generated = new List<StudioGeneratedKeyframe>();
        foreach (var zone in Options.Zones)
        {
            if (HasKeyframeNear(zone.ChannelId, timeMs, 80)) continue;
            var color = white ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
            var frame = AiTrackAnalysis.Keyframe(
                $"ai-gen-{idPrefix}-fade-{zone.ChannelId}-out-{sceneEnd}",
                zone.ChannelId, timeMs, color, sceneCut: true);
            frame.TransitionMs = transitionMs;
            frame.Brightness = white ? 100 : 0;
            frame.PowerOn = white;
            generated.Add(frame);
        }

        lock (_gate) _keyframes.AddRange(generated);
    }

    private bool HasKeyframeNear(string channelId, long timeMs, long withinMs)
    {
        lock (_gate)
        {
            return _keyframes.Any(frame => frame.ChannelId == channelId
                && Math.Abs(frame.TimestampMs - timeMs) <= withinMs);
        }
    }

    private void AddKeyframes(string idPrefix, string token, long timeMs, Dictionary<string, (byte R, byte G, byte B)> colors, bool sceneCut, uint? transitionMs = null)
    {
        var generated = new List<StudioGeneratedKeyframe>(Options.Zones.Count);
        foreach (var zone in Options.Zones)
        {
            if (!colors.TryGetValue(zone.ChannelId, out var color))
                continue;
            var frame = AiTrackAnalysis.Keyframe(
                $"ai-gen-{idPrefix}-{zone.ChannelId}-{token}",
                zone.ChannelId,
                timeMs,
                color,
                sceneCut);
            if (transitionMs is uint transition)
                frame.TransitionMs = transition;
            generated.Add(frame);
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
    [System.Runtime.Serialization.DataMember(Name = "brightness"), System.Text.Json.Serialization.JsonPropertyName("brightness")] public int Brightness { get; set; } = 100;
    [System.Runtime.Serialization.DataMember(Name = "transitionMs"), System.Text.Json.Serialization.JsonPropertyName("transitionMs")] public uint TransitionMs { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "powerOn"), System.Text.Json.Serialization.JsonPropertyName("powerOn")] public bool PowerOn { get; set; }
}

[System.Runtime.Serialization.DataContract]
public sealed class StudioGeneratedEffect
{
    [System.Runtime.Serialization.DataMember(Name = "id"), System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "channelId"), System.Text.Json.Serialization.JsonPropertyName("channelId")] public string ChannelId { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "timestampMs"), System.Text.Json.Serialization.JsonPropertyName("timestampMs")] public long TimestampMs { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "durationMs"), System.Text.Json.Serialization.JsonPropertyName("durationMs")] public long DurationMs { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "effectType"), System.Text.Json.Serialization.JsonPropertyName("effectType")] public string EffectType { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "intensity"), System.Text.Json.Serialization.JsonPropertyName("intensity")] public int Intensity { get; set; } = 100;
    [System.Runtime.Serialization.DataMember(Name = "r"), System.Text.Json.Serialization.JsonPropertyName("r")] public byte R { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "g"), System.Text.Json.Serialization.JsonPropertyName("g")] public byte G { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "b"), System.Text.Json.Serialization.JsonPropertyName("b")] public byte B { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "secondaryR"), System.Text.Json.Serialization.JsonPropertyName("secondaryR")] public byte SecondaryR { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "secondaryG"), System.Text.Json.Serialization.JsonPropertyName("secondaryG")] public byte SecondaryG { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "secondaryB"), System.Text.Json.Serialization.JsonPropertyName("secondaryB")] public byte SecondaryB { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "pulseRateHz"), System.Text.Json.Serialization.JsonPropertyName("pulseRateHz")] public double PulseRateHz { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "flashCount"), System.Text.Json.Serialization.JsonPropertyName("flashCount")] public int FlashCount { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "decayMs"), System.Text.Json.Serialization.JsonPropertyName("decayMs")] public int DecayMs { get; set; }
}

public static class AiTrackGenerationJobs
{
    private static readonly ConcurrentDictionary<string, AiTrackGenerationJob> Jobs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ActiveByItem = new(StringComparer.Ordinal);

    public static AiTrackGenerationJob Start(string itemId, string videoPath, long durationMs, AiTrackGenerationOptions options,
        string ffmpegPath, bool hdr, string? transfer, bool zscale, int videoStreamIndex, int sourceWidth, int sourceHeight,
        string? sourceIdentityPath = null)
    {
        if (ActiveByItem.TryGetValue(itemId, out var previousId)
            && Jobs.TryGetValue(previousId, out var previous)
            && (previous.State is "done" or "failed"))
            ActiveByItem.TryRemove(itemId, out _);

        var id = Guid.NewGuid().ToString("N");
        var job = new AiTrackGenerationJob(id, itemId, videoPath, sourceIdentityPath ?? videoPath, durationMs, options, sourceWidth, sourceHeight);
        if (!ActiveByItem.TryAdd(itemId, id))
            throw new InvalidOperationException("Keyframe generation is already running for this movie");
        Jobs[id] = job;
        job.Start(ffmpegPath, hdr, transfer, zscale, videoStreamIndex);
        return job;
    }

    public static AiTrackGenerationJob? Find(string jobId) => Jobs.TryGetValue(jobId, out var job) ? job : null;
}
