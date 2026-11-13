# OpenLightFX Emby Plugin

Real-time smart bulb ambient lighting synchronized with movie playback on Emby Server.

## Overview

The OpenLightFX Emby plugin plays `.lightfx` lighting tracks in real-time sync with movies. It reads track files, maps abstract channels to physical smart bulbs, and dispatches lighting commands over the local network using bulb-specific protocols.

### Supported Bulb Protocols

| Protocol | Transport | Features |
|----------|-----------|----------|
| **Wiz** | UDP (port 38899) | RGB, color temp, dimming, discovery |
| **Philips Hue** | REST (via Bridge) | RGB (CIE xy), color temp, groups, discovery |
| **LIFX** | UDP (port 56700) | RGB (HSBK), color temp, hardware transitions |
| **Generic REST** | HTTP | Configurable URL/body templates for any REST API |

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Emby Server (running instance for SDK DLLs)

## Building

### 1. Fetch SDK DLLs (first time only)

```bash
chmod +x scripts/fetch-sdk.sh
./scripts/fetch-sdk.sh
```

This copies the Emby 4.9.3.0 SDK DLLs into `lib/` (gitignored). It extracts them from `emby/embyserver:4.9.3.0` (`/system`) when Docker is available, and otherwise downloads the official `emby-server-deb_4.9.3.0_amd64.deb` of the same build. Set `EMBY_VERSION` to pin a different server version.

### 2. Build

```bash
cd src/OpenLightFX.Emby
dotnet build -c Release
```

The output DLL is at `src/OpenLightFX.Emby/bin/Release/net8.0/OpenLightFX.Emby.dll`.

The build also downloads [OpenLightFX Studio](https://github.com/openlightfx/openlightfx-studio) release asset `openlightfx-studio-dist.zip` and embeds it. `StudioVersion` selects the release (`latest` by default). If no release exists yet, the build embeds a placeholder page and still succeeds.

```bash
dotnet build -c Release -p:StudioVersion=v1.0.0
```

```bash
dotnet test -c Release
```

## Deployment

### Automatic

```bash
chmod +x scripts/deploy.sh
./scripts/deploy.sh
```

This builds, copies the DLL to the Emby server's plugins directory, and restarts Emby.

### Manual

1. Build the plugin (see above)
2. Copy `OpenLightFX.Emby.dll` to your Emby Server's `plugins/` directory:
   - Linux: `/opt/emby-server/system/plugins/`
   - Windows: `%AppData%\Emby-Server\programdata\plugins\`
3. Restart Emby Server

## Configuration

After installation, navigate to **Emby Dashboard → Plugins → OpenLightFX → Settings**.

### Bulb Setup

Bulb configuration is managed through the Bulb Setup Wizard at `openlightfx.com/config`. The wizard communicates with the local OpenLightFX Agent for LAN discovery and exports JSON configuration that you paste into the plugin settings.

### Playback Settings

- **Global time offset**: Compensate for timing differences (ms)
- **Brightness cap**: Scale all brightness values (0-100%)
- **Lookahead buffer**: Pre-buffer upcoming keyframes (ms)
- **Photosensitivity mode**: Soften flashing/strobing effects

### Track Discovery

The plugin discovers `.lightfx` files using:
1. **Sidecar**: Same directory as the movie, matching filename
2. **Subfolder**: `lightfx/` subdirectory alongside the movie
3. **IMDB match**: Configured library paths searched by IMDB ID

## REST API

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/OpenLightFX/Tracks/ByItem?itemId={id}` | GET | List available tracks for a media item |
| `/OpenLightFX/Tracks/Select` | POST | Set/clear the selected track |
| `/OpenLightFX/Status` | GET | Current playback status |
| `/OpenLightFX/Bulbs/Test?bulbId={id}` | GET | Test bulb connectivity |
| `/OpenLightFX/Studio/Session` | GET | Validate the Emby Web credential or scoped page ticket for Studio API calls |
| `/OpenLightFX/Studio/Bulbs` | GET | Configured bulbs and capability profiles |
| `/OpenLightFX/Studio/Items?search=` | GET | Search library movies |
| `/OpenLightFX/Studio/Items/{itemId}/StreamUrl` | GET | Browser-playable video URL |
| `/OpenLightFX/Studio/Tracks?itemId=` | GET | List `.lightfx` tracks for a movie |
| `/OpenLightFX/Studio/Tracks/{trackId}?itemId=` | GET, PUT | Read or save raw `.lightfx` bytes (PUT is admin-only) |
| `/OpenLightFX/Studio/Preview/Start` | POST | Start a live bulb preview (admin) |
| `/OpenLightFX/Studio/Preview/Frame` | POST | Send a preview frame (admin) |
| `/OpenLightFX/Studio/Preview/Stop` | POST | Stop preview and restore bulbs (admin) |

Studio's static editor is served at `/emby/OpenLightFX/studio/` (Emby also exposes the same route at `/OpenLightFX/studio/`). The dist should use relative asset URLs, or base `/emby/OpenLightFX/studio/`. The static shell and assets contain no user data and do not require a token. Studio reads Emby Web's same-origin `servercredentials3` credential and calls `GET /OpenLightFX/Studio/Session` with `X-Emby-Token`; the plugin validates it and returns the token with `Cache-Control: no-store` for in-memory use. The session endpoint can instead redeem the short-lived `olfx_studio_page` HttpOnly ticket when present. Users do not need an API key in the URL; `?api_key=` remains supported for direct/legacy access. Studio API calls use `X-Emby-Token`. Saving tracks and preview require an administrator. A `<video>` element cannot send that header, so `MediaInfo` and `POST .../Proxy` set the separate `olfx_studio_media` cookie and `editUrl` does not include `api_key`. Cookie tickets are random, in-memory, HttpOnly, `SameSite=Strict`, `Secure` over HTTPS (including `X-Forwarded-Proto: https`), expire after 8 hours, rotate after 4, and die with an Emby restart or when the token behind them is revoked. `StreamUrl` still appends the token because that URL is Emby's own video endpoint.

## Architecture

```
Plugin.cs                    → BasePluginSimpleUI entry point
ServerEntryPoint.cs          → IServerEntryPoint lifecycle + event wiring
Configuration/               → PluginOptions (declarative UI)
Services/                    → Track parsing, discovery, selection
Engine/                      → Playback state machine, scheduler, interpolation
Effects/                     → Effect renderers (13 types) + photosensitivity filter
Drivers/                     → IBulbDriver interface + protocol implementations
Api/                         → REST endpoints + Light Tracks page
Utilities/                   → Color conversion, safety helpers
```

## License

Proprietary — OpenLightFX
