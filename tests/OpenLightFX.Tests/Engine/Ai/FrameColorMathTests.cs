namespace OpenLightFX.Tests.Engine.Ai;

using OpenLightFX.Emby.Engine.Ai;
using Xunit;

public class FrameColorMathTests
{
    // ── Letterbox detection ──────────────────────────────────────────

    [Fact]
    public void DetectLetterbox_NoLetterbox_ReturnsFullFrame()
    {
        // 4x4 image, all pixels are mid-gray (non-dark)
        var rgb = MakeRgb(4, 4, r: 128, g: 128, b: 128);
        var (top, bottom, left, right) = FrameColorMath.DetectLetterbox(rgb, 4, 4);
        Assert.Equal(0, top);
        Assert.Equal(3, bottom);
        Assert.Equal(0, left);
        Assert.Equal(3, right);
    }

    [Fact]
    public void DetectLetterbox_TopAndBottomBars_DetectedCorrectly()
    {
        // 4x6 image: rows 0 and 5 are black, rows 1-4 are colored
        var rgb = MakeRgb(4, 6, r: 0, g: 0, b: 0);
        // Set rows 1-4 to non-dark
        for (int row = 1; row <= 4; row++)
            for (int col = 0; col < 4; col++)
                SetPixel(rgb, 4, col, row, 200, 100, 50);

        var (top, bottom, left, right) = FrameColorMath.DetectLetterbox(rgb, 4, 6);
        Assert.Equal(1, top);
        Assert.Equal(4, bottom);
        Assert.Equal(0, left);
        Assert.Equal(3, right);
    }

    // ── Center exclusion mask ────────────────────────────────────────

    [Fact]
    public void ApplyCenterExclusionMask_100Percent_ZerosEntireFrame()
    {
        var rgb = MakeRgb(10, 10, r: 200, g: 150, b: 100);
        FrameColorMath.ApplyCenterExclusionMask(rgb, 10, 10, 100);
        Assert.All(rgb, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ApplyCenterExclusionMask_0Percent_LeavesFrameUnchanged()
    {
        var rgb = MakeRgb(10, 10, r: 200, g: 150, b: 100);
        var original = (byte[])rgb.Clone();
        FrameColorMath.ApplyCenterExclusionMask(rgb, 10, 10, 0);
        Assert.Equal(original, rgb);
    }

    [Fact]
    public void ApplyCenterExclusionMask_50Percent_ZerosCenterPixel()
    {
        // Center pixel of a 10x10 frame must be zeroed at 50% exclusion
        var rgb = MakeRgb(10, 10, r: 255, g: 255, b: 255);
        FrameColorMath.ApplyCenterExclusionMask(rgb, 10, 10, 50);
        // Pixel at (5, 5) — center — should be zeroed
        int i = (5 * 10 + 5) * 3;
        Assert.Equal(0, rgb[i]);
        Assert.Equal(0, rgb[i + 1]);
        Assert.Equal(0, rgb[i + 2]);
    }

    // ── Dominant color extraction (k-means) ─────────────────────────

    [Fact]
    public void ExtractDominantColor_AllRed_ReturnsRed()
    {
        var rgb = MakeRgb(8, 8, r: 255, g: 0, b: 0);
        var (r, g, b) = FrameColorMath.ExtractDominantColor(rgb, 8 * 8);
        Assert.InRange(r, 200, 255);
        Assert.InRange(g, 0, 50);
        Assert.InRange(b, 0, 50);
    }

    [Fact]
    public void ExtractDominantColor_AllBlack_FallsBackToBlack()
    {
        // All pixels near-black — should return black (0,0,0) without throwing
        var rgb = MakeRgb(8, 8, r: 5, g: 5, b: 5);
        var (r, g, b) = FrameColorMath.ExtractDominantColor(rgb, 8 * 8);
        // Should not throw; result is some near-zero color
        Assert.InRange(r, 0, 30);
        Assert.InRange(g, 0, 30);
        Assert.InRange(b, 0, 30);
    }

    [Fact]
    public void ExtractDominantColor_MajorityBlueMinorityRed_ReturnsBlue()
    {
        // 56 blue pixels, 8 red pixels (8x8 = 64 total)
        var rgb = new byte[64 * 3];
        for (int i = 0; i < 56 * 3; i += 3) { rgb[i] = 0; rgb[i+1] = 0; rgb[i+2] = 200; }
        for (int i = 56 * 3; i < 64 * 3; i += 3) { rgb[i] = 200; rgb[i+1] = 0; rgb[i+2] = 0; }
        var (r, g, b) = FrameColorMath.ExtractDominantColor(rgb, 64);
        Assert.True(b > r, $"Expected blue > red but got R={r} B={b}");
    }

    // ── Histogram difference ─────────────────────────────────────────

    [Fact]
    public void ComputeHistogramDifference_IdenticalFrames_ReturnsZero()
    {
        var rgb = MakeRgb(8, 8, r: 100, g: 150, b: 200);
        float diff = FrameColorMath.ComputeHistogramDifference(rgb, rgb, 8 * 8);
        Assert.Equal(0f, diff, precision: 4);
    }

    [Fact]
    public void ComputeHistogramDifference_OppositeFrames_ReturnsNearOne()
    {
        var frame1 = MakeRgb(8, 8, r: 255, g: 0, b: 0);
        var frame2 = MakeRgb(8, 8, r: 0, g: 0, b: 255);
        float diff = FrameColorMath.ComputeHistogramDifference(frame1, frame2, 8 * 8);
        Assert.True(diff > 0.3f, $"Expected significant diff, got {diff}");
    }

    [Fact]
    public void ComputeHistogramDifference_SlightlyDifferentFrames_ReturnsBelowThreshold()
    {
        var frame1 = MakeRgb(8, 8, r: 100, g: 150, b: 200);
        var frame2 = MakeRgb(8, 8, r: 105, g: 148, b: 198); // small change
        float diff = FrameColorMath.ComputeHistogramDifference(frame1, frame2, 8 * 8);
        Assert.True(diff < 0.35f, $"Expected diff < 0.35 (scene cut threshold), got {diff}");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static byte[] MakeRgb(int width, int height, byte r, byte g, byte b)
    {
        var buf = new byte[width * height * 3];
        for (int i = 0; i < buf.Length; i += 3) { buf[i] = r; buf[i+1] = g; buf[i+2] = b; }
        return buf;
    }

    private static void SetPixel(byte[] rgb, int width, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * width + x) * 3;
        rgb[i] = r; rgb[i+1] = g; rgb[i+2] = b;
    }
}
