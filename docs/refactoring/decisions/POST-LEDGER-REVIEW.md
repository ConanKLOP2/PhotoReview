---
id: P01-P03
order: 12
summary: |-
  Post-ledger review of master changes since 3ef2bb5 (#186-#198+) plus screened-static hot-path rows outside Imaging: no defects in the reviewed diff; two document-only journal/undo races found (concurrent-retry misreport, cross-folder Undo contamination).
---

# Post-ledger review: master changes since the 3ef2bb5 audit snapshot + screened-static hot path (2026-09-27)

Scope: (A) every production/test function body added or changed on `origin/master` since the
`FUNCTION-BODY-AUDIT-2026-09-27.tsv` snapshot commit `3ef2bb5` (PRs #186-#198 and later) — none of these had ever
been reviewed. (B) the `screened-static` ledger rows of **production** code in the hot path outside
`PhotoReview.Imaging`: `src/PhotoReview.App/{Coordinators,ViewModels,Input}` and
`src/PhotoReview.Core/{Catalog,FileActions,Session}`, prioritizing navigation/present and file actions/journal/undo.
`PhotoReview.Imaging` rows and the ledger TSV itself were left untouched (another review is deep-reviewing that
domain concurrently). Findings R01-R18 (see `OPEN-DECISIONS.md`'s Decided table) are not repeated here.

## Findings

| ID | File:Symbol | Evidence | Severity | Action |
|---|---|---|---|---|
| P01 | `src/PhotoReview.App/Coordinators/FolderLoadCoordinator.cs`, field `_readabilityProbe` / property `ReadabilityProbe` | If `LoadAsync` throws, is cancelled, or takes the "folder does not exist" branch before reaching `probe = StartReadabilityProbe(...)`, its `finally` (`if (probe is not null) ...`) never reassigns `_readabilityProbe`, so the property keeps describing a *previous, unrelated* load's probe instead of `Task.CompletedTask`. Diagnostics/test-synchronization property only — the stale probe still correctly gates on its own `loadToken` and settles on its own, so no catalog/UI state is affected. | Low | Document only. Not worth an isolated fix: the only consumer is test synchronization, and a "fix" (resetting the field eagerly at the top of `LoadAsync`) would itself subtly change what the property means while an old load's probe is still winding down. Revisit only if a future test needs to `await ReadabilityProbe` across a failed/cancelled load. |
| P02 | `src/PhotoReview.Core/FileActions/RecoveryRetryService.cs::RetryMoveOrCopyAsync`/`ExecuteRetry`, backed by `JournalTransaction.Begin()` | Unlike `OperationJournal.Dismiss` (R09) and `AppendIfStillPending` (FA-01), a retry never re-checks the journal's *current latest entry* for the Id before mutating — it only re-validates the live filesystem (source exists, size/mtime match, destination absent), then unconditionally begins a new transaction. `AppPaths.JournalFile` is one global file shared by every process in `InstanceMode.PerFolder` (review r7 comment in `OperationJournal.cs`), and `ILiveOperationRegistry` (`WindowsLiveOperationRegistry`/`InProcessLiveOperationRegistry`) is **not mutually exclusive** — it only tells *reconcile* "an owner is on it", any number of processes can hold a live marker for the same Id simultaneously. Two processes retrying the same Failed entry can both pass the pre-checks, both begin, both attempt the mutation; the loser's `File.Move`/`Copy` throws (source now gone) and lands in `tx.Fail(ex, ...)`, appending a bare Failed entry (no `ErrorCode`, so `IsReconcileCode` does not special-case it) *after* the winner's Committed line — `ComputeLatestEntries` then reports a Move that actually succeeded as permanently Failed. The file itself is fine; the journal durably misdescribes reality. Not covered by any existing test (`RecoveryRetryServiceTests.cs` has no concurrent-retry case; same-process double-click is already guarded UI-side by `RecoveryWindow._retrying`). | High | Document only — needs design sign-off before implementing. A fix likely needs either (a) a true exclusive per-operation-Id lock before a retry mutates anything, or (b) an `OperationJournal`-level "commit/fail only if the Id's latest entry is still the snapshot passed in" guard (R09-style) applied to `JournalTransaction.Commit`/`Fail` when driven from a retry. Both are journal-semantics/concurrency changes explicitly out of scope for this pass per AGENTS.md (data-safety/journal changes need user sign-off). |
| P03 | `src/PhotoReview.Core/FileActions/UndoService.cs::ReadStartupHistory` | Scans `_journal.ReadCommittedMoves()` — the whole shared global journal, not scoped to the folder this process has open — and seeds the in-memory Undo stack with any Committed Move whose destination still exists and source doesn't. Since journal entries carry no folder/session identifier and the journal file is shared across `InstanceMode.PerFolder` processes, a process opened on folder X can, at startup, load Moves that happened entirely inside a different folder Y's PhotoReview window. Pressing Ctrl+Z in X's window would then move a file that has nothing to do with what's on screen in X, back to Y — a real, successful, unexpected file move (not corruption/loss, but a "did something the user did not intend" surprise). No existing test exercises multi-folder journal sharing for Undo (`UndoService` tests all use a single journal/folder context). | Medium | Document only — needs design sign-off. Fixing this means a journal schema change (tag entries with a folder/session id and filter `ReadStartupHistory` by it), which is a journal-format change and therefore out of scope for a routine PR per AGENTS.md. |

No other findings. Every other screened-static row reviewed in this pass (below) checked out as correct, or was
already covered by an existing decision doc, on manual/semantic review.

## Bodies reviewed, with verdict

### A. Master changes since 3ef2bb5 (PRs #186-#198+), all files under `src`/`tools`/`tests`

All correct; no defects found. Verified directly by the review lead (not delegated):

- `PhotoReview.App/Coordinators/FileActionGate.cs` — `RunQueuedAsync` FIFO queue (Q-T1) added alongside the existing
  `RunExclusiveAsync`; traced the locking discipline by hand (mutual exclusion between the two mechanisms, queue
  chaining via `_tail`, per-action exception isolation) — correct. Confirmed by
  `FileActionGateTests` (`RunQueuedAsync_SecondCallWhileRunning_IsQueuedNotDropped`,
  `RunQueuedAsync_WhileExclusivelyHeld_IsNoOp`, `RunExclusiveAsync_WhileQueuedActionPending_IsNoOp`).
- `PhotoReview.Core/FileActions/OperationJournal.cs::Dismiss` (R09 continuation) — re-checks the current latest
  entry under `_gate` before dismissing, matching the `AppendIfStillPending` (FA-01) pattern; returns
  `DismissOutcome(Dismissed, Skipped)`. Verified against `OperationJournalTests`'s new race test
  (`Dismiss_StaleSnapshotRacedByAConcurrentRetry_SkipsInsteadOfOverwritingTheNewerEntry`) and its normal-path test.
- `PhotoReview.App/RecoveryWindow.xaml.cs` — updated for the `DismissOutcome` signature; rows removed from the list
  on both Dismissed and Skipped (a skipped row is stale either way), user told via a new info dialog when any were
  skipped. Correct.
- `PhotoReview.Core/Session/SessionStore.cs` — startup temp-file sweep moved off the constructor onto a background
  `Task.Run` (`StartupSweepTask`, R14); confirmed `Load`/`Save` never touch `*.tmp` paths and the 1-day age guard
  makes a race with a concurrent `Save()`'s own temp file impossible. Verified against
  `SessionStoreSweepPerfTests` (construction near-instant regardless of stale-file count) and
  `SessionStoreTests.SaveAndLoad_WhileStartupSweepStillRunning_AreUnaffected` (a real concurrency test, not a fixed
  delay).
- `PhotoReview.App/ViewModels/MainViewModel.cs` (R05) — removed the redundant trailing `NotifyNavigationStateChanged()`
  call after `PresentAsync` in `NextAsync`/`PreviousAsync`/`FirstAsync`/`LastAsync`/`SkipAsync`. Traced
  `ImagePresenter.PresentAsync`'s early-return paths by hand: the only path that could theoretically skip both
  `UpdateStatus`/`UpdateCurrentImage` (the out-of-bounds branch in `RemoveMissingCatalogItemAsync`) is unreachable
  given `ReviewCatalog.Remove`'s contract (always returns a clamped `[0, Count-1]` index when `Count > 0`), so the
  claim "`PresentAsync` always notifies before returning, for every reachable index" holds. Pinned by
  `MainViewModelNavigationTests.NextAsync_RaisesNavigationStateChanged_ExactlyThreeTimes`. Also verified
  `RunActionAsync`/`RecycleAsync`/`MoveOrCopyToFolderAsync` moved from `RunExclusiveAsync` to `RunQueuedAsync`
  (Q-T1 wiring) — correct, matches `FileActionGate`'s new contract; `MainViewModelFileActionTests` updated
  accordingly (queued-not-dropped, `Task.WhenAll`).
- `PhotoReview.App/BenchmarkWindow.xaml.cs` (R11) — folder-exists check, enumeration, image-type filter and stat
  pass extracted into a static `EnumerateAndStat` and moved onto a background `Task.Run` with a real cancellation
  poll every 256 entries, so a large/slow/NAS folder no longer blocks the dialog or ignores Cancel. Exception
  mapping order checked (`DirectoryNotFoundException` caught ahead of the generic `IOException` clause, since it is
  a subtype); button re-enable/`Dispose()` in the outer `finally` covers every early-return path. Verified against
  `BenchmarkWindowEnumerateAndStatTests` (unit-level) and `BenchmarkWindowEnumerationUiTests` (real STA window,
  including a Cancel-during-scan test).
- `PhotoReview.Platform.Windows/InstanceForwardClient.cs` (R08) — a pipe error or empty reply *after* the request
  was written now reports `Unknown` instead of `NoInstance` (an accepted-then-dropped connection is
  indistinguishable from "nobody home" at the client). Matches the already-decided R06-R07-R11-R08 reclassification;
  `Client_OwnerHangsUpSilentlyAfterWrite_ReportsUnknown` and
  `Client_OwnerAcceptsThenHangsUpBeforeReplying_ReportsUnknown` both verified.
- `PhotoReview.Benchmarking/PerformanceTestHarness.cs` (R07) — `MeasureParallelAsync`'s semaphore slot is now
  acquired in the loop before each file's `Task.Run`, not inside it, so at most `workers` tasks are ever queued
  concurrently instead of one per file up front. Verified against
  `PerformanceHarnessConcurrencyTests.RunAsync_ManyMoreFilesThanWorkers_NeverExceedsConfiguredConcurrency` (a real
  concurrency-bound assertion using an observer callback + busy-wait, not a timing guess).
- `AppSettings.cs`/`SettingsNormalizer.cs`/`SettingsWindow.xaml.cs` default changes (`ImageSortMode.Default`,
  `KeyboardZoomAnchor.ViewportCentre`, `KineticPanEnabled` off by default) — traced through the normalizer's
  `Enum.IsDefined` repair path, the Settings window's "Defaults" button (now reads `new AppSettings()` instead of a
  hardcoded literal, so it can't drift from the POCO default again), and both `en`/`vi`/`en.notes` localization
  files for the new `recovery.dismiss.stale.*` strings — all consistent with the decided Q-R33 sort-mode default
  change and the earlier `KeyboardZoomAnchor` decision.
- `tools/check-doc-links.ps1` (R12) — added a second pass scanning `src/**/*.cs` and `tests/**/*.cs` comments for
  filenames matching this repo's doc-naming convention and verifying each still exists somewhere in the repo;
  conservative by design (only upper-case-first names). Read the regex/comment-stripping logic by hand; no false-negative
  risk found that would matter (it only under-reports, never mis-reports an existing file as missing).
- `PhotoReview.Core/Diagnostics/{DiagOptions,PhotoReviewPerf}.cs` — doc-comment path fixes only (retargeted stale
  `PERF-DIAGNOSIS-PLAN.md` references to `docs/archive/historical/PERF-DIAGNOSIS-TASKS.md`); no logic change.
- All other test-only diffs in this range (`WarmNavigationReadBoundsTests` TC06 un-skipped now that Q-T1 is
  implemented, `MouseSettingsTests`/`SettingsWindowRoundTripTests`/`AppSettingsTests`/`AppSettingsPocoTests` updated
  for the new defaults, `RecoveryWindowTests` updated for `DismissOutcome`, `JournalStartupRecoveryTests` likewise,
  `PreviewImagePersistQueueBoundTests` new pinned-constant test for the already-decided R06 persist-queue bound) —
  read and confirmed each assertion actually exercises the behavior it claims to (would fail if the guarded code
  regressed), no weak oracles found.

### B. Screened-static hot-path rows reviewed (production code, outside `PhotoReview.Imaging`)

Delegated to four parallel reviews (Sonnet), each independently reading the full files (not just the ledger's
flagged spans), cross-checking `docs/refactoring/decisions/{R01-R02-R03-R13,R04-R14,R06-R07-R11-R08,R09-R10,
Q-R05-Q-R12,Q-R35,Q-R36,Q-R37,Q-R38}.md` first to avoid re-reporting already-decided items, and checking existing
tests before flagging a gap. All four came back clean except for P01 above:

- **Navigation/present coordinators** — `FitViewController.cs`, `FolderLoadCoordinator.cs`, `IFolderLoadSink.cs`,
  `ImagePresenter.cs` (including the ledger's own `attention=concurrency` flags on `GetComparePair` and
  `ZoomDetailLoader.Update`/`LoadAsync`), `ImageTransitionDecision.cs`, `IPreloadController.cs`,
  `PreloadControllerAdapter.cs`, `SiblingFolderNavigator.cs`, `ZoomDetailLoader.cs`. All generation/version-token
  guards traced and confirmed sound (superseded work correctly abandons itself instead of racing current state);
  P01 was the only gap found.
- **File actions/journal/undo core** — `OperationJournal.cs`, `FileActionService.cs`, `UndoService.cs`,
  `JournalTransaction.cs`, `JournalErrors.cs`, `DuplicateFinder.cs`, `RecoveryRetryService.cs`,
  `RecoveryFileCheck.cs`, `JournalStartupRecovery.cs`, `ActionDestinationPolicy.cs`. Produced P02 and P03 above;
  everything else (append/retry-on-sharing-violation, `ComputeLatestEntries`/`AppendIfStillPending`/
  `ReconcilePendingOperations`'s read-latest-under-lock discipline, `ReadCommittedMovesReverse`'s windowed scan,
  `DuplicateFinder`'s survivor logic, `JournalStartupRecovery`'s startup ordering) confirmed correct.
- **File-action/undo orchestration (App layer) + `MainViewModel.cs` navigation/dispatch** —
  `FileActionController.cs`, `DuplicateCleanupController.cs`, and a full read of `MainViewModel.cs` (~1000 lines).
  Confirmed the single command-dispatch boundary (`FileActionGate`), `WaitForPendingExplorerOrderAsync` (INV-9)
  called at every navigation/file-action entry point, and no unhandled-exception crash path. No findings.
- **Input controllers + compare view model** — `PointerInputController.cs`, `KineticPan.cs`, `MouseGestures.cs`,
  `ShortcutRouter.cs`, `ReviewCommand.cs`, `CompareViewModel.cs`, `ComparePairService.cs`. No findings; the
  `ComparePairService.Find`-vs-`BuildIndex` equivalence (the one place with real optimization-vs-correctness risk)
  is already fuzz-tested (`ComparePairFuzzTests`).

Not covered in this pass (out of the explicitly prioritized navigation/present + file-actions/journal/undo scope,
left for a future pass): `StatusFormatter.cs`, `ViewerState.cs`, `InfoOverlayViewModel.cs`, `ExifFormatter.cs`,
`TitleBarFormatter.cs`, `SessionWriter.cs`, `ImageSortService.cs`, `ManagedNaturalComparer.cs`,
`ExplorerSnapshotValidator.cs`, `SourceSizeTracker.cs`, `ToolbarAutoHidePolicy.cs`/`InfoOverlayAutoHidePolicy.cs`,
`DragDropInputService.cs`, `CatalogEntry.cs`, and a few one-line interface members — none of these sit on the
navigation/present or file-action/journal/undo critical path the task asked to prioritize.
