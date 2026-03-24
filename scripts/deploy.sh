#!/usr/bin/env bash
# Build, ILRepack, and deploy the OpenLightFX plugin to the Emby server
set -euo pipefail

EMBY_HOST="${EMBY_HOST:-192.168.1.3}"
EMBY_PLUGINS="/var/lib/emby/plugins"
REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT_DIR="$REPO_DIR/src/OpenLightFX.Emby"
BUILD_DIR="$PROJECT_DIR/bin/Release/net8.0"
LIB_DIR="$REPO_DIR/lib"

echo "=== Clean build ==="
dotnet clean "$PROJECT_DIR" -c Release --nologo -v quiet 2>/dev/null || true
dotnet build "$PROJECT_DIR" -c Release --nologo

if [ ! -f "$BUILD_DIR/OpenLightFX.Emby.dll" ]; then
    echo "ERROR: Build output not found at $BUILD_DIR"
    exit 1
fi

echo ""
echo "=== ILRepack: merging Google.Protobuf into plugin DLL ==="
# Emby's plugin loader only loads one DLL per plugin — dependencies must be merged
cd "$REPO_DIR"
dotnet ilrepack \
    /lib:"$LIB_DIR" \
    /out:"$BUILD_DIR/OpenLightFX.Emby.merged.dll" \
    "$BUILD_DIR/OpenLightFX.Emby.dll" \
    "$BUILD_DIR/Google.Protobuf.dll"

mv "$BUILD_DIR/OpenLightFX.Emby.merged.dll" "$BUILD_DIR/OpenLightFX.Emby.dll"
rm -f "$BUILD_DIR/Google.Protobuf.dll"

MERGED_SIZE=$(stat --printf='%s' "$BUILD_DIR/OpenLightFX.Emby.dll")
echo "Merged DLL size: $((MERGED_SIZE / 1024)) KB"

echo ""
echo "=== Deploying to ${EMBY_HOST}:${EMBY_PLUGINS}/ ==="
# Clean up stale separate Google.Protobuf.dll if present
ssh "$EMBY_HOST" "sudo rm -f ${EMBY_PLUGINS}/Google.Protobuf.dll /var/lib/emby/plugins/Google.Protobuf.dll" 2>/dev/null || true

DLL="$BUILD_DIR/OpenLightFX.Emby.dll"
echo "  → OpenLightFX.Emby.dll ($((MERGED_SIZE / 1024)) KB)"
scp -q "$DLL" "${EMBY_HOST}:/tmp/OpenLightFX.Emby.dll"
ssh "$EMBY_HOST" "sudo mv /tmp/OpenLightFX.Emby.dll ${EMBY_PLUGINS}/OpenLightFX.Emby.dll && sudo chown root:root ${EMBY_PLUGINS}/OpenLightFX.Emby.dll && sudo chmod 644 ${EMBY_PLUGINS}/OpenLightFX.Emby.dll"

echo ""
echo "=== Restarting Emby server ==="
ssh "$EMBY_HOST" 'sudo systemctl restart emby-server'

echo "Done! Plugin deployed. Emby is restarting."
