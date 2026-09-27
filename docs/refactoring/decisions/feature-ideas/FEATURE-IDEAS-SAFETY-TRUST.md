---
id: FEATURE-IDEAS-SAFETY-TRUST
order: 30
summary: |-
  Brainstorm session: 4 feature ideas to help users trust delete/move actions by making mistakes cheaper to recover from, without adding confirmation friction.
---

# Feature Ideas: Building Trust in Delete/Move Actions

**Goal:** Help users trust the app more with irreversible-feeling actions (delete/move hundreds of files per session) by making mistakes cheaper/easier to recover from or making users feel more in control—without adding confirmation dialogs that contradict the app's speed priority.

**Date:** 2026-09-28  
**Context:** PhotoReview prioritizes review speed (AGENTS.md, Q-R19-22). Confirmation frequency is already tuned (`Move/Copy-to asks each time` per Q-R19..R22). Users perform bulk actions; mistakes should be recoverable, not prevented by friction.

---

## Survivor Ideas (4 / 4)

### 1. Session-End Summary Card

**User scenario:**  
The user has spent 30 minutes reviewing photos: deleted 47 items (2.3 GB), moved 12 to "Backup", copied 5 to "Archive". They close the folder and see a brief summary notification: "Session complete: Deleted 47 items (2.3 GB), Moved 12, Copied 5." They can glance at it to verify the session went as intended, then dismiss it or proceed.

**Rough mechanism:**
- Track deletions, moves, and copies in a lightweight session-scoped accumulator (updated on `FileActionService.ExecuteAsync` success).
- On folder close, display a non-blocking toast/card with counts and total size per action type.
- Optional detail mode: click to expand and see a short list of the top affected destinations.

**Critique survived:**
- Does NOT conflict with Q-R9 (not touching Recycle Bin, only summarizing).
- Does NOT add friction (informational, auto-dismisses after 5 seconds).
- Does NOT require guessing user intent (just reporting what happened).
- Zero data-safety risk (read-only summary).

**Complexity:** S (tracking stats + UI toast)

---

### 2. Action History Sidebar with Search

**User scenario:**  
The user realizes they may have made a mistake 10 minutes ago but is not sure exactly which action. They click the "History" sidebar toggle. A panel opens showing the last 50 actions chronologically: timestamps, action types (Delete/Move/Copy), file counts, and destination folders. They search for "vacation" to find actions involving vacation photos, spot the mistaken move ("Moved 8 vacation photos to Backup" at 14:32), click the "Undo" button next to it, and the move is reversed. They navigate to one of the photos by clicking the action, confirming it's back in the source folder.

**Rough mechanism:**
- Maintain an in-memory `SessionActionLog` appending entries from `FileActionService.ExecuteAsync` (with action type, file count, destination, timestamp, file paths).
- Display in a collapsible sidebar with chronological list.
- Implement search filter (file name, destination folder) and action-type filter (Delete/Move/Copy).
- Each action row has an "Undo" button (calls `UndoService.UndoMoveAsync` for move actions, reverses delete or copy).
- Clicking an action navigates the catalog to the first affected photo (if it still exists).

**Critique survived:**
- Does NOT conflict with P03 ("Undo limited to current session"—this IS per-session, scoped to the opened folder).
- Does NOT add friction (collapsible, optional, non-blocking).
- Does NOT require guessing user intent (user chooses what action to undo or inspect).
- Zero data-safety risk (history is informational, undo uses existing `UndoService`).

**Complexity:** M (session action tracking, sidebar UI, search/filter, per-action undo)

---

### 3. Undo-Toast with Quick-Restore Menu

**User scenario:**  
The user deletes 5 photos. Immediately, a non-blocking toast appears at the bottom right: "Deleted 5 items — [Undo] [Show Recent]". They realize 0.5 seconds later that one of them should not have been deleted. They click "Undo" and the 5 items are restored. Alternatively, if they wait and switch folders, the toast auto-dismisses. Later, they realize they accidentally deleted a different photo in a previous action. They click "Show Recent" on the toast (or a quick-access menu in the toolbar) and see a small popup with the last 10 deleted items (thumbnails or file names with size/timestamp). They find the one they want and click "Restore", which moves it back from the Recycle Bin.

**Rough mechanism:**
- On delete success, show a 5-second dismissible toast with undo and quick-restore options.
- "Undo" button calls the standard `UndoService.UndoMoveAsync` path (fastest, one-step recovery).
- "Show Recent" menu (or separate quick-access button in the toolbar) displays a small popup with the last 10 deleted items and a "Restore" button for each (which moves each from Recycle Bin back to the source folder).
- Track deleted items and timestamps in `SessionActionLog`.

**Critique survived:**
- Does NOT conflict with Q-R9 (items are in Recycle, we're just moving them back out).
- Does NOT add friction (toast is non-blocking, user-dismissible, auto-expires).
- Does NOT require guessing user intent (user decides whether to undo or show recent).
- Minimal data-safety risk (undo and restore-from-recycle are existing operations).

**Complexity:** S-M (toast UI, quick-restore menu, session item tracking)

---

### 4. Session Action Journal Export

**User scenario:**  
The user finishes a long editing session and wants to keep a record for auditing. They click "Export session log" in the session summary (or in a menu). A JSON file is downloaded with every action performed: timestamp, action type, file names, source folder, destination (if applicable), file size. Later, they import this into a spreadsheet to track which photos went where, or they archive it as proof of what they did.

**Rough mechanism:**
- After `SessionActionLog` is complete (on folder close), offer an "Export session log" button (in toast, summary card, or toolbar).
- Format as JSON or CSV: `[{timestamp, action, fileCount, totalSize, filePaths[], destination, result}]`.
- User can download and keep the file for record-keeping, auditing, or recovery planning.
- Optional: include a fingerprint or hash of affected files for forensic matching.

**Critique survived:**
- Does NOT conflict with Q-R9 (no Recycle Bin operations, just exporting data).
- Does NOT add friction (optional export, off the critical path).
- Does NOT require guessing user intent (user chooses to export).
- Zero data-safety risk (read-only export).

**Complexity:** S (format and serialize `SessionActionLog`, download dialog)

---

## Dropped Ideas (5 reasons for rejection)

### Rejected: Soft-Delete Mode for the Session

**Mechanism:** A Settings toggle: "Soft delete in this session"—all deletes route to a temporary session-specific folder (e.g., `.PhotoReview/Session-<date>/Deleted`) instead of the Recycle Bin. At session end, user reviews and permanently deletes or restores them.

**Rejection reasons:**
1. **Conflict with F-WIN-2 durability contract** (ADR 0007 amendment): F-WIN-2 guards deletion journaling—if the Recycle Bin is off/too small/unreadable, deletion is refused before journaling to prevent silent permanent deletes. Routing to a session-scoped folder bypasses this guard and could mask a data-loss scenario.
2. **Data-safety risk:** If the app crashes mid-session, the soft-deleted files are orphaned in `.PhotoReview/Session-<date>/Deleted` and the user won't know to recover them. The Recycle Bin is a well-known OS convention; a custom folder is not.
3. **Architectural complexity:** Requires changes to `FileActionService`, session cleanup, and journal interaction; not a low-friction improvement.

### Rejected: Anomaly Detection ("That's Unusual" Warnings)

**Mechanism:** Detect if the user deletes >100 files in 10 seconds or >3× their typical per-minute rate, then show a subtle warning: "You've deleted 150 items in 8 seconds—that's 3× faster than usual. Tap to review?" with a dismiss button.

**Rejection reasons:**
1. **Requires guessing user intent:** The warning assumes bulk deletion is a mistake, but many users intentionally batch-delete (e.g., cleaning up rejected photos). A warning interrupts their intended workflow and violates the speed priority.
2. **Risk of false positives:** Legitimate cleanup workflows (e.g., "delete all duplicates > 5MB") would trigger warnings repeatedly, training the user to ignore them.
3. **Adds friction:** Even a dismissible warning degrades the experience for power users who delete intentionally in bulk.

### Rejected: Persistent Undo Across Folder Switches

**Mechanism:** Extend the undo stack to survive folder switching, so `Ctrl+Z` works even after the user closes Folder A and opens Folder B.

**Rejection reason:**
1. **Violates P03 decision:** P03 explicitly limits undo to the current session (single folder opening). The user decided undo should not span restarts or different folders/windows. Extending undo across folder switches contradicts this.

### Rejected: Trash Preview on Delete

**Mechanism:** When deleting, briefly show a 2-second thumbnail preview of the items being deleted to catch mistakes (e.g., "oops, mom's photo is in there").

**Rejection reasons:**
1. **Redundant:** The user sees photos in the catalog before deleting them; a preview after the fact doesn't prevent mistakes.
2. **Violates speed priority:** Loading thumbnails for large selections is slow and blocks the UI thread, contradicting AGENTS.md's "Prioritize Review Speed" principle.

### Rejected: Auto-Save Folder Snapshots for Session Comparison

**Mechanism:** Save a lightweight snapshot (file list + metadata) each time the folder opens. On next session, show a comparison: "3 files deleted outside the app since last session."

**Rejection reason:**
1. **Weak recovery value:** This addresses external changes (files deleted by Explorer, synced folder tools), not in-app mistakes. Does not directly solve the trust problem for actions the user initiated in the app.

---

## Summary

| Idea | Name | Complexity | Why Survivor |
|---|---|---|---|
| 1 | Session-End Summary Card | S | Lightweight verification; zero friction. |
| 2 | Action History Sidebar | M | Complete visibility and per-action undo within session. |
| 3 | Undo-Toast + Quick-Restore | S-M | Catches instant regrets; non-blocking; recovers deleted items fast. |
| 4 | Session Journal Export | S | Audit trail; record-keeping; no data-safety risk. |

**Net result:** 4 strong, non-overlapping ideas. All favor recovery and visibility over prevention. None add confirmation dialogs or conflict with the speed priority, Q-R9, P03, or F-WIN-2.
