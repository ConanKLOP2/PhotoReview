---
id: Q-RAW-COMPARE-GROUP
order: 46
summary: |-
  With Compare open on a JPG+RAW capture, Delete/Move act on the whole capture; provisional agent-chosen safe fix (not yet user-confirmed): always show a confirmation that names both files, regardless of ConfirmBeforeDelete.
---

# Q-RAW-COMPARE-GROUP — Delete/Move from Compare on a capture pair (2026-09-30)

**Status:** provisional. Chosen by the fixing agent as the smallest change that removes the data-loss surprise without silently
changing semantics; the user has not yet decided between the options below.

## Context

Compare shows one member of a capture (e.g. the JPG) beside another photo, and `CompareViewModel.SelectedPath` is that single
member. Delete/Move look the entry up with `ReviewCatalog.Find(memberPath).CaptureGroup`, which returns the WHOLE capture, so the
paired RAW (and XMP) goes too. `ConfirmBeforeDelete` defaults to `false`, so both files reached the Recycle Bin with no prompt
and nothing on screen said the RAW was included.

## Options

| Option | Behaviour | Pros | Cons |
|---|---|---|---|
| A. Act on both (status quo) | Capture always moves/deletes together | Consistent with non-Compare use; no orphan RAW/JPG | Surprising from Compare (only one file is visible); silent by default |
| B. Act on the selected member only | Compare Delete/Move touch just `SelectedPath` | Matches what is on screen | Splits a capture (orphan RAW/XMP, a "pair" of one), needs partner-degrade logic, undo and catalog rules; larger behaviour change |
| C. Always confirm (chosen) | Keep A, but from a Compare selection always show a confirmation naming every file of the capture, even with `ConfirmBeforeDelete` off | Small, testable, no semantic change, no data loss without an explicit yes | One extra click for Compare + pair users; the wording must stay accurate |

## Chosen

C, implemented in `FileActionController.ExecuteFileActionCoreAsync` (`fromCompareGroup`): Recycle and Move only (Copy is
non-destructive), keys `dialog.confirmCompareGroupRecycle.message` / `dialog.confirmCompareGroupMove.message`. The permanent-delete
prompt still takes precedence when a member lacks a Recycle Bin. Declining changes nothing.

## Residual risk

- A Move action that already has `Confirm` set asks twice from Compare (the generic prompt, then this one).
- Delete/Move from Compare still act on the whole capture; if the user wants "selected member only" (B) that is a separate,
  larger change.
- Undo restores the whole capture as one entry, as for non-Compare use.
