# AI Track Pre-Processing — Design Spec

**Date:** 2026-03-27
**Status:** Approved
**Repos:** `openlightfx-emby` (engine + API), `openlightfx-marketplace` (library tab UI)

---

## Overview

Real-time AI lighting analysis cannot keep up with playback on modest hardware, particularly for 4K HDR content. This feature replaces real-time analysis with an offline pre-processing model: the user enqueues movies from the marketplace library tab, the plugin analyzes them in the background and writes `.ailfx` sidecar files, and those files are loaded at playback start like any `.lightfx` track — zero runtime cost.

---

## Section 1: Removals (openlightfx-emby)

The following are deleted outright:

**Files deleted:**
- `Engine/Ai/AiLightingWorker.cs` — streaming analysis loop, seek handling, live keyframe injection

**Code removed from existing files:**
- `Engine/PlaybackSession.cs` — `_aiKeyframeQueue` field, `AppendKeyframes()` method, `OnSeek` delegate property
- `ServerEntryPoint.cs` — `_aiWorkers` dictionary, `TryStartAiLightingSession()` method, `AiLightingEnabled` playback branch, worker disposal in `OnPlaybackStopped()` and `Dispose()`
- `Configuration/PluginOptions.cs` — `AiLightingEnabled` and `AiLookaheadMs` properties (no longer meaningful without real-time mode)

**Retained unchanged:**
- `Engine/Ai/FrameAnalysisPipeline.cs`
- `Engine/Ai/FrameColorMath.cs`
- `tests/OpenLightFX.Tests/Engine/Ai/FrameColorMathTests.cs`

Remaining AI config fields (`AiAnalysisRateFps`, `AiCenterExclusionPercent`, `AiCacheEnabled`, `AiSceneCutThreshold`, `AiMaxCpuPercent`) are kept — they govern the pre-processor.

---

## Section 2: `.ailfx` as a First-Class Track (openlightfx-emby)

### Discovery

`TrackDiscoveryService` gains a fourth discovery pass: sidecar `.ailfx` lookup. Given a video at `movie.mkv`, it checks for `movie.ailfx` in the same directory. If found, it is treated as a valid track candidate alongside any `.lightfx` sidecar.

### Parsing

`.ailfx` files are parsed directly with `LightFXTrack.Parser.ParseFrom()` — bypassing `TrackParser` to avoid the V-004 (`duration > 0`) validation check, which AI-generated files cannot satisfy (duration is a placeholder). All other playback infrastructure (keyframe scheduling, interpolation, bulb dispatch) is unchanged.

### Track List Response

`GET /OpenLightFX/Tracks/ByItem` includes an `IsAiGenerated` bool on each track entry. `.ailfx` entries return `IsAiGenerated = true`; `.lightfx` entries return `false`. The marketplace uses this flag to show "Regenerate" instead of "Select" and render an "AI" badge.

---

## Section 3: Pre-Processing Engine (openlightfx-emby)

### New File: `Engine/Ai/AiPreprocessingQueue.cs`

A single class instantiated by `ServerEntryPoint` at startup, disposed on shutdown.

**Queue item model:**

```
QueueItem {
    ItemId: string          // Emby item ID
    VideoPath: string       // Absolute path to video file
    ItemName: string        // Display name for logging/API
    State: Pending | Processing | Done | Failed
    ProgressPercent: int    // 0–100, updated per batch
    StartedAt: DateTime?
    CompletedAt: DateTime?
    Error: string?          // Set on failure
}
```

**Processing loop:**

One `Task` drains the queue sequentially. For each `Pending` item:

1. Set state to `Processing`, record `StartedAt`
2. Probe video duration via a short ffmpeg invocation (`-v quiet -print_format json -show_format`)
3. Divide total duration into 60-second batches
4. For each batch: call `FrameAnalysisPipeline.AnalyzeWindow()`, accumulate keyframes, update `ProgressPercent`
5. Between batches: check CPU throttle (`AiMaxCpuPercent`) via `/proc/stat` sample (falls back to no-throttle on non-Linux)
6. On completion: write `.ailfx` sidecar atomically (`.tmp` + rename), set state to `Done`, record `CompletedAt`
7. On exception: set state to `Failed`, record `Error`

**Batch size rationale:** 60-second batches instead of the 1-second lookahead batches used in real-time mode. Fewer ffmpeg subprocess invocations, each doing more work — meaningfully faster for offline processing.

**Duplicate guard:** `Enqueue()` rejects items already in `Pending` or `Processing` state with a conflict result. Returns success immediately for `Done` items (caller re-enqueues explicitly to regenerate).

**Cache write:** Same `LightFXTrack` protobuf format and metadata tags as the previous `AiLightingWorker.WriteCache()`:
- `"ai-generated"`
- `"source-mtime:{unix-timestamp}"`
- `"center-exclusion:{percent}"`
- `"analysis-rate:{fps}"`

**Server restart:** Queue is in-memory. Pending items are lost on restart. Completed `.ailfx` sidecars on disk are permanent and load normally on next playback.

---

## Section 4: API Endpoints (openlightfx-emby)

Four new endpoints following the existing ServiceStack route/handler pattern in `Api/TrackService.cs` (or a new `Api/AiQueueService.cs`):

| Method | Route | Description |
|---|---|---|
| `POST` | `/OpenLightFX/Ai/Enqueue` | Add item to queue. Body: `{ itemId, videoPath, itemName }`. Returns 409 if already `Pending` or `Processing`. |
| `GET` | `/OpenLightFX/Ai/Queue` | Full queue snapshot — all items with state, progress, error. |
| `GET` | `/OpenLightFX/Ai/Queue/{ItemId}` | Single item status. Used for polling during active processing. |
| `DELETE` | `/OpenLightFX/Ai/Queue/{ItemId}` | Remove a `Pending` item. Returns 409 if item is `Processing` (cannot interrupt in-progress job). |

---

## Section 5: Marketplace Changes (openlightfx-marketplace)

### FastAPI Proxy (backend)

Four new proxy routes in `backend/app/api/routes/` (following the existing `/users/me/emby/*` proxy pattern) forwarding to the plugin's new endpoints with the user's stored Emby token.

### Library Tab UI (frontend)

**Per-movie AI button** — label and behavior driven by item state:

| State | Button label | Action |
|---|---|---|
| No `.ailfx`, not queued | **Generate AI Track** | `POST /Ai/Enqueue` |
| `Pending` | **Queued** (position N) | Disabled |
| `Processing` | **Generating… 47%** | Disabled, polls every 3s |
| `Done` | **Regenerate** | `POST /Ai/Enqueue` (overwrites) |
| `Failed` | **Retry** (error tooltip) | `POST /Ai/Enqueue` |

The existing track list response's `IsAiGenerated` flag drives the "Regenerate" vs "Select" label in the existing track selection UI and shows an "AI" badge on `.ailfx` entries.

**Queue panel** — collapsible panel at the top of the library tab, visible only when the queue is non-empty:
- Populated by `GET /Ai/Queue`, polled every 3 seconds when any item is `Pending` or `Processing`
- Shows each item name, state, and progress bar
- `Pending` items have an **×** button calling `DELETE /Ai/Queue/{itemId}`

---

## Non-Goals (this version)

- Queue persistence across server restarts
- Parallel processing of multiple items
- Automatic enqueue on library scan
- Cancellation of in-progress jobs
- Per-item analysis rate override
