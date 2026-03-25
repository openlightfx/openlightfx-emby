#!/usr/bin/env python3
"""
analyze-playback.py — Decode .lightfx tracks and compare against Emby server
dispatch logs to verify playback timing accuracy.

Usage:
    scripts/analyze-playback.py decode <lightfx-file>
    scripts/analyze-playback.py log <log-file-or-url>
    scripts/analyze-playback.py compare --track <lightfx-file> --log <log-file-or-url>
"""

import argparse
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PROTO_DIR = REPO_ROOT / "src" / "OpenLightFX.Emby" / "Proto"
PROTO_FILE = PROTO_DIR / "lightfx.proto"

# ─── Proto compilation ──────────────────────────────────────────────

def _find_pb2_module():
    """Import lightfx_pb2, compiling from .proto if necessary."""
    # Check next to this script first
    script_dir = Path(__file__).resolve().parent
    candidate = script_dir / "lightfx_pb2.py"
    if candidate.exists():
        sys.path.insert(0, str(script_dir))
        import lightfx_pb2
        return lightfx_pb2

    # Auto-compile into a temp directory
    if not PROTO_FILE.exists():
        print(f"ERROR: Proto file not found at {PROTO_FILE}", file=sys.stderr)
        sys.exit(1)

    tmpdir = tempfile.mkdtemp(prefix="lightfx_proto_")
    try:
        subprocess.check_call(
            ["protoc", f"--proto_path={PROTO_DIR}", f"--python_out={tmpdir}",
             PROTO_FILE.name],
            stderr=subprocess.PIPE)
    except FileNotFoundError:
        print("ERROR: 'protoc' not found. Install with: sudo apt install protobuf-compiler",
              file=sys.stderr)
        sys.exit(1)
    except subprocess.CalledProcessError as e:
        print(f"ERROR: protoc failed: {e.stderr.decode()}", file=sys.stderr)
        sys.exit(1)

    sys.path.insert(0, tmpdir)
    import lightfx_pb2
    return lightfx_pb2


def load_track(filepath):
    """Parse a .lightfx file and return the LightFXTrack protobuf object."""
    pb2 = _find_pb2_module()
    with open(filepath, "rb") as f:
        data = f.read()
    track = pb2.LightFXTrack()
    track.ParseFromString(data)
    return track


# ─── Track decoding ─────────────────────────────────────────────────

def ms_to_time(ms):
    """Format milliseconds as mm:ss.SSS."""
    total_sec = ms / 1000.0
    minutes = int(total_sec // 60)
    seconds = total_sec % 60
    return f"{minutes:02d}:{seconds:06.3f}"


def decode_track(filepath):
    """Decode and display a .lightfx track."""
    track = load_track(filepath)

    print(f"{'═' * 60}")
    print(f"  Track: {track.metadata.title}")
    print(f"  File:  {Path(filepath).name}")
    print(f"{'═' * 60}")
    print(f"  Version:   {track.version}")
    print(f"  Duration:  {ms_to_time(track.metadata.duration_ms)} ({track.metadata.duration_ms}ms)")
    print(f"  Author:    {track.metadata.author or '(none)'}")
    if track.metadata.movie_reference.imdb_id:
        mr = track.metadata.movie_reference
        print(f"  Movie:     {mr.title} ({mr.year}) — {mr.imdb_id}")
    print(f"  Channels:  {len(track.channels)}")
    print(f"  Keyframes: {len(track.keyframes)}")
    print(f"  Effects:   {len(track.effect_keyframes)}")

    interp_names = {0: "UNSPEC", 1: "STEP", 2: "LINEAR"}
    mode_names = {0: "UNSPEC", 1: "RGB", 2: "CT"}
    effect_names = {
        0: "UNSPECIFIED", 1: "LIGHTNING", 2: "FLAME", 3: "FLASHBANG",
        4: "EXPLOSION", 5: "PULSE", 6: "STROBE", 7: "SIREN",
        8: "AURORA", 9: "CANDLE", 10: "GUNFIRE", 11: "NEON",
        12: "BREATHING", 13: "SPARK",
    }

    # Channel map for display names
    ch_names = {ch.id: ch.display_name or ch.id[:8] for ch in track.channels}

    print(f"\n{'─' * 60}")
    print("  Channels")
    print(f"{'─' * 60}")
    for ch in track.channels:
        spatial = ch.spatial_hint.replace("SPATIAL_", "") if ch.spatial_hint else "—"
        opt = " (optional)" if ch.optional else ""
        print(f"  {ch.id[:12]}…  {ch.display_name:<20} spatial={spatial}{opt}")

    print(f"\n{'─' * 60}")
    print("  Keyframes")
    print(f"{'─' * 60}")
    print(f"  {'Time':>12}  {'ms':>8}  {'Channel':<14} {'Mode':<4} {'Color':>18} {'Bright':>6} {'Trans':>7} {'Interp':<6}")

    kfs = sorted(track.keyframes, key=lambda k: (k.timestamp_ms, k.channel_id))
    for kf in kfs:
        mode = mode_names.get(kf.color_mode, "?")
        interp = interp_names.get(kf.interpolation, "?")
        ch_name = ch_names.get(kf.channel_id, kf.channel_id[:12])
        if kf.color_mode == 1:
            color = f"({kf.color.r:>3},{kf.color.g:>3},{kf.color.b:>3})"
        elif kf.color_mode == 2:
            color = f"{kf.color_temperature}K"
        else:
            color = "—"
        print(f"  {ms_to_time(kf.timestamp_ms):>12}  {kf.timestamp_ms:>8}  {ch_name:<14} {mode:<4} {color:>18} {kf.brightness:>5}% {kf.transition_ms:>5}ms {interp:<6}")

    if track.effect_keyframes:
        print(f"\n{'─' * 60}")
        print("  Effect Keyframes")
        print(f"{'─' * 60}")
        print(f"  {'Time':>12}  {'ms':>8}  {'Channel':<14} {'Effect':<12} {'Duration':>8} {'Intensity':>9}")
        for efx in sorted(track.effect_keyframes, key=lambda e: e.timestamp_ms):
            ch_name = ch_names.get(efx.channel_id, efx.channel_id[:12])
            etype = effect_names.get(efx.effect_type, f"TYPE_{efx.effect_type}")
            print(f"  {ms_to_time(efx.timestamp_ms):>12}  {efx.timestamp_ms:>8}  {ch_name:<14} {etype:<12} {efx.duration_ms:>6}ms {efx.intensity:>8}%")

    if track.safety_info:
        si = track.safety_info
        rating_names = {0: "UNSPECIFIED", 1: "SUBTLE", 2: "MODERATE", 3: "INTENSE", 4: "EXTREME"}
        print(f"\n{'─' * 60}")
        print("  Safety Info")
        print(f"{'─' * 60}")
        print(f"  Flashing:    {si.contains_flashing}")
        print(f"  Strobing:    {si.contains_strobing}")
        print(f"  Max flash:   {si.max_flash_frequency_hz:.1f} Hz")
        print(f"  Max Δbright: {si.max_brightness_delta}%")
        print(f"  Rating:      {rating_names.get(si.intensity_rating, '?')}")
        if si.warning_text:
            print(f"  Warning:     {si.warning_text}")


# ─── Unique keyframe timeline ───────────────────────────────────────

def get_unique_keyframe_timeline(track):
    """Extract unique (timestamp, color) keyframe groups, merging across channels.

    Returns list of dicts: { timestamp_ms, r, g, b, brightness, channels }
    sorted by timestamp.
    """
    # Group keyframes by (timestamp, r, g, b, brightness)
    groups = defaultdict(set)
    color_map = {}
    for kf in track.keyframes:
        if kf.color_mode != 1:
            continue
        key = (kf.timestamp_ms, kf.color.r, kf.color.g, kf.color.b, kf.brightness)
        groups[key].add(kf.channel_id)
        color_map[key] = kf

    timeline = []
    for key in sorted(groups.keys()):
        ts, r, g, b, bright = key
        timeline.append({
            "timestamp_ms": ts,
            "r": r, "g": g, "b": b,
            "brightness": bright,
            "channels": groups[key],
        })
    return timeline


# ─── Log parsing ────────────────────────────────────────────────────

# Log line patterns
RE_LOG_LINE = re.compile(
    r"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+(\w+)\s+OpenLightFX:\s+(.+)$"
)
RE_DISPATCH_RGB = re.compile(
    r"→ Bulb '([^']+)':\s+RGB\((\d+),(\d+),(\d+)\)\s+bright=(\d+)\s+trans=(\d+)ms"
)
RE_DISPATCH_CT = re.compile(
    r"→ Bulb '([^']+)':\s+CT=(\d+)K\s+bright=(\d+)\s+trans=(\d+)ms"
)
RE_DISPATCH_SKIP = re.compile(
    r"→ Bulb '([^']+)':\s+skipping dispatch"
)
RE_SEEK = re.compile(r"Seek detected.*?:\s+(\d+)ms\s+→\s+(\d+)ms")
RE_SESSION_START = re.compile(r"PlaybackSession started with (\d+) drivers")
RE_SESSION_INFO = re.compile(
    r"Lighting session started for '([^']+)'\s+\(track:\s+'([^']+)',\s+(\d+)\s+channels?,\s+(\d+)\s+keyframes?\)"
)
RE_SESSION_STOP = re.compile(r"PlaybackSession stopped")


def read_log_source(source):
    """Read log lines from a file path or URL."""
    if source.startswith("http://") or source.startswith("https://"):
        with urllib.request.urlopen(source) as resp:
            return resp.read().decode("utf-8", errors="replace").splitlines()
    else:
        with open(source, "r", encoding="utf-8", errors="replace") as f:
            return f.readlines()


def parse_log(source):
    """Parse an Emby server log and extract OpenLightFX events.

    Returns a list of event dicts sorted by timestamp.
    """
    lines = read_log_source(source)
    events = []

    for line in lines:
        line = line.rstrip()
        m = RE_LOG_LINE.match(line)
        if not m:
            continue

        timestamp_str, level, message = m.groups()
        ts = datetime.strptime(timestamp_str, "%Y-%m-%d %H:%M:%S.%f")

        event = {"timestamp": ts, "level": level, "message": message}

        # Classify the event
        dm = RE_DISPATCH_RGB.search(message)
        if dm:
            event["type"] = "dispatch_rgb"
            event["bulb_id"] = dm.group(1)
            event["r"] = int(dm.group(2))
            event["g"] = int(dm.group(3))
            event["b"] = int(dm.group(4))
            event["brightness"] = int(dm.group(5))
            event["transition_ms"] = int(dm.group(6))
            events.append(event)
            continue

        dm = RE_DISPATCH_CT.search(message)
        if dm:
            event["type"] = "dispatch_ct"
            event["bulb_id"] = dm.group(1)
            event["color_temp"] = int(dm.group(2))
            event["brightness"] = int(dm.group(3))
            event["transition_ms"] = int(dm.group(4))
            events.append(event)
            continue

        dm = RE_DISPATCH_SKIP.search(message)
        if dm:
            event["type"] = "dispatch_skip"
            event["bulb_id"] = dm.group(1)
            events.append(event)
            continue

        dm = RE_SEEK.search(message)
        if dm:
            event["type"] = "seek"
            event["from_ms"] = int(dm.group(1))
            event["to_ms"] = int(dm.group(2))
            events.append(event)
            continue

        if RE_SESSION_START.search(message):
            event["type"] = "session_start"
            events.append(event)
            continue

        dm = RE_SESSION_INFO.search(message)
        if dm:
            event["type"] = "session_info"
            event["movie_title"] = dm.group(1)
            event["track_name"] = dm.group(2)
            event["channel_count"] = int(dm.group(3))
            event["keyframe_count"] = int(dm.group(4))
            events.append(event)
            continue

        if RE_SESSION_STOP.search(message):
            event["type"] = "session_stop"
            events.append(event)
            continue

        # Generic OpenLightFX event
        event["type"] = "other"
        events.append(event)

    return events


def display_log(source):
    """Parse and display log events."""
    events = parse_log(source)

    dispatches = [e for e in events if e["type"].startswith("dispatch") and e["type"] != "dispatch_skip"]
    skips = [e for e in events if e["type"] == "dispatch_skip"]
    seeks = [e for e in events if e["type"] == "seek"]
    starts = [e for e in events if e["type"] in ("session_start", "session_info")]
    stops = [e for e in events if e["type"] == "session_stop"]

    print(f"{'═' * 60}")
    print(f"  Log Analysis")
    print(f"{'═' * 60}")
    print(f"  Total OpenLightFX events:  {len(events)}")
    print(f"  Dispatches (sent):         {len(dispatches)}")
    print(f"  Dispatches (skipped):      {len(skips)}")
    print(f"  Seeks:                     {len(seeks)}")
    print(f"  Session starts:            {len(starts)}")
    print(f"  Session stops:             {len(stops)}")

    for info in [e for e in events if e["type"] == "session_info"]:
        print(f"\n  Session: '{info['movie_title']}' — track '{info['track_name']}'")
        print(f"    {info['channel_count']} channels, {info['keyframe_count']} keyframes")

    if seeks:
        print(f"\n{'─' * 60}")
        print("  Seek Events")
        print(f"{'─' * 60}")
        for s in seeks:
            print(f"  {s['timestamp'].strftime('%H:%M:%S.%f')[:-3]}  {s['from_ms']}ms → {s['to_ms']}ms")

    if dispatches:
        print(f"\n{'─' * 60}")
        print("  Color Dispatches")
        print(f"{'─' * 60}")
        print(f"  {'Time':>15}  {'Color':>22}  {'Bright':>6}  {'Trans':>6}  Bulb")
        for d in dispatches:
            ts = d["timestamp"].strftime("%H:%M:%S.%f")[:-3]
            if d["type"] == "dispatch_rgb":
                color = f"RGB({d['r']},{d['g']},{d['b']})"
            else:
                color = f"CT={d['color_temp']}K"
            print(f"  {ts:>15}  {color:>22}  {d['brightness']:>5}%  {d['transition_ms']:>4}ms  {d['bulb_id'][:12]}…")

    # Detect duplicate dispatches
    dupes = []
    for i in range(1, len(dispatches)):
        prev, curr = dispatches[i - 1], dispatches[i]
        if (prev["type"] == curr["type"] == "dispatch_rgb"
                and prev["r"] == curr["r"] and prev["g"] == curr["g"]
                and prev["b"] == curr["b"] and prev["brightness"] == curr["brightness"]
                and prev.get("bulb_id") == curr.get("bulb_id")):
            delta = (curr["timestamp"] - prev["timestamp"]).total_seconds()
            dupes.append((prev, curr, delta))

    if dupes:
        print(f"\n  ⚠ {len(dupes)} duplicate consecutive dispatch(es) detected:")
        for prev, curr, delta in dupes[:10]:
            ts = curr["timestamp"].strftime("%H:%M:%S.%f")[:-3]
            print(f"    {ts}  RGB({curr['r']},{curr['g']},{curr['b']}) repeated {delta:.1f}s apart")


# ─── Comparison ─────────────────────────────────────────────────────

def compare(track_path, log_source):
    """Compare .lightfx track timing against Emby server log."""
    track = load_track(track_path)
    events = parse_log(log_source)
    timeline = get_unique_keyframe_timeline(track)

    dispatches = [e for e in events if e["type"] == "dispatch_rgb"]
    seeks = [e for e in events if e["type"] == "seek"]
    starts = [e for e in events if e["type"] == "session_start"]

    if not dispatches:
        print("ERROR: No RGB dispatches found in log.", file=sys.stderr)
        sys.exit(1)

    print(f"{'═' * 70}")
    print(f"  Playback Timing Comparison")
    print(f"{'═' * 70}")
    print(f"  Track:       {track.metadata.title} ({len(track.keyframes)} keyframes, {len(track.effect_keyframes)} effects)")
    print(f"  Dispatches:  {len(dispatches)} RGB commands in log")
    print(f"  Seeks:       {len(seeks)}")

    # Determine sync reference: use the last seek before the first real dispatch,
    # or the session start if no seeks
    last_seek = None
    sync_wall = None
    sync_pos_ms = 0

    for s in seeks:
        last_seek = s

    if last_seek:
        sync_wall = last_seek["timestamp"]
        sync_pos_ms = last_seek["to_ms"]
        print(f"\n  Sync ref:    {sync_wall.strftime('%H:%M:%S.%f')[:-3]} → movie pos {sync_pos_ms}ms (last seek)")
    elif starts:
        sync_wall = starts[-1]["timestamp"]
        sync_pos_ms = 0
        print(f"\n  Sync ref:    {sync_wall.strftime('%H:%M:%S.%f')[:-3]} → movie pos 0ms (session start)")
    else:
        sync_wall = dispatches[0]["timestamp"]
        sync_pos_ms = 0
        print(f"\n  Sync ref:    {sync_wall.strftime('%H:%M:%S.%f')[:-3]} → movie pos 0ms (first dispatch)")

    t0 = sync_wall - timedelta(milliseconds=sync_pos_ms)
    print(f"  Computed t0: {t0.strftime('%H:%M:%S.%f')[:-3]} (movie start in wall time)")

    # Only analyze dispatches after the sync point
    post_sync = [d for d in dispatches if d["timestamp"] >= sync_wall]

    # Match each dispatch to the nearest keyframe by color
    print(f"\n{'─' * 70}")
    print("  Dispatch → Keyframe Matching")
    print(f"{'─' * 70}")
    print(f"  {'Wall Time':>15}  {'Movie Pos':>10}  {'Color':>22}  {'Nearest KF':>10}  {'Delta':>8}")

    issues = []
    prev_dispatch = None

    for d in post_sync:
        movie_pos = (d["timestamp"] - t0).total_seconds() * 1000

        # Find the best matching keyframe by color
        best_match = None
        best_delta = float("inf")
        for kf in timeline:
            if kf["r"] == d["r"] and kf["g"] == d["g"] and kf["b"] == d["b"] and kf["brightness"] == d["brightness"]:
                delta = movie_pos - kf["timestamp_ms"]
                if abs(delta) < abs(best_delta):
                    best_delta = delta
                    best_match = kf

        ts_str = d["timestamp"].strftime("%H:%M:%S.%f")[:-3]
        color = f"RGB({d['r']},{d['g']},{d['b']}) b={d['brightness']}"

        if best_match:
            delta_s = best_delta / 1000.0
            flag = ""
            if abs(delta_s) > 2.0:
                flag = " ⚠ LATE" if delta_s > 0 else " ⚠ EARLY"
                issues.append({
                    "type": "late" if delta_s > 0 else "early",
                    "timestamp": d["timestamp"],
                    "keyframe_ms": best_match["timestamp_ms"],
                    "delta_s": delta_s,
                    "color": color,
                })
            print(f"  {ts_str:>15}  {movie_pos:>8.0f}ms  {color:>22}  {best_match['timestamp_ms']:>8}ms  {delta_s:>+7.1f}s{flag}")
        else:
            # No keyframe match — possibly an effect command
            print(f"  {ts_str:>15}  {movie_pos:>8.0f}ms  {color:>22}  {'(effect?)':>10}  {'':>8}")

        # Check for oscillation
        if prev_dispatch and prev_dispatch["type"] == "dispatch_rgb":
            if (prev_dispatch["r"] != d["r"] or prev_dispatch["g"] != d["g"]
                    or prev_dispatch["b"] != d["b"]):
                gap = (d["timestamp"] - prev_dispatch["timestamp"]).total_seconds()
                if gap < 1.5:  # Rapid color change
                    issues.append({
                        "type": "oscillation",
                        "timestamp": d["timestamp"],
                        "gap_s": gap,
                        "from_color": f"RGB({prev_dispatch['r']},{prev_dispatch['g']},{prev_dispatch['b']})",
                        "to_color": f"RGB({d['r']},{d['g']},{d['b']})",
                    })

        prev_dispatch = d

    # Summary
    print(f"\n{'─' * 70}")
    print("  Issues")
    print(f"{'─' * 70}")

    late_issues = [i for i in issues if i["type"] == "late"]
    early_issues = [i for i in issues if i["type"] == "early"]
    osc_issues = [i for i in issues if i["type"] == "oscillation"]

    if not issues:
        print("  ✓ No timing issues detected.")
    else:
        if late_issues:
            print(f"\n  ⚠ {len(late_issues)} late dispatch(es) (>2s behind keyframe):")
            for i in late_issues:
                ts = i["timestamp"].strftime("%H:%M:%S.%f")[:-3]
                print(f"    {ts}  keyframe@{i['keyframe_ms']}ms  {i['delta_s']:+.1f}s  {i['color']}")

        if early_issues:
            print(f"\n  ⚠ {len(early_issues)} early dispatch(es) (>2s ahead of keyframe):")
            for i in early_issues:
                ts = i["timestamp"].strftime("%H:%M:%S.%f")[:-3]
                print(f"    {ts}  keyframe@{i['keyframe_ms']}ms  {i['delta_s']:+.1f}s  {i['color']}")

        if osc_issues:
            print(f"\n  ⚠ {len(osc_issues)} rapid color oscillation(s) (<1.5s between changes):")
            for i in osc_issues:
                ts = i["timestamp"].strftime("%H:%M:%S.%f")[:-3]
                print(f"    {ts}  {i['from_color']} → {i['to_color']}  ({i['gap_s']:.1f}s apart)")

    # Duplicate detection
    dupes = []
    for i in range(1, len(post_sync)):
        prev, curr = post_sync[i - 1], post_sync[i]
        if (prev["r"] == curr["r"] and prev["g"] == curr["g"]
                and prev["b"] == curr["b"] and prev["brightness"] == curr["brightness"]
                and prev.get("bulb_id") == curr.get("bulb_id")):
            gap = (curr["timestamp"] - prev["timestamp"]).total_seconds()
            dupes.append((curr, gap))

    if dupes:
        print(f"\n  ⚠ {len(dupes)} redundant dispatch(es) (identical consecutive commands):")
        for d, gap in dupes[:10]:
            ts = d["timestamp"].strftime("%H:%M:%S.%f")[:-3]
            print(f"    {ts}  RGB({d['r']},{d['g']},{d['b']}) b={d['brightness']}  ({gap:.1f}s after prev)")


# ─── CLI ─────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Analyze OpenLightFX playback timing")
    sub = parser.add_subparsers(dest="command")

    # decode
    p_decode = sub.add_parser("decode", help="Decode a .lightfx track file")
    p_decode.add_argument("track", help="Path to .lightfx file")

    # log
    p_log = sub.add_parser("log", help="Parse Emby server log for OpenLightFX events")
    p_log.add_argument("source", help="Path or URL to Emby server log")

    # compare
    p_compare = sub.add_parser("compare",
        help="Compare track keyframes against log dispatches")
    p_compare.add_argument("--track", required=True, help="Path to .lightfx file")
    p_compare.add_argument("--log", required=True, help="Path or URL to Emby server log")

    args = parser.parse_args()

    if args.command == "decode":
        decode_track(args.track)
    elif args.command == "log":
        display_log(args.source)
    elif args.command == "compare":
        compare(args.track, args.log)
    else:
        parser.print_help()
        sys.exit(1)


if __name__ == "__main__":
    main()
