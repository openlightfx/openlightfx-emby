# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

OpenLightFX is an Emby Server plugin that synchronizes smart bulb lighting with movie/TV playback in real-time. It reads `.lightfx` track files (Protobuf format), maps abstract lighting channels to physical smart bulbs, and dispatches commands over LAN using protocol-specific drivers.

## Commands

**Prerequisites (first time):**
```bash
chmod +x scripts/fetch-sdk.sh
EMBY_HOST=192.168.1.3 ./scripts/fetch-sdk.sh   # Fetches Emby SDK DLLs from live server
```

**Build:**
```bash
dotnet build src/OpenLightFX.Emby -c Release
```

**Build + Deploy to Emby server:**
```bash
EMBY_HOST=192.168.1.3 ./scripts/deploy.sh
```
Deploy builds, ILRepacks `Google.Protobuf.dll` into the plugin DLL (Emby only loads one DLL per plugin), then SCPs to the server and restarts emby-server via SSH.

**Analyze .lightfx track files or server logs:**
```bash
python3 scripts/analyze-playback.py decode <lightfx-file>
python3 scripts/analyze-playback.py log <log-file-or-url>
python3 scripts/analyze-playback.py compare --track <file> --log <file>
```

## Architecture

### Entry Points
- **`Plugin.cs`** — Emby plugin manifest (`BasePluginSimpleUI`); owns configuration persistence
- **`ServerEntryPoint.cs`** — `IServerEntryPoint` lifecycle; subscribes to Emby playback events; creates/destroys `PlaybackSession` instances

### Playback Engine (`Engine/`)
- **`PlaybackSession.cs`** — Core state machine for a single playback-to-lighting session. Manages keyframe scheduling, effect expansion, interpolation, real-time clock sync against Emby's reported position, and bulb dispatch. Uses `_syncPositionMs` + wall-clock extrapolation between Emby position reports.
- **`InterpolationEngine.cs`** — STEP and LINEAR interpolation between keyframes
- **`ChannelManager.cs`** — Maps abstract track channels to physical bulbs; applies brightness caps and effect filters

### Track Handling (`Services/`)
- **`TrackParser.cs`** — Deserializes `.lightfx` Protobuf files; validates 14 rules (V-001 through V-014)
- **`TrackDiscoveryService.cs`** — Finds tracks via sidecar (same dir), subfolder (`lightfx/`), and IMDB ID search
- **`TrackSelectionService.cs`** — Persists user-selected track per media item

### Smart Bulb Drivers (`Drivers/`)
All implement `IBulbDriver`. Factory: `BulbDriverFactory.cs`.

| Driver | Protocol | Details |
|--------|----------|---------|
| `Wiz/WizDriver.cs` | UDP port 38899 | RGB + color temp, 5 cmd/sec max |
| `Hue/HueDriver.cs` | REST via Hue Bridge | CIE xy color space, groups |
| `Lifx/LifxDriver.cs` | UDP port 56700 | HSBK color, hardware transitions |
| `Govee/GoveeDriver.cs` | LAN protocol | — |
| `GenericRest/` | HTTP templates | Configurable for custom APIs |

Color space conversions (RGB↔CIE xy, RGB↔HSBK, RGB↔CT) are in `ColorConverter.cs`.

### Effects Engine (`Effects/`)
13 effect types: Lightning, Flame, Flashbang, Explosion, Pulse, Strobe, Siren, Aurora, Candle, Gunfire, Neon, Breathing, Spark. Each implements `IEffectRenderer` → produces `EffectCommand` sequences (timestamp, RGBA, transition_ms). `PhotosensitivityFilter.cs` clamps flashing to ≤3 Hz and limits brightness deltas.

### REST API (`Api/`)
- `GET /OpenLightFX/Tracks/ByItem?itemId={id}` — list available tracks
- `POST /OpenLightFX/Tracks/Select` — select/clear track for item
- `GET /OpenLightFX/Status` — current playback status
- Discovery endpoints for LAN bulb scanning

### Configuration (`Configuration/PluginOptions.cs`)
Key settings: `BulbConfigJson` (JSON array of bulb definitions), `MappingProfilesJson` (channel→bulb mappings), `GlobalTimeOffsetMs` (sync compensation), `GlobalBrightnessCap`, `LookaheadBufferMs`, `PhotosensitivityMode`, and behavior overrides for start/end/credits.

## Key Constraints
- **Single DLL requirement**: Emby's plugin loader loads exactly one DLL. `deploy.sh` uses ILRepack to merge `Google.Protobuf.dll` into the main assembly.
- **SDK DLLs not versioned**: `lib/*.dll` is gitignored; must be fetched from a running Emby server with `fetch-sdk.sh`.
- **Target framework**: .NET 8.0
- **Protobuf schema**: `Proto/lightfx.proto` — compiled automatically by `Grpc.Tools` during build.
