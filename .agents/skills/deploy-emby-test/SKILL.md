---
name: deploy-emby-test
description: Build and deploy the checked-out OpenLightFX plugin with the adjacent Studio checkout embedded. Use when the user asks to deploy or refresh the plugin on an Emby test server.
---

# Deploy the plugin with local Studio

This deploy replaces the Emby plugin DLL and restarts Emby. Run it only when the user asks for a deployment or refresh; do not deploy merely because a build passed.

1. Read this repository's `AGENTS.md` and the Studio repository's `AGENTS.md` plus the relevant J1 journey section. Check both worktrees with `git status --short` and `git branch --show-current`. Preserve local changes and the current branches; do not stash, reset, switch branches, or commit.
2. The script builds Studio from `STUDIO_REPO` (default: sibling `../openlightfx-studio`), zips that build into the plugin's ignored `obj/studio-dist` cache, builds the checked-out plugin, merges `Google.Protobuf`, backs up the old remote DLL, deploys, and restarts Emby. It deliberately does not use a Studio release asset.
3. Preview the build/dependency steps without contacting the server:

   ```bash
   STUDIO_REPO="$(cd ../openlightfx-studio && pwd)" DRY_RUN=1 scripts/deploy.sh
   ```

4. For an explicitly requested deployment to the default owner test server:

   ```bash
   STUDIO_REPO="$(cd ../openlightfx-studio && pwd)" scripts/deploy.sh
   ```

   The default host is `192.168.1.3`. Use `EMBY_HOST`, `EMBY_PLUGINS`, or `EMBY_URL` only when the user specifies a different authorized test target. The script requires batch SSH access and passwordless remote `sudo`.
5. The script verifies that the Emby service is active and that the Studio API route is registered; unauthenticated HTTP 401 is expected. It prints a remote backup path when there was an earlier DLL and attempts to restore it if restart/health checks fail.
6. Report the plugin and Studio branches deployed, the target host, the backup path, and exactly which checks passed. Do not expose API tokens, Hue credentials, or other secrets. The user must open Studio with their own Emby API key in the URL.
7. A successful deploy is only for hands-on testing. It does not satisfy `dotnet test`, Studio test tiers, the full-stack suite, or the owner's required walkthrough.
