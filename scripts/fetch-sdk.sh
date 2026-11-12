#!/usr/bin/env bash
# Copy Emby 4.9.3.0 SDK DLLs into lib/. The DLLs are not committed.
# Prefers the Docker image the full-stack rig uses. Without Docker, downloads
# the official .deb of the same server version and extracts /opt/emby-server/system.
set -euo pipefail

VERSION="${EMBY_VERSION:-4.9.3.0}"
IMAGE="${EMBY_IMAGE:-emby/embyserver:${VERSION}}"
DEB_URL="${EMBY_DEB_URL:-https://github.com/MediaBrowser/Emby.Releases/releases/download/${VERSION}/emby-server-deb_${VERSION}_amd64.deb}"
LIB_DIR="$(cd "$(dirname "$0")/.." && pwd)/lib"

DLLS=(
  MediaBrowser.Common.dll
  MediaBrowser.Controller.dll
  MediaBrowser.Model.dll
  Emby.Web.GenericEdit.dll
  ServiceStack.Text.dll
)

mkdir -p "$LIB_DIR"

copy_from_system() {
  local system_dir="$1"
  local dll
  for dll in "${DLLS[@]}"; do
    if [[ ! -f "${system_dir}/${dll}" ]]; then
      echo "Missing ${dll} in ${system_dir}" >&2
      return 1
    fi
    cp "${system_dir}/${dll}" "${LIB_DIR}/${dll}"
    echo "  → ${dll}"
  done
}

if command -v docker >/dev/null 2>&1; then
  echo "Extracting SDK DLLs from ${IMAGE} (/system)..."
  cid="$(docker create "$IMAGE")"
  trap 'docker rm "$cid" >/dev/null 2>&1 || true' EXIT
  tmp="$(mktemp -d)"
  docker cp "${cid}:/system/." "$tmp"
  copy_from_system "$tmp"
  rm -rf "$tmp"
else
  echo "Docker is not available. Downloading emby-server ${VERSION}..."
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  curl -fL --retry 3 -o "${tmp}/emby.deb" "$DEB_URL"
  dpkg-deb -x "${tmp}/emby.deb" "${tmp}/root"
  copy_from_system "${tmp}/root/opt/emby-server/system"
fi

echo "SDK DLLs are in ${LIB_DIR}/ (gitignored)."
