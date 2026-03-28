# AI Pre-Processing Queue — Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove real-time AI lighting and replace it with an offline pre-processing queue that writes `.ailfx` sidecar files, discoverable at playback start like any `.lightfx` track.

**Architecture:** Delete `AiLightingWorker.cs` and all real-time AI plumbing; add `.ailfx` as a first-class track via a new discovery pass; introduce `AiPreprocessingQueue` (sequential background Task, 60s batches, `/proc/stat` CPU throttle); expose four new REST endpoints via `AiQueueService`.

**Tech Stack:** C# / .NET 8.0, ServiceStack (Emby's fork), Google.Protobuf, ffprobe for duration probing.

---

## File Structure

| Action | File | What changes |
|--------|------|-------------|
| Delete | `src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs` | Removed entirely |
| Modify | `src/OpenLightFX.Emby/Engine/PlaybackSession.cs` | Remove `_aiKeyframeQueue`, `AppendKeyframes()`, `OnSeek` |
| Modify | `src/OpenLightFX.Emby/Configuration/PluginOptions.cs` | Remove `AiLightingEnabled`, `AiLookaheadMs` |
| Modify | `src/OpenLightFX.Emby/ServerEntryPoint.cs` | Remove `_aiWorkers` + AI session branch; add `AiPreprocessingQueue` |
| Modify | `src/OpenLightFX.Emby/Models/TrackInfo.cs` | Add `IsAiGenerated` bool |
| Modify | `src/OpenLightFX.Emby/Services/TrackDiscoveryService.cs` | Add Strategy 4: `.ailfx` sidecar |
| Modify | `src/OpenLightFX.Emby/Api/TrackService.cs` | Add `IsAiGenerated` to `TrackSummary`; populate it |
| Create | `src/OpenLightFX.Emby/Engine/Ai/AiPreprocessingQueue.cs` | New background queue class |
| Create | `src/OpenLightFX.Emby/Api/AiQueueService.cs` | 4 new REST endpoints |

---

### Task 1: Remove Real-Time AI Code

**Files:**
- Delete: `src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs`
- Modify: `src/OpenLightFX.Emby/Engine/PlaybackSession.cs`
- Modify: `src/OpenLightFX.Emby/Configuration/PluginOptions.cs`
- Modify: `src/OpenLightFX.Emby/ServerEntryPoint.cs`

- [ ] **Step 1: Delete AiLightingWorker.cs**

```bash
rm src/OpenLightFX.Emby/Engine/Ai/AiLightingWorker.cs
```

- [ ] **Step 2: Remove AI queue infrastructure from PlaybackSession.cs**

In `PlaybackSession.cs`, remove the `_aiKeyframeQueue` field (line 64), the `OnSeek` property (lines 66-70), and the `AppendKeyframes()` method (lines 107-114):

```csharp
// REMOVE these three members:
private readonly ConcurrentQueue<Keyframe> _aiKeyframeQueue = new();

public Action<ulong>? OnSeek { get; set; }

public void AppendKeyframes(IEnumerable<Keyframe> keyframes)
{
    foreach (var kf in keyframes)
        _aiKeyframeQueue.Enqueue(kf);
}
```

Also in `ProcessKeyframes()` (around line 312), remove the AI queue drain block:

```csharp
// REMOVE this block from ProcessKeyframes():
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

Also check and remove `using System.Collections.Concurrent;` only if `ConcurrentDictionary` (still used for `_drivers`) is imported from another global using. Since `ConcurrentDictionary` uses the same namespace, the import likely stays.

Also in `UpdatePosition()`, remove the two `OnSeek?.Invoke(adjusted);` calls (lines ~239 and ~261):
```csharp
// REMOVE both instances of:
OnSeek?.Invoke(adjusted); // notify AI worker
```

- [ ] **Step 3: Remove AiLightingEnabled and AiLookaheadMs from PluginOptions.cs**

Remove these two properties (approximately lines 91-100):
```csharp
// REMOVE:
[DisplayName("AI Lighting Mode")]
[Description("When enabled, AI lighting takes precedence over .lightfx track files. ...")]
public bool AiLightingEnabled { get; set; } = false;

[DisplayName("AI Lookahead (ms)")]
[Description("How far ahead of the current playback position to analyze frames (milliseconds). ...")]
public int AiLookaheadMs { get; set; } = 30000;
```

- [ ] **Step 4: Remove AI worker plumbing from ServerEntryPoint.cs**

Remove the `_aiWorkers` field (lines 47-48):
```csharp
// REMOVE:
// AI lighting workers, keyed by Emby session ID (parallel to _sessions)
private readonly ConcurrentDictionary<string, AiLightingWorker> _aiWorkers = new();
```

In `Dispose()`, remove the AI worker disposal block (lines 119-121):
```csharp
// REMOVE:
foreach (var worker in _aiWorkers.Values)
    worker.Dispose();
_aiWorkers.Clear();
```

In `OnPlaybackStopped()`, remove the AI worker disposal block (lines 166-168):
```csharp
// REMOVE:
// Dispose AI worker if one is active (writes the .ailfx cache on dispose)
if (_aiWorkers.TryRemove(sessionId, out var worker))
    worker.Dispose();
```

In `TryStartLightingSession()`, remove the AI lighting branch (lines 267-272):
```csharp
// REMOVE:
// AI lighting mode: skip track discovery, use frame analysis instead
if (options.AiLightingEnabled)
{
    await TryStartAiLightingSession(sessionId, moviePath, item.Name ?? "Unknown", options);
    return;
}
```

Remove the entire `TryStartAiLightingSession()` method (lines 410-481, the full private async Task method).

Keep `using OpenLightFX.Emby.Engine.Ai;` — it will be needed for `AiPreprocessingQueue` in Task 7.

- [ ] **Step 5: Verify build compiles**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | tail -20
```

Expected: Build succeeded with 0 errors. Fix any remaining references to deleted types.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove real-time AI lighting: delete AiLightingWorker, clean up PlaybackSession/ServerEntryPoint/PluginOptions"
```

---

### Task 2: Add IsAiGenerated to TrackInfo

**Files:**
- Modify: `src/OpenLightFX.Emby/Models/TrackInfo.cs`

- [ ] **Step 1: Add the property**

In `TrackInfo.cs`, add after `IsValid`:
```csharp
public bool IsValid { get; init; }
public bool IsAiGenerated { get; init; }  // ADD THIS LINE
public List<string> ValidationErrors { get; init; } = new();
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error|warning" | head -20
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Models/TrackInfo.cs
git commit -m "Add IsAiGenerated to TrackInfo"
```

---

### Task 3: Add .ailfx Discovery (Strategy 4)

**Files:**
- Modify: `src/OpenLightFX.Emby/Services/TrackDiscoveryService.cs`

- [ ] **Step 1: Add using for Protobuf parsing**

The file already has `using OpenLightFX.Emby.Models;`. It needs `using Openlightfx;` for `LightFXTrack`. Check if it's already present; add it if not.

- [ ] **Step 2: Insert Strategy 4 block after Strategy 1.5**

After the closing `}` of the Strategy 1.5 block (around line 78, after the `catch (DirectoryNotFoundException) { }`) and before the Strategy 2 comment, insert:

```csharp
        // Strategy 4: .ailfx sidecar — AI-generated track alongside the movie.
        // Parsed directly via protobuf (bypasses TrackParser to skip V-004 duration check).
        if (dir != null)
        {
            var aiSidecar = Path.Combine(dir, baseName + ".ailfx");
            _logger.Debug("Discovery: checking AI sidecar '{0}'", aiSidecar);
            if (File.Exists(aiSidecar) && !seenPaths.Contains(aiSidecar))
            {
                seenPaths.Add(aiSidecar);
                filesFound++;
                try
                {
                    var bytes = File.ReadAllBytes(aiSidecar);
                    var track = LightFXTrack.Parser.ParseFrom(bytes);
                    tracks.Add(new TrackInfo
                    {
                        FilePath = aiSidecar,
                        FileName = Path.GetFileName(aiSidecar),
                        Track = track,
                        IsValid = true,
                        IsAiGenerated = true
                    });
                    _logger.Debug("Discovery: AI sidecar loaded with {0} keyframe(s)",
                        track.Keyframes.Count);
                }
                catch (Exception ex)
                {
                    filesRejected++;
                    _logger.Warn("Discovery: failed to parse AI sidecar '{0}': {1}",
                        Path.GetFileName(aiSidecar), ex.Message);
                }
            }
        }
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error" | head -10
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/OpenLightFX.Emby/Services/TrackDiscoveryService.cs
git commit -m "Add .ailfx sidecar as Strategy 4 in TrackDiscoveryService"
```

---

### Task 4: Add IsAiGenerated to TrackSummary API Response

**Files:**
- Modify: `src/OpenLightFX.Emby/Api/TrackService.cs`

- [ ] **Step 1: Add IsAiGenerated field to TrackSummary class**

In `TrackSummary` (around line 167), add after `FormatVersion`:
```csharp
    [JsonPropertyName("formatVersion")]
    public string FormatVersion { get; set; } = "1.0";

    [JsonPropertyName("isAiGenerated")]
    public bool IsAiGenerated { get; set; }
```

- [ ] **Step 2: Populate IsAiGenerated in the Get(GetTracksByItem) handler**

In the `Get(GetTracksByItem)` handler, the `tracks.Select(...)` projection (around line 407):
```csharp
                Tracks = tracks.Select(t => new TrackSummary
                {
                    TrackPath = t.FilePath,
                    Title = t.Title,
                    DurationMs = t.DurationMs,
                    ChannelCount = t.ChannelCount,
                    FormatVersion = t.Track.Metadata?.TrackVersion is { Length: > 0 } tv
                        ? tv
                        : (t.Track.Version > 0 ? t.Track.Version.ToString() : "1.0"),
                    IsAiGenerated = t.IsAiGenerated  // ADD THIS LINE
                }).ToList()
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error" | head -10
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/OpenLightFX.Emby/Api/TrackService.cs
git commit -m "Add IsAiGenerated flag to TrackSummary API response"
```

---

### Task 5: Create AiPreprocessingQueue

**Files:**
- Create: `src/OpenLightFX.Emby/Engine/Ai/AiPreprocessingQueue.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace OpenLightFX.Emby.Engine.Ai;

using Google.Protobuf;
using MediaBrowser.Model.Logging;
using OpenLightFX.Emby.Configuration;
using Openlightfx;
using System.Diagnostics;
using System.Text.Json;

internal enum QueueItemState { Pending, Processing, Done, Failed }
internal enum EnqueueResult { Success, Conflict }

internal class QueueItem
{
    public required string ItemId { get; init; }
    public required string VideoPath { get; init; }
    public required string ItemName { get; init; }
    public QueueItemState State { get; set; } = QueueItemState.Pending;
    public int ProgressPercent { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Sequential background queue that pre-processes video files into .ailfx sidecar tracks.
/// One item processes at a time. Queue is in-memory — lost on server restart.
/// </summary>
internal class AiPreprocessingQueue : IDisposable
{
    private const string AiChannelId = "ai-ambient";
    private const int BatchSec = 60;

    private readonly ILogger _logger;
    private readonly List<QueueItem> _queue = new();
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    public AiPreprocessingQueue(ILogger logger)
    {
        _logger = logger;
        _workerTask = Task.Run(RunLoop);
    }

    /// <summary>
    /// Add item to queue. Returns Conflict if already Pending or Processing.
    /// For Done/Failed items, removes old entry and adds fresh Pending.
    /// </summary>
    public EnqueueResult Enqueue(string itemId, string videoPath, string itemName)
    {
        lock (_lock)
        {
            var existing = _queue.FirstOrDefault(i => i.ItemId == itemId);
            if (existing != null &&
                (existing.State == QueueItemState.Pending || existing.State == QueueItemState.Processing))
                return EnqueueResult.Conflict;

            _queue.RemoveAll(i => i.ItemId == itemId);
            _queue.Add(new QueueItem
            {
                ItemId = itemId,
                VideoPath = videoPath,
                ItemName = itemName
            });
        }
        _workAvailable.Set();
        return EnqueueResult.Success;
    }

    /// <summary>Snapshot of the full queue (thread-safe copy).</summary>
    public List<QueueItem> GetQueue()
    {
        lock (_lock)
            return _queue.ToList();
    }

    /// <summary>Single item by ID, or null if not in queue.</summary>
    public QueueItem? GetItem(string itemId)
    {
        lock (_lock)
            return _queue.FirstOrDefault(i => i.ItemId == itemId);
    }

    /// <summary>
    /// Remove a Pending item. Returns false if item is Processing (cannot interrupt)
    /// or if item is not found.
    /// </summary>
    public bool RemovePending(string itemId)
    {
        lock (_lock)
        {
            var item = _queue.FirstOrDefault(i => i.ItemId == itemId);
            if (item == null || item.State == QueueItemState.Processing) return false;
            return _queue.Remove(item);
        }
    }

    // ── Worker loop ──────────────────────────────────────────────────

    private async Task RunLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _workAvailable.Wait(token);
                _workAvailable.Reset();

                while (true)
                {
                    QueueItem? item;
                    lock (_lock)
                        item = _queue.FirstOrDefault(i => i.State == QueueItemState.Pending);

                    if (item == null) break;
                    await ProcessItem(item, token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.ErrorException("[AI] Unexpected error in queue loop", ex);
            }
        }
    }

    private async Task ProcessItem(QueueItem item, CancellationToken token)
    {
        lock (_lock)
        {
            item.State = QueueItemState.Processing;
            item.StartedAt = DateTime.UtcNow;
        }
        _logger.Info("[AI] Starting pre-processing for '{0}'", item.ItemName);

        try
        {
            var durationMs = await ProbeDurationMs(item.VideoPath, token);
            if (durationMs == 0)
                throw new InvalidOperationException("Could not determine video duration via ffprobe");

            var options = Plugin.Instance?.GetPluginOptions() ?? new PluginOptions();
            var pipeline = new FrameAnalysisPipeline(options, _logger);

            var allKeyframes = new List<Keyframe>();
            var totalBatches = (int)Math.Ceiling(durationMs / (BatchSec * 1000.0));
            var batchIndex = 0;

            for (ulong batchStart = 0; batchStart < durationMs; batchStart += (ulong)(BatchSec * 1000))
            {
                token.ThrowIfCancellationRequested();

                var batchEnd = Math.Min(batchStart + (ulong)(BatchSec * 1000), durationMs);
                _logger.Debug("[AI] Processing batch {0}/{1} ({2}ms–{3}ms) for '{4}'",
                    batchIndex + 1, totalBatches, batchStart, batchEnd, item.ItemName);

                var results = pipeline.AnalyzeWindow(item.VideoPath, batchStart, batchEnd);
                foreach (var result in results)
                    allKeyframes.Add(ToKeyframe(result));

                batchIndex++;
                lock (_lock)
                    item.ProgressPercent = (int)(100.0 * batchIndex / totalBatches);

                // CPU throttle between batches (not after the last one)
                if (batchIndex < totalBatches)
                    await ThrottleIfNeeded(options, token);
            }

            WriteSidecar(item.VideoPath, item.ItemName, allKeyframes, options);

            lock (_lock)
            {
                item.State = QueueItemState.Done;
                item.ProgressPercent = 100;
                item.CompletedAt = DateTime.UtcNow;
            }
            _logger.Info("[AI] Pre-processing complete for '{0}': {1} keyframe(s)",
                item.ItemName, allKeyframes.Count);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { item.State = QueueItemState.Failed; item.Error = "Cancelled"; }
        }
        catch (Exception ex)
        {
            lock (_lock) { item.State = QueueItemState.Failed; item.Error = ex.Message; }
            _logger.Warn("[AI] Pre-processing failed for '{0}': {1}", item.ItemName, ex.Message);
        }
    }

    // ── Duration probe ───────────────────────────────────────────────

    private async Task<ulong> ProbeDurationMs(string videoPath, CancellationToken token)
    {
        var ffprobePath = FindFfprobe();
        var args = $"-v quiet -print_format json -show_format \"{videoPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };

        try { process.Start(); }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to start ffprobe at '{0}': {1}", ffprobePath, ex.Message);
            return 0;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(15));
        if (!exited) try { process.Kill(); } catch { }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var durationStr = doc.RootElement
                .GetProperty("format")
                .GetProperty("duration")
                .GetString();
            if (double.TryParse(durationStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var sec))
                return (ulong)(sec * 1000);
        }
        catch (Exception ex)
        {
            _logger.Warn("[AI] Failed to parse ffprobe output: {0}", ex.Message);
        }

        return 0;
    }

    // ── CPU throttle ─────────────────────────────────────────────────

    private static async Task ThrottleIfNeeded(PluginOptions options, CancellationToken token)
    {
        try
        {
            var lines1 = File.ReadAllLines("/proc/stat");
            var cpu1 = ParseCpuLine(lines1[0]);
            await Task.Delay(100, token);
            var lines2 = File.ReadAllLines("/proc/stat");
            var cpu2 = ParseCpuLine(lines2[0]);

            long idle = cpu2.idle - cpu1.idle;
            long total = cpu2.total - cpu1.total;
            if (total <= 0) return;

            double usagePercent = 100.0 * (1.0 - (double)idle / total);
            if (usagePercent > options.AiMaxCpuPercent)
            {
                await Task.Delay(1000, token); // back off 1s if CPU is hot
            }
        }
        catch { /* non-Linux or /proc unavailable — skip throttle */ }
    }

    private static (long idle, long total) ParseCpuLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Format: "cpu user nice system idle iowait irq softirq ..."
        long idle = long.Parse(parts[4]);
        long total = parts.Skip(1).Sum(p => long.TryParse(p, out var v) ? v : 0L);
        return (idle, total);
    }

    // ── Keyframe conversion ──────────────────────────────────────────

    private static Keyframe ToKeyframe(AnalysisResult result)
    {
        float lum = 0.299f * result.R + 0.587f * result.G + 0.114f * result.B;
        bool powerOn = lum >= 8f;
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
            PowerOn = powerOn
        };
    }

    // ── Sidecar write ────────────────────────────────────────────────

    private void WriteSidecar(
        string videoPath, string itemName,
        List<Keyframe> keyframes, PluginOptions options)
    {
        if (keyframes.Count == 0)
        {
            _logger.Warn("[AI] No keyframes generated for '{0}' — skipping sidecar write", itemName);
            return;
        }

        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(videoPath)).ToUnixTimeSeconds();
        var track = new LightFXTrack { Version = 1 };
        track.Metadata = new TrackMetadata
        {
            Title = Path.GetFileNameWithoutExtension(videoPath) + " (AI Generated)",
            DurationMs = keyframes[^1].TimestampMs + 1000,
            Tags =
            {
                "ai-generated",
                $"source-mtime:{mtime}",
                $"center-exclusion:{options.AiCenterExclusionPercent}",
                $"analysis-rate:{options.AiAnalysisRateFps:F1}"
            }
        };
        track.Channels.Add(new Channel
        {
            Id = AiChannelId,
            DisplayName = "AI Ambient",
            SpatialHint = "SPATIAL_AMBIENT",
            Optional = false
        });
        track.Keyframes.AddRange(keyframes.OrderBy(k => k.TimestampMs));

        var finalPath = Path.ChangeExtension(videoPath, ".ailfx");
        var tmpPath = finalPath + ".tmp";
        File.WriteAllBytes(tmpPath, track.ToByteArray());
        File.Move(tmpPath, finalPath, overwrite: true);

        _logger.Info("[AI] Wrote {0} keyframe(s) to '{1}'",
            keyframes.Count, Path.GetFileName(finalPath));
    }

    // ── ffprobe discovery ────────────────────────────────────────────

    private static string FindFfprobe()
    {
        // Same strategy as FrameAnalysisPipeline.FindFfmpeg():
        // look relative to the running Emby server executable.
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (exeDir != null)
            {
                string ext = OperatingSystem.IsWindows() ? ".exe" : "";
                foreach (var rel in new[] { $"ffprobe{ext}", Path.Combine("bin", $"ffprobe{ext}") })
                {
                    var candidate = Path.Combine(exeDir, rel);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }

        return OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
    }

    // ── IDisposable ──────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        try { _workerTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
        _workAvailable.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error" | head -20
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Engine/Ai/AiPreprocessingQueue.cs
git commit -m "Add AiPreprocessingQueue: offline 60s-batch pre-processor with CPU throttle"
```

---

### Task 6: Create AiQueueService (4 REST Endpoints)

**Files:**
- Create: `src/OpenLightFX.Emby/Api/AiQueueService.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace OpenLightFX.Emby.Api;

using System.Net;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Engine.Ai;

// ─── Request DTOs ──────────────────────────────────────────────────────

[Route("/OpenLightFX/Ai/Enqueue", "POST",
    Description = "Enqueue a movie for AI pre-processing. Returns 409 if already pending/processing.")]
public class EnqueueAiItem : IReturn<AiQueueItemResponse>
{
    public string ItemId { get; set; } = string.Empty;
    public string VideoPath { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
}

[Route("/OpenLightFX/Ai/Queue", "GET",
    Description = "Get full AI pre-processing queue snapshot.")]
public class GetAiQueue : IReturn<AiQueueResponse> { }

[Route("/OpenLightFX/Ai/Queue/{ItemId}", "GET",
    Description = "Get status of a single AI queue item by Emby item ID.")]
public class GetAiQueueItem : IReturn<AiQueueItemResponse>
{
    public string ItemId { get; set; } = string.Empty;
}

[Route("/OpenLightFX/Ai/Queue/{ItemId}", "DELETE",
    Description = "Remove a Pending queue item. Returns 409 if item is currently Processing.")]
public class DeleteAiQueueItem : IReturn<object>
{
    public string ItemId { get; set; } = string.Empty;
}

// ─── Response DTOs ─────────────────────────────────────────────────────

public class AiQueueResponse
{
    [JsonPropertyName("items")]
    public List<AiQueueItemResponse> Items { get; set; } = new();
}

public class AiQueueItemResponse
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("itemName")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("progressPercent")]
    public int ProgressPercent { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// ─── Service ───────────────────────────────────────────────────────────

public class AiQueueService : IService
{
    private readonly ILogger _logger;

    public AiQueueService(ILogManager logManager)
    {
        _logger = logManager.GetLogger("OpenLightFX.AiQueue");
    }

    // ── POST /OpenLightFX/Ai/Enqueue ──────────────────────────────────

    public object Post(EnqueueAiItem request)
    {
        if (string.IsNullOrEmpty(request.ItemId))
            throw new ArgumentException("itemId is required");
        if (string.IsNullOrEmpty(request.VideoPath))
            throw new ArgumentException("videoPath is required");

        var queue = ServerEntryPoint.Instance?.AiPreprocessingQueue;
        if (queue == null)
            throw new InvalidOperationException("AI pre-processing queue is not available");

        var result = queue.Enqueue(request.ItemId, request.VideoPath, request.ItemName);
        if (result == EnqueueResult.Conflict)
        {
            // Note: returning a distinguishable error for the 409 case.
            // If the Emby ServiceStack fork supports HttpError, use:
            //   throw new HttpError(HttpStatusCode.Conflict, "Item is already pending or processing");
            // Otherwise, throw ArgumentException (maps to 400) and handle on the proxy side.
            throw new InvalidOperationException("CONFLICT: Item is already Pending or Processing");
        }

        _logger.Info("[AI] Enqueued item '{0}' ({1})", request.ItemName, request.ItemId);
        return ToResponse(queue.GetItem(request.ItemId)!);
    }

    // ── GET /OpenLightFX/Ai/Queue ──────────────────────────────────────

    public object Get(GetAiQueue request)
    {
        var queue = ServerEntryPoint.Instance?.AiPreprocessingQueue;
        var items = queue?.GetQueue() ?? new List<QueueItem>();
        return new AiQueueResponse { Items = items.Select(ToResponse).ToList() };
    }

    // ── GET /OpenLightFX/Ai/Queue/{ItemId} ────────────────────────────

    public object Get(GetAiQueueItem request)
    {
        var queue = ServerEntryPoint.Instance?.AiPreprocessingQueue;
        var item = queue?.GetItem(request.ItemId);
        if (item == null)
            throw new FileNotFoundException($"Queue item '{request.ItemId}' not found");
        return ToResponse(item);
    }

    // ── DELETE /OpenLightFX/Ai/Queue/{ItemId} ─────────────────────────

    public object Delete(DeleteAiQueueItem request)
    {
        var queue = ServerEntryPoint.Instance?.AiPreprocessingQueue;
        if (queue == null)
            throw new InvalidOperationException("AI pre-processing queue is not available");

        var item = queue.GetItem(request.ItemId);
        if (item == null)
            throw new FileNotFoundException($"Queue item '{request.ItemId}' not found");

        if (item.State == QueueItemState.Processing)
            throw new InvalidOperationException("CONFLICT: Cannot remove an item that is currently Processing");

        queue.RemovePending(request.ItemId);
        return new object();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static AiQueueItemResponse ToResponse(QueueItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        State = item.State.ToString(),
        ProgressPercent = item.ProgressPercent,
        StartedAt = item.StartedAt,
        CompletedAt = item.CompletedAt,
        Error = item.Error
    };
}
```

**Note on HTTP 409:** The Emby ServiceStack fork may not expose `HttpError` directly. After deploying, test the conflict path and verify the HTTP status code returned. If it returns 500 instead of 409, update the proxy in `openlightfx-marketplace` to check for the `"CONFLICT:"` prefix in the error message, or investigate using `MediaBrowser.Model.Services.HttpError` from the SDK.

- [ ] **Step 2: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error" | head -20
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/OpenLightFX.Emby/Api/AiQueueService.cs
git commit -m "Add AiQueueService: 4 REST endpoints for AI pre-processing queue"
```

---

### Task 7: Wire AiPreprocessingQueue into ServerEntryPoint

**Files:**
- Modify: `src/OpenLightFX.Emby/ServerEntryPoint.cs`

- [ ] **Step 1: Add field and public property**

After the `_discoveryCoordinator` and `_identifyService` fields (around line 42), add:
```csharp
    // AI pre-processing queue (singleton, alive for the plugin lifetime)
    private AiPreprocessingQueue? _aiPreprocessingQueue;
```

After the existing public API properties at the bottom of the class (around line 395), add:
```csharp
    public AiPreprocessingQueue? AiPreprocessingQueue => _aiPreprocessingQueue;
```

- [ ] **Step 2: Instantiate in Run()**

In `Run()`, after `_pollTimer.Start();` add:
```csharp
        _aiPreprocessingQueue = new AiPreprocessingQueue(_logger);
        _logger.Info("OpenLightFX AI pre-processing queue started");
```

- [ ] **Step 3: Dispose in Dispose()**

In `Dispose()`, after `_pollTimer?.Dispose();` and before the sessions loop, add:
```csharp
        _aiPreprocessingQueue?.Dispose();
        _aiPreprocessingQueue = null;
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | grep -E "error" | head -20
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/OpenLightFX.Emby/ServerEntryPoint.cs
git commit -m "Wire AiPreprocessingQueue into ServerEntryPoint lifecycle"
```

---

### Task 8: Build, Deploy, and Verify

**Files:** None new — build + deploy script.

- [ ] **Step 1: Full release build**

```bash
dotnet build src/OpenLightFX.Emby -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 2: Deploy to Emby server**

```bash
EMBY_HOST=192.168.1.3 ./scripts/deploy.sh
```

Expected: ILRepack completes, SCP succeeds, `emby-server` restarts.

- [ ] **Step 3: Verify queue endpoint is alive**

```bash
curl -s -X GET "http://192.168.1.3:8096/OpenLightFX/Ai/Queue" \
  -H "X-Emby-Token: <your-api-key>" | python3 -m json.tool
```

Expected: `{"items": []}`

- [ ] **Step 4: Enqueue a movie and poll it**

First get an item ID from Emby:
```bash
curl -s "http://192.168.1.3:8096/Items?IncludeItemTypes=Movie&Recursive=true&Limit=1&Fields=Path" \
  -H "X-Emby-Token: <your-api-key>" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['Items'][0])"
```

Then enqueue (replace `ITEM_ID`, `VIDEO_PATH`, `ITEM_NAME`):
```bash
curl -s -X POST "http://192.168.1.3:8096/OpenLightFX/Ai/Enqueue" \
  -H "X-Emby-Token: <your-api-key>" \
  -H "Content-Type: application/json" \
  -d '{"itemId":"ITEM_ID","videoPath":"VIDEO_PATH","itemName":"ITEM_NAME"}' \
  | python3 -m json.tool
```

Expected: `{"itemId": "...", "state": "Pending", "progressPercent": 0, ...}`

- [ ] **Step 5: Poll until Done**

```bash
watch -n3 'curl -s "http://192.168.1.3:8096/OpenLightFX/Ai/Queue" \
  -H "X-Emby-Token: <your-api-key>" | python3 -m json.tool'
```

Expected: Item transitions `Pending → Processing (progress ticks up) → Done`.

- [ ] **Step 6: Verify .ailfx file was written and discovered**

After Done state, verify:
```bash
# Check .ailfx exists alongside the video
ls -la "$(dirname VIDEO_PATH)"/*.ailfx

# Verify it appears in track discovery
curl -s "http://192.168.1.3:8096/OpenLightFX/Tracks/ByItem?itemId=ITEM_ID" \
  -H "X-Emby-Token: <your-api-key>" | python3 -m json.tool
```

Expected: Track list includes an entry with `"isAiGenerated": true`.

- [ ] **Step 7: Commit final state tag**

```bash
git tag v1.2.0-ai-preprocessing
git log --oneline -8
```
