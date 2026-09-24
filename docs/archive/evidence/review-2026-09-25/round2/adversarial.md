# Adversarial review, round 2: `review/2026-09-25-integration` vs `master`

Method: read `git diff master...HEAD` (117 files, ~43 commits) in the worktree `.claude/worktrees/integ`. Built `PhotoReview.Core.Tests` and ran the Core suite (non-Native, non-Slow, non-Manual: 558 passed, 0 failed). Nothing touched the real Recycle Bin. Line numbers refer to the integration branch.

Overall: the JournalTransaction refactor (CORE-07) is behaviour-equivalent to master (journal ordering, durability mode, error codes and cancellation all unchanged). No High or Critical finding. The findings below are all Medium or lower.

Summary: 0 High, 4 Medium, 9 Low, 3 Info.

---

## R2-A-01 (Medium): Recovery retry survives closing the window, so the result and journal warning are lost and nothing serialises against other file actions
- Where: `src/PhotoReview.App/RecoveryWindow.xaml.cs:243-277` (`ExecuteRetryAsync`, `Retry_Click`), `src/PhotoReview.App/Services/WpfDialogService.cs:79` (no CancellationToken passed), `src/PhotoReview.Core/FileActions/RecoveryRetryService.cs:66` (`Task.Run(..., ct)`).
- Evidence: the window is modal (`ShowDialog`), but nothing blocks closing it with the X button while the retry runs. After the await, `if (!IsLoaded) return;` returns silently. `RetryMoveOrCopyAsync` is called without a token, so the pool thread keeps copying or moving. The retry does not go through `FileActionService.TryBegin()`, so it is not part of the busy guard shared with FileActionService and UndoService.
- Scenario:
  1. The user retries a multi-minute cross-drive Copy and closes the window.
  2. `ShowDialog` returns and the main window is re-enabled.
  3. The user presses an action key or Undo on the same file, while the retry thread is still moving it. Both operate on the same path with no mutual exclusion.
  4. The user never sees the outcome. This includes the "Succeeded but journal could not be written" warning (`JournalPersisted=false`), which is exactly the case where the user needs to know.
  5. If the app exits mid-copy, the pool thread is a background thread and dies. A Prepared record is left (recoverable by design), but the user gets no message.
- Fix (either option):
  - Cancel window closing while `_retrying` is true, via `OnClosing` with `e.Cancel = true`.
  - Or route the retry through the shared busy guard (`TryBegin` and `End`) and show the result through `IDialogService` when the window is gone.
- Confidence: verified by reading.

## R2-A-02 (Medium): IMG-01 does not purge old flattened alpha entries, so already-cached PNG/WebP previews keep rendering with black backgrounds
- Where: `src/PhotoReview.Imaging/Caching/PreviewCacheFile.cs:50` (`CurrentVersion = 5`, unchanged in this diff), `:241` (`header[7] = 0`), `:204` (the read path assumes an opaque `Bgr32`).
- Evidence: the diff only stops new alpha writes. Entries written by earlier builds have hasAlpha=0 and the same version 5. They pass validation and are served straight from the disk cache.
- Scenario: a user who browsed a folder of transparent PNGs or WebPs before upgrading keeps seeing black-flattened previews until the cache is cleared or pruned away.
- Fix: bump `CurrentVersion` to 6, so old entries are treated as corrupt and re-decoded once. The cache is disposable, so the one-time cost is small.
- Confidence: verified by reading. I did not reproduce it with real files.

## R2-A-03 (Medium/Low): IMG-01 never caches fully opaque RGBA/indexed sources, so PNG/WebP folders re-decode after every RAM-LRU eviction
- Where: `PreviewImageService.cs:486-488` (`!PreviewCacheFile.HasAlpha(bmp)`), `PreviewCacheFile.cs:232-241`, and `WicDirectDecoder.cs:138,192`.
- Evidence:
  - `HasAlpha` is decided by pixel format only.
  - WicDirect emits `Pbgra32` for everything not in `OpaquePixelFormats`. That covers 32bppBGRA/RGBA PNGs and WebP, even when every alpha is 255 (screenshots, most RGBA exports), and 8bpp indexed PNG/GIF without transparency.
  - `HasAlpha` is true for all of these, so none is ever persisted.
- Scenario: a folder of opaque RGBA PNG screenshots gets no disk-cache benefit. Every visit after RAM eviction is a full decode, and the preload hit-rate after restart is 0.
- Q-R1 decided that alpha images are not cached, but the decision presumably assumed alpha meant real transparency.
- Suggested fix: when the format is alpha-capable, scan the (downscaled, at most about 2200 px) bitmap once for `A < 255`, and persist only if no pixel is transparent. Alternatively, cache alpha images in a lossless PNG payload.
- Confidence: verified by reading. The performance impact is an estimate, not measured.

## R2-A-04 (Medium/Low): SessionWriter.Dispose can dispose the semaphore under a drained-but-unwritten timer batch
- Where: `src/PhotoReview.Core/Session/SessionWriter.cs:88-99` (`Dispose`), `:113-137` (`WritePending` and `WriteBatch`).
- Evidence: the timer thread takes the batch in `WritePending` (`_pending.Clear()` under `_gate`), then calls `_writeLock.Wait()` outside the lock. If `Dispose` runs between those two points:
  - `Flush(bounded)` finds `_pending` empty and does nothing.
  - `_writeLock.CurrentCount == 1`, so `Dispose()` disposes the semaphore.
  - The timer thread then calls `_writeLock.Wait()` on a disposed object and throws `ObjectDisposedException` inside `RunTimerAsync`.
  - That exception is unobserved (it ends up in the UnobservedTaskException handler), and the last session write is lost.
- Window: narrow (microseconds), but it coincides with the 500 ms debounce firing at shutdown.
- Fix: do not dispose `_writeLock` (a SemaphoreSlim used without `AvailableWaitHandle` holds no unmanaged resources), or catch `ObjectDisposedException` in `WriteBatch`.
- Related: after a bounded-wait timeout at shutdown, the newer state "b" is dropped while the older in-flight write "a" lands. That is a documented trade-off (Q-R5), not a bug.
- Confidence: verified by reading. I did not reproduce it.

## R2-A-05 (Low): IMG-11 clamps the cache but not the preload scheduler's full-folder threshold
- Where: `src/PhotoReview.App/App.xaml.cs:151` (`fullFolderRamThresholdBytes: settings.ImageCacheCapacityBytes`, unclamped), `src/PhotoReview.Benchmarking/BenchmarkImageExecutor.cs:46`, `PreloadScheduler.cs:284`, and the clamp itself in `PreviewImageService.cs:138`.
- Evidence: on a 16 GB machine the default 16 GiB threshold is unchanged but the LRU is clamped to 8 GiB. `ShouldPreloadWholeFolder` still says yes for a 12 GiB folder, while the LRU holds only 8 GiB.
- Scenario: the preload decodes the whole folder, the LRU evicts, and preload plus memory pressure churns. The memory probe partly mitigates this.
- Fix: expose the effective capacity from `PreviewImageService` (or call `RamBudgetPolicy.ClampToPhysicalMemory`) and use it for the threshold.
- Confidence: verified by reading; the thrash itself is a suspicion.

## R2-A-06 (Low): IMG-11 clamps each cache to 50% of RAM independently
- Where: `PreviewImageService.cs:138` and `SourceBytesCache.cs:16-19`.
- Evidence: the preview cache (50%) and the source-bytes cache (50%) together can claim 100%. The source-bytes cache is off by default (`UseSourceBytesCache=false`), but the setting exists. The sum is not checked and no log warns about it.
- Fix: clamp the sum (for example 40% for the preview cache and 20% for source bytes), or log a warning when the sum exceeds 60%.
- Confidence: verified by reading.

## R2-A-07 (Low): GetPhysicalMemoryBytes relies on `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- Where: `src/PhotoReview.Imaging/Preload/RamBudgetPolicy.cs:26`.
- Evidence:
  - If the value is 0 the clamp silently fails open (`physicalBytes <= 0` returns the request unchanged), and the log line then reports an unclamped budget with no warning.
  - Under `GCHeapHardLimit` or a container, the value is the limit, not RAM. That is intended for the container case, but the log says "physical RAM".
  - `MemoryBudgetClampTests` only asserts `physical > 0` inside the test process, where a GC has already run.
- Fix: fall back to `WindowsMemoryProbe.GetSnapshot().TotalPhysicalBytes` when the GC value is 0, or log a warning when it is unknown.
- Confidence: suspicion.

## R2-A-08 (Low): the "cheap validation" in RetryMoveOrCopyAsync still stats files on the caller (UI) thread
- Where: `RecoveryRetryService.cs:47-56` (`FileExists`, `GetFileStat`, `FileExists` before `Task.Run`).
- Evidence: on a network share or a sleeping disk, three synchronous I/O calls freeze the UI, which is what CORE-06 tried to avoid.
- Fix: move the whole method body into `Task.Run`.
- Confidence: verified by reading.

## R2-A-09 (Low): TOOL-02 warm-up decodes `files[0]`, which is also the first timed "cold-read" sample
- Where: `src/PhotoReview.Benchmarking/PerformanceTestHarness.cs:67-72`.
- Evidence:
  - The warm-up leaves `files[0]` in the OS file cache, so the first "cold" sample is a warm read.
  - `before = WorkingSet64` is taken before the warm-up, so the working-set delta includes JIT and native init.
  - The old delete workload (`SendToRecycleBin` via VisualBasic) was replaced by `WindowsRecycleBin.SendToRecycleBin`, so numbers are not comparable with old baselines, and the stale comment about "permanent delete" was removed.
- Fix: warm up on a synthetic tiny in-memory image or a separate file, and take `before` after the warm-up.
- Confidence: verified by reading.

## R2-A-10 (Low): AccessibilityNamesTests is weaker than its claim
- Where: `tests/PhotoReview.Integration.Tests/AccessibilityNamesTests.cs:28-45,66`.
- Evidence:
  - The walker only checks `TextBox`, `ComboBox`, `CheckBox`, `RadioButton` and `Button`. The `ToggleButton` "⋮" (`ToolsButton`) is not a `Button` subclass, so it is skipped.
  - A `Button` whose content is only an emoji has an automation peer name equal to the emoji text, so an icon-only button with no `AutomationProperties.Name` passes.
  - `"C:\a.jpg"` (line 66) is a normal string, so `\a` is a BEL escape. It is harmless here, but it is a latent bug.
- Mutation: removing `AutomationProperties.Name` from the "📂" or "⋮" button would still pass.
- Fix: include `ToggleButton` and flag names that contain no letters or digits.
- Confidence: verified by reading.

## R2-A-11 (Low): TestIsolationRulesTests works per file, not per class
- Where: `tests/PhotoReview.Architecture.Tests/TestIsolationRulesTests.cs:29-33`.
- Evidence: it checks that the file contains `[Collection("GlobalState")]` anywhere. A file with two classes, only one attributed, passes even if the class that mutates the environment is the other one.
- Fix: check per class (Roslyn, or a regex per `class` block).
- Confidence: verified by reading.

## R2-A-12 (Low): CI and gate run Integration-trait tests twice, and nothing checks that TEST_FILTER matches the other copies
- Where: `.github/workflows/ci.yml` (`TEST_FILTER` no longer excludes Integration, plus the extra "Run Integration-category tests" step), `tools/verify-all.ps1:246-256`.
- Evidence:
  - `Category=Integration` tests in Core.Tests and Integration.Tests now run once in the main step and once in the extra step (for example `PlatformPrimitivesTests`, `OperationJournalTests`). This wastes CI time and duplicates trx files.
  - The filter string is copied into the workflow, `verify-all.ps1` and AGENTS.md, and only comments claim they match. A drift-check test is missing.
  - The extra step covers only two projects. An `Integration` plus `Slow` test added to another test project is still skipped silently, which is the exact failure Q-R3 wants to prevent.
- Correct as written: `"$env:TEST_FILTER"` under `shell: pwsh` handles the `&` fine, and the loop step throws on a non-zero exit.
- Fix: iterate all test projects in the extra step, and add an Architecture test that compares the three filters.
- Confidence: verified by reading.

## R2-A-13 (Low): ActionDestinationPolicy is stricter than the runtime guard for interior `..`, and Apply now blocks on an incomplete Move/Copy action
- Where: `ActionDestinationPolicy.cs:33-42` and `ActionProfilesWindow.xaml.cs:83-100`.
- Evidence:
  - The editor policy rejects `Sorted\..\Keep` (which stays inside the photo folder), but the runtime guard resolves it and accepts it (`FileActionService.cs:102-105` uses the resolved path).
  - An unused slot with Move/Copy and an empty destination makes the whole Apply fail (`Empty` maps to "invalid"). Previously that failed only at execution time.
- UNC (`\\srv\share\x`, fully qualified), `Sorted\Keep`, trailing dot/space and case differences are all handled correctly. `IsPathFullyQualified` and `StartsWith(..., OrdinalIgnoreCase)` work as intended. Symlinks and junctions inside the photo folder pointing outside are not resolved (accepted).
- Fix: make the editor validate by resolving the path the same way the runtime does, and consider allowing empty destinations for unused slots.
- Confidence: verified by reading.

## R2-A-14 (Info): UndoService `ConfigureAwait(false)` moves post-await state changes to pool threads
- Where: `UndoService.cs:182-231`.
- Evidence: `_lastUndoAction = null`, `_moveHistory.Push(move)` and `End()` now run on pool threads. The busy guard (`TryBegin`) is shared, so concurrent access is excluded, and the test `UndoLastAsync_DoesNotResumeOnCapturedContext` is non-vacuous (removing a `ConfigureAwait` produces a Post). The fields are not `volatile`, but async continuation gives the needed barriers. No bug found.
- Confidence: verified by reading.

## R2-A-15 (Info): DiskCacheStore incremental tracking (IMG-10) is safe, but drifts low if another process writes to the same directory
- Where: `DiskCacheStore.cs:26-40,178-195`.
- Evidence:
  - Errors are all on the high side (overwrites, double counting during a scan). A full scan is forced every 256 skipped passes, and `ClearDirectory` resets to unknown.
  - Two instances of PhotoReview on different folders share one cache directory and do not report writes to each other, so a cache can exceed its quota by up to 256 passes' worth of writes.
  - The tests `PrunePassUnderQuotaSkipsFullScan` and `NotedWritesOverQuotaEvictOldest` are genuinely mutation-sensitive.
- Confidence: verified by reading.

## R2-A-16 (Info): housekeeping
- `src/PhotoReview.Core/Session/SessionWriter.cs:1` gained a UTF-8 BOM (diff noise). The BOM sits before the first `using`, so it is harmless.
- `PreviewCacheFile.cs:221-231` has two stacked `<summary>` blocks (the first belongs to `HasAlpha(BitmapSource)`).
- `ActionDestinationPolicy` and `DestinationOutsideSource` are never journaled, so the `JournalErrors` code mapping for it is only used for the localized message.

---

## Checked and found equivalent or correct
- CORE-07 JournalTransaction: same order (Prepared, mutate, verify, Committed/Failed) and same Failed-only-after-Prepared rule in `FileActionService`; retry keeps `failWithoutPrepared`. `BeginAsync` keeps PowerLossSafe on a pool thread and Fast inline. Error codes are unchanged, and `MutationCompleted` is set at the same points (after verify, and after `SendToRecycleBin`). Cancellation semantics are unchanged.
- CORE-08 reverse reader: the boundary and cut-line logic is correct in all cases I traced (single long line, start == 0, boundary on a line start). The Recycle/Copy skip token cannot appear inside an escaped JSON string.
- IMG-03 headroom probe: correct (re-probes after each queued decode and every 50 ms). `HeadroomLost_MidBatch_StopsNewStarts` fails on the old behaviour.
- IMG-07 JPEG segment walker: equivalent to the old duplicated loops (also for standalone markers and SOS).
- IMG-08 decoder cache: decoders hold no per-instance mutable state that I found.
- CORE-05 `TryRestore`: COM release order is correct on every path. Not executed.
- Theme migration: every `Dark.*` key used in XAML or C# is defined in `Themes/DarkPalette.xaml` (checked by script). MainWindow merges the palette; every dialog window merges `DarkControls.xaml`, which in turn merges the palette. `RecoveryPathPanel` resolves through the parent window. No hex literals remain outside `Themes/`. All `LabeledBy` label/control pairs match by name.
- CA2007 scope: the editorconfig glob covers Core, Imaging, Imaging.TurboJpeg and Platform.Windows. The Core.Tests build has 0 warnings and 0 errors.
- RecoveryWindow re-entrancy: `_retrying` blocks a second Retry, and Clear/Retry buttons are disabled during the retry. Exceptions from the delegate are surfaced by `try/catch` in `Retry_Click`. Only a closed window loses the result (R2-A-01).
