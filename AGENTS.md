# AGENTS.md — OpenLightFX Emby plugin

Read this whole file before you change anything, then the design doc for your task. If a task brief and this file disagree, stop and ask.

The companion repository is [openlightfx/openlightfx-studio](https://github.com/openlightfx/openlightfx-studio): the track editor this plugin hosts. Its `AGENTS.md` and `docs/journeys/J1-first-light.md` define the product journey; read at least the journey summary there.

## 1. What OpenLightFX is for

A **track** (`.lightfx`) is lighting written once for one movie. A **room** is a real space with one screen and some smart bulbs. **Studio** is where a person writes a track and previews it on their bulbs. **This plugin** runs inside Emby Server and:

1. hosts Studio (a static SPA) at `/emby/OpenLightFX/studio/` and serves the API it uses;
2. discovers, identifies, and drives bulbs (WiZ, LIFX, Hue, Govee, generic REST);
3. plays the movie's active track in sync with Emby playback, in the room whose screen is playing.

The person is not a developer. After the first-journey work they set everything up in Studio. They never paste JSON and ideally never open this plugin's Emby settings page, which shrinks to a link to Studio plus playback settings.

## 2. Why this file exists

The last Studio rebuild was test-green and unusable, because screens were built before the journey was designed and agents worked from thin briefs. The plugin side had its own versions of that: mapping profiles that only worked if channel ids happened to match, discovery ids that change every scan, an Identify that cannot find configured bulbs, and a device list that only shows devices playing right now. Fix things in the direction of the journey, not around it.

## 3. The journey, from the plugin's side

J1 — First light (spec in the Studio repo):

| Step                 | Plugin work                                                                                                                    |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| 1. Set up a room     | Rooms CRUD, Emby device list (screens), discovery with stable bulb ids, Identify by id, and Hue pairing. No legacy import.   |
| 2. Pick a movie      | `GET /OpenLightFX/Studio/Items` (movies only) with poster URLs, track count, active flag.                                      |
| 3. Open/create track | List, read, validate, and save `.lightfx` files; project sidecar.                                                              |
| 4. Edit              | Edit-proxy video and MediaInfo, tone-mapped Frame, live preview through a room using the same routing as playback.              |
| 5. Watch in Emby     | Set the movie's active track; on Emby playback, resolve device → room → bulbs → routing and play.                             |

Design for steps 1, 4 (preview), and 5: [`docs/design/rooms-api.md`](docs/design/rooms-api.md).

## 4. Domain model

| Term     | Meaning                                                                                                   |
| -------- | --------------------------------------------------------------------------------------------------------- |
| Room     | Name, exactly one screen, its bulbs. Stored in `{DataPath}/OpenLightFX/rooms.json` (proposed).             |
| Screen   | Emby `deviceId` of the playback device in the room. In at most one room.                                   |
| Bulb     | One light with a stable id (`wiz:<mac>`, `hue:<bridge>:<light>`, …). In one room or none, at one position. |
| Position | `left`, `center`, `right`, `surround-left`, `surround-right`, `ambient`.                                  |
| Layout   | Ambient; 2.0 = L R; 3.0 = L C R; 5.0 = L C R SL SR. Declared by the track (`channel_layout`, FP-2).       |
| Channel  | Part of a track. Represents a position (`spatial_hint`), never a bulb.                                    |

Routing joins them: **fold-down** when a room has no bulb for a channel's position (SL→L, SR→R, C→L and R, anything→Ambient bulbs→every bulb), **fill** when a bulb's position has no channel (C←L+R mix, SL←L, SR←R, Ambient←mix of all), **mixing** when a bulb gets several channels (max brightness, brightness-weighted color), and a per-bulb **Follows** override. The exact tables are in the Studio J1 spec and the shared fixture `tests/fixtures/routing/cases.json`. Routing has **one implementation** (`RoomRouter`, proposed) used by playback and by preview. Studio never re-implements it.

Pasted bulb JSON, mapping profiles, active profile names, and device-profile overrides are removed. Playback uses a room selected by the Emby device; no legacy settings fallback or import exists.

## 5. Architecture

```
Emby playback events (ISessionManager)
  → ServerEntryPoint.TryStartLightingSession(session, item, deviceId)
      → TrackDiscoveryService (sidecar / lightfx/ subfolder / IMDb in scan paths)
      → TrackSelectionService (active track per item, optionally scoped to device)
      → RoomStore: deviceId → room → bulbs; transient position routing → playback
      → PlaybackSession (state machine, keyframes, effects lookahead, seek detection)
          → InterpolationEngine, EffectRendererFactory, PhotosensitivityFilter
          → ChannelManager (bulb ← channel state, brightness cap)
          → IBulbDriver (Wiz/UDP 38899, Lifx/UDP 56700, Govee/UDP 4003, Hue REST via bridge, generic REST)

Studio (static SPA, embedded zip) ← StudioAssetService at /emby/OpenLightFX/studio/
Studio API ← StudioService at /OpenLightFX/Studio/*
  Items, MediaInfo, Proxy (+ Proxy/Media), Frame, Tracks, Projects, Preview/Start|Frame|Stop
  [J1] Setup, Rooms, Devices, Discover, Bulbs, Identify, Hue/Pair, Routing, ActiveTrack
```

Key facts:

- **Single DLL.** Emby loads one assembly per plugin, so `Google.Protobuf` is merged in with ILRepack (`dotnet tool restore && dotnet ilrepack …`, see `scripts/deploy.sh` for the exact invocation and the Studio repo's `scripts/fullstack/run.sh` for a non-production one).
- **Studio is embedded.** The build downloads the `openlightfx-studio-dist.zip` release asset (`-p:StudioVersion=latest` by default, via `build/FetchStudioDist`) into `obj/studio-dist/` and embeds it. The full-stack harness drops a local Studio build there instead.
- **JSON** on Studio routes is camelCase through `[DataContract]`/`[DataMember(Name = …)]`, which is what Emby's serializer honours on Studio responses. Older routes in `DiscoveryService`/`TrackService` use `[JsonPropertyName]`; use the `DataMember` pattern for new routes and check the actual JSON in a full-stack test.
- **Auth.** Studio routes are `[Authenticated]`; writes and bulb control are `[Authenticated(Roles = "Admin")]`. The video element and asset loads use opaque, expiring, scope-limited cookie tickets (`Studio/StudioSessionAuth.cs`), never a raw token in a URL or cookie.
- **Edit proxy.** `StudioProxyJob` encodes H.264 fragmented MP4 (GOP ≤ 500 ms) with Emby's ffmpeg; `StudioGrowingStream` serves it with HTTP Range while it grows; MediaInfo reports `missing | building (encodedThroughMs) | ready | failed (reason)`. Emby's ffmpeg has no zscale; the Frame endpoint tonemaps with `tonemap=hable` and falls back to Emby's tonemap filter. Uses ffprobe.
- **Saving.** `PUT /OpenLightFX/Studio/Tracks/{trackId}` validates with `TrackParser` (V- codes) and writes `{movieDir}/lightfx/{trackId}.lightfx`; `If-Match` = `updatedAt`, 409 only when the file exists and differs. Sidecar `{trackId}.studio.json` next to it.
- **Preview** (`StudioPreviewHost`) shares drivers with playback and is mutually exclusive with it: start returns 409 `playback_active` unless `takeover`. Stop restores bulb state.
- **Proto.** `src/OpenLightFX.Emby/Proto/lightfx.proto` must stay byte-identical to Studio's `proto/lightfx.proto`. Changes are proposed in Studio's `docs/requirements/format-proposals.md` first.

## 6. Repository layout

| Path                                  | What                                                                                     |
| ------------------------------------- | ---------------------------------------------------------------------------------------- |
| `src/OpenLightFX.Emby/Api/`           | ServiceStack services: `StudioService`, `StudioAssetService`, `TrackService`, `DiscoveryService`, `LightTracksPage`. |
| `src/OpenLightFX.Emby/Studio/`        | Studio hosting, auth tickets, edit proxy, preview host, track files, sidecar.            |
| `src/OpenLightFX.Emby/Engine/`        | `PlaybackSession`, `ChannelManager`, `InterpolationEngine`.                              |
| `src/OpenLightFX.Emby/Effects/`       | 13 effect renderers and the photosensitivity filter.                                     |
| `src/OpenLightFX.Emby/Drivers/`       | `IBulbDriver` per protocol, `BulbDriverFactory`, Hue pairing.                             |
| `src/OpenLightFX.Emby/Discovery/`     | Per-protocol discovery, coordinator, in-memory store, identify.                         |
| `src/OpenLightFX.Emby/Configuration/` | `PluginOptions` (Emby generic settings editor) and playback setting parsing.                 |
| `src/OpenLightFX.Emby/Services/`      | Track discovery, parsing/validation, selection.                                          |
| `src/OpenLightFX.Emby/Models/`        | Bulb configs (polymorphic on `protocol`), transient channel mapping, track info.          |
| `src/OpenLightFX.Emby/Rooms/`         | Room storage, stable bulb ids, and position-based playback mapping.                        |
| `tests/OpenLightFX.Emby.Tests/`       | xUnit. `Fixtures/sampling` is a byte-for-byte copy of Studio's shared fixtures (`SOURCE` names the commit). |
| `docs/design/`                        | Design docs. `rooms-api.md` is the J1 plugin design.                                     |
| `scripts/fetch-sdk.sh`                | Puts Emby 4.9.3.0 SDK DLLs in `lib/` (from the Docker image, else the official .deb).    |
| `scripts/deploy.sh`                   | Empoweres an agent to deploy for testing.                                                |
| `scripts/analyze-playback.py`, `.github/skills/` | Owner's log-analysis helper.                                                   |

## 7. Running things

.NET 8 SDK.

```bash
./scripts/fetch-sdk.sh                                # once; fills lib/ (gitignored). Needs Docker or network.
dotnet build src/OpenLightFX.Emby -c Release          # embeds the latest Studio release
dotnet test -c Release                                # xUnit (90+ tests)
```

`fetch-sdk.sh` pulls from the `emby/embyserver:4.9.3.0` image (or the matching .deb).

**Full stack** lives in the Studio repo, because it tests both together:

```bash
cd ../openlightfx-studio            # a local clone of openlightfx-studio, on a developer machine with Docker
OLFX_EMBY_BRANCH=<your branch> OLFX_FULLSTACK_REQUIRE=1 npm run test:fullstack
```

It boots Emby 4.9.3.0 in Docker on `localhost:8096` (`docker-compose.fullstack.yml`), clones this repo at `OLFX_EMBY_BRANCH` (or uses a checkout at `.emby-stack/emby/src`), builds and ILRepacks the plugin with that Studio build embedded, installs it, starts the headless `openlightfx-simulator` (emulated bulbs; NDJSON `command_received` events are what tests assert on), and runs the Playwright full-stack tests in Chromium and Firefox. This repo has **no CI**, so a plugin PR is not verified until you have run `dotnet test` and the full stack, and the PR says which commit you ran them on.

## 8. Definition of done

1. The behavior is in an approved spec (Studio `docs/journeys/…` or `docs/requirements/studio-requirements.md`) or design doc here, and the PR names the requirement IDs.
2. `dotnet test -c Release` passes. Pure logic (routing, mixing, ids, validation) has unit tests over shared fixtures, copied byte-for-byte from the Studio repo.
3. The full stack passes with this branch (`OLFX_EMBY_BRANCH`), in Chromium and Firefox, against real Emby and the simulator. For anything the user sees, the Studio walkthrough video for that step has been recorded and watched.
4. No test is skipped, loosened, or listed as an expected failure to hide a real bug. No "works on mocks only".
5. Responses never contain secrets (Emby tokens, Hue bridge keys). Errors return `{ error, message }` with the human message Studio will show.
6. Pasted bulb JSON, mapping profiles, and per-device profile overrides are unsupported. Existing `.lightfx` tracks and track selections remain supported.

"All tests pass" is necessary, not sufficient.

