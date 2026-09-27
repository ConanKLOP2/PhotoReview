---
id: FEATURE-IDEAS-ORGANIZATION
order: 900
summary: Five lightweight feature ideas for photo organization and re-findability that align with PhotoReview's file-system-first philosophy.
---

# Feature Ideas: Organization & Re-Findability

**Date:** 2026-09-28  
**Scope:** Brainstorm proposals for helping users organize and find photos without adding heavyweight database/tagging infrastructure.  
**Philosophy Guard:** All ideas maintain file-system-first approach, minimize persistent state drift, and respect the app's speed/simplicity priorities.

---

## Survivors (5 Ideas)

### 1. Sidecar Folder Notes (`.photoreview.md`)

**User Scenario:**  
User reviews a folder with 500 wedding photos, selects 50 keepers, moves rejects to a subfolder. They want to leave a note for their colleague: "Wedding day 1 — 50 selected, 320 rejected, keep originals here, hi-res exports in \\Archives\\Wedding-2024".

**Mechanism:**  
- App reads/writes a `.photoreview.md` file (or `.photoreview.json` for structured data) stored in each reviewed folder.
- Optional UI: "Folder notes" button in toolbar opens a read-only panel showing notes (click to edit in default text editor), or integrates inline editor.
- Notes file is human-readable and editable in any text editor (Notepad, VS Code, etc.).
- On folder open, display a subtle indicator (icon, tooltip) if notes exist.

**Critique Survived:**
- **Philosophy conflict?** No. File-system-first ✓, lightweight ✓, no database.
- **Sync/correctness risk?** No. It's a plain file on disk; no special sync logic needed.
- **Real gap?** Yes. Users currently lack a way to annotate folders they've reviewed (post-it notes are not reliable; folder rename is too aggressive).
- **OS already does this?** Partially. Folder descriptions exist in Windows but are limited and not portable; sidecar `.md` is more flexible.

**Complexity:** S (Simple)  
**Priority note:** Complements Idea 4 (folder tagging) — together they cover structured (tags) and freeform (notes) metadata.

---

### 2. Folder Bookmarks / Quick Access

**User Scenario:**  
User regularly reviews photos in three nested folders: `D:\Projects\2024\Events\Weddings`, `D:\Archive\Backlog\2023`, and `D:\Work\Product Shots`. Navigating the tree each session is tedious.

**Mechanism:**
- App maintains a "Bookmarks" list (persisted in `AppSettings` alongside existing preferences, or in a small sidecar).
- UI: "Bookmark this folder" button in toolbar (or keyboard shortcut); bookmarked folders appear in a quick-access panel/menu below the folder tree.
- Right-click on bookmark to rename, move up/down, or delete.
- Optional: Show bookmark count as a badge, or auto-collapse the folder tree and expand only bookmarked ancestors.

**Critique Survived:**
- **Philosophy conflict?** Minimal. App-side state (not file-based), but it's UI preference, similar to window geometry and toolbar visibility.
- **Sync/correctness risk?** None. Bookmarks are preferences; if they break (folder deleted), app gracefully skips them.
- **Real gap?** Yes. Windows Quick Access exists but is OS-global and not app-specific; PhotoReview users benefit from dedicated quick-access to their photo folders.
- **OS already does this?** Partially. Quick Access folders exist, but they're global and not optimized for a review app.

**Complexity:** S (Simple)  
**Integration:** Extends existing settings and UI; straightforward to implement.

---

### 3. Smart Collections / EXIF-Based Grouping within Folder

**User Scenario:**  
User has a folder `Mixed Shots` containing 500 photos from two different shooting days (2024-05-15 and 2024-05-16). The files are listed in jumbled filesystem order. User wants to see them grouped by capture date (or camera model, or exposure) to understand the shoot structure without creating subfolders.

**Mechanism:**
- New "Group by" dropdown in the toolbar: `Off`, `Capture Date (Month)`, `Capture Date (Day)`, `Camera Model`, `Exposure`, `ISO` (or custom EXIF fields).
- When grouping is active, files are displayed in collapsible groups (e.g., "2024-05-15" with 230 photos, "2024-05-16" with 270 photos).
- Groups are computed on demand from EXIF metadata (cached for the session to avoid repeated reads).
- Clicking a group header collapses/expands it; navigation within a group skips between groups (or stays within if an option is set).
- **Not persisted to folder** — just a temporary view per session.

**Critique Survived:**
- **Philosophy conflict?** No. File-system-first ✓; grouping is a view layer, not persistent or database-backed.
- **Sync/correctness risk?** No. Groups are computed on-the-fly and transient.
- **Real gap?** Yes. Users often struggle with mixed-date folders in a single shoot. Windows Explorer can group by EXIF but is slow and clunky; PhotoReview can do this efficiently since it already reads EXIF for other features.
- **OS already does this?** Partially. Windows Explorer supports EXIF grouping but is not optimized for large folders; PhotoReview's lightweight EXIF parsing is faster.

**Complexity:** M (Medium)  
**Performance consideration:** EXIF reading must be cached and optional (not automatic for every folder to avoid IO cost). Add a "load all EXIF" button or lazy-load-on-group-click.

---

### 4. Folder Tagging Convention Helper

**User Scenario:**  
User categorizes folders by adding semantic tags: `#wedding`, `#archive`, `#delete-after-review`, `#2024-editing`. They want to:
- Quickly visually scan folder names for tags in Explorer and the app.
- Optionally auto-tag folders by EXIF (e.g., tag by capture year: `2024-Wedding-[auto-2024]`).
- Remove/rename tags without manually editing folder names.

**Mechanism:**
- App documents a folder-naming convention: `FolderName [#tag1 #tag2]` at the end of the folder name.
- UI: Right-click folder → "Add tag" → offer suggestions (user's previous tags, system defaults like `#archive`, `#delete`, `#review`).
- Visual: Folder names with tags are highlighted in the folder tree (e.g., tags shown in a different color or as badges).
- Optional: "Find folders with tag" search (filter folder tree to show only folders matching one or more tags).
- Auto-tag option: "Auto-tag by year" → adds `#2024`, `#2025` to folder names based on EXIF majority date in each folder.

**Critique Survived:**
- **Philosophy conflict?** No. File-system-first ✓✓✓ (uses standard folder names, not hidden metadata); lightweight ✓; OS-native ✓ (tags are visible in Explorer).
- **Sync/correctness risk?** None. Folder names are on disk and authoritative.
- **Real gap?** Yes. Users want semantic categorization without renaming entire folders or creating a database.
- **OS already does this?** Partially. Windows folder names are freeform; this is a convention + UI helper to make it efficient and visual.

**Complexity:** S (Simple) for basic tagging; M (Medium) if including auto-tag and visual highlighting in the tree.

---

### 5. Recently Reviewed Folders Quick Jump

**User Scenario:**  
User was reviewing photos in `D:\2024-08\Vacation\Hawaii` yesterday and did significant work (moved 200 files, made notes). Today they return to the app and want to jump back to the same folder without navigating the tree.

**Mechanism:**
- App tracks recently-opened folders in the session journal (or a small persistent history, e.g., last 20 folders).
- UI: "Recent folders" dropdown in the toolbar (or keyboard shortcut like `Ctrl+Shift+H`).
- Click to jump to that folder; if it no longer exists, app shows a message and removes it from the list.
- Optional: Show last-opened timestamp or number of items in the folder (read from cache if available).

**Critique Survived:**
- **Philosophy conflict?** No. Session-based, lightweight ✓; extends existing journal tracking.
- **Sync/correctness risk?** None. Folder history is a convenience feature; missing or deleted folders are gracefully skipped.
- **Real gap?** Yes. Users often work on the same folders across multiple sessions; current app requires re-navigating the folder tree each time.
- **OS already does this?** No. Windows File Explorer doesn't track per-app recently-opened folders.

**Complexity:** S (Simple)  
**Integration:** Extends existing session journal; minimal UI changes.

---

## Dropped Ideas (Why)

### ❌ Folder Color/Category Labels (Session or Sidecar)
- **Why dropped:** Folder naming convention with tags (Idea 4) is more powerful and OS-native. Color labels alone add visual clutter without semantic meaning. Users can use folder-name tags instead (e.g., `#archive`, `#review-done`).

### ❌ Search Across Folder Tree (Recursive Regex + EXIF)
- **Why dropped:** 
  - Breaks the app's single-folder-at-a-time design philosophy.
  - OS tools (Windows Search, Everything) already excel at this.
  - Contradicts core principle: "minimize disk reads" — recursive EXIF scanning of thousands of files would be expensive.
  - Too much scope creep for a lightweight app.
  - **Alternative:** Users can use Explorer's built-in search or Everything; PhotoReview focuses on fast review of one folder at a time.

### ❌ Folder Snapshot / Per-Folder State Preservation
- **Why dropped:** 
  - Nice-to-have but lower priority than other ideas.
  - Session journal already tracks global state (zoom, position, sort); extending it per-folder adds complexity (where to store per-folder snapshots?).
  - Risk: If user renames folder or moves files, snapshots become stale.
  - **Compromise:** Consider as a future enhancement after Ideas 1–5 are stable.

### ❌ Metadata Sidecar (Extended / Structured)
- **Why dropped:** 
  - Scope creep. Idea 1 (folder notes) covers the common case.
  - If structured metadata is needed (photographer, location, copyright), consider a single `.photoreview.json` per folder, but this is best left to user—they can create it manually or extend Idea 1 later.
  - Risk: Complex sidecar schema could diverge from actual files and create sync/correctness issues.

---

## Summary: What to Propose

| Feature | Complexity | Priority | Rationale |
|---------|-----------|----------|-----------|
| Sidecar Folder Notes (Idea 1) | S | High | Covers annotation use case; file-system native; zero sync risk. |
| Folder Bookmarks (Idea 2) | S | High | Quick access to favorite folders; low friction; extends existing settings. |
| EXIF-Based Grouping (Idea 3) | M | Medium | Solves mixed-date folder problem; requires careful performance tuning (lazy EXIF load). |
| Folder Tagging Helper (Idea 4) | S–M | High | OS-native, powerful, lightweight; enables semantic categorization without renaming folders. |
| Recent Folders Quick Jump (Idea 5) | S | Medium | Convenient; extends existing session tracking; low effort. |

**Recommended first batch:** Ideas 1, 2, 4 (all Simple, high value, minimal risk).  
**Future phase:** Ideas 3 (requires EXIF caching strategy) and 5 (depends on session/journal architecture review).

---

## Notes for Implementation

1. **Sidecar naming:** Use `.photoreview.md` (markdown, human-readable) or `.photoreview.json` (structured). Avoid dot-leading Windows hide behavior if documentation is meant to be visible in Explorer.
   
2. **EXIF grouping caching:** Do not default to automatic grouping (IO cost). Implement as opt-in; cache results per session to avoid repeated disk reads.

3. **Tag convention:** Document clearly (e.g., "Folder naming: `Name [#tag1 #tag2]`") so users can adopt it even without app UI support.

4. **Settings persistence:** Use existing `AppSettings` for bookmarks and recent-folders lists; store in `%LOCALAPPDATA%\PhotoReview\settings.json` (already done for other settings).

5. **Conflict with file operations:** All ideas use sidecar files or app settings—no risk of interfering with photo delete/move operations (which use Recycle Bin and journal).
