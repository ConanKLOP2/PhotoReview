---
id: FEATURE-IDEAS-LARGE-LIBRARY
order: 50
summary: |-
  Proposed resilience & UX ideas for huge libraries or slow-link storage: slow-link indicator, graceful degradation, persistent metadata cache.
---

# Feature ideas for large libraries and slow-link scenarios

**Context:** Q-R29-C2 shipped an adaptive preload throttle (caps concurrent preload to 1 when link is slow, detected via `DecodeMillisecondsEwma > 200ms`). This fixes bandwidth contention on slow links (NAS, wifi) measured on fixture500. The user's real pain points: (1) folder too big to fit in RAM (F4 example: 2058 files, 17.6 GiB on 16 GiB cache), (2) folder on slow/networked storage. Beyond preload throttling, three new feature ideas are proposed below.

## 1. Slow-link visual indicator

**User scenario:** User reviewing 2000 photos on a NAS. Photos take 5+ seconds to load. User wonders: "Is the app broken?" or "Is my NAS down?" Currently, they see no feedback—just sluggish behavior. With a visual indicator, they immediately understand "link is slow, it's not broken" and adjust expectations (take a break, pre-warm a specific range, etc.).

**Rough mechanism:** Reuse the existing `DecodeMillisecondsEwma` signal from Q-R29-C2 (already computed by `PreloadScheduler` and used to detect slow links). Add a UI status indicator (icon + tooltip in the status bar or toolbar) showing link status:
- Green icon: "Local disk" (EWMA < 50 ms)
- Yellow icon: "Slow link" (EWMA 50–200 ms)
- Red icon: "Very slow link" (EWMA > 200 ms)

Update the indicator as the EWMA crosses thresholds during a review session. Tooltip shows concrete data: "Link average: 250 ms/decode" or "Decoding at ~15 MB/s".

**Critique survived:**
- **Does not duplicate Q-R29-C2:** Q-R29-C2 is behavioral (adaptive throttle). This is UX feedback/visibility.
- **Real scenario:** Yes. Any user on NAS or slow wifi would benefit. This user's fixture (`Xiuren`) may be local, but the pain point is explicitly stated in the task.
- **UI complexity:** Very low. It's a single status indicator, reuses existing signal, no new menu items.

**Complexity:** S

**Validation:** Could test with `SlowLinkSourceReader` on fixture500 in the harness. Does NOT require real NAS; simulated link is sufficient.

---

## 2. Graceful degradation mode for libraries larger than cache

**User scenario:** User loads F4 (2058 files, 17.6 GiB) on a machine with 16 GiB RAM cache. Currently, preload thrashes: the folder exceeds the cache budget, so preload evicts old previews while loading new ones, never reports idle, and navigation feels sluggish. With graceful degradation, user can switch to "Fast Mode": load preview at half resolution (1/4 memory overhead), skip preload thumbnail caching, and prioritize viewer decode—this keeps memory under the cache ceiling and eliminates thrashing.

**Rough mechanism:** 
1. On folder load, estimate library size (folder stat + sample file sizes).
2. If estimated library > configured cache size, offer user a toggle: "Use Fast Mode" (or "Conserve Memory") with a tooltip explaining "Library is larger than cache; enable to reduce image quality and avoid slowness."
3. When Fast Mode is on:
   - Limit preview resolution to half-width/half-height (1/4 memory cost) — reuse existing `PreviewImageService.Decode` resolution scaling.
   - Skip preload thumbnail caching (only cache thumbnails on-demand when user navigates; preload still prefetches the images, just not the downsample).
   - Increase `PreloadScheduler` viewer-busy priority further: when Fast Mode is active, cap preload to 0 concurrent reads while viewer is active (vs. the normal 1–4).

**Critique survived:**
- **Does not duplicate Q-R29-C2:** Q-R29-C2 is bandwidth mitigation. This is memory adaptation (different problem, different solution).
- **Real scenario:** Yes. F4 is the user's real fixture and demonstrates this exact pain point. The codebase already has evidence of this issue (Q-R29-C2 measurements show F4 never reaches idle due to cache eviction).
- **UI complexity:** Low. One toggle in Settings > Performance, or a one-time prompt on folder load ("Library is larger than cache; use Fast Mode?"). No menu clutter.
- **Caveat:** Reduces image quality, which is a trade-off. User needs to understand they're trading quality for responsiveness.

**Complexity:** M

**Validation:** Test with F4 (exceeds cache) on a local disk to verify memory pressure drops. Ideally also test F4 + `SlowLinkSourceReader` (slow link + oversized folder) to see if Fast Mode helps both constraints together. Does NOT require real NAS; local testing is sufficient, but NAS validation would strengthen confidence.

---

## 3. Persistent metadata cache for folder state

**User scenario:** User reviews a 500-file NAS folder. First open: slow (folder stat, file metadata reads, thumbnail preload = 30+ seconds on slow link). User closes app and reopens the same folder to continue reviewing the next day. Currently, the app re-reads all metadata and regenerates thumbnails from scratch (another 30+ seconds). With persistent metadata cache, second open checks if the folder changed (quick local stat), finds it hasn't, and loads cached thumbnails instantly (no network reads needed).

**Rough mechanism:**
1. After preload completes (entire folder scanned and previewed), save a folder state snapshot to a local cache file in `appdata\PhotoReview\MetadataCache\` (named by folder path hash). Snapshot includes:
   - Folder file list (names, sizes, mtimes, hashes—enough to detect changes).
   - Precomputed thumbnail metadata (dimensions, cache keys, bloom filter of which files have cached previews).
   - Thumbnail image data (store thumbnails in a local cache file for fast loading).

2. On next open of the same folder:
   - Load snapshot from cache.
   - Run a quick folder stat (list files, compare count + mtimes against snapshot).
   - If unchanged, skip preload and load cached thumbnails from disk (instant).
   - If changed (files added, deleted, or modified), update cache incrementally and preload as normal.

3. User can clear the cache manually: Settings > Performance > "Clear Metadata Cache".

**Critique survived:**
- **Does not duplicate Q-R29-C2:** Q-R29-C2 is runtime behavior (scheduler tuning). This is persistent caching across sessions.
- **Real scenario:** Yes. NAS users often review the same folders repeatedly (e.g., wallpaper archive review, batch processing). This user's fixture (`Xiuren` wallpaper folder) is a good example.
- **UI complexity:** Low. Just one Settings button to clear cache; the logic is internal.
- **Caveat:** Cache invalidation must be robust. If files change externally (user edits a file via Explorer while the app is closed), the cache must detect it. This requires careful mtime comparison and possibly a bloom filter to handle large folders efficiently.

**Complexity:** M

**Validation:** Can test locally with a simulated folder (create files, modify mtimes, verify cache detects changes). However, validating on real NAS with real file edits (e.g., user modifies a file between app runs) would catch edge cases the local test might miss (e.g., NAS time-sync quirks, SMB cache behavior). Real NAS validation recommended before implementation.

---

## Summary: survivors and trade-offs

**Survivors (3 of 3 ideas):**
1. **Slow-link indicator** (S complexity, no NAS needed) — Low-cost UX win, reuses existing signal, directly addresses "user confusion on slow link".
2. **Graceful degradation** (M complexity, local validation sufficient but NAS helpful) — Addresses oversized-folder pain point (F4 scenario), trades image quality for responsiveness, needs user education.
3. **Persistent metadata cache** (M complexity, NAS validation strongly recommended) — Addresses repeated-access workflow on NAS, adds cache invalidation complexity, good ROI for frequent folder reviewers.

**Dropped ideas:** None proposed here; the task was to propose strong, non-overlapping ideas beyond Q-R29-C2, and these three are distinct (UX feedback, memory adaptation, workflow caching).
