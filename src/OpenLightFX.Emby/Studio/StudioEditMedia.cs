using System.Globalization;
using System.Text.Json;

namespace OpenLightFX.Emby.Studio;

public sealed record StudioSourceInfo(
    long RuntimeMs,
    int Width,
    int Height,
    string VideoCodec,
    string AudioCodec,
    bool Hdr,
    string ColorTransfer,
    double Fps);

public static class StudioFfprobe
{
    public static StudioSourceInfo Parse(string json, long itemRuntimeMs = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        long probedMs = 0;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var duration)
            && double.TryParse(duration.GetString() ?? duration.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            probedMs = (long)Math.Round(seconds * 1000.0);
        }

        var width = 0;
        var height = 0;
        var video = "";
        var audio = "";
        var transfer = "";
        var fps = 24.0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() : null;
                var name = stream.TryGetProperty("codec_name", out var codecName) ? codecName.GetString() ?? "" : "";
                if (type == "video" && video.Length == 0)
                {
                    video = name;
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    transfer = stream.TryGetProperty("color_transfer", out var color) ? color.GetString() ?? "" : "";
                    if (stream.TryGetProperty("avg_frame_rate", out var rate))
                        fps = ParseRate(rate.GetString());
                }
                else if (type == "audio" && audio.Length == 0)
                {
                    audio = name;
                }
            }
        }

        var runtime = itemRuntimeMs > 0 ? itemRuntimeMs : probedMs;
        return new StudioSourceInfo(runtime, width, height, video, audio, IsHdr(transfer), transfer, fps <= 0 ? 24 : fps);
    }

    public static bool IsHdr(string? colorTransfer)
    {
        if (string.IsNullOrWhiteSpace(colorTransfer))
            return false;
        return colorTransfer.Contains("smpte2084", StringComparison.OrdinalIgnoreCase)
            || colorTransfer.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase)
            || colorTransfer.Equals("pq", StringComparison.OrdinalIgnoreCase)
            || colorTransfer.Equals("hlg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHlg(string? colorTransfer) =>
        !string.IsNullOrWhiteSpace(colorTransfer)
        && (colorTransfer.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase)
            || colorTransfer.Equals("hlg", StringComparison.OrdinalIgnoreCase));

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
            return 24;
        var slash = rate.IndexOf('/');
        if (slash < 0)
            return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 24;
        if (!double.TryParse(rate[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(rate[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            || den == 0)
            return 24;
        return num / den;
    }
}

public static class StudioProxyPlan
{
    public const int MaxLongSide = 1280;

    public static int GopFrames(double fps)
    {
        if (fps < 1)
            fps = 24;
        var gop = Math.Max(1, (int)Math.Floor(fps * 0.5));
        while (gop > 1 && IntervalMs(fps, gop) > 500)
            gop--;
        return gop;
    }

    public static int IntervalMs(double fps) => IntervalMs(fps < 1 ? 24 : fps, GopFrames(fps));

    public static IReadOnlyList<string> Arguments(string inputPath, string outputPath, double fps)
    {
        var gop = GopFrames(fps);
        var scale =
            "scale='min(1280,iw)':'min(1280,ih)':force_original_aspect_ratio=decrease," +
            "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        return new[]
        {
            "-y",
            "-i", inputPath,
            "-vf", scale,
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-profile:v", "high",
            "-preset", "veryfast",
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", gop.ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0",
            "-x264-params", $"keyint={gop}:min-keyint={gop}:scenecut=0:open-gop=0",
            "-flags", "+cgop",
            "-bf", "0",
            "-c:a", "aac",
            "-ac", "2",
            "-b:a", "128k",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-f", "mp4",
            "-progress", "pipe:2",
            "-nostats",
            outputPath
        };
    }

    private static int IntervalMs(double fps, int gop) =>
        (int)Math.Round(1000.0 * gop / fps);
}

public static class StudioFramePlan
{
    public const string HdrHeader = "hable-bt709";
    public const string SdrHeader = "none";

    public static int ClampWidth(int? requested)
    {
        var width = requested ?? 1280;
        if (width < 16)
            width = 16;
        if (width > 1920)
            width = 1920;
        return width;
    }

    public static string Filter(bool hdr, string? colorTransfer, int maxWidth)
    {
        var scale = $"scale='min({maxWidth},iw)':-2";
        if (!hdr)
            return $"format=rgb24,{scale}";

        var tin = StudioFfprobe.IsHlg(colorTransfer) ? "arib-std-b67" : "smpte2084";
        return "zscale=tin=" + tin + ":t=linear:npl=100,format=gbrpf32le,zscale=p=bt709," +
               "tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=rgb24," + scale;
    }

    public static string ToneMap(bool hdr) => hdr ? HdrHeader : SdrHeader;

    public static IReadOnlyList<string> Arguments(string inputPath, long timeMs, bool hdr, string? colorTransfer, int? maxWidth)
    {
        var seconds = (timeMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        return new[]
        {
            "-hide_banner",
            "-ss", seconds,
            "-accurate_seek",
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", Filter(hdr, colorTransfer, ClampWidth(maxWidth)),
            "-f", "image2pipe",
            "-vcodec", "png",
            "pipe:1"
        };
    }

    /// <summary>showinfo prints pts_time. Fall back to the requested time.</summary>
    public static long SourceTimeMs(string? ffmpegLog, long requestedMs)
    {
        if (string.IsNullOrEmpty(ffmpegLog))
            return requestedMs;
        const string marker = "pts_time:";
        var index = ffmpegLog.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return requestedMs;
        var start = index + marker.Length;
        var end = start;
        while (end < ffmpegLog.Length && (char.IsDigit(ffmpegLog[end]) || ffmpegLog[end] == '.'))
            end++;
        if (end == start)
            return requestedMs;
        if (!double.TryParse(ffmpegLog[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return requestedMs;
        return (long)Math.Round(seconds * 1000.0);
    }
}

public static class StudioFfmpegProgress
{
    public static bool TryReadEncodedMs(string line, out long encodedMs)
    {
        encodedMs = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        line = line.Trim();
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            return Micros(line["out_time_us=".Length..], out encodedMs);
        if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            // Emby's ffmpeg prints this field in microseconds.
            return Micros(line["out_time_ms=".Length..], out encodedMs);
        }
        if (line.StartsWith("out_time=", StringComparison.Ordinal)
            && TimeSpan.TryParse(line["out_time=".Length..], CultureInfo.InvariantCulture, out var time))
        {
            encodedMs = (long)time.TotalMilliseconds;
            return true;
        }
        return false;
    }

    private static bool Micros(string text, out long encodedMs)
    {
        encodedMs = 0;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            return false;
        encodedMs = micros / 1000;
        return true;
    }
}

public readonly record struct StudioByteSlice(long Start, long End, int Status);

public static class StudioByteRange
{
    public static StudioByteSlice Resolve(string? rangeHeader, long length)
    {
        if (length < 0)
            length = 0;
        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return new StudioByteSlice(0, length == 0 ? 0 : length - 1, 200);

        var spec = rangeHeader["bytes=".Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return new StudioByteSlice(0, 0, 416);

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];
        if (!long.TryParse(startText, out var start))
            start = 0;
        var end = long.TryParse(endText, out var parsedEnd) ? parsedEnd : length - 1;
        if (start < 0)
            start = 0;
        if (length == 0 || start >= length)
            return new StudioByteSlice(start, start, 416);
        if (end < start)
            end = start;
        if (end >= length)
            end = length - 1;
        return new StudioByteSlice(start, end, 206);
    }
}

public static class StudioEditUrls
{
    public static string ProxyMedia(string? absoluteUri, string itemId, string? apiKey)
    {
        var origin = StudioStreamUrls.FromAbsoluteUri(absoluteUri);
        var url = $"{origin.Origin}{origin.PathBase}/OpenLightFX/Studio/Items/{Uri.EscapeDataString(itemId)}/Proxy/Media";
        if (!string.IsNullOrEmpty(apiKey))
            url += "?api_key=" + Uri.EscapeDataString(apiKey);
        return url;
    }
}
