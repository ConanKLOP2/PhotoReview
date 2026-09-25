# ADR 0003: Journal startup reads recent committed moves from the tail

Status: Accepted  
Date: 2026-09-19

## Decision

`OperationJournal.ReadCommittedMoves` uses a full forward scan for journals
smaller than 1 MiB. For larger journals it reads JSONL lines backwards from the
end of the file and returns the 200 most recent committed Move entries. This is
the startup path used by `UndoService.LoadFromJournal`.

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
the Recovery window. Known limit: a Prepared entry of ANOTHER running instance
(different folder, same journal) that is mid-flight at that moment can be
marked Failed; the entry then shows in Recovery with its real file state.
