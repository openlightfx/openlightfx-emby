---
name: analyze-playback
description: >
  Analyze OpenLightFX playback timing by comparing .lightfx track keyframes
  against Emby server dispatch logs. Use this skill when investigating timing
  issues, verifying playback accuracy, or debugging light command dispatch.
---

# Analyze OpenLightFX Playback Timing

This skill describes how to investigate playback timing issues in the OpenLightFX Emby plugin by comparing expected keyframe times from a `.lightfx` track against actual dispatch timestamps from the Emby server log.

## Prerequisites

- `protoc` compiler (`sudo apt install protobuf-compiler`)
- Python 3 with the `protobuf` package (`python3-protobuf` on Debian/Ubuntu)
- Access to the Emby server log (file or URL)
- The `.lightfx` track file that was played

## Step 1: Obtain the Emby Server Log

Fetch the log from the Emby server API:

```bash
# URL format (replace host and api_key)
curl -o /tmp/embyserver.txt \
  'https://{EMBY_HOST}/emby/System/Logs/embyserver.txt?Sanitize=true&api_key={API_KEY}'
```

The default Emby server in this project is at `192.168.1.3`. The log file can also be found directly on the server at `/var/log/emby-server/embyserver.txt`.

## Step 2: Decode the Track

```bash
scripts/analyze-playback.py decode /path/to/Movie.lightfx
```

This prints all track metadata, channels, keyframes (with timestamps in mm:ss.SSS and raw ms), effect keyframes, and safety info.

**Key things to look for:**
- Keyframe timestamps — these are when lighting changes SHOULD happen
- Duplicate keyframes at the same timestamp (e.g., one setting color, another setting brightness)
- Effect keyframes — type, duration, and intensity
- Whether all keyframes use STEP or LINEAR interpolation

## Step 3: Parse the Log

```bash
scripts/analyze-playback.py log /tmp/embyserver.txt
# Or directly from URL:
scripts/analyze-playback.py log 'https://host/emby/System/Logs/embyserver.txt?api_key=KEY'
```

This extracts all OpenLightFX events including dispatches, seeks, session boundaries, and detects duplicate consecutive commands.

**Key things to look for:**
- Session start time and how long until first real dispatch
- Seek events — these reset the playback clock
- Dispatch skip messages — these mean all color values are zero (no keyframe reached yet)
- Duplicate identical dispatches (redundant network traffic)

## Step 4: Compare Track vs Log

```bash
scripts/analyze-playback.py compare \
  --track /path/to/Movie.lightfx \
  --log /tmp/embyserver.txt
```

This automatically:
1. Identifies the last seek event as the sync reference point
2. Computes movie t=0 in wall-clock time
3. Matches each dispatch to the nearest keyframe by color
4. Reports deltas (expected vs actual timing)
5. Flags issues: late dispatches (>2s), rapid oscillation (<1.5s color changes), redundant sends

## Interpreting Results

### Delta values
- **+0.0s to +0.5s**: Normal — within polling interval granularity (default 500ms)
- **+0.5s to +1.0s**: Acceptable — may include network/processing latency
- **+1.0s to +2.0s**: Slightly late — could indicate clock sync issues
- **>+2.0s**: Problem — likely a bug in clock management or effect lifetime
- **Negative**: Dispatch arrived before keyframe time — indicates clock running ahead

### Common issues and their code paths

| Symptom | Likely Cause | Code Location |
|---------|-------------|---------------|
| Consistent ~1s lag for all dispatches | Clock not started until first Emby event; Emby progress polling delay | `PlaybackSession.UpdatePosition()` — clock sync logic |
| Oscillating between two colors | Stale backward Emby progress events resetting clock | `PlaybackSession.UpdatePosition()` — backward sync rejection |
| Effect color persists 5+ seconds past duration | Effect commands not cleaned up at effect end time | `PlaybackSession.ProcessEffects()` — `EffectWindow.EffectEndMs` check |
| Dispatch skips for several seconds at start | Clock starts before Emby begins playback; first keyframes are RGB(0,0,0) | `PlaybackSession.StartAsync()` / `_clockStarted` flag |
| Identical commands sent repeatedly | No dedup on dispatch (should compare with `_lastSentCommands`) | `PlaybackSession.DispatchCurrentState()` |
| False seek at session start | Clock races ahead during Emby buffering, then Emby reports position 0 | `PlaybackSession._clockStarted` deferred start |

### Dispatch pipeline data flow

```
Tick() called by poll timer (default 500ms)
  → Extrapolate position: _syncPositionMs + (Stopwatch.now - _syncTimestampTicks)
  → ProcessKeyframes: binary search → InterpolationEngine.Interpolate → ChannelManager.SetChannelState
  → ProcessEffects: active EffectWindows override channel state (only within duration)
  → DispatchCurrentState: for each mapped bulb, get command from ChannelManager
    → Skip if all zeros (no data yet)
    → Skip if identical to _lastSentCommands[bulbId]
    → DispatchToBulb: driver.SetState / SetColorTemperature / SetBrightness
```

## Manually Decoding a .lightfx File

If the analyzer script is not available, you can decode manually:

```bash
# Compile proto for Python
protoc --proto_path=src/OpenLightFX.Emby/Proto --python_out=/tmp lightfx.proto

# Decode in Python
python3 -c "
import sys; sys.path.insert(0, '/tmp')
import lightfx_pb2
track = lightfx_pb2.LightFXTrack()
track.ParseFromString(open('FILE.lightfx', 'rb').read())
for kf in sorted(track.keyframes, key=lambda k: k.timestamp_ms):
    print(f'{kf.timestamp_ms:>8}ms  ch={kf.channel_id[:12]}  RGB({kf.color.r},{kf.color.g},{kf.color.b}) bright={kf.brightness}')
"
```

## Manually Extracting Log Dispatches

```bash
grep 'OpenLightFX:.*→ Bulb' /tmp/embyserver.txt | grep -v 'skipping'
```

Key log patterns:
- `→ Bulb '{id}': RGB(r,g,b) bright=N trans=Nms` — color dispatch
- `→ Bulb '{id}': CT=NK bright=N trans=Nms` — color temperature dispatch
- `→ Bulb '{id}': skipping dispatch (no color data yet)` — all-zero state
- `Seek detected: Nms → Nms` — clock reset from position jump
- `PlaybackSession started with N drivers` — session initialized

## Reference: Proto Field Numbers

| Field | Tag | Wire Type | Description |
|-------|-----|-----------|-------------|
| LightFXTrack.version | 1 | varint | Schema version (1) |
| LightFXTrack.metadata | 2 | message | Track metadata |
| LightFXTrack.channels | 3 | repeated message | Channel definitions |
| LightFXTrack.keyframes | 5 | repeated message | Regular keyframes |
| LightFXTrack.effect_keyframes | 6 | repeated message | Effect keyframes |
| LightFXTrack.safety_info | 8 | message | Safety metadata |
| Keyframe.timestamp_ms | 3 | varint (uint64) | Time from movie start |
| Keyframe.color | 5 | message | RGBColor (r=1, g=2, b=3) |
| Keyframe.brightness | 7 | varint | 0-100 |
| Keyframe.transition_ms | 8 | varint | Transition duration |
| Keyframe.interpolation | 9 | varint | 1=STEP, 2=LINEAR |
| EffectKeyframe.timestamp_ms | 3 | varint (uint64) | Effect start time |
| EffectKeyframe.duration_ms | 4 | varint (uint64) | Effect duration |
| EffectKeyframe.effect_type | 5 | varint | See EffectType enum |
