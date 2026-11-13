# Rooms API — design

**Status:** draft for owner review. No code is written against this until it is approved.

**Journey:** [Studio `docs/journeys/J1-first-light.md`](https://github.com/openlightfx/openlightfx-studio/blob/main/docs/journeys/J1-first-light.md). That document owns the screens, the requirement IDs (ROOM-, MODEL-, EDIT-, WATCH-), the layouts, and the fold-down and fill tables. This document owns how the plugin stores rooms, which routes Studio calls, and how Emby playback finds the room and routes a track to its bulbs.

## Summary

- Rooms, screens, bulbs, and positions move out of three pasted JSON settings (`BulbConfigJson`, `MappingProfilesJson`, `DeviceProfileOverridesJson`) and `ActiveProfileName` into one plugin-owned file, `{DataPath}/OpenLightFX/rooms.json`, edited only through Studio.
- A room has exactly one screen (an Emby `deviceId`). A bulb has a stable id and is in at most one room, at one position. A bulb in no room is known but never driven.
- Routing (channel → bulbs, including fold-down, fill, mixing, and the per-bulb Follows override) is computed in one place, a new `RoomRouter`, and used by both Emby playback and Studio preview. Studio never computes routing; it asks for it.
- Playback resolves `session.DeviceId` → room → bulbs → routing. A device in no room gets no lighting. Mapping profiles go away.
- Existing installs keep working: until rooms exist, playback uses the legacy profile path unchanged. A one-time import, previewed in Studio, creates rooms from the legacy settings without deleting them.
- The plugin configuration page shrinks to a link to Studio plus the playback settings.

## What exists today (main at `1ab2d76`)

| Area                   | Code                                                                                    | Behavior that matters here                                                                                                                                                                                                                         |
| ---------------------- | --------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Bulb inventory         | `Models/BulbConfig.cs`, `PluginOptions.BulbConfigJson`                                    | Polymorphic on `protocol` (Wiz, Hue, Lifx, Govee, Rest). `id`, `name`, `ipAddress`, `port`, `spatialPosition` (free text: the openlightfx.com wizard writes "Left", "Surround Left"…), `macAddress`, `model`, `capabilityOverrides`. Hue adds `hueApiKey`. |
| Channel → bulb mapping | `Models/MappingProfile.cs`, `PluginOptions.MappingProfilesJson`, `ActiveProfileName`      | A profile is a list of `{channelId, bulbIds[]}`. `channelId` matches a track channel id, then its display name (`ChannelManager.ResolveChannelId`). The openlightfx.com wizard names profiles after rooms.                                         |
| Device → profile       | `PluginOptions.DeviceProfileOverridesJson`, `ServerEntryPoint.TryStartLightingSession`   | `{deviceId: profileName}`; else `ActiveProfileName`; else the first profile.                                                                                                                                                                       |
| Playback               | `ServerEntryPoint.TryStartLightingSession` → `PlaybackSession` → `ChannelManager`          | `ChannelManager` builds bulb → channels; the first channel listed wins per bulb (EMB-032). No mixing.                                                                                                                                              |
| Auto-mapping           | `ChannelManager.SuggestMapping`                                                          | Compares `bulb.SpatialPosition` to `channel.SpatialHint` case-insensitively. Wizard data has "Left", tracks have "SPATIAL_LEFT", so it never matches. Unused by playback.                                                                          |
| Discovery              | `Discovery/*`, `POST /OpenLightFX/Discover`                                               | WiZ, LIFX, Govee broadcast; Hue finds bridges, not lights. Results dedupe by MAC then IP+protocol and live in `DiscoveredBulbStore` for 30 minutes. **Each scan gives every bulb a new random GUID**, so ids are not stable.                        |
| Identify               | `Discovery/IdentifyService.cs`, `POST /OpenLightFX/Identify/{BulbId}`                     | Looks the id up in `DiscoveredBulbStore` only, so it fails for configured bulbs and after 30 minutes. Hue identify is a 1.5 s no-op.                                                                                                              |
| Hue pairing            | `Drivers/Hue/HueBridgePairing.cs`, `/OpenLightFX/Hue/Pair*`, `/OpenLightFX/Hue/Lights`     | Link-button pairing returns an API key; lights listed per bridge.                                                                                                                                                                                  |
| Bulk save              | `POST /OpenLightFX/Discover/SaveConfig`                                                   | The openlightfx.com wizard's write path: replaces bulbs and profiles.                                                                                                                                                                              |
| Devices                | `GET /OpenLightFX/Devices` (`TrackService.Get(GetDevices)`)                              | Lists **active sessions only**, so a TV that is off does not appear.                                                                                                                                                                               |
| Track selection        | `Services/TrackSelectionService.cs`                                                      | `track-selections.json` in `{DataPath}/OpenLightFX`. Keys `itemId` (global) or `itemId:deviceId`. `TrackService` constructs its own second instance, so the two caches can disagree.                                                               |
| Studio bulbs           | `GET /OpenLightFX/Studio/Bulbs`                                                          | Bulbs from `BulbConfigJson` with capabilities.                                                                                                                                                                                                     |
| Studio preview         | `POST /OpenLightFX/Studio/Preview/{Start,Frame,Stop}`, `Studio/StudioPreviewHost.cs`       | Start takes a channel → bulbs `mapping` and **writes it into the active mapping profile** (`StudioService.RememberMapping`). Frame takes per-bulb states. Takeover refuses while Emby playback owns the bulbs (409 `playback_active`).            |
| Tracks                 | `Studio/StudioTrackFiles.cs`, `PUT /OpenLightFX/Studio/Tracks/{TrackId}`                  | `{movieDir}/lightfx/{trackId}.lightfx`, validated with `TrackParser`, `If-Match` on `updatedAt`. Kept unchanged.                                                                                                                                   |

Kept as is: the edit proxy and MediaInfo, the Frame endpoint, track and sidecar save, the cookie tickets, `StudioPreviewHost`'s exclusivity with playback, the drivers, effects, `InterpolationEngine`, and `PhotosensitivityFilter`.

## Storage

One file, `{IApplicationPaths.DataPath}/OpenLightFX/rooms.json`, next to `track-selections.json`. Written atomically (write `rooms.json.tmp`, then replace). A single `RoomStore` instance, created by `ServerEntryPoint` and shared with the API services (do not repeat the two-instance `TrackSelectionService` pattern), guards it with a lock.

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-11-13T14:00:00.000Z",
  "legacyImport": { "importedAt": "2026-11-13T14:00:00.000Z", "source": "BulbConfigJson+MappingProfilesJson" },
  "rooms": [
    {
      "id": "r_6f2c1a",
      "name": "Living Room",
      "screen": { "deviceId": "8b1e…", "deviceName": "Living Room TV", "appName": "Emby for Android TV" },
      "updatedAt": "2026-11-13T14:00:00.000Z"
    }
  ],
  "bulbs": [
    {
      "id": "wiz:a8bb50c3d201",
      "name": "Desk lamp",
      "roomId": "r_6f2c1a",
      "position": "left",
      "follows": null,
      "config": { "protocol": "Wiz", "ipAddress": "192.168.1.40", "port": 38899, "macAddress": "A8:BB:50:C3:D2:01", "model": "ESP01_SHRGB1C_31" },
      "lastSeenAt": "2026-11-13T13:58:00.000Z"
    }
  ]
}
```

- `position` is one of `left`, `center`, `right`, `surround-left`, `surround-right`, `ambient`, or null (in the room but not yet placed). `follows` is null (Auto) or one of the same six.
- `config` is the existing `BulbConfig` JSON shape, so `BulbDriverFactory.Create` keeps working. Hue lights carry the bridge key in `config.hueApiKey`. **The key is never returned by any route**; responses use a `BulbDto` that omits it.
- A bulb's `roomId` null means "known, not in a room". Such bulbs are never driven by playback or preview.

### Stable bulb ids

Ids must survive rescans and DHCP changes, so they come from hardware identity, not from the scan:

| Protocol   | Id                                     | Notes                                                                                     |
| ---------- | -------------------------------------- | ----------------------------------------------------------------------------------------- |
| WiZ, LIFX, Govee | `{protocol}:{mac, lowercase, no separators}` | All three report a MAC in discovery.                                                     |
| Hue        | `hue:{bridgeId}:{lightId}`             | `bridgeId` from the bridge's `/api/config`; `lightId` from `/lights`.                      |
| Rest       | `rest:{existing id}`                   | Never discovered; only arrives by legacy import.                                          |
| No MAC     | `{protocol}:ip:{ip}`                   | Fallback; upgraded to the MAC form the first time a scan reports one.                     |

On every scan, a discovered bulb whose id matches a known bulb updates that bulb's `ipAddress` and `lastSeenAt` and keeps its name, room, and position. Imported legacy bulbs keep their legacy id until a scan matches them by MAC (or IP + protocol), then are re-keyed to the stable id in one write.

## Routes

All under `/emby`. Reads use `[Authenticated]`; writes use `[Authenticated(Roles = "Admin")]`, like the existing Studio routes. JSON is camelCase through `[DataMember(Name=…)]`, matching `StudioService`. Errors are `{ "error": "code", "message": "human text" }` with the status shown.

### Setup

`GET /OpenLightFX/Studio/Setup` →

```json
{ "roomCount": 0, "isAdmin": true, "legacy": { "bulbs": 6, "profiles": 2, "deviceOverrides": 1, "imported": false } }
```

Studio uses it to choose the first screen (FLOW-001) and to show the import card (ROOM-011).

### Rooms

| Route                                    | Body / result                                                                                                                                                                                                                                       |
| ---------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /OpenLightFX/Studio/Rooms`          | `[{ id, name, screen, bulbs: [BulbDto], suggestedLayout: "LAYOUT_3_0", coveredPositions: ["left","center","right"], updatedAt }]`                                                                                                                       |
| `GET /OpenLightFX/Studio/Rooms/{roomId}` | One room, same shape. 404 `room_not_found`.                                                                                                                                                                                                       |
| `POST /OpenLightFX/Studio/Rooms`         | `{ name, screenDeviceId, bulbs: [{ id, position, follows }] }` → 201 with the room. 400 `name_required`, `name_taken`, `screen_required`, `unknown_bulb`.                                                                                             |
| `PUT /OpenLightFX/Studio/Rooms/{roomId}` | Same body; replaces the room's name, screen, and bulb set. `If-Match: {updatedAt}` required → 412 `stale` if it differs. A bulb or screen used by another room → 409 `in_other_room` with `{ bulbIds, deviceIds, otherRoomId }` unless `moveFromOtherRooms: true`. |
| `DELETE /OpenLightFX/Studio/Rooms/{roomId}` | 204. Its bulbs get `roomId: null`; its screen is free.                                                                                                                                                                                         |

`BulbDto` = `{ id, name, protocol, model, ipAddress, macAddress, roomId, position, follows, capabilities: { rgb, colorTemp, minTransitionMs }, lastSeenAt, reachable }`.

### Devices (screens)

`GET /OpenLightFX/Studio/Devices` →

```json
[
  { "deviceId": "8b1e…", "name": "Living Room TV", "appName": "Emby for Android TV", "lastUserName": "jon",
    "lastActivity": "2026-11-13T13:10:00Z", "roomId": "r_6f2c1a", "unassignedPlayback": null }
]
```

Source: Emby's `IDeviceManager.GetDevices(new DeviceQuery())` (present in the 4.9.3.0 SDK in `lib/MediaBrowser.Controller.dll`), which includes devices that are not connected right now, merged with `ISessionManager.Sessions` for live activity. `unassignedPlayback` is `{ itemName, at }` when the plugin recently skipped lighting because this device is in no room (WATCH-002). The existing `GET /OpenLightFX/Devices` stays for compatibility.

### Bulbs

| Route                                               | Body / result                                                                                                                                                                                                                                     |
| --------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /OpenLightFX/Studio/Discover`                 | `{ timeoutMs?: 5000 }` → `{ bulbs: [BulbDto], hueBridges: [{ bridgeId, ip, paired }] }`. Runs `DiscoveryCoordinator`, assigns stable ids, merges with known bulbs (updating IP and `lastSeenAt`), and persists new bulbs as known-not-in-a-room so Identify and Rename work on them. One scan at a time: a second call while one runs → 409 `discovery_running`. |
| `GET /OpenLightFX/Studio/Bulbs`                     | `[BulbDto]`: every known bulb. Replaces today's response of the same route (Studio is the only caller).                                                                                                                                         |
| `PUT /OpenLightFX/Studio/Bulbs/{bulbId}`            | `{ name }` → BulbDto. Room and position change only through the room routes, so a room write is the single place membership changes.                                                                                                           |
| `DELETE /OpenLightFX/Studio/Bulbs/{bulbId}`         | Forget a bulb that is in no room. 409 `in_room` otherwise.                                                                                                                                                                                       |
| `POST /OpenLightFX/Studio/Bulbs/{bulbId}/Identify`  | → `{ durationMs: 1500 }` after the flash. Uses `BulbDriverFactory.Create(config)` on the known bulb, so it works for placed bulbs, after rescans, and for Hue lights (via the bridge with the stored key), then restores the previous state when the driver can read it. 409 `preview_active` or `playback_active` if the bulb is in use; 504 `no_answer` if the driver gets no response. |
| `POST /OpenLightFX/Studio/Hue/Pair`                 | `{ bridgeId }` → `{ status: "paired" | "press_button" }`. Wraps `HueBridgePairing.RequestApiKey`; on success lists the bridge's lights and stores them as known bulbs with the key in `config`. Never returns the key.                           |

### Legacy import

`GET /OpenLightFX/Studio/Import/Legacy` returns the plan without writing; `POST` applies the same plan and returns the rooms. 409 `rooms_exist` if any room exists (import is for first run; re-running is not supported).

Algorithm, from `ConfigurationService.ParseBulbConfig`, `ParseMappingProfiles`, and `ParseDeviceProfileOverrides`:

1. For each mapping profile, in order, create a room named after it (deduplicated "Bedroom (2)").
2. For each `{channelId, bulbIds}` in the profile, each bulb not yet assigned joins this room. A bulb listed by an earlier profile stays there; the later listing is reported as `skipped: "bulb_in_earlier_room"`.
3. A bulb's position is the first that parses from: its `spatialPosition` ("Left", "left", "SPATIAL_LEFT", "Surround Left", "surround-left", "Behind Screen" → center, "Ceiling"/"Floor" → ambient); else the profile `channelId` it was mapped by, parsed the same way; else null (in the room, not placed), reported `unplaced: "no_position"`.
4. The room's screen is a `deviceId` whose override names this profile. With several, the one with the latest `IDeviceManager` activity wins; the rest are reported `skipped: "one_screen_per_room"`. With none, the room has no screen and is reported `needs_screen` (Studio opens it so the person picks one).
5. Bulbs in no profile become known bulbs in no room.
6. `ActiveProfileName` matters only when no override exists: if exactly one room has no screen and the active profile created it, the report says so, nothing more.
7. The legacy settings are not modified. `rooms.json.legacyImport` records that the import happened.

The plan is `{ rooms: [{ name, screen, bulbs: [{ id, name, position }] }], unassignedBulbs: [...], notes: [{ code, message, ref }] }`. The fixture `tests/fixtures/legacy/config.json` with its expected plan is shared with Studio (ROOM-011).

### Routing

`GET /OpenLightFX/Studio/Rooms/{roomId}/Routing?layout=LAYOUT_5_0`, or `POST` the same path with `{ channels: [{ id, spatialHint }] }` for a track whose channels are not a plain layout (legacy tracks). →

```json
{
  "layout": "LAYOUT_5_0",
  "channels": [
    { "channelId": "left", "position": "left", "bulbIds": ["wiz:a8…"], "foldedInto": null },
    { "channelId": "surround-left", "position": "surround-left", "bulbIds": ["wiz:a8…"], "foldedInto": "left" }
  ],
  "bulbs": [
    { "bulbId": "wiz:a8…", "sources": [ { "channelId": "left", "kind": "native" }, { "channelId": "center", "kind": "fold" }, { "channelId": "surround-left", "kind": "fold" } ] }
  ]
}
```

Studio uses it only to label lanes (EDIT-004). Both playback and preview call `RoomRouter` directly.

### Preview (changed)

| Route                                       | Today                                       | Proposed                                                                                                                                                                    |
| ------------------------------------------- | ------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /OpenLightFX/Studio/Preview/Start`    | `{ mapping: {channel: [bulbIds]}, takeover }`, writes the mapping into the active profile | `{ roomId, channels: [{ id, spatialHint }], takeover }`. The plugin builds `RoomRouter` routing for that room and those channels, opens drivers for the room's bulbs. Nothing is written to settings; `RememberMapping` and `StudioMappingProfiles` are deleted. 409 `playback_active` unchanged; 400 `room_has_no_bulbs`. |
| `POST /OpenLightFX/Studio/Preview/Frame`    | `{ states: [{ bulbId, r, g, b, brightness, transitionMs }] }` | `{ states: [{ channelId, r, g, b, brightness, transitionMs }] }`. The plugin mixes per bulb with the same `BulbMixer` playback uses.                                         |
| `POST /OpenLightFX/Studio/Preview/Stop`     | Restores bulbs                              | Unchanged.                                                                                                                                                                  |

### Active track

`POST /OpenLightFX/Studio/Items/{itemId}/ActiveTrack` with `{ trackId }` → `{ trackId, embyUrl: "/web/index.html#!/item?id={itemId}&serverId={serverId}" }`. Resolves the track path through `StudioTrackIndex`, calls `TrackSelectionService.SetSelectedTrack(itemId, path)` (global), and clears any `itemId:deviceId` selections for that item so every room plays it (WATCH-001, OQ-J1-7). Uses the single shared `TrackSelectionService`.

### Library additions

`GET /OpenLightFX/Studio/Items` gains `trackCount` and `hasActiveTrack` per item, from `TrackDiscoveryService` (already cached) and `TrackSelectionService`. Only `Movie` items, as today.

## Playback resolution

`ServerEntryPoint.TryStartLightingSession(sessionId, item, deviceId)` changes after track selection (which is unchanged):

1. If `rooms.json` has no rooms, use today's legacy path (profiles and overrides) unchanged. Existing users see no change until they import or create a room.
2. Otherwise find the room whose `screen.deviceId == deviceId`. None → log at Info "Device {name} ({deviceId}) is in no room; no lighting", record `unassignedPlayback` for the Devices route, return. Legacy profiles are not consulted once rooms exist.
3. The room's bulbs with a position (or a `follows`) are the bulbs. None → log and return.
4. Read the track's layout: `metadata.channel_layout` if set (FP-2), else infer from channel hints (J1 "Tracks that predate layouts"): `TrackLayout.Infer(track)`.
5. `RoomRouter.Route(channels, bulbs)` → per bulb, an ordered list of `(channelId, kind)` sources, using the fold-down and fill tables and `follows`.
6. `PlaybackSession` and `ChannelManager` take that routing instead of a `MappingProfile`. `ChannelManager.GetBulbCommand(bulbId)` mixes the bulb's sources with `BulbMixer` (max brightness, brightness-weighted RGB, Kelvin converted with the existing `InterpolationEngine` conversion first). Effects: the bulb's native source wins while it has an active effect; folded effects apply only otherwise.
7. Everything after that (drivers, effect expansion, photosensitivity filter, brightness cap, time offset) is unchanged.

Studio preview follows the same steps 3–6 with the room picked in Studio instead of the device.

### New and changed code

| Code                                 | Kind     | Responsibility                                                                                         |
| ------------------------------------ | -------- | ------------------------------------------------------------------------------------------------------ |
| `Rooms/RoomStore.cs`                 | new      | Load, validate, atomically save `rooms.json`; one shared instance.                                     |
| `Rooms/Room.cs`, `Rooms/KnownBulb.cs`, `Rooms/Position.cs` | new | Model. `Position.Parse` accepts every legacy spelling.                                         |
| `Rooms/StableBulbId.cs`              | new      | Id rules above.                                                                                        |
| `Rooms/TrackLayout.cs`               | new      | Layout constants, inference for legacy tracks, V-012/V-013 helpers.                                    |
| `Rooms/RoomRouter.cs`                | new      | Fold-down, fill, follows. Pure; tested against `tests/fixtures/routing/cases.json`.                    |
| `Rooms/BulbMixer.cs`                 | new      | Mixing rule. Pure.                                                                                     |
| `Rooms/LegacyImporter.cs`            | new      | Plan and apply.                                                                                        |
| `Api/RoomsService.cs`                | new      | The routes above.                                                                                      |
| `Engine/ChannelManager.cs`, `Engine/PlaybackSession.cs` | changed | Take routing instead of `MappingProfile`; legacy path wraps a profile into routing so there is one engine. |
| `ServerEntryPoint.cs`                | changed  | Steps 1–6; shares `RoomStore` and one `TrackSelectionService`.                                         |
| `Api/StudioService.cs`               | changed  | Preview start/frame bodies; ActiveTrack; Items counts; remove `RememberMapping`.                       |
| `Services/TrackParser.cs`            | changed  | V-012, V-013 when `channel_layout` is set.                                                              |
| `Proto/lightfx.proto`                | changed  | FP-2 field 14 and FP-3 comment. Copy kept identical to Studio's `proto/lightfx.proto`.                  |
| `Configuration/PluginOptions.cs`     | changed  | See below.                                                                                             |
| `Studio/StudioMappingProfiles.cs`, `ChannelManager.SuggestMapping` | removed | Superseded by `RoomRouter`.                                                        |
| `POST /OpenLightFX/Discover/SaveConfig` | deprecated | Kept one release for the openlightfx.com wizard, then removed with that page.                     |

## Plugin settings page

Wireframe: Studio `docs/journeys/wireframes/07-plugin-settings.png`.

`PluginOptions.EditorDescription` becomes a short paragraph and a link: "Rooms, bulbs, and light tracks are set up in OpenLightFX Studio. **Open OpenLightFX Studio** (`/emby/OpenLightFX/studio/`)". `BulbConfigJson`, `MappingProfilesJson`, `ActiveProfileName`, and `DeviceProfileOverridesJson` are hidden from the generic editor (kept as properties so existing values still load and import), the remaining playback settings stay under a "Playback" heading until Studio has a settings screen (OQ-J1-6). The generic editor cannot run script, so the link is a plain anchor in the description; if Emby escapes it, the description shows the URL as text. Verify on the full stack before relying on it.

## Tests

- **Unit (xUnit, `tests/OpenLightFX.Emby.Tests`)**: `RoomRouterTests` and `BulbMixerTests` over the shared routing fixture; `LegacyImporterTests` over the shared legacy fixture; `StableBulbIdTests`; `RoomStoreTests` (atomic write, `If-Match`, one room per bulb and per device); `TrackLayoutTests` (inference on a copy of John Wick's channel list, V-012/V-013).
- **Shared fixtures**: `tests/fixtures/routing/cases.json` and `tests/fixtures/legacy/config.json` (+ expected plan) are authored in the Studio repo and copied byte-for-byte here, as `tests/fixtures/sampling` already is. A test fails if the copies differ in `SOURCE`.
- **Full stack** (Studio repo, `OLFX_FULLSTACK_REQUIRE=1 npm run test:fullstack`): discovery and Identify against the simulator's NDJSON, room CRUD, legacy import, preview through a room, and WATCH-002 Emby playback on a device in a room and on one that is not.

## Risks and things to verify on the full stack

- `IDeviceManager.GetDevices` signature and fields in 4.9.3.0 (`DeviceQuery`, `DeviceInfo.LastUserName`, `AppName`, `DateLastActivity`): confirmed present as names in the SDK DLLs, not yet called.
- Discovery from inside Docker needs host networking or the simulator on the compose network; the full-stack compose already places the simulator beside Emby.
- Hue `bridgeId` and per-light identify need the simulator's Hue bridge to expose `/api/config` and `/lights`; check before writing ROOM-010's test.
- Emby's generic options editor may escape HTML in `EditorDescription`; if it does, a small `IPluginConfigurationPage` with a static link replaces it.
