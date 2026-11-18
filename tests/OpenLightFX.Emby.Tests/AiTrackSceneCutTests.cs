using OpenLightFX.Emby.Api;
using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class AiTrackSceneCutTests
{
    [Fact]
    public void ConvertRgbToHsv_matches_OpenCV_8bit_rgb_to_hsv()
    {
        AssertHsv((0, 0, 0), (0, 0, 0));
        AssertHsv((255, 255, 255), (0, 0, 255));
        AssertHsv((255, 0, 0), (0, 255, 255));
        AssertHsv((0, 255, 0), (60, 255, 255));
        AssertHsv((0, 0, 255), (120, 255, 255));
        AssertHsv((128, 128, 128), (0, 0, 128));
        AssertHsv((140, 128, 120), (12, 36, 140));
        AssertHsv((255, 128, 0), (15, 255, 255));
        AssertHsv((80, 0, 120), (140, 255, 120));
        AssertHsv((200, 0, 4), (179, 255, 200));
        AssertHsv((1, 2, 3), (105, 170, 3));
        AssertHsv((10, 20, 30), (105, 170, 30));
        AssertHsv((90, 40, 10), (11, 227, 90));
        AssertHsv((255, 0, 255), (150, 255, 255));
        AssertHsv((0, 255, 255), (90, 255, 255));
        AssertHsv((255, 255, 0), (30, 255, 255));
    }

    [Fact]
    public void Scores_match_ContentDetector_on_solid_frames()
    {
        AssertScore((0, 0, 0), (255, 255, 255), 85);
        AssertScore((255, 255, 255), (0, 0, 0), 85);
        AssertScore((255, 0, 0), (0, 255, 0), 20);
        AssertScore((255, 0, 0), (0, 0, 255), 40);
        AssertScore((0, 0, 0), (128, 128, 128), 128.0 / 3);
        AssertScore((255, 255, 255), (128, 128, 128), 127.0 / 3);
        AssertScore((0, 0, 0), (255, 0, 0), 170);
        AssertScore((255, 0, 0), (255, 128, 0), 5);
        AssertScore((140, 128, 120), (128, 128, 128), 20);
        // Hue is not circular: 179 and 0 differ by 179, not by 1. Value also moves 255 → 200.
        AssertScore((255, 0, 0), (200, 0, 4), 78);
        AssertScore((80, 0, 120), (255, 128, 0), 260.0 / 3);
    }

    [Fact]
    public void Scores_match_ContentDetector_on_a_patterned_frame()
    {
        var first = Pattern();
        var second = (byte[])first.Clone();
        Set(second, 0, 0, 0, 0, 255);
        Set(second, 1, 1, 255, 0, 0);
        Set(second, 2, 2, 0, 255, 0);
        Set(second, 3, 3, 0, 0, 0);
        var third = RollRight(first);
        var detector = new ContentDetector(threshold: 27, minSceneLength: 0);
        var cuts = new List<int>();

        cuts.AddRange(detector.ProcessFrame(first, 4, 4));
        Assert.Equal(0, detector.LastScore);
        cuts.AddRange(detector.ProcessFrame(second, 4, 4));
        Assert.Equal(9.229166666666666, detector.LastScore, 9);
        cuts.AddRange(detector.ProcessFrame(third, 4, 4));
        Assert.Equal(91.64583333333333, detector.LastScore, 9);
        cuts.AddRange(detector.ProcessFrame(first, 4, 4));
        Assert.Equal(87.66666666666667, detector.LastScore, 9);
        Assert.Equal(new[] { 2, 3 }, cuts);
    }

    [Fact]
    public void First_frame_scores_zero_and_is_not_a_cut()
    {
        var detector = new ContentDetector();
        var cuts = detector.ProcessFrame(Solid(8, 8, 255, 255, 255), 8, 8);
        Assert.Empty(cuts);
        Assert.Equal(0, detector.LastScore);
    }

    [Fact]
    public void Threshold_27_accepts_a_hard_cut_and_rejects_a_small_hue_change()
    {
        var hard = new ContentDetector();
        var soft = new ContentDetector();
        var cuts = new List<int>();
        for (var i = 0; i < 16; i++)
        {
            var hardFrame = i < 15 ? Solid(2, 2, 0, 0, 0) : Solid(2, 2, 255, 255, 255);
            var softFrame = i < 15 ? Solid(2, 2, 255, 0, 0) : Solid(2, 2, 0, 255, 0);
            cuts.AddRange(hard.ProcessFrame(hardFrame, 2, 2));
            Assert.Empty(soft.ProcessFrame(softFrame, 2, 2));
        }

        Assert.Equal(new[] { 15 }, cuts);
        Assert.Equal(85, hard.LastScore);
        Assert.Equal(20, soft.LastScore);
    }

    public static IEnumerable<object[]> FlashCases()
    {
        yield return Case(new[] { 15 }, 40, 15, new[] { 15 });
        yield return Case(new[] { 14 }, 40, 15, Array.Empty<int>());
        yield return Case(new[] { 15, 30 }, 60, 15, new[] { 15, 30 });
        yield return Case(new[] { 10, 15 }, 40, 15, Array.Empty<int>());
        yield return Case(new[] { 40, 45, 50, 55 }, 90, 15, new[] { 40 });
        yield return Case(new[] { 40, 45, 80 }, 120, 15, new[] { 40, 80 });
        yield return Case(new[] { 100 }, 130, 30, new[] { 100 });
        yield return Case(new[] { 100, 110 }, 140, 30, new[] { 100 });
        yield return Case(new[] { 100, 400 }, 430, 30, new[] { 100, 400 });
        yield return Case(new[] { 100, 110, 400, 415 }, 460, 30, new[] { 100, 415 });
        yield return Case(new[] { 100, 110, 580 }, 600, 30, new[] { 100 });
        yield return Case(new[] { 100, 200, 210, 220, 230, 240, 400 }, 450, 30, new[] { 100, 200, 240, 400 });

        static object[] Case(int[] above, int length, int minSceneLength, int[] expected) =>
            new object[] { above, length, minSceneLength, expected };
    }

    [Theory]
    [MemberData(nameof(FlashCases))]
    public void Flash_filter_matches_PySceneDetect_merge_mode(int[] above, int length, int minSceneLength, int[] expected)
    {
        var detector = new ContentDetector(threshold: 27, minSceneLength: minSceneLength);
        var cuts = new List<int>();
        var white = false;
        var black = Solid(2, 2, 0, 0, 0);
        var bright = Solid(2, 2, 255, 255, 255);
        var aboveSet = new HashSet<int>(above);
        for (var i = 0; i < length; i++)
        {
            if (aboveSet.Contains(i)) white = !white;
            cuts.AddRange(detector.ProcessFrame(white ? bright : black, 2, 2));
        }

        Assert.Equal(expected, cuts);
    }

    [Fact]
    public void Studio_slider_maps_onto_the_PySceneDetect_score()
    {
        Assert.Equal(0.27, new PostStudioGeneration().SceneCutThreshold);
        Assert.Equal(27, ContentDetector.ThresholdFromStudioSetting(0.27));
        Assert.Equal(27, ContentDetector.ThresholdFromStudioSetting(ContentDetector.StudioSettingDefault));
        Assert.Equal(35, ContentDetector.ThresholdFromStudioSetting(0.35));
        Assert.Equal(1, ContentDetector.ThresholdFromStudioSetting(0.01));
        Assert.Equal(1, ContentDetector.ThresholdFromStudioSetting(0));
        Assert.Equal(100, ContentDetector.ThresholdFromStudioSetting(1));
        Assert.Equal(100, ContentDetector.ThresholdFromStudioSetting(40));
    }

    [Fact]
    public void Detection_size_keeps_aspect_and_does_not_upscale()
    {
        Assert.Equal((256, 144), AiTrackAnalysis.DetectionSize(1920, 1080));
        Assert.Equal((256, 106), AiTrackAnalysis.DetectionSize(1920, 800));
        Assert.Equal((200, 100), AiTrackAnalysis.DetectionSize(200, 100));
        Assert.Equal((100, 80), AiTrackAnalysis.DetectionSize(101, 81));
        Assert.Equal((256, 144), AiTrackAnalysis.DetectionSize(0, 0));
    }

    [Fact]
    public void Prefill_decode_asks_ffmpeg_to_use_available_hardware()
    {
        var args = AiTrackAnalysis.DecodeArguments("/movie.mkv", "scale=256:144[cut]");
        Assert.Contains("-hwaccel", args);
        Assert.Contains("auto", args);
        Assert.Contains("-i", args);
        Assert.Contains("/movie.mkv", args);
    }

    [Fact]
    public void Analysis_filter_splits_colour_samples_from_full_frame_cut_detection()
    {
        var filter = AiTrackAnalysis.Filter(2, 1, 256, 144, "");
        Assert.Equal(
            "[0:2]split=2[color_in][cut_in];" +
            "[color_in]fps=1,scale=64:36,format=rgb24[color];" +
            "[cut_in]fps=24,scale=256:144:flags=bilinear,format=rgb24[cut]",
            filter);

        var hdr = AiTrackAnalysis.Filter(0, 0.5, 256, 106, "zscale=tin=smpte2084:t=linear:npl=100,");
        Assert.Contains("[0:0]split=2[color_in][cut_in];", hdr, StringComparison.Ordinal);
        Assert.Contains("[color_in]fps=0.5,zscale=tin=smpte2084:t=linear:npl=100,scale=64:36,format=rgb24[color];", hdr, StringComparison.Ordinal);
        Assert.Contains("[cut_in]fps=24,zscale=tin=smpte2084:t=linear:npl=100,scale=256:106:flags=bilinear,format=rgb24[cut]", hdr, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_body_skips_the_cut_and_the_frames_before_the_next_cut()
    {
        Assert.False(SceneSampling.IsBodyFrame(0, 0, 48));
        Assert.False(SceneSampling.IsBodyFrame(11, 0, 48));
        Assert.True(SceneSampling.IsBodyFrame(12, 0, 48));
        Assert.True(SceneSampling.IsBodyFrame(41, 0, 48));
        Assert.False(SceneSampling.IsBodyFrame(42, 0, 48));
        Assert.False(SceneSampling.IsBodyFrame(48, 0, 48));

        // 15-frame scene: the main window is empty, so the body is after a short skip.
        Assert.False(SceneSampling.IsBodyFrame(12, 10, 25));
        Assert.True(SceneSampling.IsBodyFrame(13, 10, 25));
        Assert.False(SceneSampling.IsBodyFrame(25, 10, 25));
    }

    [Fact]
    public void Keyframes_use_a_short_transition_on_cuts_and_the_opening_sample_otherwise()
    {
        var cut = AiTrackAnalysis.Keyframe("id", "left", 5000, (255, 0, 0), sceneCut: true);
        Assert.Equal(200u, cut.TransitionMs);
        Assert.True(cut.PowerOn);
        Assert.Equal(5000, cut.TimestampMs);

        var opening = AiTrackAnalysis.Keyframe("id", "left", 0, (1, 1, 1), sceneCut: false);
        Assert.Equal(0u, opening.TransitionMs);
        Assert.False(opening.PowerOn);

        var early = AiTrackAnalysis.Keyframe("id", "left", 100, (40, 40, 40), sceneCut: false);
        Assert.Equal(100u, early.TransitionMs);
        Assert.True(early.PowerOn);
    }

    private static void AssertHsv((byte R, byte G, byte B) rgb, (byte H, byte S, byte V) expected)
    {
        var hue = new byte[1];
        var sat = new byte[1];
        var val = new byte[1];
        ContentDetector.ConvertRgbToHsv(new[] { rgb.R, rgb.G, rgb.B }, hue, sat, val);
        Assert.Equal(expected, (hue[0], sat[0], val[0]));
    }

    private static void AssertScore((byte R, byte G, byte B) from, (byte R, byte G, byte B) to, double expected)
    {
        var detector = new ContentDetector();
        detector.ProcessFrame(Solid(8, 8, from.R, from.G, from.B), 8, 8);
        detector.ProcessFrame(Solid(8, 8, to.R, to.G, to.B), 8, 8);
        Assert.Equal(expected, detector.LastScore, 9);
    }

    private static byte[] Pattern() => new byte[]
    {
        255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0,
        255, 0, 255, 0, 255, 255, 128, 128, 128, 10, 20, 30,
        200, 0, 4, 80, 0, 120, 255, 128, 0, 0, 0, 0,
        255, 255, 255, 1, 2, 3, 90, 40, 10, 140, 128, 120
    };

    private static void Set(byte[] frame, int x, int y, byte r, byte g, byte b)
    {
        var offset = (y * 4 + x) * 3;
        frame[offset] = r;
        frame[offset + 1] = g;
        frame[offset + 2] = b;
    }

    private static byte[] RollRight(byte[] source)
    {
        var rolled = new byte[source.Length];
        const int width = 4;
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < width; x++)
        {
            var fromX = (x + width - 1) % width;
            var from = (y * width + fromX) * 3;
            var to = (y * width + x) * 3;
            rolled[to] = source[from];
            rolled[to + 1] = source[from + 1];
            rolled[to + 2] = source[from + 2];
        }

        return rolled;
    }

    private static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var frame = new byte[width * height * 3];
        for (var i = 0; i < frame.Length; i += 3)
        {
            frame[i] = r;
            frame[i + 1] = g;
            frame[i + 2] = b;
        }

        return frame;
    }
}
