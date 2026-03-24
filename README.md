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

This copies the required Emby SDK DLLs from the server to `lib/`. Set `EMBY_HOST` to override the default server address (`192.168.1.3`).

### 2. Build

```bash
cd src/OpenLightFX.Emby
dotnet build -c Release
```

The output DLL is at `src/OpenLightFX.Emby/bin/Release/net8.0/OpenLightFX.Emby.dll`.

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
