# Function-level codebase audit — 2026-09-25

**Baseline:** `origin/master` `dd85670` (`v2.0.90`). Review only; no production source/config changes. The three read-only lanes used detached worktrees at this exact commit. The coordinator reviewed CLI, PowerShell tools, CI and test support. References below are to this baseline.

## Coverage and evidence

| Scope | Files | Review performed |
|---|---:|---|
| Core + Platform.Windows | 96 C# | Static read-through of all files and implemented functions; caller/test tracing for findings. |
| Imaging + TurboJpeg + Benchmarking + PerfAnalysis + Localization.Generator | 60 C# | Static read-through of all files and implemented functions; caller/test tracing for findings. |
| App | 66 C#, 11 XAML | Static read-through of all files and event handlers; caller/test tracing for findings. |
| Benchmark CLI, PowerShell tools, CI | 8 C#, 10 PowerShell scripts, 1 workflow | Static function and pipeline review by coordinator. |
| Tests + shared test support | 223 C# | Test review in progress; production findings were cross-checked against relevant test bodies. Do not interpret inventory as full individual test audit yet. |

`dotnet build PhotoReview.slnx -c Release --nologo`: **pass, 0 warnings**. `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Slow" --nologo`: **1779 passed, 6 skipped, 0 failed**. No GUI, Native, Slow, or Manual run. Static source findings below remain open despite the green gate. A corrupt JPEG fixture run of `--decoder-bench` returned **exit 0, `Groups=[]`**; the temporary fixture was removed.

## Findings to fix first

| ID | Priority | Evidence | Finding and impact | Corrective test / change |
|---|---|---|---|---|
| FA-01 | P1 | Source race; no runtime repro | [`OperationJournal.ReconcilePendingOperations`](../../src/PhotoReview.Core/FileActions/OperationJournal.cs) can classify an active `Prepared` Move/Copy from another process as abandoned. Per-folder instances share one journal; process B may append `Failed` after process A committed, leaving a false terminal state. | Barrier-controlled two-writer test; identify operation ownership or recheck terminal state under the journal lock before appending. |
| IMG-01 | P1 | Valid JPEG byte sequence traced through code; no native fixture run | [`TurboJpegDecoder.TryReadSegment`](../../src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs) does not skip repeated `0xFF` marker fill or parameterless TEM. A valid APP1/APP2 segment after such a marker is missed; orientation defaults to 1 or ICC presence to false, causing wrong rotation/color. | Add JPEG fixtures with marker fill/TEM before EXIF and ICC; compare against WPF/libjpeg-turbo, then fix segment walker. |
| TEST-01 | P1 | Source-verified side effect; default suite was run | [`PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder`](../../tests/PhotoReview.Integration.Tests/PerformanceHarnessWarmupTests.cs) uses the stable `PerformanceTestHarness.DefaultReportPath` and deletes it in `finally`. A normal test run can overwrite/delete another benchmark report under `%TEMP%`. The path was absent after this review's gate; its prior state is unknown. | Make the default path overrideable for the test or test a pure path selector; use an owned `TempRoot` only. Do not rerun this test until isolated. |
| TOOL-01 | P2 | Runtime reproduced | [`DecoderBenchmark.RunAsync`](../../tools/PhotoReview.Benchmark.Cli/DecoderBenchmark.cs) skips groups with zero successful decodes yet returns success. A folder with one invalid `.jpg` produced exit 0 and `Groups=[]`. | Return a failing exit code when no successful decode exists, and record failure counts in console/summary. |
| APP-01 | P2 | Source-verified lifecycle path; no close-during-load repro | [`MainWindow.Window_Closed`](../../src/PhotoReview.App/MainWindow.xaml.cs) does not dispose/cancel `FolderLoadCoordinator`; [`App.Dispose`](../../src/PhotoReview.App/App.xaml.cs) does not dispose its service provider. A folder scan/Explorer await can continue after close, and DI-owned caches are not released by the container. | Block a fake folder scan, close the window, assert cancellation and no subsequent sink update; dispose coordinator/provider with clear ownership. |
| APP-02 | P2 | Source-verified repeated call | [`FolderLoadCoordinator.LoadAsync`](../../src/PhotoReview.App/Coordinators/FolderLoadCoordinator.cs) flushes and loads the session, then [`MainViewModel.OnCatalogReady/OnEmpty`](../../src/PhotoReview.App/ViewModels/MainViewModel.cs) flushes and loads it again. Every folder open repeats disk I/O and deserialization on the presentation path. | Count session reads with a fake store; pass the already loaded session to the sink once. |
| PERF-01 | P2 | Source-verified benchmark mismatch | [`BenchmarkImageExecutor`](../../src/PhotoReview.Benchmarking/BenchmarkImageExecutor.cs) ignores `BenchmarkProfile.NextWindow`, `PreviousWindow`, and `FullFolder`. Named profiles therefore do not execute their requested preload windows/whole-folder policy. The CLI also does not apply `DetailedLogging`, unlike the WPF benchmark window; `logging-on/off` compare the same logging state. | Assert each profile's effective scheduler/logging config and use the same profile application path for UI and CLI. |
| PERF-02 | P2 | Source-verified instrumentation bug | [`PerfSession.WaitIdleAsync`](../../tools/PhotoReview.Benchmark.Cli/PerfSession.cs) hardcodes `preloadDone = true`; it can mark `waitIdle` complete while preload is still active, contaminating warm/cold timings. | Use the actual `PreloadController.IsIdle`, with a controlled blocked-preload test. |
| TOOL-02 | P2 | Source-verified path guard | [`verify-all.ps1` `Publish-ReleaseDirectory`](../../tools/verify-all.ps1) recursively deletes any supplied `-ReleaseDirectory` whose final component is `publish` or `PhotoReview-self-contained`, even outside the repo. The leaf-name guard is insufficient to establish ownership. | Resolve and constrain the target to an approved output root, or require a marker created by the publisher; test external same-leaf path rejection without deleting it. |
| TOOL-03 | P2 | Source-verified option interaction | [`run-matrix.ps1`](../../tools/diag/run-matrix.ps1) rejects `-ColdDiskCache -SharedAppCache`, but `-Conditions cold-diskcache -SharedAppCache` reaches `Clear-DiskCache` on the real app cache. | Reject the equivalent condition combination or require an explicit destructive opt-in; test the branch with a fake cache root. |

## Policy and lower-priority findings

| ID | Evidence | Finding |
|---|---|---|
| APP-03 | Existing test codifies behavior | [`FileActionController`](../../src/PhotoReview.App/Coordinators/FileActionController.cs) skips `_undoService.Register` after a successful action if the folder changed during I/O. A cross-drive Move can finish but disappear from current-session Undo. Current tests intentionally assert no history; decide the intended policy (Q-R20). |
| CORE-01 | Direct invariant counterexample | [`ReviewCatalog.ReplaceOrder`](../../src/PhotoReview.Core/Catalog/ReviewCatalog.cs) accepts `[A,A]` when current entries are `[A,B]` and returns true. Current production callers validate Explorer order first, limiting exposure; the public catalog method itself is unsound. |
| CORE-02 | Source race; no blocked-writer repro | [`FileLog.Dispose`](../../src/PhotoReview.Core/Diagnostics/FileLog.cs) can dispose `_drained` after a 2-second join timeout while the writer's `Drain` finally still calls `Set`, risking an unhandled background exception. |
| CORE-03 | Channel semantics | [`PerfCsvListener`](../../src/PhotoReview.Core/Diagnostics/PerfCsvListener.cs) uses `DropWrite` but counts drops only on `TryWrite == false`; the trace trailer can undercount dropped rows. |
| PERF-03 | Source metric mismatch | [`BenchmarkRanking.Rank`](../../src/PhotoReview.Benchmarking/BenchmarkModels.cs) compares p95 latency across profiles whose sample contains a different number of worker images; the ranking mixes units of work. |
| TOOL-04 | Source metric mismatch | [`procmon-summary.ps1`](../../tools/diag/procmon-summary.ps1) counts preview-cache files only as `.png`; production uses `.pv4`, so cache I/O is misclassified as `other`. |
| TOOL-05 | Source-verified supported-format mismatch | [`benchmark-folder.ps1`](../../tools/benchmark-folder.ps1) includes `.webp`; [`ImageFileTypes`](../../src/PhotoReview.Core/Catalog/ImageFileTypes.cs) does not. A WebP-only folder is benchmarked though the app cannot review it. |
| TEST-02 | Test body review | Several Core benchmark scenario and action-sequence tests assert their own simulation without calling production behavior. `HotPathHonestyRuleTests` rejects TODO stubs but permits an assertion-free `[Fact]`. These green tests do not enforce their advertised contract. |
| TEST-03 | Skipped test review | Five `MainWindowBehaviorTests.Fit` cases are skipped with a stale “feature not yet implemented” reason, while the feature is on master; GUI acceptance remains open. |
| APP-04 | UI consistency; product choice | `ActionProfilesWindow` disallows saving zero actions, while settings JSON accepts `Actions=[]`. `SettingsWindow.Defaults_Click` also leaves custom actions intact; decide whether action profiles are intentionally preserved. |

## Remediation order

1. **Data and test isolation:** TEST-01, FA-01, TOOL-02/03. Use only temp-owned fixtures and fakes; never run cleanup against the user's Recycle Bin. Test the exact journal interleaving and path guards before changing implementation.
2. **Image correctness and lifecycle:** IMG-01, APP-01, APP-02, CORE-02. Add regression fixtures and cancellation/ownership assertions. Native GUI acceptance remains separate from headless test results.
3. **Benchmark trustworthiness:** TOOL-01, PERF-01/02/03, TOOL-04/05, CORE-03. Record effective profile configuration and require at least one successful sample.
4. **Contracts and UX:** decide Q-R20, then APP-03/04 and CORE-01. Replace tautological tests with production-path tests; retain current policy only if explicitly chosen.

Keep changes in isolated branches from current `master`, with disjoint file ownership. Merge safety fixes before benchmark/UX changes, then run Release build (0 warnings), default filtered tests, targeted new regression tests, doc/i18n gates, and explicit GUI checks for visible behavior. Do not interpret this static review as native GUI acceptance or measured performance improvement.
