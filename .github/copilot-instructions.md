# Copilot Instructions — OpenLightFX Emby Plugin

## Build & Deploy

```bash
# First-time setup: fetch Emby SDK DLLs from the server into lib/
# Set EMBY_HOST to override default (192.168.1.3)
./scripts/fetch-sdk.sh

# Build
cd src/OpenLightFX.Emby
dotnet build -c Release

# Build + ILRepack (merge Google.Protobuf into single DLL) + deploy to Emby server
./scripts/deploy.sh
```

The plugin must ship as a **single DLL** — Emby's plugin loader only loads one assembly per plugin. `deploy.sh` uses ILRepack to merge `Google.Protobuf.dll` into the output. There are no tests in this repository.

## Architecture

This is an Emby Server plugin (.NET 8) that plays `.lightfx` lighting tracks in real-time sync with movie playback, dispatching commands to smart bulbs over the LAN.

### Data flow

```
Emby playback events (ISessionManager)
  → ServerEntryPoint (event wiring + polling timer)
    → PlaybackSession (per-session state machine: Idle → Loading → Playing ⇄ Paused → Stopped)
      → TrackDiscoveryService (find .lightfx sidecar/subfolder/IMDB match)
      → TrackParser (protobuf → LightFXTrack)
      → InterpolationEngine (binary search bracketing keyframes → linear/step blend)
      → EffectRendererFactory → IEffectRenderer (13 types → timed EffectCommands)
      → PhotosensitivityFilter (≤3 Hz flash rate, ≤50% brightness delta, ≥200ms transitions)
      → ChannelManager (channel→bulb reverse mapping, brightness cap)
      → IBulbDriver implementations (protocol-specific dispatch)
```

### Key subsystems

- **Drivers/** — `IBulbDriver` interface with protocol implementations (Wiz/UDP, Hue/REST, LIFX/UDP binary, Govee/UDP, GenericRest/HTTP). All async, `IAsyncDisposable`, rate-limited per protocol. Created via `BulbDriverFactory.Create(BulbConfig)`.
- **Effects/** — `IEffectRenderer` / `BaseEffectRenderer` with 13 effect types. Each renderer produces `List<EffectCommand>` (offset-based timed color commands). Renderers adapt to bulb capabilities via `BulbMeetsCapability()` / `ShouldSimplify()`.
- **Engine/** — `PlaybackSession` owns the playback state machine, keyframe processing (pre-sorted, per-channel binary search), effect expansion (lookahead buffer), and seek detection (>5s jump clears state). `InterpolationEngine` is static, handles RGB↔color-temp cross-mode blending via Tanner Helland algorithm. `ChannelManager` maps channels to bulbs (first-mapped channel wins per EMB-032).
- **Services/** — `TrackParser` validates protobuf tracks (V-001 through V-011). `TrackDiscoveryService` searches sidecar → subfolder → IMDB match (cached). `TrackSelectionService` persists selections to JSON with optional device scope (EMB-038).
- **Api/** — REST endpoints via Emby's ServiceStack `IService`. Two service classes: `DiscoveryService` (bulb scanning, Hue pairing) and `TrackService` (status, settings, track selection, playback control).
- **Discovery/** — `IDiscoveryModule` per protocol, coordinated by `DiscoveryCoordinator` (concurrent with timeout, MAC-based dedup). Results cached in `DiscoveredBulbStore` (30-min expiry).
- **Configuration/** — `PluginOptions` extends `EditableOptionsBase` (Emby declarative UI). `ConfigurationService` deserializes JSON fields (bulbs, profiles, device overrides) with validation.
- **Proto/** — `lightfx.proto` defines the track format (proto3). `Grpc.Tools` auto-generates C# classes at build time.

### Plugin entry points

- `Plugin.cs` — `BasePluginSimpleUI<PluginOptions>`, singleton via `Plugin.Instance`. Emby admin UI menu entry.
- `ServerEntryPoint.cs` — `IServerEntryPoint`, subscribes to `PlaybackStart`/`PlaybackStopped`/`PlaybackProgress` events, runs a polling timer (default 500ms) that calls `Session.Tick()`. Static `Instance` accessed by REST API. Sessions stored in `ConcurrentDictionary<string, PlaybackSession>`.

## Conventions

### Code style

- One public class per file, namespace matches directory: `OpenLightFX.Emby.{Subsystem}`
- Records for immutable DTOs (`BulbState`, `EffectCommand`, `EffectContext`, `InterpolatedState`)
- Static classes for stateless utilities (`ColorConverter`, `SafetyWarningHelper`, `InterpolationEngine`)
- Visual section separators: `// ─── Section Name ───`
- `System.Text.Json` with `CamelCase` naming policy (no Newtonsoft)
- Nullable reference types enabled throughout

### Async & concurrency

- All I/O is `async Task`. Drivers implement `IAsyncDisposable`.
- `Task.WhenAll()` for concurrent bulb dispatch
- `ConcurrentDictionary` for thread-safe session/state collections
- Rate limiting via `Stopwatch.GetTimestamp()` + `lock` (per-driver)

### Error handling

- Event handlers: try-catch, log, never throw
- Bulb unreachable: skip dispatch, continue playback (graceful degradation)
- Validation failures: return empty list or null with logged warnings
- Logging via Emby's `ILogger` (`MediaBrowser.Model.Logging`)

### Adding a new bulb driver

1. Create `Drivers/{Protocol}Driver.cs` implementing `IBulbDriver` and `IAsyncDisposable`
2. Define rate limits and capability profile via `GetCapabilities()` → `BulbCapabilityProfile`
3. Register in `BulbDriverFactory.Create()` switch
4. Add `Discovery/{Protocol}Discovery.cs` implementing `IDiscoveryModule`
5. Register in `DiscoveryCoordinator`

### Adding a new effect

1. Create `Effects/{Name}Renderer.cs` extending `BaseEffectRenderer`
2. Implement `Render()` → return `List<EffectCommand>` with offset-based timing
3. Use `ShouldSimplify()` to provide fallback for slow bulbs
4. Add the `EffectType` enum value in `lightfx.proto` and regenerate
5. Register in `EffectRendererFactory` constructor
