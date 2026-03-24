#!/usr/bin/env bash
# Fetch Emby SDK DLLs from the server into lib/
set -euo pipefail

EMBY_HOST="${EMBY_HOST:-192.168.1.3}"
EMBY_SYSTEM="/opt/emby-server/system"
# Plugin install path (for reference): /var/lib/emby/plugins
LIB_DIR="$(cd "$(dirname "$0")/.." && pwd)/lib"

DLLS=(
    "MediaBrowser.Common.dll"
    "MediaBrowser.Controller.dll"
    "MediaBrowser.Model.dll"
    "Emby.Web.GenericEdit.dll"
)

mkdir -p "$LIB_DIR"

echo "Fetching Emby SDK DLLs from ${EMBY_HOST}..."
for dll in "${DLLS[@]}"; do
    echo "  → $dll"
    scp "${EMBY_HOST}:${EMBY_SYSTEM}/${dll}" "${LIB_DIR}/${dll}"
done

echo "Done. SDK DLLs are in ${LIB_DIR}/"
