# AI Pre-Processing Queue — Marketplace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add AI track generation UI to the marketplace library tab: a collapsible queue panel at the top and a per-movie AI button, backed by four new FastAPI proxy routes.

**Architecture:** Four proxy routes in `backend/app/api/routes/users.py` forward to the plugin's new `/OpenLightFX/Ai/*` endpoints. The library API is updated to include video path and installed-track info. The Svelte library tab gains `aiQueue` state with 3-second polling when active.

**Tech Stack:** FastAPI (Python), httpx for proxying, SvelteKit 5 / TypeScript (Svelte 5 runes: `$state`, `$derived`).

**Prerequisite:** Plugin plan (`2026-03-27-ai-preprocessing-plugin.md`) must be deployed before this plan is tested end-to-end.

**Branch:** All changes in `openlightfx-marketplace` should be made on a feature branch.

---

## File Structure

| Action | File | What changes |
|--------|------|-------------|
| Modify | `backend/app/api/routes/users.py` | Add 4 AI proxy routes; update `get_emby_library` to include `video_path` and `has_ai_track` |
| Modify | `frontend/src/lib/api/index.ts` | Update `EmbyLibraryItem` type + `getEmbyLibrary` mapping; add `enqueueAiItem`, `getAiQueue`, `deleteAiQueueItem` |
| Modify | `frontend/src/routes/emby/+page.svelte` | Add `aiQueue` state + polling; add queue panel; add per-movie AI button |

---

### Task 1: Create Feature Branch

**Files:** None.

- [ ] **Step 1: Create and switch to branch**

Run this in the `openlightfx-marketplace` repo:
```bash
cd ~/git/openlightfx/openlightfx-marketplace
git checkout -b feat/ai-preprocessing-queue
```

- [ ] **Step 2: Confirm clean state**

```bash
git status
```

Expected: `nothing to commit, working tree clean` (or only untracked files).

---

### Task 2: Add FastAPI Proxy Routes and Update Library Response

**Files:**
- Modify: `backend/app/api/routes/users.py`

- [ ] **Step 1: Update get_emby_library to include video_path and has_ai_track**

In `get_emby_library`, the `Items` API call currently requests `Fields=ProviderIds`. Update it to also include `Path`:

```python
        resp = await client.get(
            f"{emby_url}/Items",
            params={"IncludeItemTypes": "Movie", "Recursive": "true", "Fields": "ProviderIds,Path"},
            headers={"X-Emby-Token": api_key},
        )
```

In `fetch_item`, add `video_path` and `has_ai_track` to the returned dict:

```python
    async def fetch_item(client: httpx.AsyncClient, item: dict) -> dict:
        item_id = item.get("Id")
        imdb_id = item.get("ProviderIds", {}).get("Imdb")
        video_path = item.get("Path")  # ADD: needed to enqueue AI processing
        try:
            tracks_resp = await client.get(
                f"{emby_url}/OpenLightFX/Tracks/ByItem",
                params={"itemId": item_id},
                headers={"X-Emby-Token": api_key},
            )
            tracks_data = tracks_resp.json() if tracks_resp.status_code == 200 else {}
        except Exception:
            tracks_data = {}

        installed_tracks = tracks_data.get("tracks", []) if isinstance(tracks_data, dict) else []
        has_ai_track = any(t.get("isAiGenerated", False) for t in installed_tracks)

        # ... rest of marketplace_tracks lookup unchanged ...

        return {
            "emby_item_id": item_id,
            "title": item.get("Name"),
            "year": item.get("ProductionYear"),
            "imdb_id": imdb_id,
            "video_path": video_path,          # NEW
            "has_ai_track": has_ai_track,      # NEW
            "installed_tracks": installed_tracks,
            "marketplace_tracks": marketplace_tracks,
        }
```

The complete updated `fetch_item` function (replace entirely):
```python
    async def fetch_item(client: httpx.AsyncClient, item: dict) -> dict:
        item_id = item.get("Id")
        imdb_id = item.get("ProviderIds", {}).get("Imdb")
        video_path = item.get("Path")
        try:
            tracks_resp = await client.get(
                f"{emby_url}/OpenLightFX/Tracks/ByItem",
                params={"itemId": item_id},
                headers={"X-Emby-Token": api_key},
            )
            tracks_data = tracks_resp.json() if tracks_resp.status_code == 200 else {}
        except Exception:
            tracks_data = {}

        installed_tracks = tracks_data.get("tracks", []) if isinstance(tracks_data, dict) else []
        has_ai_track = any(t.get("isAiGenerated", False) for t in installed_tracks)

        marketplace_tracks = []
        if imdb_id:
            mkt_result = await db.execute(
                select(Track).where(
                    Track.movie_imdb_id == imdb_id,
                    Track.is_published,
                )
            )
            marketplace_tracks = [
                {"id": str(t.id), "title": t.title}
                for t in mkt_result.scalars().all()
            ]

        return {
            "emby_item_id": item_id,
            "title": item.get("Name"),
            "year": item.get("ProductionYear"),
            "imdb_id": imdb_id,
            "video_path": video_path,
            "has_ai_track": has_ai_track,
            "installed_tracks": installed_tracks,
            "marketplace_tracks": marketplace_tracks,
        }
```

- [ ] **Step 2: Add the four AI proxy routes**

Append to the end of `users.py` (after the last route):

```python
# ── AI Pre-Processing Queue Proxy ──────────────────────────────────────


@router.post("/me/emby/ai/enqueue")
async def enqueue_ai_item(
    body: dict,
    user: User = Depends(get_current_active_user),
):
    """Proxy: Enqueue a movie for AI pre-processing.
    Body: { itemId: str, videoPath: str, itemName: str }
    Returns 409 if item is already Pending or Processing.
    """
    emby_url, api_key = _require_emby(user)
    async with httpx.AsyncClient(timeout=30.0) as client:
        resp = await client.post(
            f"{emby_url}/OpenLightFX/Ai/Enqueue",
            json=body,
            headers={"X-Emby-Token": api_key},
        )
    if resp.status_code == 409 or (
        not resp.is_success and "CONFLICT" in (resp.text or "").upper()
    ):
        raise HTTPException(status_code=409, detail="Item is already pending or processing")
    if not resp.is_success:
        raise HTTPException(status_code=resp.status_code, detail=resp.text or "Plugin error")
    return resp.json()


@router.get("/me/emby/ai/queue")
async def get_ai_queue(
    user: User = Depends(get_current_active_user),
):
    """Proxy: Get full AI pre-processing queue snapshot."""
    emby_url, api_key = _require_emby(user)
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(
            f"{emby_url}/OpenLightFX/Ai/Queue",
            headers={"X-Emby-Token": api_key},
        )
    return _safe_json(resp)


@router.get("/me/emby/ai/queue/{item_id}")
async def get_ai_queue_item(
    item_id: str,
    user: User = Depends(get_current_active_user),
):
    """Proxy: Get status of a single AI queue item."""
    emby_url, api_key = _require_emby(user)
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(
            f"{emby_url}/OpenLightFX/Ai/Queue/{item_id}",
            headers={"X-Emby-Token": api_key},
        )
    return _safe_json(resp)


@router.delete("/me/emby/ai/queue/{item_id}")
async def delete_ai_queue_item(
    item_id: str,
    user: User = Depends(get_current_active_user),
):
    """Proxy: Remove a Pending AI queue item. Returns 409 if item is Processing."""
    emby_url, api_key = _require_emby(user)
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.delete(
            f"{emby_url}/OpenLightFX/Ai/Queue/{item_id}",
            headers={"X-Emby-Token": api_key},
        )
    if resp.status_code == 409 or (
        not resp.is_success and "CONFLICT" in (resp.text or "").upper()
    ):
        raise HTTPException(status_code=409, detail="Cannot remove an item that is currently processing")
    if not resp.is_success and resp.status_code != 200:
        raise HTTPException(status_code=resp.status_code, detail=resp.text or "Plugin error")
    return {}
```

- [ ] **Step 3: Verify Python syntax**

```bash
cd ~/git/openlightfx/openlightfx-marketplace
python3 -c "import ast; ast.parse(open('backend/app/api/routes/users.py').read()); print('OK')"
```

Expected: `OK`

- [ ] **Step 4: Commit**

```bash
git add backend/app/api/routes/users.py
git commit -m "Add AI queue proxy routes and include video_path/has_ai_track in library response"
```

---

### Task 3: Update API Client (TypeScript)

**Files:**
- Modify: `frontend/src/lib/api/index.ts`

- [ ] **Step 1: Update EmbyLibraryItem interface**

Find the `EmbyLibraryItem` interface (around line 133) and update it:

```typescript
export interface EmbyLibraryItem {
  item_id: string;
  title: string;
  year?: number;
  poster_url?: string;
  imdb_id?: string;
  selected_track_path?: string;
  video_path?: string;    // ADD: absolute path to the video file on the Emby server
  has_ai_track: boolean;  // ADD: true if a .ailfx sidecar exists
  tracks: { id: string; title: string; path: string }[];
}
```

- [ ] **Step 2: Add AiQueueItem type and AI API functions**

After the `getEmbyItemTracks` function (around line 563), add:

```typescript
// ── AI Queue Types ────────────────────────────────────────────────────

export interface AiQueueItem {
  itemId: string;
  itemName: string;
  state: 'Pending' | 'Processing' | 'Done' | 'Failed';
  progressPercent: number;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

// ── AI Queue API ──────────────────────────────────────────────────────

export async function enqueueAiItem(
  itemId: string,
  videoPath: string,
  itemName: string,
): Promise<AiQueueItem> {
  return apiFetch('/users/me/emby/ai/enqueue', {
    method: 'POST',
    body: JSON.stringify({ itemId, videoPath, itemName }),
  });
}

export async function getAiQueue(): Promise<{ items: AiQueueItem[] }> {
  return apiFetch('/users/me/emby/ai/queue');
}

export async function deleteAiQueueItem(itemId: string): Promise<void> {
  return apiFetch(`/users/me/emby/ai/queue/${encodeURIComponent(itemId)}`, {
    method: 'DELETE',
  });
}
```

- [ ] **Step 3: Update getEmbyLibrary to map new fields**

In `getEmbyLibrary` (around line 545), update the `.map()` callback to include `video_path` and `has_ai_track`:

```typescript
export async function getEmbyLibrary(): Promise<EmbyLibraryItem[]> {
  const raw = await apiFetch<any>('/users/me/emby/library');
  return (raw.items ?? []).map((item: any) => ({
    item_id: item.emby_item_id,
    title: item.title,
    year: item.year ?? undefined,
    poster_url: item.poster_url ?? undefined,
    imdb_id: item.imdb_id ?? undefined,
    selected_track_path: item.selected_track_path ?? undefined,
    video_path: item.video_path ?? undefined,           // ADD
    has_ai_track: item.has_ai_track ?? false,           // ADD
    // Use marketplace_tracks for available tracks to install
    tracks: (item.marketplace_tracks ?? []).map((t: any) => ({
      id: t.id,
      title: t.title,
      path: t.id,
    })),
  }));
}
```

- [ ] **Step 4: Update imports in +page.svelte**

At the top of `+page.svelte`, add the new imports to the existing import block:
```typescript
import {
  // ... existing imports ...
  enqueueAiItem,
  getAiQueue,
  deleteAiQueueItem,
} from '$lib/api';
import type {
  // ... existing types ...
  AiQueueItem,
} from '$lib/api';
```

- [ ] **Step 5: Verify TypeScript compiles**

```bash
cd ~/git/openlightfx/openlightfx-marketplace/frontend
npm run check 2>&1 | tail -20
```

Expected: 0 errors (or only pre-existing errors unrelated to these changes).

- [ ] **Step 6: Commit**

```bash
git add frontend/src/lib/api/index.ts
git commit -m "Add AiQueueItem type and AI queue API functions; update EmbyLibraryItem with video_path/has_ai_track"
```

---

### Task 4: Add AI State and Polling to Svelte Component

**Files:**
- Modify: `frontend/src/routes/emby/+page.svelte`

- [ ] **Step 1: Add AI queue state variables**

In the `// ─── State ─────` section (around line 62), after the `selectingTrack` state variable, add:

```typescript
  // AI pre-processing queue
  let aiQueue = $state<AiQueueItem[]>([]);
  let aiQueuePollTimer: ReturnType<typeof setInterval> | null = null;
  let enqueuingAi = $state<string | null>(null); // item_id being enqueued
```

- [ ] **Step 2: Add AI queue functions**

After the `selectTrack` function (around line 293), add:

```typescript
  // ─── AI Queue ─────────────────────────────────────────────────────────────

  async function fetchAiQueue() {
    try {
      const data = await getAiQueue();
      aiQueue = data.items ?? [];
      // Start polling if any item is active; stop when all are settled
      const hasActive = aiQueue.some(
        (i) => i.state === 'Pending' || i.state === 'Processing',
      );
      if (hasActive && aiQueuePollTimer === null) {
        aiQueuePollTimer = setInterval(fetchAiQueue, 3000);
      } else if (!hasActive && aiQueuePollTimer !== null) {
        clearInterval(aiQueuePollTimer);
        aiQueuePollTimer = null;
        // Refresh library so has_ai_track updates after processing completes
        libraryLoaded = false;
        await loadLibrary();
      }
    } catch {
      // Silently ignore — queue polling is best-effort
    }
  }

  async function enqueueAi(item: EmbyLibraryItem) {
    if (!item.video_path) {
      toastStore.error('No video path available for this item');
      return;
    }
    enqueuingAi = item.item_id;
    try {
      await enqueueAiItem(item.item_id, item.video_path, item.title);
      toastStore.success(`Queued "${item.title}" for AI processing`);
      await fetchAiQueue();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        toastStore.info('Already queued or processing');
        await fetchAiQueue();
      } else {
        toastStore.error(e instanceof Error ? e.message : 'Failed to enqueue');
      }
    } finally {
      enqueuingAi = null;
    }
  }

  async function removeFromAiQueue(itemId: string) {
    try {
      await deleteAiQueueItem(itemId);
      await fetchAiQueue();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        toastStore.error('Cannot remove: item is currently processing');
      } else {
        toastStore.error(e instanceof Error ? e.message : 'Failed to remove from queue');
      }
    }
  }

  function getQueueEntry(itemId: string): AiQueueItem | undefined {
    return aiQueue.find((i) => i.itemId === itemId);
  }

  function aiButtonLabel(item: EmbyLibraryItem): string {
    const q = getQueueEntry(item.item_id);
    if (q) {
      if (q.state === 'Pending') {
        const pos = aiQueue.filter((i) => i.state === 'Pending').indexOf(q) + 1;
        return `Queued (${pos})`;
      }
      if (q.state === 'Processing') return `Generating… ${q.progressPercent}%`;
      if (q.state === 'Done') return 'Regenerate';
      if (q.state === 'Failed') return 'Retry';
    }
    return item.has_ai_track ? 'Regenerate' : 'Generate AI Track';
  }

  function aiButtonDisabled(item: EmbyLibraryItem): boolean {
    const q = getQueueEntry(item.item_id);
    if (!q) return false;
    return q.state === 'Pending' || q.state === 'Processing';
  }
```

- [ ] **Step 3: Fetch the AI queue when library loads**

In `loadLibrary()`, after `libraryLoaded = true;`, add a call to fetch the queue:
```typescript
  async function loadLibrary() {
    if (libraryLoaded) return;
    libraryLoading = true;
    try {
      library = await getEmbyLibrary();
      libraryLoaded = true;
      await fetchAiQueue();  // ADD: initialize queue state alongside library
    } catch (e) {
      toastStore.error(e instanceof Error ? e.message : 'Failed to load library');
    } finally {
      libraryLoading = false;
    }
  }
```

- [ ] **Step 4: Clean up poll timer on component unmount**

At the end of `onMount` (or add an `onDestroy` import + call), clean up the interval. Add at the top of the `<script>` block:
```typescript
  import { onMount, onDestroy } from 'svelte';
```

Then at the end of the script section (before `</script>`), add:
```typescript
  onDestroy(() => {
    if (aiQueuePollTimer !== null) {
      clearInterval(aiQueuePollTimer);
      aiQueuePollTimer = null;
    }
  });
```

- [ ] **Step 5: Commit**

```bash
git add frontend/src/routes/emby/+page.svelte
git commit -m "Add AI queue state, polling, and helper functions to Svelte component"
```

---

### Task 5: Add Queue Panel to Library Tab

**Files:**
- Modify: `frontend/src/routes/emby/+page.svelte`

- [ ] **Step 1: Add queue panel HTML between the search bar and the movie list**

In the library tab section (around line 689), after the `<div class="flex items-center justify-between...">` search/header block (after its closing `</div>` around line 703), and before `{#if libraryLoading}`, insert the queue panel:

```svelte
          <!-- AI Queue Panel (visible only when queue is non-empty) -->
          {#if aiQueue.length > 0}
            <div class="mb-4 rounded-xl overflow-hidden" style="border:1px solid #3d3d6b;">
              <div class="px-4 py-3 flex items-center justify-between" style="background:#1e1e3a;">
                <span class="text-sm font-semibold" style="color:#c4b5fd;">AI Processing Queue</span>
                <span class="text-xs" style="color:#94a3b8;">{aiQueue.length} item{aiQueue.length !== 1 ? 's' : ''}</span>
              </div>
              <div class="divide-y" style="border-color:#252540;">
                {#each aiQueue as qItem (qItem.itemId)}
                  <div class="px-4 py-3 flex items-center gap-3" style="background:#161628;">
                    <!-- Name and state -->
                    <div class="flex-1 min-w-0">
                      <p class="text-sm font-medium truncate">{qItem.itemName}</p>
                      {#if qItem.state === 'Processing'}
                        <div class="mt-1.5 h-1.5 rounded-full overflow-hidden" style="background:#252540;">
                          <div
                            class="h-full rounded-full transition-all"
                            style="background:#6c63ff; width:{qItem.progressPercent}%;"
                          ></div>
                        </div>
                        <p class="text-xs mt-1" style="color:#94a3b8;">{qItem.progressPercent}% complete</p>
                      {:else if qItem.state === 'Done'}
                        <p class="text-xs mt-0.5" style="color:#22c55e;">✓ Complete</p>
                      {:else if qItem.state === 'Failed'}
                        <p class="text-xs mt-0.5 truncate" style="color:#ef4444;" title={qItem.error ?? ''}>
                          ✗ {qItem.error ?? 'Failed'}
                        </p>
                      {:else}
                        <p class="text-xs mt-0.5" style="color:#94a3b8;">Waiting…</p>
                      {/if}
                    </div>
                    <!-- State badge -->
                    <span
                      class="text-xs px-2 py-0.5 rounded-full font-medium shrink-0"
                      style="background:{qItem.state === 'Done' ? '#16a34a22' : qItem.state === 'Failed' ? '#dc262622' : '#6c63ff22'}; color:{qItem.state === 'Done' ? '#22c55e' : qItem.state === 'Failed' ? '#ef4444' : '#c4b5fd'};"
                    >{qItem.state}</span>
                    <!-- Remove button (Pending only) -->
                    {#if qItem.state === 'Pending'}
                      <button
                        onclick={() => removeFromAiQueue(qItem.itemId)}
                        class="shrink-0 w-6 h-6 rounded flex items-center justify-center text-sm hover:bg-white/10"
                        style="color:#94a3b8;"
                        title="Remove from queue"
                      >×</button>
                    {/if}
                  </div>
                {/each}
              </div>
            </div>
          {/if}
```

- [ ] **Step 2: Verify the panel renders correctly when queue is empty vs. non-empty**

Start the dev server and check that the panel is hidden when `aiQueue` is empty and appears when items are added. Since the queue requires a live Emby connection, a quick sanity check is confirming no Svelte compile errors:

```bash
cd ~/git/openlightfx/openlightfx-marketplace/frontend
npm run check 2>&1 | grep -E "Error|error" | head -10
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/routes/emby/+page.svelte
git commit -m "Add collapsible AI queue panel to library tab"
```

---

### Task 6: Add Per-Movie AI Button to Library Tab

**Files:**
- Modify: `frontend/src/routes/emby/+page.svelte`

- [ ] **Step 1: Add AI button to each movie row**

In the movie list (around line 757), after the closing `</div>` of the "Info" block and before the closing `</div>` of the movie row, add the AI button alongside the existing track selector. The current structure has a conditional block `{#if item.tracks.length > 0}` for the track selector. The AI button should always show (it's not gated on marketplace tracks).

Find the `<!-- Track selector -->` comment and the block that follows it. Replace the entire `{#if item.tracks.length > 0}` block with one that includes both the track selector AND the AI button:

```svelte
                  <!-- Actions: track selector + AI button -->
                  <div class="flex items-center gap-2 shrink-0 flex-wrap justify-end">
                    <!-- Track selector (marketplace tracks) -->
                    {#if item.tracks.length > 0}
                      <select
                        class="px-3 py-1.5 rounded-lg text-xs max-w-48"
                        style="background:#1a1a2e; border:1px solid #3d3d6b; color:#e2e8f0;"
                        id="track-select-{item.item_id}"
                      >
                        <option value="">— Select track —</option>
                        {#each item.tracks as track}
                          <option value={track.path} selected={item.selected_track_path === track.path}>
                            {track.title}
                          </option>
                        {/each}
                      </select>
                      <button
                        onclick={() => {
                          const sel = document.getElementById(`track-select-${item.item_id}`) as HTMLSelectElement;
                          if (sel?.value) selectTrack(item.item_id, sel.value);
                        }}
                        disabled={selectingTrack === item.item_id}
                        class="px-3 py-1.5 rounded-lg text-xs font-semibold disabled:opacity-50"
                        style="background:#6c63ff; color:white;"
                      >
                        {selectingTrack === item.item_id ? '…' : 'Select'}
                      </button>
                    {/if}

                    <!-- AI Track button -->
                    {#if item.video_path}
                      {@const qEntry = getQueueEntry(item.item_id)}
                      <button
                        onclick={() => enqueueAi(item)}
                        disabled={aiButtonDisabled(item) || enqueuingAi === item.item_id}
                        title={qEntry?.state === 'Failed' ? (qEntry.error ?? 'Failed') : undefined}
                        class="px-3 py-1.5 rounded-lg text-xs font-semibold disabled:opacity-50 whitespace-nowrap"
                        style="background:{item.has_ai_track || qEntry ? '#1a2e1a' : '#1a1a2e'}; color:{item.has_ai_track || qEntry?.state === 'Done' ? '#22c55e' : qEntry?.state === 'Failed' ? '#ef4444' : '#94a3b8'}; border:1px solid {item.has_ai_track || qEntry ? '#22c55e44' : '#3d3d6b'};"
                      >
                        {enqueuingAi === item.item_id ? '…' : aiButtonLabel(item)}
                      </button>
                    {/if}
                  </div>
```

- [ ] **Step 2: Verify TypeScript check**

```bash
cd ~/git/openlightfx/openlightfx-marketplace/frontend
npm run check 2>&1 | grep -E "Error|error" | head -10
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/routes/emby/+page.svelte
git commit -m "Add per-movie AI button to library tab with state-driven labels"
```

---

### Task 7: End-to-End Verification

**Files:** None new.

**Prerequisites:** Plugin plan deployed, marketplace backend running locally (or on staging), Emby connected.

- [ ] **Step 1: Start local backend and frontend**

```bash
cd ~/git/openlightfx/openlightfx-marketplace
docker compose up -d  # or however local dev is started
```

- [ ] **Step 2: Open the Emby library tab and confirm new columns load**

Navigate to the Emby page, open the library tab. Confirm:
- Movies load without JS errors
- Movies with no `.ailfx` show "Generate AI Track" button
- Movies with a `.ailfx` show "Regenerate" button

- [ ] **Step 3: Click "Generate AI Track" on a short movie**

Click the button and confirm:
- Button changes to "Queued (1)" immediately
- Queue panel appears at the top of the library tab
- After ~3s the queue panel shows "Processing" with a progress bar
- Progress bar increments as batches complete
- On completion: button changes to "Regenerate", queue panel item shows "Done", library refreshes

- [ ] **Step 4: Click × on a Pending item**

If multiple items are queued, confirm:
- Clicking × on a Pending item removes it from the queue panel
- Cannot remove a Processing item (× not shown)

- [ ] **Step 5: Create PR**

```bash
cd ~/git/openlightfx/openlightfx-marketplace
gh pr create \
  --title "Add AI pre-processing queue UI to library tab" \
  --body "$(cat <<'EOF'
## Summary
- Adds 4 FastAPI proxy routes for AI queue (enqueue, list, get, delete)
- Updates library response to include video_path and has_ai_track
- Adds collapsible AI queue panel at top of library tab (polls every 3s when active)
- Adds per-movie AI button with state-driven labels (Generate/Queued/Generating/Regenerate/Retry)

## Test plan
- [ ] Load library tab, confirm AI button shows for all movies
- [ ] Click Generate AI Track, confirm queue panel appears and progress updates
- [ ] Verify "Regenerate" label after processing completes
- [ ] Verify × removes Pending items but not Processing items
- [ ] Verify queue poll stops and library refreshes when all items complete

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL printed.
