# AI Lighting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fully-local, real-time AI lighting mode that extracts dominant background colors from video frames via FFmpeg and generates lighting keyframes on the fly, with a `.ailfx` protobuf sidecar cache for instant subsequent playbacks.

**Architecture:** A background `AiLightingWorker` runs during playback, calling FFmpeg once per poll interval to extract a batch of frames from the lookahead window. Each frame is preprocessed (letterbox crop, center exclusion mask) and then k-means clustered to find the dominant background color. Generated keyframes are injected into the live `PlaybackSession` via a new `AppendKeyframes()` method. Results are cached as a `.ailfx` sidecar (valid `LightFXTrack` protobuf, importable in openlightfx-studio).

**Tech Stack:** .NET 8.0, xUnit, FFmpeg subprocess (batch per poll interval), Google.Protobuf (already in project), pure C# k-means.

**Prerequisite:** `lib/*.dll` must be present — run `EMBY_HOST=192.168.1.3 ./scripts/fetch-sdk.sh` first.

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/OpenLightFX.Emby/Engine/Ai/FrameColorMath.cs` | Pure math: letterbox detect, center mask, k-means, histogram diff |
| Create | `src/OpenLightFX.Emby/Engine/Ai/FrameAnalysisPipeline.cs` | FFmpeg subprocess frame extraction, calls FrameColorMath |
| Create | `src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs` | Background worker: analysis loop, cache r/w, seek handling |
| Modify | `src/OpenLightFX.Emby/Engine/PlaybackSession.cs` | Add `_aiKeyframeQueue`, `AppendKeyframes()`, `OnSeek` delegate |
| Modify | `src/OpenLightFX.Emby/ServerEntryPoint.cs` | Branch on `AiLightingEnabled` in `TryStartLightingSession` |
| Modify | `src/OpenLightFX.Emby/Configuration/PluginOptions.cs` | New AI config fields |
| Create | `tests/OpenLightFX.Tests/OpenLightFX.Tests.csproj` | xUnit test project |
| Create | `tests/OpenLightFX.Tests/Engine/Ai/FrameColorMathTests.cs` | Unit tests for color math |

---

## Task 1: Add AI config fields to PluginOptions

**Files:**
- Modify: `src/OpenLightFX.Emby/Configuration/PluginOptions.cs`

- [ ] **Step 1: Add fields after the `AdditionalScanPaths` property**

```csharp
    // --- AI Lighting ---
    [DisplayName("AI Lighting Mode")]
    [Description("When enabled, AI lighting takes precedence over .lightfx track files. " +
        "The plugin analyzes each video's frames in real time and drives bulbs to match " +
        "the dominant background color.")]
    public bool AiLightingEnabled { get; set; } = false;

    [DisplayName("AI Lookahead (ms)")]
    [Description("How far ahead of the current playback position to analyze frames (milliseconds). " +
        "Larger values give the worker more headroom on slow hardware.")]
    public int AiLookaheadMs { get; set; } = 30000;

    [DisplayName("AI Analysis Rate (fps)")]
    [Description("Frames extracted per second of video time. 2.0 is sufficient for scene-level changes.")]
    public float AiAnalysisRateFps { get; set; } = 2.0f;

    [DisplayName("AI Center Exclusion (%)")]
    [Description("Diameter of the center exclusion ellipse as a percentage of the frame. " +
        "Pixels inside this ellipse (foreground subjects) are ignored during color analysis. " +
        "Default 50 discards the central half of the frame.")]
    public int AiCenterExclusionPercent { get; set; } = 50;

    [DisplayName("AI Cache Enabled")]
    [Description("When enabled, analysis results are saved as a .ailfx sidecar file " +
        "next to the video for instant reuse on subsequent playbacks.")]
    public bool AiCacheEnabled { get; set; } = true;

    [DisplayName("AI Scene Cut Threshold")]
    [Description("Histogram difference (0.0–1.0) above which a scene boundary is detected. " +
        "Lower = more sensitive. Default 0.35 catches hard cuts without triggering on gradual shifts.")]
    public float AiSceneCutThreshold { get; set; } = 0.35f;

    [DisplayName("AI Max CPU (%)")]
    [Description("Worker pauses analysis when system CPU usage exceeds this percentage, " +
        "preventing AI lighting from competing with the Emby transcoder.")]
    public int AiMaxCpuPercent { get; set; } = 50;
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
cd /home/jon/git/openlightfx/openlightfx-emby
dotnet build src/OpenLightFX.Emby -c Release --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Configuration/PluginOptions.cs
git commit -m "feat: add AI lighting config fields to PluginOptions"
```

---

## Task 2: Create xUnit test project

**Files:**
- Create: `tests/OpenLightFX.Tests/OpenLightFX.Tests.csproj`
- Create: `tests/OpenLightFX.Tests/.gitkeep`

- [ ] **Step 1: Create the test project**

```bash
mkdir -p /home/jon/git/openlightfx/openlightfx-emby/tests/OpenLightFX.Tests
cd /home/jon/git/openlightfx/openlightfx-emby/tests/OpenLightFX.Tests
dotnet new xunit --framework net8.0 --no-restore
```

- [ ] **Step 2: Replace the generated .csproj with this content**

Replace `tests/OpenLightFX.Tests/OpenLightFX.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" PrivateAssets="all" />
  </ItemGroup>

  <!-- Emby SDK references (required to compile the main project) -->
  <ItemGroup>
    <Reference Include="MediaBrowser.Common">
      <HintPath>../../lib/MediaBrowser.Common.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Controller">
      <HintPath>../../lib/MediaBrowser.Controller.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Model">
      <HintPath>../../lib/MediaBrowser.Model.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Emby.Web.GenericEdit">
      <HintPath>../../lib/Emby.Web.GenericEdit.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/OpenLightFX.Emby/OpenLightFX.Emby.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Delete the generated UnitTest1.cs placeholder**

```bash
rm /home/jon/git/openlightfx/openlightfx-emby/tests/OpenLightFX.Tests/UnitTest1.cs
```

- [ ] **Step 4: Add test project to solution**

```bash
cd /home/jon/git/openlightfx/openlightfx-emby
dotnet sln add tests/OpenLightFX.Tests/OpenLightFX.Tests.csproj
```

- [ ] **Step 5: Restore and verify build**

```bash
dotnet restore tests/OpenLightFX.Tests
dotnet build tests/OpenLightFX.Tests --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add tests/ openlightfx-emby.sln
git commit -m "chore: add xUnit test project"
```

---

## Task 3: Implement FrameColorMath (pure math, no FFmpeg)

**Files:**
- Create: `src/OpenLightFX.Emby/Engine/Ai/FrameColorMath.cs`
- Create: `tests/OpenLightFX.Tests/Engine/Ai/FrameColorMathTests.cs`

This class is the testable core. All methods are `internal static` and operate on raw `byte[]` (RGB24: R,G,B,R,G,B,...). No Emby or FFmpeg dependencies.

Add `[assembly: InternalsVisibleTo("OpenLightFX.Tests")]` to expose internals to the test project.

- [ ] **Step 1: Add InternalsVisibleTo to the main project**

In `src/OpenLightFX.Emby/Plugin.cs`, add this line before the namespace declaration (or create a new file `src/OpenLightFX.Emby/AssemblyInfo.cs`):

Create `src/OpenLightFX.Emby/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("OpenLightFX.Tests")]
```

- [ ] **Step 2: Write the failing tests first**

Create `tests/OpenLightFX.Tests/Engine/Ai/FrameColorMathTests.cs`:

```csharp
namespace OpenLightFX.Tests.Engine.Ai;

using OpenLightFX.Emby.Engine.Ai;

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
```

- [ ] **Step 3: Run tests to confirm they fail with "type not found"**

```bash
cd /home/jon/git/openlightfx/openlightfx-emby
dotnet test tests/OpenLightFX.Tests --nologo 2>&1 | head -30
```

Expected: compile error `The type or namespace name 'FrameColorMath' could not be found`

- [ ] **Step 4: Create `FrameColorMath.cs`**

Create `src/OpenLightFX.Emby/Engine/Ai/FrameColorMath.cs`:

```csharp
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
        const float nearBlackLuminance = 25.5f; // 10% of 255

        // Initialize cluster centers to evenly-spaced pixels
        var centers = new (float R, float G, float B)[k];
        for (int c = 0; c < k; c++)
        {
            int pi = (c * pixelCount / k) * 3;
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
        // 64-bucket histograms (top 6 bits of each channel)
        var h1R = new int[64]; var h1G = new int[64]; var h1B = new int[64];
        var h2R = new int[64]; var h2G = new int[64]; var h2B = new int[64];

        for (int i = 0; i < pixelCount; i++)
        {
            int pi = i * 3;
            h1R[frame1[pi] >> 2]++;      h1G[frame1[pi+1] >> 2]++; h1B[frame1[pi+2] >> 2]++;
            h2R[frame2[pi] >> 2]++;      h2G[frame2[pi+1] >> 2]++; h2B[frame2[pi+2] >> 2]++;
        }

        float diff = 0f;
        for (int b = 0; b < 64; b++)
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
```

- [ ] **Step 5: Run tests to confirm they pass**

```bash
dotnet test tests/OpenLightFX.Tests --nologo
```

Expected: all tests pass, e.g. `Passed! - Failed: 0, Passed: 10, Skipped: 0`

- [ ] **Step 6: Commit**

```bash
git add src/OpenLightFX.Emby/AssemblyInfo.cs \
        src/OpenLightFX.Emby/Engine/Ai/FrameColorMath.cs \
        tests/OpenLightFX.Tests/Engine/Ai/FrameColorMathTests.cs
git commit -m "feat: implement FrameColorMath — letterbox, center mask, k-means, histogram diff"
```

---

## Task 4: Extend PlaybackSession with AppendKeyframes and OnSeek

**Files:**
- Modify: `src/OpenLightFX.Emby/Engine/PlaybackSession.cs`

- [ ] **Step 1: Add the queue field and public members**

In `PlaybackSession.cs`, add these two new fields alongside the existing private fields (around line 60, after `_lastSentCommands`):

```csharp
    // AI keyframe injection: worker appends to this queue; ProcessKeyframes drains it each tick
    private readonly ConcurrentQueue<Keyframe> _aiKeyframeQueue = new();

    /// <summary>
    /// Raised when a seek is detected. AI lighting worker uses this to reset its analysis cursor.
    /// The argument is the new playback position in milliseconds.
    /// </summary>
    public Action<ulong>? OnSeek { get; set; }
```

- [ ] **Step 2: Add the AppendKeyframes public method**

Add this method after the `CurrentPositionMs` property (around line 95):

```csharp
    /// <summary>
    /// Appends AI-generated keyframes to the session. Thread-safe — called from AiLightingWorker.
    /// Keyframes are merged into the sorted keyframe list on the next Tick().
    /// </summary>
    public void AppendKeyframes(IEnumerable<Keyframe> keyframes)
    {
        foreach (var kf in keyframes)
            _aiKeyframeQueue.Enqueue(kf);
    }
```

- [ ] **Step 3: Drain the queue at the top of ProcessKeyframes**

In `ProcessKeyframes(ulong positionMs)`, add these lines at the very beginning of the method body (before the `foreach` loop):

```csharp
        // Drain AI keyframe queue into the sorted list
        while (_aiKeyframeQueue.TryDequeue(out var aiKf))
        {
            // Binary search for insertion point (sort key: ChannelId then TimestampMs)
            int lo = 0, hi = _sortedKeyframes.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var m = _sortedKeyframes[mid];
                int cmp = string.Compare(m.ChannelId, aiKf.ChannelId, StringComparison.Ordinal);
                if (cmp == 0) cmp = m.TimestampMs.CompareTo(aiKf.TimestampMs);
                if (cmp < 0) lo = mid + 1; else hi = mid;
            }
            _sortedKeyframes.Insert(lo, aiKf);
        }
```

- [ ] **Step 4: Fire OnSeek when a seek is detected**

There are two seek-detection branches in `UpdatePosition()` — one for backward jumps (around line 214) and one for forward jumps (around line 235). In **both** branches, add the `OnSeek` invocation immediately after `_lastDispatchedMs = 0;`:

```csharp
                    _lastDispatchedMs = 0;
                    OnSeek?.Invoke(adjusted); // notify AI worker
```

(Do this in both the backward and forward seek branches.)

- [ ] **Step 5: Build to confirm no compile errors**

```bash
dotnet build src/OpenLightFX.Emby -c Release --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add src/OpenLightFX.Emby/Engine/PlaybackSession.cs
git commit -m "feat: add AppendKeyframes and OnSeek to PlaybackSession for AI lighting"
```

---

## Task 5: Implement FrameAnalysisPipeline (FFmpeg batch frame extraction)

**Files:**
- Create: `src/OpenLightFX.Emby/Engine/Ai/FrameAnalysisPipeline.cs`

This class calls `ffmpeg` as a subprocess once per analysis batch, extracting all frames in a window in a single invocation. The output is raw RGB24 bytes piped to stdout.

**FFmpeg binary location:** The pipeline tries `/usr/bin/ffmpeg`, then `/usr/local/bin/ffmpeg`, then `ffmpeg` (PATH). On Emby servers, ffmpeg is always present since Emby uses it for transcoding.

- [ ] **Step 1: Create `FrameAnalysisPipeline.cs`**

Create `src/OpenLightFX.Emby/Engine/Ai/FrameAnalysisPipeline.cs`:

```csharp
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

    private byte[]? _previousFrameRgb;
    private ulong _previousFrameTimestampMs;

    public FrameAnalysisPipeline(PluginOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _ffmpegPath = FindFfmpeg();
    }

    /// <summary>
    /// Analyzes frames from [startMs, endMs) at the configured analysis rate.
    /// Returns one AnalysisResult per frame analyzed. Results are in timestamp order.
    /// Returns an empty list if ffmpeg is unavailable or extraction fails.
    /// </summary>
    public List<AnalysisResult> AnalyzeWindow(string videoPath, ulong startMs, ulong endMs)
    {
        var results = new List<AnalysisResult>();
        if (!File.Exists(videoPath)) return results;

        double startSec = startMs / 1000.0;
        double durationSec = (endMs - startMs) / 1000.0;
        float fps = Math.Max(0.1f, _options.AiAnalysisRateFps);

        var frames = ExtractFrames(videoPath, startSec, durationSec, fps);
        if (frames == null) return results;

        double intervalSec = 1.0 / fps;
        for (int i = 0; i < frames.Count; i++)
        {
            ulong frameTimestampMs = startMs + (ulong)(i * intervalSec * 1000);
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
            _previousFrameTimestampMs = frameTimestampMs;
        }

        return results;
    }

    /// <summary>Resets state on seek — previous frame reference is no longer valid.</summary>
    public void ResetOnSeek()
    {
        _previousFrameRgb = null;
        _previousFrameTimestampMs = 0;
    }

    // ── Private helpers ──────────────────────────────────────────────

    private List<byte[]>? ExtractFrames(string videoPath, double startSec, double durationSec, float fps)
    {
        // One ffmpeg invocation extracts all frames in the window at the requested fps.
        // Output: raw RGB24 bytes, each frame exactly BytesPerFrame bytes.
        var args = $"-ss {startSec:F3} -t {durationSec:F3} -i \"{videoPath}\" " +
                   $"-vf \"fps={fps:F2},scale={FrameWidth}:{FrameHeight}\" " +
                   $"-f rawvideo -pix_fmt rgb24 -loglevel error pipe:1";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to start ffmpeg at '{0}': {1}", _ffmpegPath, ex.Message);
            return null;
        }

        var frames = new List<byte[]>();
        var stdout = process.StandardOutput.BaseStream;
        var buf = new byte[BytesPerFrame];

        while (true)
        {
            int bytesRead = ReadExact(stdout, buf, 0, BytesPerFrame);
            if (bytesRead < BytesPerFrame) break;
            frames.Add((byte[])buf.Clone());
        }

        process.WaitForExit(timeoutMilliseconds: 5000);
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
        string[] candidates = { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "ffmpeg" };
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate == "ffmpeg") return candidate; // trust PATH as last resort
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return "ffmpeg";
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
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
dotnet build src/OpenLightFX.Emby -c Release --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Engine/Ai/FrameAnalysisPipeline.cs
git commit -m "feat: implement FrameAnalysisPipeline — FFmpeg batch frame extraction and color analysis"
```

---

## Task 6: Implement AiLightingWorker — analysis loop and seek handling

**Files:**
- Create: `src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs`

- [ ] **Step 1: Create `AiLightingWorker.cs`**

Create `src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs`:

```csharp
namespace OpenLightFX.Emby.Engine.Ai;

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
    private const ulong MinBufferMs = 2000; // matches PlaybackSession's existing floor

    private readonly PlaybackSession _session;
    private readonly string _videoPath;
    private readonly PluginOptions _options;
    private readonly FrameAnalysisPipeline _pipeline;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;

    // Cursor: next timestamp (ms) to analyze from
    private ulong _nextUnanalyzedMs;

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
    /// Otherwise returns false and the caller should invoke Start().
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

            // Verify the cache was built from the current version of the video file
            var expectedMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(_videoPath)).ToUnixTimeSeconds().ToString();
            var mtimeTag = track.Metadata?.Tags.FirstOrDefault(t => t.StartsWith("source-mtime:"));
            if (mtimeTag == null || mtimeTag != $"source-mtime:{expectedMtime}")
            {
                _logger.Debug("[AI] Cache mtime mismatch for '{0}', will re-analyze", _videoPath);
                File.Delete(cachePath);
                return false;
            }

            _logger.Info("[AI] Loaded {0} keyframes from cache for '{1}'", track.Keyframes.Count, _videoPath);
            _session.AppendKeyframes(track.Keyframes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to load cache for '{0}': {1}", _videoPath, ex.Message);
            try { File.Delete(cachePath); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Starts the background analysis loop. Call only when TryLoadCache() returned false.
    /// Wire session.OnSeek to NotifySeek() before calling this.
    /// </summary>
    public void Start()
    {
        _nextUnanalyzedMs = MinBufferMs;
        _workerTask = Task.Run(RunLoop, _cts.Token);
        _logger.Info("[AI] Analysis worker started for '{0}'", _videoPath);
    }

    /// <summary>Called by PlaybackSession.OnSeek. Resets the analysis cursor.</summary>
    public void NotifySeek(ulong seekPositionMs)
    {
        // On forward seek: jump cursor to new position so we analyze the right window
        if (seekPositionMs > _nextUnanalyzedMs)
        {
            Interlocked.Exchange(ref _nextUnanalyzedMs, seekPositionMs + MinBufferMs);
            _logger.Debug("[AI] Seek forward to {0}ms — resetting analysis cursor", seekPositionMs);
        }
        // On backward seek: existing keyframes are still valid; cursor stays where it is
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
                    _logger.Debug("[AI] CPU above {0}% threshold — skipping batch", _options.AiMaxCpuPercent);
                    continue;
                }

                var currentPos = _session.CurrentPositionMs;
                var windowEnd = currentPos + (ulong)_options.AiLookaheadMs;

                if (_nextUnanalyzedMs >= windowEnd) continue; // lookahead fully covered

                // Analyze one 1000ms batch per loop iteration
                var batchEnd = Math.Min(_nextUnanalyzedMs + 1000, windowEnd);
                _logger.Debug("[AI] Analyzing {0}ms–{1}ms", _nextUnanalyzedMs, batchEnd);

                var results = _pipeline.AnalyzeWindow(_videoPath, _nextUnanalyzedMs, batchEnd);
                if (results.Count > 0)
                {
                    var keyframes = results.Select(ToKeyframe).ToList();
                    _session.AppendKeyframes(keyframes);

                    lock (_allKeyframesLock)
                        _allKeyframes.AddRange(keyframes);

                    _logger.Debug("[AI] Generated {0} keyframe(s) from {1} frame(s)", keyframes.Count, results.Count);
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

    private Keyframe ToKeyframe(AnalysisResult result)
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
            // Read /proc/stat for a 100ms sample on Linux
            var lines1 = File.ReadAllLines("/proc/stat");
            var cpu1 = ParseCpuLine(lines1[0]);
            Thread.Sleep(100);
            var lines2 = File.ReadAllLines("/proc/stat");
            var cpu2 = ParseCpuLine(lines2[0]);

            long idle = cpu2.idle - cpu1.idle;
            long total = cpu2.total - cpu1.total;
            if (total == 0) return false;

            double usagePercent = 100.0 * (1.0 - (double)idle / total);
            return usagePercent > _options.AiMaxCpuPercent;
        }
        catch
        {
            return false; // not Linux or /proc unavailable — don't throttle
        }
    }

    private static (long idle, long total) ParseCpuLine(string line)
    {
        // "cpu  user nice system idle iowait irq softirq ..."
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long idle = long.Parse(parts[4]);
        long total = parts.Skip(1).Sum(p => long.TryParse(p, out var v) ? v : 0);
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
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(_videoPath)).ToUnixTimeSeconds();
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

            using var fs = File.Create(tmpPath);
            track.WriteTo(fs);
            fs.Close();
            File.Move(tmpPath, finalPath, overwrite: true);

            _logger.Info("[AI] Wrote cache with {0} keyframes to '{1}'", keyframes.Count, finalPath);
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to write cache: {0}", ex.Message);
        }
    }

    private string GetCachePath() => Path.ChangeExtension(_videoPath, ".ailfx");

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
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
dotnet build src/OpenLightFX.Emby -c Release --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs
git commit -m "feat: implement AiLightingWorker — streaming analysis loop, seek handling, cache r/w"
```

---

## Task 7: Wire ServerEntryPoint for AI mode

**Files:**
- Modify: `src/OpenLightFX.Emby/ServerEntryPoint.cs`

The goal is to branch early in `TryStartLightingSession`: when `AiLightingEnabled` is true, skip track discovery and call the new `TryStartAiLightingSession` instead.

We also need to track active `AiLightingWorker` instances so they can be disposed on playback stop.

- [ ] **Step 1: Add the worker dictionary field**

In `ServerEntryPoint.cs`, add alongside the `_sessions` dictionary (around line 44):

```csharp
    // AI lighting workers, keyed by Emby session ID (parallel to _sessions)
    private readonly ConcurrentDictionary<string, AiLightingWorker> _aiWorkers = new();
```

Add the using directive at the top of the file:

```csharp
using OpenLightFX.Emby.Engine.Ai;
```

- [ ] **Step 2: Dispose workers on playback stop**

In `OnPlaybackStopped`, after `session.StopAsync()` is called, add worker disposal:

```csharp
            if (_sessions.TryRemove(sessionId, out var session))
            {
                _ = session.StopAsync();
                _logger.Info("Lighting session stopped: {0}", sessionId);
            }
            // Dispose AI worker if one is active (writes the .ailfx cache on dispose)
            if (_aiWorkers.TryRemove(sessionId, out var worker))
                worker.Dispose();
```

- [ ] **Step 3: Add the AI mode branch in TryStartLightingSession**

At the very top of `TryStartLightingSession`, after the `moviePath` null check (after line 244 approximately), add:

```csharp
            var options = GetOptions();

            // AI lighting mode: skip track discovery, use frame analysis instead
            if (options.AiLightingEnabled)
            {
                await TryStartAiLightingSession(sessionId, moviePath, item.Name ?? "Unknown", options);
                return;
            }
```

Note: remove the duplicate `var options = GetOptions();` that already exists later in the method — it's now hoisted to the top of the branch. Actually, to avoid refactoring the whole method, simply add a check at the top before the existing `options` declaration. Find the line `var options = GetOptions();` (around line 252) and **replace** it with:

```csharp
            var options = GetOptions();

            // AI lighting mode: skip track discovery, use frame analysis instead
            if (options.AiLightingEnabled)
            {
                await TryStartAiLightingSession(sessionId, moviePath, item.Name ?? "Unknown", options);
                return;
            }
```

- [ ] **Step 4: Add TryStartAiLightingSession method**

Add this new private method at the bottom of `ServerEntryPoint.cs`, before the closing `}` of the class:

```csharp
    private async Task TryStartAiLightingSession(
        string sessionId, string moviePath, string itemName, PluginOptions options)
    {
        _logger.Info("[AI] Starting AI lighting session: session={0}, item='{1}'", sessionId, itemName);

        var bulbs = _configService.ParseBulbConfig(options.BulbConfigJson);
        if (bulbs.Count == 0)
        {
            _logger.Warn("[AI] No bulbs configured — skipping AI lighting for '{0}'", itemName);
            _noTrackSessions.TryAdd(sessionId, true);
            return;
        }

        // Build a synthetic LightFXTrack with a single ai-ambient channel
        var syntheticTrack = new Openlightfx.LightFXTrack { Version = 1 };
        syntheticTrack.Metadata = new Openlightfx.TrackMetadata
        {
            Title = $"{itemName} (AI Lighting)",
            DurationMs = 1, // placeholder — V-004 requires > 0; not used for live playback
            Tags = { "ai-generated" }
        };
        syntheticTrack.Channels.Add(new Openlightfx.Channel
        {
            Id = "ai-ambient",
            DisplayName = "AI Ambient",
            SpatialHint = "SPATIAL_AMBIENT",
            Optional = false
        });

        var trackInfo = new Models.TrackInfo
        {
            FilePath = moviePath + ".ailfx",
            FileName = Path.GetFileNameWithoutExtension(moviePath) + ".ailfx",
            Track = syntheticTrack,
            IsValid = true
        };

        // Map all bulbs to the ai-ambient channel
        var aiProfile = new Models.MappingProfile
        {
            Name = "AI Ambient",
            Mappings = new List<Models.ChannelMapping>
            {
                new Models.ChannelMapping
                {
                    ChannelId = "ai-ambient",
                    BulbIds = bulbs.Select(b => b.Id).ToList()
                }
            }
        };

        var session = new Engine.PlaybackSession(trackInfo, bulbs, aiProfile, options, _effectFactory, _logger);

        var worker = new AiLightingWorker(session, moviePath, options, _logger);
        session.OnSeek = worker.NotifySeek;

        if (_sessions.TryAdd(sessionId, session))
        {
            await session.StartAsync();

            // Try to load from cache first; only start analysis loop if cache miss
            if (!worker.TryLoadCache())
                worker.Start();

            _aiWorkers.TryAdd(sessionId, worker);
            _logger.Info("[AI] Session started for '{0}'", itemName);
        }
        else
        {
            worker.Dispose();
        }
    }
```

- [ ] **Step 5: Build to confirm no compile errors**

```bash
dotnet build src/OpenLightFX.Emby -c Release --nologo
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Run all tests to confirm nothing broken**

```bash
dotnet test tests/OpenLightFX.Tests --nologo
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/OpenLightFX.Emby/ServerEntryPoint.cs
git commit -m "feat: wire AI lighting mode in ServerEntryPoint"
```

---

## Task 8: Deploy and verify

- [ ] **Step 1: Build and deploy**

```bash
EMBY_HOST=192.168.1.3 ./scripts/deploy.sh
```

Expected: `Done! Plugin deployed. Emby is restarting.`

- [ ] **Step 2: Enable AI lighting in plugin settings**

In the Emby admin UI → Plugins → OpenLightFX → Settings:
- Set **AI Lighting Mode** = enabled
- Leave all other AI settings at defaults

- [ ] **Step 3: Start playback of any movie**

Watch the Emby server log:

```bash
ssh 192.168.1.3 'sudo journalctl -u emby-server -f | grep OpenLightFX'
```

Expected log lines (in order):
1. `[AI] Starting AI lighting session: session=...`
2. `[AI] Analysis worker started for '...'`
3. `[AI] Analyzing 2000ms–3000ms` (and so on, every 500ms)
4. `[AI] Generated N keyframe(s) from N frame(s)`

- [ ] **Step 4: Verify bulbs respond**

Bulbs should change color within a few seconds of playback start, tracking the background color of the current scene.

- [ ] **Step 5: Stop and restart playback — verify cache hit**

Stop playback. Check that a `.ailfx` file was created next to the video file. Start playback again:

```
[AI] Loaded N keyframes from cache for '...'
```

Bulbs should respond immediately (no analysis delay).

- [ ] **Step 6: Commit any fixes, push branch**

```bash
git push -u origin feature/ai-lighting
```

---

## Self-Review Notes

- **V-004 (duration > 0):** Synthetic track sets `DurationMs = 1` as a placeholder. This is only used for validation during `TrackParser.Parse()`, which is never called for the live synthetic track. The cache file sets `DurationMs` to `lastKeyframe.TimestampMs + 1000`. ✓
- **V-010 (timestamp ≤ duration):** Cache keyframes are sorted before writing; duration is set to last timestamp + 1000. ✓
- **V-012 (transition_ms ≤ timestamp_ms):** `FrameAnalysisPipeline.AnalyzeWindow()` caps `transitionMs = min(transitionMs, frameTimestampMs)`. ✓
- **V-006 (keyframes sorted):** Cache write sorts keyframes by `TimestampMs` before writing. ✓
- **Seek backward:** Worker cursor stays, existing keyframes remain valid. ✓
- **Seek forward past analyzed window:** Worker jumps cursor to `seekPos + MinBufferMs`, bulbs hold last color until catch-up. ✓
- **CPU throttle:** Linux-only via `/proc/stat`; non-Linux falls through safely. ✓
- **ILRepack:** No new managed NuGet dependencies; `deploy.sh` unchanged. ✓
