---
id: FEATURE-IDEAS-WORKFLOW-SPEED
summary: Brainstorm and self-critique for workflow-speed features (photo review acceleration)
---

# Feature Ideas: Workflow Speed for Photo Review

**Purpose:** Propose new features that would make reviewing hundreds/thousands of photos faster and less tedious.

**Brainstorm criteria:**
- Does it solve a real, common user scenario (not "cool but nobody needs it")?
- Does it fit the app's actual architecture without requiring a disproportionate rewrite?
- Does it conflict with existing decisions in `OPEN-DECISIONS.md`?
- If it touches delete/move operations, does it follow the data-safety conventions in `docs/adr/0007-io-durability-contract.md`?

**Result:** 4 ideas survived self-critique; 6 were dropped.

---

## Survivor 1: Hash-Grouped Duplicate Sidebar

**User Scenario**

User imports 1500 photos from a camera with burst mode enabled. The camera captured many 3-way or continuous bursts of the same scene, resulting in 300–400 near-identical shots. User wants to identify these duplicates and decide which ones to keep **without** having to notice by eye that photo_042.jpg and photo_043.jpg look the same as they scroll through individually. Goal: cut culling time by 30–50% vs. reviewing each frame in sequence.

**How It Works**

- Background hash service (already exists, `IHashService`) computes file hashes and fingerprints during preload or on-demand.
- UI sidebar (toggleable, Settings > Display > Show Duplicates or similar) groups files by:
  - **Exact duplicates** (identical file hash) — e.g., "3 files with same hash: photo_042.jpg, photo_043.jpg, photo_044.jpg | Size: 2.1 MB"
  - **Near duplicates** (same visual fingerprint, e.g., JPEG re-encoded at different quality) — e.g., "5 similar: photo_042.jpg, photo_043.jpg, ..."
- Clicking a group expands to show thumbnails of all files in that group stacked or in a mini-grid.
- Visual marker (badge, border color, or overlay) on main image indicates whether the current image has a duplicate.
- User can select multiple files within a group and delete/move them together (respects INV-4: file action service still gates one action at a time, so batch is internal to UI, not a bypass of safety).

**Critique Findings**

✅ **Real scenario:** Burst-mode duplicates after camera import are extremely common. User perspective is "I know these are similar, I just need to identify and review them quickly."

✅ **Architecture fit:** 
- `IHashService` already exists and is used for compare mode. Fingerprinting would reuse existing image decoder infrastructure.
- Sidebar is UI-only; no changes to file action flow or invariants.
- INV-4 (one file action at a time) is **not** violated — user still deletes individually or in small groups; batch selection is just UI grouping, not a bypass of single-action gating.
- Preload can compute hashes in background; no UI thread blockage.

✅ **No conflict with existing decisions:**
- Q-R30 (UI feedback): adding a sidebar is consistent with other info panels.
- No changes to delete/move/undo logic.

✅ **Data safety:** 
- Read-only grouping display.
- Delete operations use existing `FileActionService` → `OperationJournal` → Recycle Bin flow (ADR 0007 applies).

⚠ **Potential reviewer concern:** "Why not just use Compare mode to check duplicates?" 
- **Answer:** Compare mode is 2-at-a-time, manual navigation. This auto-groups all duplicates **across the entire folder** at once, making it possible to see at a glance that 50 frames are near-identical (useful for deciding "keep frame 1, delete the rest"). Compare mode still available for edge cases (e.g., pixel-level comparison).

**Complexity Estimate:** **Medium**
- Hash/fingerprint background computation: ~1 week (uses existing service, just needs UI trigger/scheduling).
- Sidebar UI (grouping display, expand/collapse, thumbnails): ~1.5 weeks.
- Integration with file selection/delete: ~0.5 weeks.

**Total:** ~3 weeks, mid-priority (nice-to-have, not core to review speed but accelerates duplicate-heavy sessions).

---

## Survivor 2: Spatial Lookahead Sidebar (Thumbnail Contact Sheet)

**User Scenario**

User is reviewing a folder of 1000+ photos. They're currently looking at photo #150, and they want to know: "What's coming next? Is the next batch similar to these?" or "Should I slow down and review more carefully before we switch to landscape shots?" Currently, they have to scroll/arrow-key through to see. Goal: enable batch-level decision-making by showing what's coming next without having to visit each image first.

**How It Works**

- Bottom or right sidebar shows a scrollable grid of 10–20 thumbnail previews from the preload queue (configured in Settings > Performance > Preload window).
- Thumbnails reflect the current folder order (Default/Name/Explorer order).
- Clicking any thumbnail jumps to that image.
- Grid updates in real-time as user navigates (forward or backward).
- Optional: color-code or label groups by inferred similarity (e.g., "Next 10 are landscape") if fingerprinting is enabled (Survivor 1).

**Critique Findings**

✅ **Real scenario:** User anxiety about "what comes next" is valid. In a 1000-photo session, being able to see the next 20 at a glance helps users batch-decide: "OK, next batch is all similar family portraits, I'll be more selective here." Reduces navigation overhead.

✅ **Architecture fit:**
- Preload system already maintains a window of upcoming images (Q-R31: `PreloadForwardCount` / `PreloadBackwardCount`).
- Thumbnail cache already exists; sidebar just renders cached thumbnails.
- `ReviewCatalog` and `PreloadScheduler` already expose the forecast; sidebar binds to it.
- **No changes to file action logic or invariants.**

✅ **No conflict with existing decisions:**
- Q-R31 (preload window settings): uses existing preload infrastructure, no change needed.
- UI-only addition.

✅ **Data safety:** Read-only display.

⚠ **Potential reviewer concern:** "This adds UI clutter and reduces viewport space for the main image."
- **Answer:** Sidebar is toggleable (Settings > Display). Many users will benefit; those who don't can hide it. Real-estate trade-off is minor — desktop app can afford right-edge or bottom-bar usage. Lookahead is particularly useful for folders > 500 images where navigation fatigue is real.

⚠ **Potential reviewer concern:** "Doesn't this duplicate the compare mode?"
- **Answer:** Compare mode is for detailed 1:1 comparison. This is a spatial preview to help navigation decisions.

**Complexity Estimate:** **Medium**
- Expose preload forecast API: ~0.5 weeks.
- Sidebar UI (grid layout, thumbnail rendering, scrolling): ~1 week.
- Scroll/navigation sync: ~0.5 weeks.
- Settings toggle: ~0.5 weeks.

**Total:** ~2.5 weeks, medium-priority (moderate speedup for large folders, low risk).

---

## Survivor 3: Session Undo Log Sidebar

**User Scenario**

User has been deleting photos for 30 minutes, working through 500 images. Suddenly they realize: "Wait, did I delete that one photo with the blue dress? I'm not sure." They want to see a list of everything they've deleted/moved in **this session only** and restore a specific file without having to Ctrl+Z their way back through 50 actions. Goal: confidence in bulk operations without fear of "oops, I deleted the wrong one and didn't notice."

**How It Works**

- New sidebar panel (toggleable, Settings > Display > Show Session History or similar) lists all file actions taken in the current session.
- Organized by action type (Deleted, Moved to X, Copied to Y) with timestamps.
- Each entry shows the file name, size, and action details (e.g., "Deleted: photo_123.jpg | 2.3 MB | 14:32").
- Clicking "Restore" on any deleted entry calls `UndoService.Undo()` to move it back from Recycle Bin.
- Optional: search/filter by name or date range.

**Critique Findings**

✅ **Real scenario:** User anxiety about data loss during bulk operations is well-founded. Especially given that Recycle Bin can be accidentally emptied, users want a visible record of "what did I delete?" within this session.

✅ **Architecture fit:**
- `UndoService` and `OperationJournal` already track all operations (INV-6).
- Decision P03 explicitly states "Undo limited to current session" — this feature fits perfectly, it shows only current-session actions.
- Sidebar just reads from existing undo/journal data; no changes to file action logic.
- **No violation of invariants.**

✅ **No conflict with existing decisions:**
- P03 (session-limited undo): this implements exactly that use case (session visibility).
- ADR 0007 (durability): uses existing undo/recovery paths, no new durability logic needed.

✅ **Data safety:** 
- Read-only display of journal.
- Restore uses existing `UndoService.Undo()`, which already validates Recycle Bin safety.

⚠ **Potential reviewer concern:** "Ctrl+Z already exists; why add a sidebar?"
- **Answer:** Ctrl+Z undoes the **most recent** action. If user deleted A, B, C in that order and only wants B back, they'd press Ctrl+Z twice to undo C and B, or three times to undo all. Sidebar shows all three, user clicks B's restore button (**one action**). Also, the visual list gives confidence: "I can see exactly what I deleted, and it's the right stuff."

**Complexity Estimate:** **Small**
- Wire `OperationJournal` data to sidebar: ~0.5 weeks (journal already exists, just expose as read-only list).
- UI (list, timestamps, restore buttons): ~0.5 weeks.
- Search/filter (optional): ~0.5 weeks.

**Total:** ~1.5 weeks, low-priority but high-confidence feature (low risk, high user confidence gain).

---

## Survivor 4: Live Folder Metrics / Progress Summary

**User Scenario**

User is culling 750 photos from a wedding shoot. After 30 minutes and ~250 deletes/moves, they want to know: "How many are left? How much longer will this take at my current pace?" Currently, they have no sense of progress within the session except "I've been doing this for a while." Goal: provide **progress feedback** to help user pace themselves and estimate session completion time.

**How It Works**

- Compact info bar (top-right corner, bottom bar, or info panel) shows live statistics:
  - **Remaining:** N images (in current folder, not deleted/moved)
  - **Deleted:** N images (in Recycle Bin)
  - **Moved:** N images (to other folders)
  - **Total (session start):** N images
  - Optional: **Deletion rate:** X images per minute (rolling 5-min average)
  - Optional: **Estimated time to finish:** (Remaining / Rate) if rate is stable

- Updates in real-time after each file action (delete, move, undo).
- Can be toggled off in Settings > Display.

**Critique Findings**

✅ **Real scenario:** User progress feedback is important in long culling sessions (500+ images). Knowing "I'm 40% done" or "I'm deleting at 5 files/min, so ~30 min left" helps users make a break decision and stay focused.

✅ **Architecture fit:**
- `ReviewCatalog` already tracks file state (count, which files remain).
- `FileActionService` and `UndoService` already log each action with timestamp.
- Stats are just sums and rates; minimal computation.
- **No changes to any invariants or file action logic.**

✅ **No conflict with existing decisions:**
- Q-R30 (UI feedback): adding a progress widget is consistent with info panels.
- No data model or file action changes.

✅ **Data safety:** Read-only computation.

⚠ **Potential reviewer concern:** "This is nice-to-have but not critical to core workflow speed."
- **Answer:** Correct, it's a confidence/UX feature, not a speed feature directly. However, it **enables faster decisions** by reducing user anxiety ("am I done yet?"). Also, it's low-effort (just sums and rates), so ROI is high.

**Complexity Estimate:** **Small**
- Metrics computation (counts, rates): ~0.5 weeks.
- UI (display, updates, optional rate smoothing): ~0.5 weeks.
- Settings toggle: ~0.2 weeks.

**Total:** ~1.2 weeks, low-priority but low-risk (confidence/UX, not correctness-critical).

---

## Dropped Ideas

### ❌ Batch Selection with Multi-Image Delete/Move
**Dropped because:** Conflicts with INV-4 ("Only one file action runs at a time"). Batch delete/move would either need to:
1. Run N actions serially (slow, defeats batch purpose), or
2. Change INV-4 to allow concurrent batch actions (requires redesign of recovery logic, undo stacking, and journal semantics — too risky for marginal speedup).

### ❌ Smart Keyboard Macros ("Record Action Sequence")
**Dropped because:** 
- Real scenario is weak. Most users don't use macros; they just press keys in sequence (Delete, Down, Delete, Down is 4 keypresses, not much overhead).
- Keyboard shortcuts already fully configurable.
- Adds state-machine complexity for marginal gain.

### ❌ Confidence Ratings with Session Persistence
**Dropped because:** Conflicts with P03 ("Undo limited to current session"). 
- If ratings stored per session, they're lost when app closes (P03 decision, user chose).
- If ratings stored per-folder (as a side JSON file), conflicts with "no metadata files in photo folder" principle.
- Would require overriding P03 decision, which is not justified.

### ❌ Preset Automation Rules ("If JPEG > 10 MB, move to archive")
**Dropped because:**
- Automates away the core activity (manual review/culling). PhotoReview is a **review tool**, not an auto-curation tool.
- Rule-based heuristics are error-prone (wrong threshold = silent data loss).
- User comes to PhotoReview to **decide**, not to delete based on rules.

### ❌ A/B Gesture Control ("Swipe to compare")
**Dropped because:**
- Gesture interaction not standard on Windows desktop (app isn't touch-optimized).
- Compare mode already exists and is accessible.
- Scenario is vague.

### ❌ Folder Bookmarks / Session Resume Across Folders
**Dropped because:** Conflicts with session resume design (Q-R25, INV-9).
- Session resume already exists but is **per-folder** (opening a different folder resets the session).
- Resuming a "snapshot" after the folder's files change (deletes/renames on disk) could diverge from reality.
- Would require architecture redesign of session storage and catalog state.

---

## Summary

**Survived:** 4 ideas (Survivors 1–4)  
**Dropped:** 6 ideas

**Survivors ranked by estimated value / complexity:**

1. **Session Undo Log Sidebar** (Small) — Lowest risk, high confidence gain for users doing bulk deletes.
2. **Live Folder Metrics** (Small) — Low risk, helps users pace long sessions.
3. **Spatial Lookahead Sidebar** (Medium) — Moderate complexity, moderate speedup for large folders.
4. **Hash-Grouped Duplicate Sidebar** (Medium) — Highest value for burst-mode sessions, medium complexity.

**Recommendation for next step:** Start with Survivors 3 and 4 (both Small, low risk). Validate user need via user testing or feedback. Then tackle Survivors 2 and 1 if user demand is confirmed.
