# AI-Driven Automated Lighting — Design Spec

**Date:** 2026-03-26
**Status:** Approved

---

## Overview

OpenLightFX currently requires a hand-authored `.lightfx` track file to drive lighting during playback. This feature adds an AI lighting mode that analyzes the video file itself in real time, extracts dominant background colors per scene, and generates lighting commands automatically — no track file required.

When enabled, AI lighting takes global precedence over any `.lightfx` track.

---

## Goals

- Fully local: no external API calls, no cloud services
- Real-time streaming lookahead: analyze frames ahead of playback, inject keyframes into the live session
- Background-color-keyed: ignore the center of each frame (foreground subjects); key lighting on peripheral background regions
- Cache results as a sidecar `.ailfx` protobuf file for instant subsequent playbacks
- `.ailfx` files are valid `LightFXTrack` protobufs (reuses `lightfx.proto`), importable into openlightfx-studio for manual editing

---

## Non-Goals (v1)

- Spatial channel mapping (left, right, surround) — single unified ambient channel only
- ML/AI model for scene classification or mood detection — color histogram approach only
- Per-device AI lighting overrides
- Cloud or API-based analysis

---

## Architecture

### New Components

**`Engine/Ai/AiLightingWorker.cs`**
Background worker instantiated by `ServerEntryPoint` when a session starts with AI mode enabled. Owns the analysis loop, seeks FFmpeg ahead of the current playback position, and pushes generated keyframes into the live `PlaybackSession` via `AppendKeyframes()`. Also writes the `.ailfx` cache file as it goes.

**`Engine/Ai/FrameAnalysisPipeline.cs`**
Stateless. Given a video file path and a target timestamp, extracts a frame via in-process FFmpeg, applies preprocessing (downscale, letterbox crop, center exclusion mask), runs k-means color clustering, and returns a `(timestampMs, R, G, B, transitionMs)` result.

### Modified Components

**`Engine/PlaybackSession.cs`**
Adds a `ConcurrentQueue<Keyframe> _aiKeyframeQueue` and a public `AppendKeyframes(IEnumerable<Keyframe>)` method. The existing `ProcessKeyframes()` loop drains this queue at the top of each tick, merging entries into `_sortedKeyframes`. All other `PlaybackSession` logic is unchanged.

**`ServerEntryPoint.cs`**
When `AiLightingEnabled` is true, skips `.lightfx` track discovery entirely. Creates `PlaybackSession` with a single synthetic `ai-ambient` channel and no initial keyframes, then starts `AiLightingWorker`. Wires `OnSeek` notification to the worker.

**`Configuration/PluginOptions.cs`**
Adds AI lighting configuration fields (see Configuration section).

**`Engine/ChannelManager.cs`**
When AI mode is active, automatically maps all configured bulbs to the `ai-ambient` channel. User can override with a named mapping profile to exclude specific bulbs.

### Synthetic Channel

A single channel `"ai-ambient"` is synthesized at session start. It has no spatial hint. All configured bulbs are mapped to it. Existing settings — `GlobalBrightnessCap`, `GlobalTimeOffsetMs`, `PhotosensitivityMode` — apply automatically through the normal dispatch path.

---

## Frame Analysis Pipeline

### Frame Extraction

Uses `Sdcb.FFmpeg` (MIT licensed) as an in-process library. No subprocess spawning. Native FFmpeg seek API targets the video file directly at the requested timestamp. Frame extraction time: ~10–30ms on x86, ~20–50ms on ARM.

Frames are immediately downscaled to **64×36 pixels** before any further processing. This is sufficient to capture color distribution and keeps all subsequent operations trivially fast.

### Preprocessing

1. **Letterbox/pillarbox removal:** Detect uniform dark border rows and columns (luminance threshold) and crop them before analysis.
2. **Center exclusion mask:** Zero out pixels within a configurable ellipse centered on the frame. Default: 50% of frame diameter. This discards foreground subjects (faces, focal objects) and retains background/peripheral regions — the correct signal for ambient room lighting.

### Scene Boundary Detection

Compute a histogram difference between the current frame and the previous extracted frame. If the difference exceeds `AiSceneCutThreshold` (default 0.35), a scene cut is flagged. Scene cuts produce a keyframe with a fast transition (`~200ms`). Gradual color shifts between cuts produce keyframes with slow transitions scaled to the color delta magnitude (`1000–3000ms`).

If consecutive frames produce a color shift below 20% of `AiSceneCutThreshold`, no new keyframe is emitted — the existing LINEAR interpolation in `PlaybackSession` handles the transition.

### Dominant Color Extraction

k-means clustering with k=3 on the preprocessed pixel set. The winning cluster is the one with the highest combined pixel weight that is not near-black (luminance < 10%). This handles dark scenes correctly — it selects the most visually prominent color rather than the most statistically common one.

No ML model is required. A saliency model hook is reserved for a future version if weighted pixel sampling proves valuable.

---

## Streaming Lookahead Worker

### Analysis Loop

```
loop:
  currentPos = session.CurrentPositionMs
  targetWindowStart = currentPos + 2000            (fixed minimum buffer, same as existing LookaheadBufferMs floor)
  targetWindowEnd   = currentPos + AiLookaheadMs   (configurable, default 30000ms)

  if nextUnanalyzedMs < targetWindowEnd:
    // batchMs = one second of video time worth of frames (1000 / AiAnalysisRateFps frames)
    analyze frames from nextUnanalyzedMs to min(nextUnanalyzedMs + 1000ms, targetWindowEnd)
    push keyframes → session.AppendKeyframes()
    write keyframes → .ailfx cache file (accumulated in memory, written on session end)
    advance nextUnanalyzedMs by 1000ms

  sleep PollIntervalMs (reuses existing setting, default 500ms)
```

### Seek Handling

`PlaybackSession` already detects seeks internally (forward/backward jumps >5000ms). A new `Action<ulong> OnSeek` delegate property is added to `PlaybackSession`; `ServerEntryPoint` wires it to `AiLightingWorker.NotifySeek(positionMs)` at session construction. The worker:
1. Resets `nextUnanalyzedMs` to `seekPosition + AiMinBufferMs`
2. Flushes any queued keyframes in `_aiKeyframeQueue` that are now behind the seek point
3. Immediately runs one catch-up analysis pass before returning to the normal loop

Latency on seek: one worker poll interval (≤500ms) before new keyframes arrive.

### CPU Throttle

Before each analysis batch, the worker checks system CPU usage. If usage exceeds `AiMaxCpuPercent` (default 50%), the worker skips the batch and sleeps for one additional poll interval. This prevents AI lighting from interfering with the Emby transcoder or other server workloads.

### Graceful Degradation

If the worker falls behind the lookahead window, the last dispatched color holds on all bulbs — no flash, no dark, no error. The worker catches up silently. If FFmpeg fails to extract a frame, that timestamp is skipped and the next batch is attempted normally.

---

## Cache (.ailfx Files)

### Format

A valid `LightFXTrack` protobuf serialized using the existing `lightfx.proto` schema. File extension: `.ailfx`. Location: same directory as the video file (sidecar).

AI-specific metadata is stored in `metadata.tags`:
- `"ai-generated"` — marks the file as machine-authored
- `"source-mtime:{unix-timestamp}"` — last-modified time of the video file at analysis time (cache invalidation key)
- `"center-exclusion:{percent}"` — exclusion zone parameter used
- `"analysis-rate:{fps}"` — frames-per-second setting used
- `"ffmpeg-version:{version}"` — FFmpeg version used

### Cache Hit Path

On session start, before starting the analysis loop, `AiLightingWorker` checks for a sidecar `.ailfx` file. If found:
1. Parse with `TrackParser.Parse()` (existing code, zero changes)
2. Verify `source-mtime` tag matches the current video file mtime
3. On match: hand the full keyframe set to `PlaybackSession.AppendKeyframes()` at once, then exit — no analysis loop runs
4. On mismatch or parse failure: delete the stale cache file, proceed with normal analysis

### Cache Write Path

As the worker generates keyframes during live analysis, it accumulates them in memory. On session end (playback stopped normally, not on crash or seek-restart), it serializes the complete `LightFXTrack` and writes the `.ailfx` file atomically (write to `.ailfx.tmp`, then rename).

### Studio Import

Because `.ailfx` is a valid `LightFXTrack` protobuf, it can be opened directly in openlightfx-studio. A user can take the AI-generated result and refine it manually — adding effect keyframes, adjusting specific scenes, correcting colors. The refined file can be saved as `.lightfx` and will be loaded normally on next playback (AI mode would need to be disabled or the file renamed to take precedence).

---

## Configuration

New fields added to `PluginOptions`:

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `AiLightingEnabled` | bool | false | Master switch; when true, overrides `.lightfx` track discovery |
| `AiLookaheadMs` | int | 30000 | How far ahead of current position to analyze (ms) |
| `AiAnalysisRateFps` | float | 2.0 | Frames extracted per second of video time |
| `AiCenterExclusionPercent` | int | 50 | Diameter of center exclusion ellipse as % of frame |
| `AiCacheEnabled` | bool | true | Whether to write and read `.ailfx` sidecar cache files |
| `AiSceneCutThreshold` | float | 0.35 | Histogram difference threshold for scene boundary detection |
| `AiMaxCpuPercent` | int | 50 | Worker pauses analysis if system CPU usage exceeds this |

---

## Dependency

**`Sdcb.FFmpeg`** (MIT license) added to `OpenLightFX.Emby.csproj`. Provides in-process FFmpeg bindings for frame extraction. Must be ILRepacked into the single plugin DLL by `deploy.sh`, consistent with how `Google.Protobuf.dll` is currently handled.

---

## Future Considerations

- **Spatial channels (v2):** Divide the frame into left/center/right/top/bottom regions, each feeding a separate channel. Maps to existing spatial hint infrastructure in `lightfx.proto`.
- **ML saliency model (v2):** Plug a small ONNX model into `FrameAnalysisPipeline` to weight pixels by visual importance before clustering. `Microsoft.ML.OnnxRuntime` (MIT) is the target runtime.
- **Pre-analysis mode (v2):** Full offline analysis pass triggered by a library scan event, producing `.ailfx` files before first playback.
- **Per-item AI overrides:** Allow disabling AI lighting for specific media items where it produces poor results.
