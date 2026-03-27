namespace OpenLightFX.Emby.Engine.Ai;

/// <summary>
/// Pure math operations on raw RGB24 pixel buffers (R,G,B,R,G,B,...).
/// No FFmpeg or Emby dependencies — fully unit-testable.
/// </summary>
internal static class FrameColorMath
{
    private const byte DarkLuminanceThreshold = 20; // pixel rows/cols below this are treated as letterbox

    /// <summary>
    /// Detects uniform dark letterbox/pillarbox borders and returns the crop bounds
    /// (inclusive pixel coordinates of the non-dark content area).
    /// Returns (0, height-1, 0, width-1) if no letterbox is detected.
    /// </summary>
    public static (int top, int bottom, int left, int right) DetectLetterbox(
        byte[] rgb, int width, int height)
    {
        int top = 0, bottom = height - 1, left = 0, right = width - 1;

        for (int row = 0; row < height / 2; row++)
        {
            if (IsRowDark(rgb, row, width)) top = row + 1;
            else break;
        }
        for (int row = height - 1; row > height / 2; row--)
        {
            if (IsRowDark(rgb, row, width)) bottom = row - 1;
            else break;
        }
        for (int col = 0; col < width / 2; col++)
        {
            if (IsColDark(rgb, col, width, height)) left = col + 1;
            else break;
        }
        for (int col = width - 1; col > width / 2; col--)
        {
            if (IsColDark(rgb, col, width, height)) right = col - 1;
            else break;
        }

        // Sanity: if crop ate the whole frame, return full frame
        if (top >= bottom || left >= right)
            return (0, height - 1, 0, width - 1);

        return (top, bottom, left, right);
    }

    /// <summary>
    /// Zeroes out pixels inside a center ellipse of the given diameter percentage.
    /// exclusionPercent = 50 means the ellipse diameter is half the frame width/height.
    /// Modifies the buffer in place.
    /// </summary>
    public static void ApplyCenterExclusionMask(byte[] rgb, int width, int height, int exclusionPercent)
    {
        if (exclusionPercent <= 0) return;

        // At 100% exclusion, zero the entire frame without ellipse math
        if (exclusionPercent >= 100)
        {
            Array.Clear(rgb, 0, rgb.Length);
            return;
        }

        float cx = width / 2f;
        float cy = height / 2f;
        float rx = cx * exclusionPercent / 100f;
        float ry = cy * exclusionPercent / 100f;
        if (rx < 0.5f || ry < 0.5f) return;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = (x - cx) / rx;
                float dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1f)
                {
                    int i = (y * width + x) * 3;
                    rgb[i] = 0;
                    rgb[i + 1] = 0;
                    rgb[i + 2] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Extracts the dominant non-black color from a raw RGB24 buffer using k-means (k=3, 10 iterations).
    /// pixelCount is the number of pixels (buffer length must be pixelCount * 3).
    /// Returns (0, 0, 0) if all pixels are near-black.
    /// </summary>
    public static (byte R, byte G, byte B) ExtractDominantColor(byte[] rgb, int pixelCount)
    {
        const int k = 3;
        const int iterations = 10;
        const float nearBlackLuminance = 10f; // max-channel threshold: excludes only truly near-black clusters

        // Initialize cluster centers: first pixel, last pixel, and middle pixel.
        // This spread reduces the risk of all centers landing in one color region.
        var centers = new (float R, float G, float B)[k];
        int[] seedIndices = [0, pixelCount - 1, pixelCount / 2];
        for (int c = 0; c < k; c++)
        {
            int pi = seedIndices[c] * 3;
            centers[c] = (rgb[pi], rgb[pi + 1], rgb[pi + 2]);
        }

        var assignments = new int[pixelCount];
        var counts = new int[k];
        var sumsR = new float[k];
        var sumsG = new float[k];
        var sumsB = new float[k];

        for (int iter = 0; iter < iterations; iter++)
        {
            // Assign each pixel to the nearest center
            for (int i = 0; i < pixelCount; i++)
            {
                int pi = i * 3;
                float r = rgb[pi], g = rgb[pi + 1], b = rgb[pi + 2];
                float minDist = float.MaxValue;
                int nearest = 0;
                for (int c = 0; c < k; c++)
                {
                    float dr = r - centers[c].R;
                    float dg = g - centers[c].G;
                    float db = b - centers[c].B;
                    float dist = dr * dr + dg * dg + db * db;
                    if (dist < minDist) { minDist = dist; nearest = c; }
                }
                assignments[i] = nearest;
            }

            // Update centers
            Array.Clear(counts, 0, k);
            Array.Clear(sumsR, 0, k);
            Array.Clear(sumsG, 0, k);
            Array.Clear(sumsB, 0, k);
            for (int i = 0; i < pixelCount; i++)
            {
                int c = assignments[i];
                int pi = i * 3;
                sumsR[c] += rgb[pi];
                sumsG[c] += rgb[pi + 1];
                sumsB[c] += rgb[pi + 2];
                counts[c]++;
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                    centers[c] = (sumsR[c] / counts[c], sumsG[c] / counts[c], sumsB[c] / counts[c]);
            }
        }

        // Pick the most populated cluster that is not near-black
        int bestCluster = -1;
        int bestCount = 0;
        for (int c = 0; c < k; c++)
        {
            float lum = 0.299f * centers[c].R + 0.587f * centers[c].G + 0.114f * centers[c].B;
            if (lum < nearBlackLuminance) continue;
            if (counts[c] > bestCount) { bestCount = counts[c]; bestCluster = c; }
        }

        // Fallback: all clusters near-black — return the most populated one
        if (bestCluster == -1)
        {
            for (int c = 0; c < k; c++)
                if (counts[c] > bestCount) { bestCount = counts[c]; bestCluster = c; }
        }

        if (bestCluster == -1 || bestCount == 0) return (0, 0, 0);

        return (
            (byte)Math.Clamp((int)centers[bestCluster].R, 0, 255),
            (byte)Math.Clamp((int)centers[bestCluster].G, 0, 255),
            (byte)Math.Clamp((int)centers[bestCluster].B, 0, 255)
        );
    }

    /// <summary>
    /// Computes the normalized L1 histogram difference between two RGB24 frames.
    /// Returns a value in [0, 1]. Above ~0.35 typically indicates a scene cut.
    /// Both buffers must be pixelCount * 3 bytes.
    /// </summary>
    public static float ComputeHistogramDifference(byte[] frame1, byte[] frame2, int pixelCount)
    {
        // 16-bucket histograms (top 4 bits of each channel).
        // 16 buckets (each ~16 intensity units wide) keeps small frame-to-frame
        // color variations in the same bucket, reducing false scene-cut detections.
        const int buckets = 16;
        const int shift = 4; // 256 / 16 = each bucket spans 16 values
        var h1R = new int[buckets]; var h1G = new int[buckets]; var h1B = new int[buckets];
        var h2R = new int[buckets]; var h2G = new int[buckets]; var h2B = new int[buckets];

        for (int i = 0; i < pixelCount; i++)
        {
            int pi = i * 3;
            h1R[frame1[pi] >> shift]++;      h1G[frame1[pi+1] >> shift]++; h1B[frame1[pi+2] >> shift]++;
            h2R[frame2[pi] >> shift]++;      h2G[frame2[pi+1] >> shift]++; h2B[frame2[pi+2] >> shift]++;
        }

        float diff = 0f;
        for (int b = 0; b < buckets; b++)
        {
            diff += Math.Abs(h1R[b] - h2R[b]);
            diff += Math.Abs(h1G[b] - h2G[b]);
            diff += Math.Abs(h1B[b] - h2B[b]);
        }

        // Max possible L1 diff per channel = 2 * pixelCount. Three channels: 6 * pixelCount.
        return diff / (6f * pixelCount);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static bool IsRowDark(byte[] rgb, int row, int width)
    {
        int offset = row * width * 3;
        for (int x = 0; x < width; x++)
        {
            int i = offset + x * 3;
            float lum = 0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2];
            if (lum > DarkLuminanceThreshold) return false;
        }
        return true;
    }

    private static bool IsColDark(byte[] rgb, int col, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int i = (y * width + col) * 3;
            float lum = 0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2];
            if (lum > DarkLuminanceThreshold) return false;
        }
        return true;
    }
}
