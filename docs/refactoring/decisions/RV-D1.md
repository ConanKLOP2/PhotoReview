---
id: RV-D1
order: 47
summary: |-
  Move undo and group-Move compensation treat the moved file at its destination as unchanged when its size is equal and its write time is within 2 s of the recorded one (FAT rounds to 2 s, exFAT to 10 ms); compares of files that never left their volume stay exact (option A, 2026-10-01, RV-C01).
---

# RV-D1 — "destination unchanged" check for Move undo on a volume that rounds mtime

**Decided:** option A (user, 2026-10-01). Implemented in `fix/rv-fileactions-undo` (RV-C01).

## Problem

Undo of a Move and the compensation of a failed group Move compared the destination file's `LastWriteUtc`
exactly with the stamp the source had before the move. A cross-volume Move onto FAT (2 s) or exFAT (10 ms)
rounds that stamp, so Ctrl+Z always said "destination changed after move" and a failed group Move left the
already-moved members stuck at the destination. `RecoveryFileCheck` already skipped the destination mtime.

## Options

| Option | Rule | Verdict |
|---|---|---|
| **A** | size equal and \|mtime delta\| <= 2 s at every destination-side compare | chosen: works for in-session and journal-restored records; the 2 s window is the coarsest Windows rounding |
| B | re-stat the destination right after the move and fingerprint that stamp | in-session only; a journal-restored undo would still need A |
| C | size only | weakest identity; same-size edits would be undone over |

## Rule as implemented

- `FileActions/FileFingerprint.MatchesMovedDestination(stat, size, recordedUtc)`: `Length == size` and
  `|LastWriteUtc - recordedUtc| <= 2 s`.
- Tolerant (the file is on, or came back from, the move destination): `UndoService.UndoMoveAsync` destination
  check; `UndoService.UndoGroupMoveAsync` "already back" check and the check of the file to move back;
  `FileActionService.RestoreMovedMembers` (compensation); `FileActionService.InspectGroupMember` for Move
  members (a member the compensation put back made a round trip through the destination volume).
- Exact (the file never left its volume): `FileActionService.RestoreMovedMembers` in-flight partial check,
  `InspectGroupMember` for Copy/Recycle, `RecoveryRetryService` source checks, `UndoService.UndoGroupRecycleAsync`.
- Undo journal entries record the stamp of the file as it is now at the moved location, so a Recovery retry
  or reconcile of the undo compares exactly against what really sits there.
- Consistent with the 2026-09-30 RAW decision "group Copy reconcile compares by size only"
  ([raw/PROGRESS](../raw/PROGRESS.md)): both accept the destination volume's stamp rounding; Copy keeps size-only.

## Tests

`UndoServiceTests.UndoMoveAsync_DestinationOnFatRoundedStamp_RestoresFile`,
`UndoServiceGroupTests.UndoGroupMoveAsync_DestinationOnFatRoundedStamp_RestoresAllMembers`,
`CaptureGroupActionRollbackTests.ExecuteGroupAsync_MoveFailsMidway_FatDestination_CompensationRestoresMovedMembers`,
guards `UndoMoveAsync_DestinationSizeChanged_Refuses`, `UndoMoveAsync_DestinationStampMovedBy3Seconds_Refuses`,
`UndoGroupMoveAsync_FatDestinationStampMovedBy3Seconds_Refuses` (the tolerance is bounded). The FAT volume is
simulated by `InMemoryFileSystem.StampOnMove`.
