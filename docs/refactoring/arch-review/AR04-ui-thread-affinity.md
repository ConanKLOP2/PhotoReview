# AR04 — Enforce UI-thread affinity in the App layer (ADR 0005)

**Finding:** F5 · **Decision:** Q-AR2 (accept ADR 0005) · **Branch:** `refactor/ar04-ui-thread-affinity` · **Size:** ~1 day + GUI/perf session · **Prerequisites:** AR02a–c merged, AR02e baseline recorded

## 1. Problem (verified at `fbdf48e`)

- `ReviewCatalog` is documented "not thread-safe, UI thread only" (`src/PhotoReview.Core/Catalog/ReviewCatalog.cs:11`).
- `src/PhotoReview.App` contains **47** `ConfigureAwait(false)`: `MainViewModel` 17, `FileActionController` 8, `ImagePresenter` 8, `FolderLoadCoordinator` 6, `DuplicateCleanupController` 4, `SiblingFolderNavigator` 2, `CompareViewModel` 2.
- Concrete races on the main path:
  - `FolderLoadCoordinator.cs:85` and `:111` — `await Task.Run(scan/sort).ConfigureAwait(false)`; afterwards the same method calls `_sink.ResetCaches()`, `_catalog.Reset(entries)`, `_catalog.SetCurrent(...)`, `_catalog.ReplaceOrder(...)` on a pool thread while key handlers read `_catalog` on the UI thread.
  - `ImagePresenter.cs:208` (`await previewTask.ConfigureAwait(false)`) → subsequent `RemoveMissingCatalogItemAsync` (`:315`, `:331-348`) calls `_catalog.Remove` off the UI thread.
  - `MainViewModel.cs:219,232,244,266` → `NotifyNavigationStateChanged()` raises `PropertyChanged` from pool threads; `:312` `OpenFolderAsync` after an off-thread continuation.
- Side effect: `WpfPresentationSink.InvokeUi` (`Services/WpfPresentationSink.cs`) must use a synchronous `Dispatcher.Invoke` for every update because it cannot know which thread it is on — it blocks a pool thread per call.
- No `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in `src/PhotoReview.App` (checked) → removing `ConfigureAwait(false)` cannot introduce a sync-over-async deadlock today.

## 2. Rule (ADR 0005)

- **App layer (ViewModels, Coordinators, Services, Windows): never `ConfigureAwait(false)`.** Continuations return to the WPF `SynchronizationContext`.
- **Core / Imaging / Platform.Windows: always `ConfigureAwait(false)`** and do their CPU/I-O inside `Task.Run` or truly async I/O. Heavy work must never run synchronously before the first `await` of a method that App calls.
- `ReviewCatalog`, `ViewerState`, `CompareViewModel`, `MainViewModel` state are mutated only on the UI thread.

## 3. Steps

1. **Audit callee prefixes (before removing anything).** For each awaited callee, check that it does not do heavy synchronous work before its first real await (after the change, that prefix would run on the UI thread):
   - `PreviewImageService.GetPreviewAsync` → work is inside `DecodeAndCacheAsync` = `Task.Run` (`PreviewImageService.cs:222`) ✔. `GetOriginalDimensionsAsync` → `Task.Run(ReadInfo)` (`:420`) ✔.
   - `ThumbnailCache.GetAsync` → `File.Exists(cachePath)` (`:133`) runs synchronously before `Task.Run` (`:198`). Already on the UI thread today when `PresentAsync` is entered from a key handler; accept, but move the `File.Exists` into the `Task.Run` if AR02e shows thumbnail stalls.
   - `FolderLoadCoordinator` `Task.Run` bodies (`:~70-85`, `:~95-111`) must not touch `_catalog`/`_sink` (read and confirm).
   - `FileActionController` / `DuplicateCleanupController` → `FileActionService`, `UndoService`, `FileHashService` (hashing must be inside `Task.Run`; verify `FileHashService.cs`).
   Record the audit result table in the PR description.
2. **Remove `ConfigureAwait(false)` file by file**, one commit per file, in this order (low → high risk): `CompareViewModel`, `SiblingFolderNavigator`, `DuplicateCleanupController`, `FileActionController`, `MainViewModel`, `FolderLoadCoordinator`, `ImagePresenter`. Run `dotnet test --filter "Category!=Manual"` after each commit.
3. **Architecture test** `tests/PhotoReview.Architecture.Tests/AppThreadAffinityTests.cs`:
   - `AppLayer_DoesNotUseConfigureAwaitFalse`: scan `src/PhotoReview.App/**/*.cs` (skip `obj/`, `bin/`) for `ConfigureAwait(false)` → empty.
   - `AppLayer_DoesNotBlockOnTasks`: scan for `.Result`, `.Wait()`, `GetAwaiter().GetResult()` → empty.
4. **Debug guard on `ReviewCatalog`** (Core, no WPF): add `[Conditional("DEBUG")] internal void AssertOwnerThread()` + `public void BindToCurrentThread()` storing `Environment.CurrentManagedThreadId`; mutators (`Reset`, `SetCurrent`, `Remove`, `ReplaceOrder`, `UpdateMetadata`, …) call `AssertOwnerThread()`; no-op until bound. `MainViewModelCompositionRoot.Create` (runs on the UI thread) calls `catalog.BindToCurrentThread()`. Unit tests that do not bind are unaffected; integration tests (production graph after AR02) are protected automatically.
5. **Sink visibility:** in `WpfPresentationSink.InvokeUi`, when the `Dispatcher.Invoke` branch is taken, increment a `ReviewMetrics` counter `CrossThreadPresentCount` (and log once when logging is on). Expected value after AR04: 0. Keep the fallback branch (safety net) in this PR.
6. **Perf gate (real machine):** rerun the AR02e scenarios. Accept if `key → present` P95 regresses ≤ 5 % **and** ≤ 1 ms versus the AR02e baseline, hit rate unchanged ±1 pt. Note: continuations are now posted at `DispatcherPriority.Normal` by `DispatcherSynchronizationContext` whereas `Dispatcher.Invoke` ran at `Send`; under heavy input (held arrow key) this can reorder work — that is precisely what the P95 check measures.
7. **GUI acceptance (real machine):** hold → for 10 s on a 500-image folder; delete (Recycle) repeatedly while navigating; delete a file in Explorer while it is being shown (missing-file path, `RemoveMissingCatalogItemAsync`); switch folders quickly (drag-drop twice within 1 s); open a folder while Explorer snapshot is late (INV-7/INV-9); duplicate cleanup on 200 files; Undo after each action. Debug build: no `AssertOwnerThread` failure; Diagnostics: `CrossThreadPresentCount = 0`.

## 4. Docs on completion
- ADR 0005 → Accepted; `docs/architecture.md` gets a "Threading" paragraph.
- `ACTIVE-TASKS.md`: WD01 → DONE via AR04; remove "OC14 → WD01" edge.
- `task_on_progress.md` Critical Process Rules: replace "No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01" with "App layer: no `ConfigureAwait(false)` (ADR 0005)"; delete the deferred "Batch 3 ConfigureAwait" note.

## 5. Risks / rollback
- Hidden synchronous prefix in a callee → UI stall. Mitigated by step 1 and the perf gate.
- Tests without a `SynchronizationContext` behave exactly as before (continuations on the pool either way).
- Rollback: per-file commits allow partial revert; the architecture test can temporarily carry an allow-list with an issue link (never silently).
