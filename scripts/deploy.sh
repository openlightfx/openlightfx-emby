#!/usr/bin/env bash
# Build Studio from a local checkout, embed it in the plugin, merge dependencies,
# and deploy the plugin to an Emby server for owner testing.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/deploy.sh [--help]

Builds the Studio checkout at STUDIO_REPO (default: ../openlightfx-studio), embeds
that exact build in this plugin, ILRepack-merges Google.Protobuf, and deploys to
EMBY_HOST (default: 192.168.1.3). The remote plugin is backed up before replacement.
Set DRY_RUN=1 to build and package without changing the server.

Environment:
  STUDIO_REPO       Studio checkout (default: sibling ../openlightfx-studio)
  EMBY_HOST         SSH host (default: 192.168.1.3)
  EMBY_PLUGINS      Remote plugin directory (default: /var/lib/emby/plugins)
  EMBY_URL          Health-check base URL (default: http://EMBY_HOST:8096)
  DRY_RUN           1 to skip SSH upload and server restart (default: 0)
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi
if [[ $# -ne 0 ]]; then
  usage >&2
  exit 2
fi

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STUDIO_REPO="$(cd "${STUDIO_REPO:-$REPO_DIR/../openlightfx-studio}" && pwd)"
EMBY_HOST="${EMBY_HOST:-192.168.1.3}"
EMBY_PLUGINS="${EMBY_PLUGINS:-/var/lib/emby/plugins}"
EMBY_URL="${EMBY_URL:-http://$EMBY_HOST:8096}"
DRY_RUN="${DRY_RUN:-0}"
PROJECT_DIR="$REPO_DIR/src/OpenLightFX.Emby"
BUILD_DIR="$PROJECT_DIR/bin/Release/net8.0"
LIB_DIR="$REPO_DIR/lib"
REMOTE_DLL="$EMBY_PLUGINS/OpenLightFX.Emby.dll"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)-$$"
STUDIO_VERSION="local-test-$STAMP"

[[ "$EMBY_HOST" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Invalid EMBY_HOST" >&2; exit 2; }
[[ "$EMBY_PLUGINS" =~ ^/[A-Za-z0-9/._-]+$ ]] || { echo "Invalid EMBY_PLUGINS path" >&2; exit 2; }
[[ "$DRY_RUN" == 0 || "$DRY_RUN" == 1 ]] || { echo "DRY_RUN must be 0 or 1" >&2; exit 2; }
[[ -f "$STUDIO_REPO/package.json" && -f "$STUDIO_REPO/AGENTS.md" ]] || {
  echo "STUDIO_REPO does not look like the OpenLightFX Studio checkout: $STUDIO_REPO" >&2
  exit 2
}
[[ -f "$REPO_DIR/AGENTS.md" ]] || { echo "Plugin AGENTS.md is missing" >&2; exit 2; }

printf 'Studio checkout: %s\nPlugin checkout: %s\nTarget: %s (%s)\n' \
  "$STUDIO_REPO" "$REPO_DIR" "$EMBY_HOST" "$EMBY_PLUGINS"

# Build the user's current Studio tree, not the latest published release.
(
  cd "$STUDIO_REPO"
  npm run build
)
[[ -f "$STUDIO_REPO/build/index.html" ]] || { echo "Studio build did not produce build/index.html" >&2; exit 1; }

DIST="$PROJECT_DIR/obj/studio-dist/openlightfx-studio-dist.zip"
mkdir -p "$(dirname "$DIST")"
rm -f "$DIST"
(
  cd "$STUDIO_REPO/build"
  zip -qr "$DIST" .
)
printf '%s' "$STUDIO_VERSION" > "$DIST.version"
unzip -tq "$DIST"
echo "Embedded Studio stamp: $STUDIO_VERSION"

# A matching version stamp tells FetchStudioDist to keep this local artifact.
dotnet build "$PROJECT_DIR/OpenLightFX.Emby.csproj" -c Release --nologo -p:StudioVersion="$STUDIO_VERSION"
[[ -f "$BUILD_DIR/OpenLightFX.Emby.dll" && -f "$BUILD_DIR/Google.Protobuf.dll" ]] || {
  echo "Plugin build outputs are incomplete" >&2
  exit 1
}

(
  cd "$REPO_DIR"
  dotnet tool restore
  dotnet ilrepack \
    /lib:"$LIB_DIR" \
    /out:"$BUILD_DIR/OpenLightFX.Emby.merged.dll" \
    "$BUILD_DIR/OpenLightFX.Emby.dll" \
    "$BUILD_DIR/Google.Protobuf.dll"
)
mv "$BUILD_DIR/OpenLightFX.Emby.merged.dll" "$BUILD_DIR/OpenLightFX.Emby.dll"
rm -f "$BUILD_DIR/Google.Protobuf.dll"
DEPLOY_DLL="$BUILD_DIR/OpenLightFX.Emby.dll"
sha256sum "$DEPLOY_DLL"

if [[ "$DRY_RUN" == 1 ]]; then
  echo "DRY_RUN=1: build/package validated; server was not contacted or changed."
  exit 0
fi

# Confirm connectivity and a running server before uploading anything.
ssh -o BatchMode=yes -o ConnectTimeout=8 "$EMBY_HOST" \
  'systemctl is-active --quiet emby-server'
BACKUP="$REMOTE_DLL.bak-$STAMP"
ssh -o BatchMode=yes "$EMBY_HOST" \
  "if sudo test -f '$REMOTE_DLL'; then sudo cp -a '$REMOTE_DLL' '$BACKUP'; fi"
TEMP_DLL="/tmp/OpenLightFX.Emby.dll-$STAMP"
scp -q "$DEPLOY_DLL" "$EMBY_HOST:$TEMP_DLL"
ssh -o BatchMode=yes "$EMBY_HOST" \
  "sudo install -o root -g root -m 644 '$TEMP_DLL' '$REMOTE_DLL' && sudo rm -f '$TEMP_DLL' '$EMBY_PLUGINS/Google.Protobuf.dll'"
if ! ssh -o BatchMode=yes "$EMBY_HOST" 'sudo systemctl restart emby-server'; then
  echo "Emby restart failed; restoring the previous plugin DLL if available." >&2
  ssh -o BatchMode=yes "$EMBY_HOST" \
    "if sudo test -f '$BACKUP'; then sudo cp -a '$BACKUP' '$REMOTE_DLL' && sudo systemctl restart emby-server; fi" || true
  exit 1
fi

# The authenticated Studio API must be registered (401 without a token is expected).
HTTP_CODE=000
for _ in $(seq 1 30); do
  if ssh -o BatchMode=yes "$EMBY_HOST" 'systemctl is-active --quiet emby-server'; then
    HTTP_CODE="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 4 "$EMBY_URL/OpenLightFX/Studio/Items" || true)"
    case "$HTTP_CODE" in
      200|301|302|401|403) break ;;
    esac
  fi
  sleep 2
done

case "$HTTP_CODE" in
  200|301|302|401|403)
    echo "Deployment healthy: Emby active; Studio API returned HTTP $HTTP_CODE without a token."
    echo "Backup (if a prior DLL existed): $EMBY_HOST:$BACKUP"
    ;;
  *)
    echo "Health check failed (HTTP $HTTP_CODE); restoring the previous plugin DLL if available." >&2
    ssh -o BatchMode=yes "$EMBY_HOST" \
      "if sudo test -f '$BACKUP'; then sudo cp -a '$BACKUP' '$REMOTE_DLL' && sudo systemctl restart emby-server; fi" || true
    exit 1
    ;;
esac
