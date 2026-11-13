using OpenLightFX.Emby.Api;
using OpenLightFX.Emby.Studio;
using Xunit;
using SsJson = ServiceStack.Text.JsonSerializer;

namespace OpenLightFX.Emby.Tests;

public class EditMediaTests
{
    private const string ProbeJson = """
        {
          "format": { "duration": "120.000" },
          "streams": [
            {
              "codec_type": "video",
              "codec_name": "hevc",
              "width": 1920,
              "height": 1080,
              "avg_frame_rate": "24/1",
              "color_transfer": "smpte2084"
            },
            { "codec_type": "audio", "codec_name": "aac" }
          ]
        }
        """;

    [Fact(DisplayName = "MEDIA-015 @fixture a zero runtime is replaced by the probe")]
    public void Zero_runtime_uses_the_probe()
    {
        var probed = StudioFfprobe.Parse(ProbeJson, itemRuntimeMs: 0);
        Assert.Equal(120000, probed.RuntimeMs);
        Assert.True(probed.Hdr);
        Assert.Equal("hevc", probed.VideoCodec);

        var kept = StudioFfprobe.Parse(ProbeJson, itemRuntimeMs: 5000);
        Assert.Equal(5000, kept.RuntimeMs);
    }

    [Fact(DisplayName = "PLUG-001 @fixture media info JSON is camelCase and names proxy state")]
    public void Media_info_json_uses_camel_case()
    {
        var json = SsJson.SerializeToString(new StudioMediaInfoResponse
        {
            RuntimeMs = 120000,
            Width = 1920,
            Height = 1080,
            VideoCodec = "hevc",
            Hdr = true,
            ColorTransfer = "smpte2084",
            AudioCodec = "aac",
            Proxy = new StudioProxyStatusResponse
            {
                State = "building",
                EncodedThroughMs = 0,
                Fps = 24,
                KeyframeIntervalMs = 500,
                Width = 1280,
                Height = 720,
                EditUrl = "http://127.0.0.1:8096/OpenLightFX/Studio/Items/1/Proxy/Media"
            }
        });

        Assert.Contains("\"runtimeMs\":120000", json);
        Assert.Contains("\"videoCodec\":\"hevc\"", json);
        Assert.Contains("\"state\":\"building\"", json);
        Assert.DoesNotContain("RuntimeMs", json);
        Assert.DoesNotContain("Transcode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "PLUG-002 @fixture HDR frames use tonemap=hable to bt709")]
    public void Hdr_frame_filter_is_hable()
    {
        var pq = StudioFramePlan.Filter(true, "smpte2084", 1280);
        Assert.Contains("tin=smpte2084", pq);
        Assert.Contains("tonemap=hable:desat=0", pq);
        Assert.Contains("zscale=t=bt709:m=bt709:r=tv", pq);
        Assert.Equal("hable-bt709", StudioFramePlan.ToneMap(true));

        var hlg = StudioFramePlan.Filter(true, "arib-std-b67", 1280);
        Assert.Contains("tin=arib-std-b67", hlg);
        Assert.Contains("tonemap=hable:desat=0", hlg);

        var sdr = StudioFramePlan.Filter(false, "bt709", 1280);
        Assert.DoesNotContain("tonemap", sdr);
        Assert.Equal("none", StudioFramePlan.ToneMap(false));
        Assert.Equal(1280, StudioFramePlan.ClampWidth(null));
        Assert.Equal(1920, StudioFramePlan.ClampWidth(4000));

        var emby = StudioFramePlan.Filter(true, "smpte2084", 1280, zscale: false);
        Assert.DoesNotContain("zscale", emby);
        Assert.Contains("supertonemap=tonemap=hable:desat=0", emby);
        Assert.EndsWith("format=rgb24,scale='min(1280,iw)':-2", emby);
        Assert.True(StudioFramePlan.HasZscale(" ... zscale            V->V       Apply resizing, colorspace and bit depth conversion.\n"));
        Assert.False(StudioFramePlan.HasZscale(" .S. tonemap           V->V       Conversion to/from different dynamic ranges.\n"));

        var args = StudioFramePlan.Arguments("/movies/clip.mkv", 2500, true, "smpte2084", null);
        Assert.Contains("-accurate_seek", args);
        Assert.Contains("2.5", args);
        Assert.Equal(2480, StudioFramePlan.SourceTimeMs("n:0 pts_time:2.48 ", 2500));
        Assert.Equal(2500, StudioFramePlan.SourceTimeMs("no pts", 2500));
    }

    [Theory(DisplayName = "PLUG-003 @fixture the proxy is H.264 with a keyframe at least every 500 ms")]
    [InlineData(24)]
    [InlineData(23.976)]
    [InlineData(30)]
    [InlineData(60)]
    public void Proxy_arguments_force_a_short_closed_gop(double fps)
    {
        var interval = StudioProxyPlan.IntervalMs(fps);
        Assert.InRange(interval, 1, 500);

        var args = string.Join(" ", StudioProxyPlan.Arguments("/in.mkv", "/out.mp4", fps));
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-ac 2", args);
        Assert.Contains("-sc_threshold 0", args);
        Assert.Contains("scenecut=0", args);
        Assert.Contains("open-gop=0", args);
        Assert.Contains("+cgop", args);
        Assert.Contains("frag_keyframe+empty_moov", args);
        Assert.DoesNotContain("-re", args, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcode", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "MEDIA-004 @fixture the edit URL is not an Emby transcode")]
    public void Edit_url_is_a_direct_file()
    {
        var url = StudioEditUrls.ProxyMedia(
            "http://127.0.0.1:8096/OpenLightFX/Studio/Items/1/MediaInfo",
            "1",
            "test-admin");
        Assert.StartsWith("http://127.0.0.1:8096/OpenLightFX/Studio/Items/1/Proxy/Media", url);
        Assert.Contains("api_key=test-admin", url);
        Assert.DoesNotContain("Transcode", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "MEDIA-006 @fixture a byte range is 206 and a seek past the file is not a transcode")]
    public void Range_requests_do_not_invent_a_transcode()
    {
        var partial = StudioByteRange.Resolve("bytes=0-100", 5000);
        Assert.Equal(206, partial.Status);
        Assert.Equal(0, partial.Start);
        Assert.Equal(100, partial.End);

        var past = StudioByteRange.Resolve("bytes=9000-9100", 1000);
        Assert.Equal(416, past.Status);

        Assert.Equal(1500, StudioFfmpegProgress.TryReadEncodedMs("out_time_ms=1500000", out var ms) ? ms : -1);
        Assert.Equal(1500, StudioFfmpegProgress.TryReadEncodedMs("out_time=00:00:01.500000", out var clock) ? clock : -1);
    }

    [Fact(DisplayName = "MEDIA-016 @fixture an open-ended read follows a growing proxy and a closed range does not")]
    public void Open_ended_reads_follow_the_growing_file()
    {
        Assert.True(StudioProxyReadPlan.IsOpenEnded(null));
        Assert.True(StudioProxyReadPlan.IsOpenEnded("bytes=0-"));
        Assert.False(StudioProxyReadPlan.IsOpenEnded("bytes=0-100"));
        Assert.Equal(0, StudioProxyReadPlan.RangeStart("bytes=0-"));
        Assert.Equal(9000, StudioProxyReadPlan.RangeStart("bytes=9000-"));
        Assert.Equal("edit proxy failed", StudioProxyReadPlan.PublicReason(null));
        Assert.DoesNotContain("api_key", StudioProxyReadPlan.PublicReason("ffmpeg exited and api_key=secret"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "ERR-001 @fixture a failed proxy keeps a reason and can be forgotten")]
    public void Failed_proxy_keeps_a_reason()
    {
        var job = new StudioProxyJob("item", Path.Combine(Path.GetTempPath(), "missing.mp4"), 24, 320, 240);
        job.MarkFailed("ffmpeg exited with code 1");
        Assert.Equal("failed", job.State);
        Assert.Equal("ffmpeg exited with code 1", job.Reason);
        job.MarkFailed("saw api_key=abc");
        Assert.DoesNotContain("api_key", job.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "MEDIA-003 @fixture a growing proxy stream stays open until the encode finishes")]
    public async Task Growing_stream_reads_bytes_appended_during_the_build()
    {
        var path = Path.Combine(Path.GetTempPath(), "olfx-grow-" + Guid.NewGuid().ToString("N"));
        var job = new StudioProxyJob("grow", path, 24, 320, 240);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
        await using var stream = new StudioGrowingStream(job, 0);
        Assert.False(stream.CanSeek);
        var buffer = new byte[8];
        var first = await stream.ReadAsync(buffer.AsMemory(0, 8));
        Assert.Equal(4, first);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..4]);

        var waiting = stream.ReadAsync(buffer.AsMemory(0, 8)).AsTask();
        await Task.Delay(80);
        Assert.False(waiting.IsCompleted);

        await using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            await writer.WriteAsync(new byte[] { 5, 6, 7, 8 });
            await writer.FlushAsync();
        }

        var second = await waiting;
        Assert.Equal(4, second);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, buffer[..4]);
        job.MarkReady(8000);
        Assert.Equal(0, await stream.ReadAsync(buffer));
        File.Delete(path);
    }
}
