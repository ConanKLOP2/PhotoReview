# ADR 0003: Journal startup reads recent committed moves from the tail

Status: Accepted  
Date: 2026-09-19

## Decision

`OperationJournal.ReadCommittedMoves` uses a full forward scan for journals
smaller than 1 MiB. For larger journals it reads JSONL lines backwards from the
end of the file and returns the 200 most recent committed Move entries. It
remains used by `UndoService`'s in-session fallback fingerprint lookup and by
journal consistency checks; see the P03 amendment below for the startup
Undo-bootstrap caller that used to sit on top of it.

Pending operation reconciliation remains a full scan. That preserves INV-6:
every latest `Prepared` entry is reconciled before recovery, including entries
older than the recent Undo history window. Failed and committed journal records
remain append-only and are never rewritten by this decision.

## Rationale

The Undo stack is bounded to recent user actions at startup, while a full scan
of a growing append-only journal needlessly parses historical terminal records.
Reverse tail reading avoids allocating the complete journal and makes startup
work proportional to the recent history. The 200-entry window matches the
startup recovery contract and is large enough for the user-facing Undo history.

## Threshold and acceptance

The 1 MiB threshold avoids reverse-reader overhead for normal small journals.
The target is less than 100 ms for a 100,000-line journal on the development
machine. The performance test verifies that the result is bounded to 200 recent
committed moves and that the operation remains within the threshold.

## Consequences

Undo history older than the latest 200 committed Moves is not restored at
startup. Pending operations are still fully discovered and reconciled. If the
product later requires an unbounded Undo history, this ADR must be revisited in
favour of compaction or an indexed journal.

## Wiring (review r7, 2026-09-25)

T46d (commit 9a5f7b8) removed the only caller of the reconcile and of
`UndoService.LoadFromJournal`. It is restored as `JournalStartupRecovery.RunAsync`,
started from `App` startup after the instance lock is taken and the window is
shown. The journal scan and file checks run on the thread pool; only the Undo
history is seeded on the UI thread, below Moves the user already made. Pending
entries stamped after startup began (this process's own in-flight actions) are
not reconciled. Entries marked Failed are reported once with an offer to open
the Recovery window. A Prepared entry of ANOTHER running instance (different
folder, same journal) that is mid-flight is not marked Failed: since Q-R27 the
reconcile skips entries whose live marker (`ILiveOperationRegistry`) exists (see
`architecture.md`, "Thao tác đang chạy"). Only the narrow window described in the
FA-01 comment remains; such an entry shows in Recovery with its real file state.

## Amendment: Undo bootstrap removed (P03, 2026-09-27)

The "only the Undo history is seeded on the UI thread" step described above is
gone. `JournalStartupRecovery.RunAsync` no longer takes an `UndoService`/
`IUiScheduler` at all - it only reconciles pending operations now. Reason
(decision P03, [`decisions/P03.md`](../refactoring/decisions/P03.md)): seeding
the Undo stack from the journal at startup let Ctrl+Z move back a file the
user never touched in this session (after a restart, or from another window in
`InstanceMode.PerFolder`). Undo is now strictly limited to actions registered
in the current process. `UndoService.LoadFromJournal`, `ReadStartupHistory` and
`SeedHistory` were deleted as dead code. `OperationJournal.ReadCommittedMoves`
(and its reverse-tail reading, still described above) stays: it is still used
by `UndoService`'s in-session fingerprint fallback and directly by tests that
assert journal consistency.

## Amendment: single-parse reader and compaction (2026-09-28)

- Every full read parses each line once (`JournalLineParser`: options-level enum converters record whether
  Type/State were recognized) instead of `JsonDocument` + `JsonSerializer`; the skip rules are unchanged.
- The journal is no longer strictly append-only: after the startup reconcile (same pool thread),
  `OperationJournal.TryCompact` rewrites a journal of at least 1 MiB when at least 25 % of it can go. Dropped
  are only entries that precede a Committed/Dismissed entry of the same Id and lie outside the last 200
  committed Moves (`JournalCompactionPlan` documents why no read API can observe the difference). Appends of
  other processes are excluded with the existing share-mode contract (a deny-writers handle; appenders retry),
  lines appended after the snapshot are carried over, and the new file replaces the old one with one atomic
  POSIX-semantics rename (`PhysicalJournalCompactionFiles`); a crash before the rename leaves the journal as it was.
