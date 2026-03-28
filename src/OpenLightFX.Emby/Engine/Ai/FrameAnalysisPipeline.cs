namespace OpenLightFX.Emby.Engine.Ai;

using MediaBrowser.Model.Logging;
using OpenLightFX.Emby.Configuration;
using System.Diagnostics;

/// <summary>
/// Extracts frames from a video file using an ffmpeg subprocess and analyzes them
/// for dominant background color. One subprocess call per batch (all frames in window).
/// </summary>
internal class FrameAnalysisPipeline
{
    private const int FrameWidth = 64;
    private const int FrameHeight = 36;
    private const int BytesPerFrame = FrameWidth * FrameHeight * 3; // RGB24

    private readonly PluginOptions _options;
    private readonly ILogger _logger;
    private readonly string _ffmpegPath;

    private volatile byte[]? _previousFrameRgb;

    // Initialized from plugin settings; flipped to false if zscale/hwaccel fails during the session.
    private volatile bool _useToneMapping;
    private volatile bool _useHwAccel = true;

    public FrameAnalysisPipeline(PluginOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _ffmpegPath = FindFfmpeg();
        _useToneMapping = options.AiToneMappingEnabled;
    }

    /// <summary>
    /// Analyzes frames from [startMs, endMs) at the configured analysis rate.
    /// Returns one AnalysisResult per frame analyzed. Results are in timestamp order.
    /// Returns an empty list if ffmpeg is unavailable or extraction fails.
    /// </summary>
    public List<AnalysisResult> AnalyzeWindow(string videoPath, ulong startMs, ulong endMs)
    {
        var results = new List<AnalysisResult>();
        if (endMs <= startMs) return results;
        if (!File.Exists(videoPath)) return results;

        double startSec = startMs / 1000.0;
        double durationSec = (endMs - startMs) / 1000.0;
        float fps = Math.Max(0.1f, _options.AiAnalysisRateFps);

        var frames = ExtractFrames(videoPath, startSec, durationSec, fps, _useToneMapping, _useHwAccel);
        if (frames == null) return results;

        // If tone mapping produced 0 frames, zscale likely failed — disable and retry.
        if (frames.Count == 0 && _useToneMapping)
        {
            _logger.Warn("[AI] Tone-mapped extraction returned 0 frames — zscale may be unavailable " +
                         "or rejected this file's color metadata. Falling back to simple filter chain.");
            _useToneMapping = false;
            frames = ExtractFrames(videoPath, startSec, durationSec, fps, false, _useHwAccel);
            if (frames == null) return results;
        }

        // If hardware decode produced 0 frames, disable it and retry with software decode.
        if (frames.Count == 0 && _useHwAccel)
        {
            _logger.Warn("[AI] Hardware-accelerated extraction returned 0 frames — falling back to software decode.");
            _useHwAccel = false;
            frames = ExtractFrames(videoPath, startSec, durationSec, fps, _useToneMapping, false);
            if (frames == null) return results;
        }

        double intervalSec = 1.0 / fps;
        ulong intervalMs = (ulong)Math.Round(intervalSec * 1000);
        for (int i = 0; i < frames.Count; i++)
        {
            ulong frameTimestampMs = startMs + (ulong)i * intervalMs;
            var frameRgb = frames[i];

            // Apply preprocessing in place
            var (top, bottom, left, right) = FrameColorMath.DetectLetterbox(frameRgb, FrameWidth, FrameHeight);
            var cropped = CropToRegion(frameRgb, FrameWidth, top, bottom, left, right);
            int croppedWidth = right - left + 1;
            int croppedHeight = bottom - top + 1;
            int croppedPixelCount = croppedWidth * croppedHeight;

            FrameColorMath.ApplyCenterExclusionMask(cropped, croppedWidth, croppedHeight,
                _options.AiCenterExclusionPercent);

            var (r, g, b) = FrameColorMath.ExtractDominantColor(cropped, croppedPixelCount);

            // Detect scene cut via histogram diff against previous frame (use original, not cropped)
            bool isSceneCut = false;
            if (_previousFrameRgb != null)
            {
                float diff = FrameColorMath.ComputeHistogramDifference(
                    _previousFrameRgb, frameRgb, FrameWidth * FrameHeight);
                isSceneCut = diff >= _options.AiSceneCutThreshold;
            }

            uint transitionMs = isSceneCut ? 200u : 1500u;
            // V-012: transition must not start before t=0
            transitionMs = (uint)Math.Min(transitionMs, frameTimestampMs);

            results.Add(new AnalysisResult(frameTimestampMs, r, g, b, transitionMs, isSceneCut));

            _previousFrameRgb = frameRgb;
        }

        return results;
    }

    /// <summary>Resets state on seek — previous frame reference is no longer valid.</summary>
    public void ResetOnSeek()
    {
        _previousFrameRgb = null;
    }

    // ── Private helpers ──────────────────────────────────────────────

    private List<byte[]>? ExtractFrames(
        string videoPath, double startSec, double durationSec, float fps,
        bool useToneMapping, bool useHwAccel)
    {
        // One ffmpeg invocation extracts all frames in the window at the requested fps.
        // Output: raw RGB24 bytes, each frame exactly BytesPerFrame bytes.
        string vf;
        if (useToneMapping)
        {
            // Tone-map HDR10/HLG → SDR via zscale (libzimg). npl=100 targets 100-nit SDR reference.
            // For SDR content the zscale steps are effectively no-ops.
            vf = $"fps={fps:F2},scale={FrameWidth}:{FrameHeight}," +
                 $"zscale=t=linear:npl=100,format=gbrpf32le," +
                 $"zscale=p=bt709,tonemap=tonemap=hable:desat=0," +
                 $"zscale=t=bt709:m=bt709:r=tv";
        }
        else
        {
            vf = $"fps={fps:F2},scale={FrameWidth}:{FrameHeight}";
        }

        // -hwaccel auto: try any available hardware decoder (VAAPI, NVDEC, D3D11VA, etc.),
        // fall back to software if none found. No -hwaccel_output_format — letting ffmpeg
        // keep frames in their native hardware format (p010le, nv12, etc.) avoids chroma
        // plane misalignment on 10-bit HDR content. The software filters (scale, zscale)
        // trigger an automatic hwdownload at the point they're needed.
        string hwaccelArgs = useHwAccel ? "-hwaccel auto " : "";

        var args = $"{hwaccelArgs}-ss {startSec:F3} -t {durationSec:F3} -i \"{videoPath}\" " +
                   $"-vf \"{vf}\" " +
                   $"-f rawvideo -pix_fmt rgb24 -loglevel error pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to start ffmpeg at '{0}': {1}", _ffmpegPath, ex.Message);
            return null;
        }

        // Capture stderr asynchronously to prevent pipe buffer deadlock; log on failure.
        var stderrTask = process.StandardError.ReadToEndAsync();

        var frames = new List<byte[]>();
        var stdout = process.StandardOutput.BaseStream;
        var buf = new byte[BytesPerFrame];

        while (true)
        {
            int bytesRead = ReadExact(stdout, buf, 0, BytesPerFrame);
            if (bytesRead < BytesPerFrame) break;
            frames.Add((byte[])buf.Clone());
        }

        bool exited = process.WaitForExit(timeout: TimeSpan.FromSeconds(5));
        if (!exited)
        {
            _logger.Warn("[AI] ffmpeg timed out after 5s — killing process");
            try { process.Kill(); } catch { }
        }

        if (frames.Count == 0 && stderrTask.IsCompleted)
        {
            var stderr = stderrTask.Result.Trim();
            if (!string.IsNullOrEmpty(stderr))
                _logger.Warn("[AI] ffmpeg stderr: {0}", stderr);
        }

        return frames;
    }

    /// <summary>Reads exactly count bytes from stream; returns actual bytes read (may be less at EOF).</summary>
    private static int ReadExact(Stream stream, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buf, offset + total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Extracts the crop region as a new flat RGB24 byte array.</summary>
    private static byte[] CropToRegion(byte[] rgb, int width, int top, int bottom, int left, int right)
    {
        int croppedWidth = right - left + 1;
        int croppedHeight = bottom - top + 1;
        var cropped = new byte[croppedWidth * croppedHeight * 3];
        for (int row = top; row <= bottom; row++)
        {
            int srcOffset = (row * width + left) * 3;
            int dstOffset = ((row - top) * croppedWidth) * 3;
            Buffer.BlockCopy(rgb, srcOffset, cropped, dstOffset, croppedWidth * 3);
        }
        return cropped;
    }

    private static string FindFfmpeg()
    {
        // Find ffmpeg relative to the running Emby server executable. This works on any
        // platform and installation path — no hardcoded locations needed.
        // Emby on Linux: /opt/emby-server/EmbyServer  → looks for bin/ffmpeg alongside it
        // Emby on Windows: C:\Program Files\Emby Server\EmbyServer.exe → looks for ffmpeg.exe
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (exeDir != null)
            {
                string ext = OperatingSystem.IsWindows() ? ".exe" : "";
                foreach (var rel in new[] { $"ffmpeg{ext}", Path.Combine("bin", $"ffmpeg{ext}") })
                {
                    var candidate = Path.Combine(exeDir, rel);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }

        // Fall back to PATH
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }
}

/// <summary>Result of analyzing one video frame.</summary>
internal record AnalysisResult(
    ulong TimestampMs,
    byte R,
    byte G,
    byte B,
    uint TransitionMs,
    bool IsSceneCut);
