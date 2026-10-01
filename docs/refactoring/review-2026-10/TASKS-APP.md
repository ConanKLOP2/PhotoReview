# Review 2026-10: App tasks (RV-A*)

Conventions, gates and PR mapping: [`PLAN.md`](PLAN.md). Paths are under `src/PhotoReview.App/` unless stated;
tests under `tests/PhotoReview.App.Tests/` (WPF window tests in `tests/PhotoReview.Integration.Tests/`, `Category=UI`).
Line numbers at `151f4964`.

---

## PR 5 `fix/rv-fileaction-gate` **[strong]** — RV-A01

### RV-A01 — `RunQueuedAsync` starts the work while holding the lock · MED · PLAUSIBLE
- **Where:** `Coordinators/FileActionGate.cs:94-105` (`RunQueuedAsync`), `:107-127` (`RunAfterAsync`).
- **Problem:** when `_tail` is already complete, `RunAfterAsync(_tail, work)` runs `work` synchronously up to its first
  incomplete `await` INSIDE `lock (_lock)` and BEFORE `_tail = mine`. Consequences:
  1. Re-entrancy: if the queued action shows a modal confirmation (nested dispatcher loop) before its first await, a
     second `RunQueuedAsync` from that loop re-enters the Monitor on the same thread, sees the OLD completed `_tail`,
     and runs concurrently → INV-4 (serial, FIFO file actions) broken.
  2. Any other thread calling `IsHeld`/`TryEnter`/`WhenReleasedAsync`/`Exit` blocks until the dialog closes.
- **Step 0 — confirm the trigger:** read `FileActionController` call sites of `RunQueuedAsync` and record in the PR
  whether any queued work shows `ShowConfirmation` (or any `ShowDialog`) before its first real await. The bug is real
  for the gate regardless (item 2); item 1 decides the severity note.
- **Step 1 — reproducer tests** (`Coordinators/FileActionGateTests.cs`, no timing, no dispatcher needed):
  - `RunQueuedAsync_WorkReentersSynchronously_SecondActionWaitsForFirst`: work A, in its synchronous prefix, calls
    `gate.RunQueuedAsync(B)` and records; then awaits a `TaskCompletionSource` the test controls. Assert B has NOT
    started; complete A's TCS; assert B runs after A finished (order list = A-start, A-end, B-start).
  - `IsHeld_FromOtherThreadWhileQueuedWorkInSyncPrefix_DoesNotBlock`: work A blocks its synchronous prefix on a
    `ManualResetEventSlim`; from `Task.Run` call `gate.IsHeld` and assert it returns (use `Wait(TimeSpan)` on the probe
    task with a generous bound purely as a hang guard, then release A). Must fail (time out) on current code.
- **Step 2 — fix:** never invoke user work under the lock:
  ```csharp
  public Task<bool> RunQueuedAsync(Func<Task> work)
  {
      ArgumentNullException.ThrowIfNull(work);
      Task previous; TaskCompletionSource<bool> mine;
      lock (_lock)
      {
          if (_held != 0) return Task.FromResult(false);
          _queueLength++;
          previous = _tail;
          mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          _tail = mine.Task;
      }
      _ = RunAfterAsync(previous, work, mine); // completes `mine` (result/exception) in its finally
      return mine.Task;
  }
  ```
  `RunAfterAsync` keeps the "await previous, swallow its exception" rule, then runs `work`, sets `mine`'s
  result/exception, and decrements `_queueLength` under the lock. Exceptions must still propagate through the
  returned task exactly as today (existing tests cover this; keep them green).
- **Step 3:** mutation: move `_tail = …` back after the work start → test 1 fails; wrap the work start in the lock → test 2 fails.
- **Done when:** all existing `FileActionGateTests` and `FileActionController*Tests` green; strong-model review of the diff.

---

## PR 7 `fix/rv-app-coordinators` (sonnet; A05/A06 reviewed by strong) — RV-A03..A10

### RV-A03 — Plain-key shortcuts ignore modifiers · MED · PLAUSIBLE · needs RV-D3
- **Where:** `Input/ShortcutRouter.cs:191-229` (`TryResolve`). Only Undo (`:146`), OpenFolder (`:164`) and
  Move/CopyToFolder (`:265`) look at `modifiers`. Shortcut settings store a bare key (no modifier syntax), so "bound
  modifiers" is always None. Alt combos never match today: WPF reports them as `Key.System` and only the Fullscreen
  check uses `systemKey` (`:105-111`), so Alt+F4 does not trigger the default F4 action.
- **Today (defaults):** Ctrl+Delete / Shift+Delete → Recycle; Ctrl+Enter → action "Group-2" (Move); Ctrl+F3/F4 → Move
  actions; Ctrl+F5 → Backup (Copy); Ctrl+Space → Skip; Ctrl+C → Compare; Ctrl+F → ToggleFit; Ctrl+W/H → FitWidth/Height;
  Ctrl+Add/Subtract → zoom; Ctrl+Right/Left → Next/Previous (arrow pan runs only with no modifier, `MainWindow.xaml.cs:538`).
- **Step 1** (`Input/ShortcutRouterTests.cs`, option A): theory over Recycle and each default action key ×
  {Ctrl, Shift, Ctrl+Shift} → `TryResolve` returns null. Pin the unchanged ones: with Ctrl, Next/Previous/ZoomIn/ZoomOut/
  ToggleFit still resolve; Shift+M → MoveToFolder(forcePicker: true); Ctrl+M → null; Alt+F4 (`Key.System`, systemKey F4) → null.
- **Step 2:** for Recycle and the action loop (`:191-203`) require `modifiers == ModifierKeys.None`. Keep Undo/OpenFolder
  and the Move/Copy-to-folder block unchanged.
- **Step 3:** add the missing router tests from the review: same key bound to two commands (priority order),
  `hasImage=false` for each group, Compare key when `!isCompareVisible && !hasComparePair` (no fall-through).

### RV-A04 — Post-present stat failure leaves status "Loading" · LOW · CONFIRMED
- **Where:** `Coordinators/ImagePresenter.cs:582-584` (`PresentCoreAsync`, the non-RAM post-present stat).
- **Problem:** on `Missing`/`Error` the method returns before `UpdateStatus` and before the session save.
- **Step 1:** `ImagePresenterTests.StatErrors` partial (or the file holding stat tests):
  `PresentAsync_PostPresentStatMissing_StatusIsReadyAndSessionSaved` and `..._StatError_...`. Use the existing stat
  seam of the presenter tests (deterministic; do NOT rely on the real toucher thread — see the known flaky test
  `PresentAsync_WhenSourceChangesDuringDecode_ShowsLocalizedImageError`).
- **Step 2:** on a non-Found result: `UpdateStatus(Ready(...))` using the size known before the stat, save the session,
  then run the existing missing-file handling. Decide and test whether the missing file is removed now or on next present.

### RV-A05 — Superseded folder load lets exceptions escape · LOW · PLAUSIBLE
- **Where:** `Coordinators/FolderLoadCoordinator.cs:319` (`catch (Exception) when (!cancelled && IsFolderCurrent)`).
- **Step 1:** `FolderLoadCoordinatorTests.Races` area: start load A, start load B (A superseded), make A's sink
  `OnCatalogReady` throw → `await loadA` completes without exception; B unaffected; an error is logged.
- **Step 2:** add `catch (Exception ex) when (!IsLoadCurrent(token))` → log at Warn ("superseded load failed") and return.
  Keep `OperationCanceledException` behaviour unchanged.

### RV-A06 — Superseded load overwrites `ReadabilityProbe` · LOW · CONFIRMED
- **Where:** `Coordinators/FolderLoadCoordinator.cs:331` (finally assigns `_readabilityProbe`).
- **Step 1:** A superseded by B, B's probe set, then A unwinds → `coordinator.ReadabilityProbe` is still B's task
  (reference equality). Use a gate in the fake scanner to let A unwind after B started.
- **Step 2:** assign only when `ReferenceEquals(_loadCts, thisLoadCts)` (the load token is still current).

### RV-A07 — Folder scan ignores cancellation · LOW · PLAUSIBLE
- **Where:** `Coordinators/FolderLoadCoordinator.cs:133-165`; `IFileSystem.EnumerateFilesWithStat` has no token.
- **Step 1:** fake file system that yields entries one by one and counts them; cancel after 10 → enumeration stops
  within a few entries (assert count < total), LoadAsync(A) returns promptly.
- **Step 2:** check `loadToken.ThrowIfCancellationRequested()` in the enumeration predicate/loop (no interface change
  needed); if the interface must change, add an optional `CancellationToken` parameter with default.
- **Note:** keep the scan single-pass (AGENTS.md rule 1: minimize disk reads).

### RV-A08 — Sibling navigation loops forever on direction 0 · LOW · PLAUSIBLE
- **Where:** `Coordinators/SiblingFolderNavigator.cs:44` and `:110` (`for (...; i += direction)`).
- **Step 1:** new `Coordinators/SiblingFolderNavigatorTests.cs`: direction 0 and 2 → `ArgumentOutOfRangeException`.
- **Step 2:** `if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction))` (same as
  `SiblingFolderService.GetTarget`).
- **Step 3 (tests from review, same class):** generation changes during the background search → no `OpenFolder`;
  unreadable sibling (IOException/UnauthorizedAccess) skipped; root folder (no parent) → no-op.

### RV-A09 — "Moved to …" status raced by the next present · LOW · PLAUSIBLE
- **Where:** `Coordinators/FileActionController.cs:645-653` (`MoveOrCopyToFolderAsync`), fire-and-forget
  `presentTask` in `ExecuteFileActionCoreAsync`.
- **Step 1:** `FileActionControllerOrderingTests.MoveToFolder_NextPresentStillRunning_FinalStatusIsMovedTo`: presenter
  fake whose present completes after the controller writes its status (controlled TCS).
- **Step 2:** expose the present task from `ExecuteFileActionCoreAsync` and await it before writing the final status
  (do not block navigation: the await is inside the already-async action). Alternative if awaiting changes ordering
  tests: write the status through a "sticky" status API that a Ready status does not overwrite for one present.
  Pick the first unless an existing ordering test forbids it; record the choice.

### RV-A10 — Duplicate batch silent when the folder changed · LOW · PLAUSIBLE · needs RV-D5
- **Where:** `Coordinators/DuplicateCleanupController.cs:205-228` (`RemoveDuplicatesAsync` loop).
- **Step 1:** `DuplicateCleanupControllerTests.RemoveDuplicates_FolderChangedDuringBatch_ReportsLateCompletion`:
  switch folder after the 1st of 3 recycles → a late-completion status with the count is reported.
- **Step 2 (option A):** reuse the late-status sink pattern of `FileActionController.ReportLateCompletion`;
  keep the batch non-undoable and say so in a code comment + `decisions/RV-D5.md`.
- **Step 3:** per-file failure aggregation test (2 ok, 1 IOException → status lists 1 failure).

---

## PR 8 `fix/rv-app-shell` (sonnet) — RV-A02, A11..A17

### RV-A02 — Fullscreen from a maximized window may keep the taskbar · MED · PLAUSIBLE
- **Where:** `MainWindow.xaml.cs:178-184` (`ApplyFullscreenState`).
- **Problem:** already `Maximized` → setting `WindowStyle=None` and `WindowState=Maximized` again is a no-op for WPF's
  work-area sizing; the borderless window may keep the work-area size (taskbar visible).
- **Step 1 — real-machine check FIRST** (Claude runs it on the user's PC, AGENTS.md "Real-machine checks"; screenshot
  with computer-use): Release build, maximize, press the fullscreen key (default F11; F is ToggleFit), check whether the taskbar is covered. If it is
  covered → close as NOT-A-BUG and add only the window test below.
- **Step 2 (if reproduced):** when entering fullscreen from `Maximized`: set `WindowState = Normal` first, then apply
  style, then `Maximized`. Exit path restores `_stateBeforeFullscreen` (unchanged).
- **Step 3:** `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.*` (UI): toggle from Normal, Maximized,
  Minimized; exit restores the prior state; closing while fullscreen saves the pre-fullscreen placement.

### RV-A11 — `ViewerState._fitAxisViewport` goes stale · LOW · CONFIRMED
- **Where:** `ViewModels/ViewerState.cs:233` (`SwapSourceSize`), `:378` (`UpdateViewport` returns early outside Fit).
- **Step 1:** `ViewModels/ViewerStateTests.cs`: FitWidth at viewport 1000 → `UpdateViewport(1900, …)` →
  `SwapSourceSize` to a different pixel size → zoom = 1900·dpi/newWidth.
- **Step 2:** refresh `_fitAxisViewport` in `UpdateViewport` whenever a fit axis is active, before the early return.

### RV-A12 — Sort mode change does not re-sort the open folder · LOW · PLAUSIBLE · needs RV-D4
- **Where:** `ViewModels/MainViewModel.cs:753` (`ShowSettings`, `reloadFolder` set), `Coordinators/FolderLoadCoordinator.cs:98`.
- **Step 1:** `MainViewModelAdvancedTests` (RawSettings partial pattern): change `ImageSortMode` in the settings fake
  → folder reload requested with the current image kept as `initialPath`.
- **Step 2 (option A):** add `old.ImageSortMode != new.ImageSortMode` to the `reloadFolder` condition.
  Option B instead: edit `settings.sortMode.hint` in `en.json`/`vi.json`.

### RV-A13 — `BatchReviewWindow` stats every file on the UI thread · LOW · CONFIRMED
- **Where:** `BatchReviewWindow.xaml.cs:15`, `:20` (`new FileInfo(path).Length`).
- **Fix:** pass sizes in from the caller (the duplicate finder already stat'ed/hashed the files; extend the item model
  with `Length`), remove the per-path `FileInfo`. AGENTS.md rule 1 (minimize disk reads).
- **Test:** UI test that the window builds from supplied sizes with nonexistent paths (proves no stat): no exception,
  sizes shown.

### RV-A14 — `ShowRecovery` swallows a journal read error silently · LOW · PLAUSIBLE
- **Where:** `Services/WpfDialogService.cs:84` (`journal.ReadPendingAndFailedOperations()` unguarded).
- **Fix:** catch `IOException`/`UnauthorizedAccessException` → `ShowError(Tr.<existing recovery read error or new key>)`, log.
- **Test:** new `WpfDialogServiceTests` (UI) with a throwing journal fake → error shown, nothing escapes; null Owner → no throw.

### RV-A15 — Placement write leaves a temp file on failure · LOW · CONFIRMED
- **Where:** `WindowPlacementService.cs:~62` (`WriteAtomically`, `File.WriteAllText(temp, …)` outside the try).
- **Fix:** move the write inside the try so the cleanup deletes the temp file.
- **Test:** `WindowPlacementServiceTests.Save_WriteFails_LeavesNoTempFile` (read-only target dir or injected writer).

### RV-A16 — `MainWindow` never unsubscribes `SettingsStore.Changed` · LOW · CONFIRMED
- **Where:** `MainWindow.xaml.cs:86` (subscribe), `Window_Closed` (unsubscribes only `Localizer.CurrentChanged`).
- **Fix:** store the handler in a field; `-=` in `Window_Closed`.
- **Test:** UI test: open + close a `MainWindow`, raise `Changed` → no handler runs (count via a probe), and the window
  is collectable (`WeakReference` after `GC.Collect`, if the existing harness supports it; otherwise only the count).

### RV-A17 — `TracePresented` Rendering handlers pile up while minimized · LOW · PLAUSIBLE
- **Where:** `Services/WpfPresentationSink.cs:~100` (handler removed on the 2nd `CompositionTarget.Rendering` tick).
- **Fix:** keep at most one pending handler (replace the pending trace instead of adding another); also unhook on window close.
- **Test:** call `TracePresented` 5× without rendering → one subscription (count via an internal test seam).
- **Note:** only active with the perf listener on; keep the change minimal.
