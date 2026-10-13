# Testing

Detail behind [`AGENTS.md` > Tests](../AGENTS.md#tests): categories, parallel local runs, and the hang guard.

## Test projects

Seven xUnit projects run by `tools/verify-all.ps1` and CI: `Architecture.Tests`, `Core.Tests`, `Imaging.Tests`, `Integration.Tests`, `App.Tests`, plus (NO-WPF shell, WP-01) `Shell.Tests` (no WPF) and `Shell.Integration.Tests` (real `PhotoReview.exe`/HWND, `Category=UI`) (all under `tests/PhotoReview.*`). Frozen NO-WPF contract signatures: `Architecture.Tests/ContractSurfaceTests` vs `Approved/contracts.v1.txt` (regenerated only by its `Category=Manual` test, lead only). Two helper libraries are not test projects: `PhotoReview.TestSupport` (net10.0: `TempRoot`, `Wait`, `FakeMemoryProbe`, `RawCorpus`, ...) and `PhotoReview.TestSupport.Windows` (WPF-dependent fixtures such as `InMemoryRawHeaderSource`). Test-only fakes live there, not in `src/`.

## Categories

`HotPath` (fast unit tests), `Slow`, `Architecture`, `Integration`, `Manual`, `Native`, and `UI` (real WPF `Application`/STA-dispatcher tests — mostly `PhotoReview.Integration.Tests`, plus a few in `PhotoReview.App.Tests`; always paired with `[Collection("GlobalState")]`). Filter on any of these with `dotnet test --filter "Category=X"`.

## Parallel local runs

`tools/verify-all.ps1 -Parallel` runs the test projects concurrently (one `dotnet test` process each) — ~2.1x speedup (170s -> 81s, default filter); doesn't change xUnit's in-assembly `[Collection("GlobalState")]` serialization. Add `-Hidden` to keep UI-test windows off the shared desktop.

## Hidden desktop (local only)

`tools/run-tests-hidden.ps1 [dotnet-test-args]` (or `verify-all.ps1 -Hidden`) runs `dotnet test` on a private, non-interactive Win32 desktop (`CreateDesktop`): `Category=UI` real-WPF windows and `MessageBox`es never appear on your desktop or steal focus. Defaults to the CI filter and hang flags; exit code, output (also logged under `TestResults\hidden-desktop-runner\`) and the hang guard behave exactly like a normal run. CI is unchanged.

## UI test guards: dialogs and logged errors

`StaTestHost.RunAsync` (Integration.Tests) fails the body on two silent-failure channels (audit D-02/D-03). **Dialogs**: a real `MessageBox` is force-closed and fails the test (`Win32DialogGuard`); to assert that the app *shows* a confirmation/error, pass `TestHostHooks.Dialogs = new RecordingDialogs { ConfirmationAnswer = ... }` (records `ShowConfirmation`/`ShowError`/`ShowMessage`, default answer No, everything else still the production `WpfDialogService`). **Logged errors**: the harness swaps `AppLog.Instance` for an enabled temp-file log and fails the body when the app logged an `AppLog.Error` (what `FireAndLog` leaves for a faulted fire-and-forget task) that the test did not announce with `StaTestHost.ExpectLoggedError(substring, reason, mustOccur)`. Limit: only `AppLog` callers are seen, not code holding `FileLog.Default`/`ILog` directly, and an error logged between two `RunAsync` pumps is not attributed. Probes: `StaTestHostLogGuardTests`; Delete/Undo dialogs and in-place undo: `MainWindowDialogAndUndoTests`.

## Hang guard

`tests/test.runsettings` caps `dotnet test` at 120s/test, 20min/session, no flags needed; prefer `verify-all.ps1` ([detail](refactoring/decisions/TEST-HANG-GUARD.md)).

## Fuzz tests

`tests/PhotoReview.Imaging.Tests/Robustness/BinaryFuzz.cs` is the shared harness for `BinaryReaderFuzzTests` (binary readers fed untrusted bytes): a bounded, reproducible mutant corpus (truncation, length-field overwrites, seeded bit flips/splices) run under a wall-clock bound. It asserts only "no hang" and "every failure is the reader's documented clean failure"; a failing case prints a label that reproduces it.

## Fault-injection (crash-consistency) tests

`tests/PhotoReview.Core.Tests/FileActions/FileActionFaultInjection*Tests.cs` run each file-action scenario (Move, Copy, Delete via a fake bin, group actions, Undo, Recovery retry, interrupted startup reconcile) once per mutating call x fault kind (`FaultInjectionFileSystem`: crash before/after/torn, or one transient failure). `FaultInvariants` then checks no photo lost, duplicated or overwritten, every journal verdict true, nothing Prepared, a second reconcile a no-op. A new scenario is one `FaultInjectionMatrix.AssertAllFaultPointsAsync` call; in-memory only (no real disk or Recycle Bin). `JournalCompactionFaultInjectionTests` does the same for journal compaction. Fault kinds also include `FailUnauthorized` (UnauthorizedAccessException) and, opt-in via `kinds:`, `CancelAfter` (the rig's token is cancelled after the call). `FileActionFaultInjectionGapTests` + `FaultInjectionSecondOrder` add the second-order cells (a Recovery retry interrupted after a real crash, two faulted starts in a row, Dismiss, permanent delete on a bin-less volume, created-folder cleanup). The cell-by-cell matrix and how to add a fault point: [NOWPF-FAULTINJ-JOURNAL](refactoring/decisions/NOWPF-FAULTINJ-JOURNAL.md).

## Property tests

`tests/PhotoReview.TestSupport/PropertyRunner.cs` runs seeded random properties (`Properties/` folders of Core/Imaging tests; no FsCheck, the offline restore has no such package). CI uses fixed seeds 1..3. Set `PHOTOREVIEW_PROP_SEED=<int>` to replay one seed or `random` for one fresh seed; every failure message names the seed, the iteration and this variable.

## Stale-preview tests (INV-1)

"An old preview overwrites the new one" is tested without time: every decode, thumbnail, zoom original and refresh stat is a gate the test opens in the order it chooses (`App.Tests/Coordinators/StalePreviewHarness` + `StalePreviewOverwriteTests`; shell layer `Shell.Tests/Viewing/PresentationStaleOrderTests`; real shell `Shell.Integration.Tests/Startup/ShellStalePreviewTests`). The invariant is checked on the whole sink event log, not only the final state. Table of paths x orderings, how to add a case: [NOWPF-STALEPREVIEW-TESTS](refactoring/decisions/NOWPF-STALEPREVIEW-TESTS.md).
## Mutation tests

[Stryker.NET](MUTATION-TESTING.md) measures how well the tests pin behaviour (manual, not part of CI): how to run it safely and the 2026-10-03 baseline.

## Perf results as JSON and `compare` (local, optional)

`PhotoReview.Benchmark.Cli --startup-probe ...` also writes a structured result (schema `photoreview.perf-result` v1: scenario, machine CPU/RAM/OS/GPU, commit, runs, background CPU + noise flags, per-metric samples with P50/P95/min/max/MAD) to `work/perf/*.json` (git-ignored) and `<out>/perf-result.json`; `--perf-export <perf-session dir>` does the same for `--perf-session` runs (GC counts/pauses, peak WS, source reads, decode ms, key settle). Compare two files (a baseline and a new run):

```powershell
.\tools\diag\perf-compare.ps1 -Baseline tools\perf-baselines\$env:COMPUTERNAME.json -Current work\perf\startup-probe-<machine>-<utc>.json
# or: PhotoReview.Benchmark.Cli.exe compare <baseline.json> <current.json> [--threshold-percent 5 --min-abs-ms 1 --busy-cpu 60 --json out.json --markdown out.md]
```

Per metric the high-side outliers are dropped by MAD (WP-29 rule), medians are compared against `max(5 %, absolute floor)` (1 ms, 16 MB, 1 count) and a 95 % interval of the difference: **REGRESSION** only when the whole interval is beyond the threshold, **WARN** when the point estimate is but the interval is not, **INCONCLUSIVE** (never a false regression) when the machine is busy (> 60 % CPU), the spread is > 25 % of the median, samples < 3, a metric is missing, or the machine/parameters differ. Exit codes: 0 PASS/WARN, 1 REGRESSION, 2 usage/schema/scenario error, 3 INCONCLUSIVE. `.\toolserify-all.ps1 -PerfGate` runs the published exes through `tools/diag/perf-gate.ps1` against `tools/perf-baselines/<COMPUTERNAME>.json` (skipped when absent; INCONCLUSIVE is a warning; off in CI). Baselines are committed only when the owner decides (`perf-gate.ps1 -UpdateBaseline`). Detail and a worked run: [perf note](refactoring/perf/2026-10-13-perf-json-compare.md).

## Shell tests (NO-WPF)

`PhotoReview.Shell.Tests` (no WPF, runs in CI) covers the Win32 shell logic and Direct2D rendering; its pixel checks run on WARP (`Category=Integration`). `PhotoReview.Shell.Integration.Tests` drives the real `PhotoReview.exe` window (`Category=UI`): run it on the hidden desktop with `tools/run-tests-hidden.ps1 tests/PhotoReview.Shell.Integration.Tests` (arguments go to `dotnet test`). `PhotoReview.exe --smoke` is the quick check: it creates the window and exits 0.

Native Win32 dialogs (WP-35 GĐ1, `Shell.Tests/Dialogs/Native`, `Category=UI`; detail [NOWPF-WP35-G1-WIN32-DIALOGS](refactoring/decisions/NOWPF-WP35-G1-WIN32-DIALOGS.md)): real dialog windows on a fresh STA thread with its own owner window, no sleeps. The user is simulated inside the `ready` callback of `NativeDialog.ShowModal` (it runs after the window is shown and the owner disabled, before the modal loop) with synchronous `SendMessage` / `IsDialogMessage` (Enter, Esc, Tab), so an assertion failure surfaces through `ShowModal`. Dark flags are asserted through the `INativeDialogChrome` seam (`RecordingChrome`) and `WM_CTLCOLOR*` return values; DPI by sending `WM_DPICHANGED`; leaks by `GetGuiResources` before/after; the `WM_QUIT` repost with `PeekMessage`. `LazyLoadTests.NativeSecondaryWindows_OpenWithoutLoadingWpfOrTheBridge` (WpfBridge.Tests, clean child process) proves no WPF assembly is loaded. Do not post `WM_APP + 1` in a test: it is the dispatcher's wake message.

Native dialog controls and windows (WP-35 GĐ2, same folder; detail [NOWPF-WP35-G2-WIN32-DIALOGS](refactoring/decisions/NOWPF-WP35-G2-WIN32-DIALOGS.md)): `ControlGalleryDialog` (test-only) holds one of every new control; `NativeControlsTests` drives them with messages (combo, owner-draw badge list drawn into a memory DC and read back with `GetPixel`, report listview + `NM_CUSTOMDRAW` colours and header, progress, trackbar, edit/radio/check) and asserts the dark flags through `DarkDialogChrome(setTheme)` (the `SetWindowTheme` seam; `RecordingChrome` stays for the "applied once" check). Dark itself is judged by eye: `PHOTOREVIEW_DIALOG_SNAPSHOTS=<dir>` + `NativeDialogSnapshotTests` (Manual) writes `gallery*.png` (100 %, 150 %, sorted, the combo drop-down window). `BenchmarkDialogTests` use the real shell loop (`UiThread`: dispatcher + `MessageLoop` + sync context) because the window is modeless: the test posts a key and a marker message in order and waits for the marker, never for a clock; Esc/Close/X mid-run and `Win32SecondaryWindowPort.Dispose` are checked on a gated fake session. `--probe-native` in `Shell.WpfBridge.Tests` also opens Benchmark (modeless) and Action profiles in a clean process and proves no WPF assembly loads.

Native Recovery window (WP-35 GĐ4, same folder; detail [NOWPF-WP35-G4-RECOVERY-WIN32](refactoring/decisions/NOWPF-WP35-G4-RECOVERY-WIN32.md)): `RecoveryDialogTests` drive the Win32 view of `RecoveryWindowPresenter` on the real shell loop (`UiThread`) with fake `ITaskDialogPort`/clipboard/Explorer seams, so confirmations and the Retry/Dismiss delegates are observable; `RecoveryDialogDataSafetyTests` run the REAL `RecoveryRetryService` + `OperationJournal` on a private temp folder (`wp35g4-recovery-*`, never a real Recycle Bin: no `IRecycleBin` is passed) and assert source, destination and journal bytes after every Retry/Dismiss/Clear. A test that needs a state that is still "in flight" gates the delegate with a `TaskCompletionSource` and waits on a marker message, never a clock. `Shell.Integration.Tests` closes the SHELL while Recovery is open and asserts exit 0 with journal and files unchanged.

Wait on a condition, never on a count or a clock (three shell tests turned red at random on the WARP runner until they did): RenderNow may be deferred by FramePacer (swap chain not ready), so assert frame-born state only after ShellFrames.RenderUntil(main, condition) on the UI thread; read counters that you compare in ONE UI turn; and inject the clock (ShellStartupOptions.KeepWarmTimestamp) instead of pausing past a throttle or window. Wait for IsRenderRequested/IdleWaits/IsBlocked, not for a fixed number of milliseconds.

The golden references are shared by the WPF build and the shell: `tests/Fixtures/golden/*.v1.json` (G-VIEW `viewport-layout`, G-INPUT `input-scripts`, G-KEY `key-names`, G-MENU `context-menu`, G-OVL `overlay-boxes`) and `pixel/*.png` (G-PIX). Re-record only on purpose, with `PHOTOREVIEW_GOLDEN_RECORD=1` through `tools/diag/record-golden.ps1` (the recorders refuse to write without it). `PHOTOREVIEW_REGENERATE_CONTRACTS=1` regenerates the frozen public-surface file checked by `Architecture.Tests/ContractSurfaceTests` (lead only, `Category=Manual`).

## Golden files (NO-WPF equivalence reference)

`tests/Fixtures/golden/*.v1.json` are recorded from the real WPF `MainWindow` (`tools/diag/record-golden.ps1`, hidden desktop, deterministic output) and are the reference the Win32 shell must reproduce. `Integration.Tests/Golden/GoldenWpfConformanceTests` (`Category=UI`) replays them on WPF every run, so a WPF behaviour change turns it red until the diff is reviewed and the golden re-recorded on purpose; never hand-edit them. Detail: [NOWPF-WP10-GOLDEN-RECORDER](refactoring/decisions/NOWPF-WP10-GOLDEN-RECORDER.md).

G-PIX (WP-28): `tests/Fixtures/golden/pixel/*.png` are the last frame of the same WPF `MainWindow` (`RenderTargetBitmap`, recorded by the same script; `PixelGoldenWpfConformanceTests`, `Category=UI`, keeps them true). `Shell.Tests/Golden/PixelGoldenTests` (`Category=Integration`, WARP, runs in CI) draws the same picture/zoom/DPI with the shell and holds it to NO-WPF-EXEC-PLAN 7.3 (2): geometry <= 1 px, PSNR >= 32 dB at Fit, >= 28 dB at other zooms, byte-identical at whole-pixel 100 %; a miss writes a diff image (`tools/diag/pixel-golden-report.ps1` writes all of them). HUD panel boxes: `PixelGoldenHudTests` (`Category=Native`). Detail and the measured deviations (NE-9): [NOWPF-WP28-GOLDEN-PIXEL](refactoring/decisions/NOWPF-WP28-GOLDEN-PIXEL.md). WP-28b extension (`PixelGoldenExtTests`, `PixelGoldenCrossfadeTests`, `Category=Integration`, golden `cmp-*`/`f11-*`/`rawsize-*`, recorded by `PixelExtGoldenRecordTests` via the same script): compare of two pictures (borders and canvas byte-identical to WPF), the first frame after F11/DPI change on a fake 720x480 px screen, a RAW-style preview bitmap under the original's layout, and the mid-way crossfade against an independent blend (no WPF equivalent). Detail: [NOWPF-WP28B-GOLDEN-PIXEL-EXT](refactoring/decisions/NOWPF-WP28B-GOLDEN-PIXEL-EXT.md).

## Timing tests

Never assert a wall-clock budget in a gated test: a full parallel run on a shared runner makes it flake (`GroupEntries` 10k paths: 4-7 ms alone, 27-65 ms inside the full `Core.Tests` run). Guard hot paths with a deterministic proxy instead (`GC.GetAllocatedBytesForCurrentThread()` bound, operation counts) and keep stopwatch numbers in a `Category=Manual` report test (example: `CaptureGroupBuilderTests`).

## Thread identity and timing under load

Three CI flakes in 24 hours (2026-10-12) shared one cause: a test trusted a thread id or the clock of a loaded runner. Rules (audit table: [NOWPF-THREADAUDIT-TESTS](refactoring/decisions/NOWPF-THREADAUDIT-TESTS.md)):

- **Never compare the `ManagedThreadId` of a thread that has died** with a later thread: ids are recycled (a dead decode thread's id was handed to a thread-pool thread). Record `Thread.CurrentThread` and use `Assert.Same`/`Assert.NotSame`.
- **A thread-pool thread is not a "caller"** when the assertion is "the work did not run on the caller": after the first `await`/`yield` it goes back to the pool and may run the work itself (same id, a false failure). Make the caller a `new Thread` that blocks on `.GetAwaiter().GetResult()` (like an awaiting UI thread) and compare `Thread` objects. A dedicated, live UI/dispatcher thread of the test is safe to compare by id.
- **No real-time windows**: do not open a keep-alive/deadline window with the wall clock and expect N events inside it. Inject a clock seam and advance it from the test, then wait on observed state (`SpinWait.SpinUntil(..., Bound)`). A rate check may only be an upper bound against elapsed time (load makes the code slower, never faster); liveness is a bounded wait.
- The `TEST-THREADID` architecture rule rejects a new `Assert.Equal/NotEqual` on a thread id (`...ThreadId`, `callerThread`, `workerThread`, ...); audited-safe uses sit in `test-quality-allowlist.txt`.
- To prove a fix, repeat the test >= 300 times in one process while ~12 busy threads occupy the cores, before and after.

## RAW corpus tests (manual CI)

The regular CI never fetches the RAW sample corpus (`tools/fetch-raw-samples.ps1`, 23 CC0 files, ~590 MB), so tests that need a real camera file return early there. Run the manual workflow **RAW corpus tests (manual)** (`.github/workflows/raw-corpus.yml`, Actions > Run workflow; the file must exist on the default branch) to fetch the corpus and run `Imaging.Tests` (including `Category=Native`) with `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`: a missing sample or `libraw.dll` then fails the test instead of skipping it. Locally, set the same variables after `fetch-raw-samples.ps1` to reproduce.

## Measuring coverage

`powershell -ExecutionPolicy Bypass -File tools/coverage.ps1` (~6 min; `-SkipBuild`, `-Project Core` (short or full test-project names), `-OutDir <absolute or relative path>`, `-ReportOnly`, `-MinLines`, `-Top`; it exits non-zero when the merged cobertura has no packages or lines; a single `-Project` once produced an empty merge because one file was splatted as a bare string) runs each test project through `run-tests-hidden.ps1` (hidden desktop, `Category=UI` never skipped, CI filter, blame-hang 120s + 20 min session cap) with the VSTest **Code Coverage** collector, merges with `dotnet-coverage` and writes `merged.cobertura.xml`, `summary.json` and `coverage-report.md` (per assembly and per file, lowest first) to `work\coverage\` (git-ignored, never committed).

Why this path: the collector already ships in `Microsoft.NET.Test.Sdk` (no package added to any project, production or test); only the dev-time local tool `dotnet-coverage` (`dotnet-tools.json`) is new, and it only merges. `coverlet.collector` was not needed. Excluded: test/support/benchmark assemblies, `[GeneratedCode]`/`[CompilerGenerated]`/`[ExcludeFromCodeCoverage]`, `*.g.cs`/`*.g.i.cs`/`obj\`. The collector's cobertura has no branch data, so **Block %** (native xml of the same merge; an untaken branch leaves its block uncovered) is the branch-level metric. Native/Slow/Manual tests are not run, so P/Invoke-heavy code (`Platform.Windows`, LibRaw) is under-reported.

Baseline 2026-10-06 (master `c27f5010`; one flaky `Imaging.Tests` test failed in the instrumented run, see the PR):

| Assembly | Lines | Line % | Blocks | Block % |
|---|---:|---:|---:|---:|
| PhotoReview.Platform.Windows | 884 | 58.7 | 2103 | 52.3 |
| PhotoReview.Imaging.LibRaw | 481 | 83.4 | 1021 | 82.9 |
| PhotoReview.App | 4635 | 89.3 | 9970 | 85.5 |
| PhotoReview.Imaging.TurboJpeg | 340 | 95.3 | 651 | 93.4 |
| PhotoReview.Imaging | 2156 | 97.6 | 4100 | 95.5 |
| PhotoReview.Core | 3938 | 98.0 | 8616 | 96.1 |
| PhotoReview.Imaging.Raw | 1822 | 99.2 | 3391 | 98.6 |

Lowest-covered files with >= 30 coverable lines (40 lowest by line %):

| File (under `src/PhotoReview.`) | Lines | Line % | Block % |
|---|---:|---:|---:|
| App/Diagnostics/PerfDispatcherHooks.cs | 41 | 0.0 | 0.0 |
| Platform.Windows/WindowsDisplayClock.cs | 99 | 12.1 | 14.7 |
| Platform.Windows/Explorer/ExplorerOrderService.cs | 223 | 48.0 | 37.2 |
| App/Services/WpfPresentationSink.cs | 57 | 54.4 | 91.3 |
| Platform.Windows/RecycleBinCapacity.cs | 68 | 54.4 | 51.1 |
| App/ActionProfilesWindow.xaml.cs | 101 | 54.5 | 53.4 |
| App/Services/WpfImageSurface.cs | 62 | 59.7 | 68.1 |
| Platform.Windows/WindowsRecycleBin.cs | 190 | 61.6 | 48.3 |
| Imaging.LibRaw/LibRawDecoder.cs | 200 | 63.5 | 68.2 |
| App/BenchmarkWindow.xaml.cs | 148 | 72.3 | 69.3 |
| App/Localization/LocalizationService.cs | 40 | 80.0 | 73.6 |
| App/DiagnosticsWindow.xaml.cs | 63 | 82.5 | 67.3 |
| App/MainWindow.xaml.cs | 378 | 83.1 | 79.9 |
| App/SettingsWindow.xaml.cs | 655 | 83.2 | 86.4 |
| App/RecoveryWindow.xaml.cs | 132 | 83.3 | 74.0 |
| App/MainWindowConvergence.cs | 42 | 83.3 | 57.1 |
| Imaging/Decoding/FallbackImageDecoder.cs | 62 | 85.5 | 81.7 |
| Platform.Windows/InstanceScope.cs | 105 | 85.7 | 78.1 |
| App/Services/WpfDialogService.cs | 73 | 86.3 | 77.3 |
| App/RecoveryPresenter.cs | 164 | 87.8 | 86.7 |
| App/App.xaml.cs | 213 | 88.3 | 72.3 |
| App/MainWindowHelpers.cs | 53 | 88.7 | 83.9 |
| App/WindowPlacementService.cs | 59 | 89.8 | 91.8 |
| Core/Diagnostics/PerfCsvListener.cs | 152 | 90.8 | 85.9 |
| Core/Catalog/DragDropInputService.cs | 36 | 91.7 | 90.9 |
| Imaging/ImageCacheKey.cs | 32 | 93.8 | 97.8 |
| Core/Session/SessionStore.cs | 71 | 94.4 | 97.0 |
| Imaging.LibRaw/LibRawAvailability.cs | 56 | 94.6 | 90.3 |
| Imaging.TurboJpeg/TurboJpegDecoder.cs | 295 | 95.3 | 93.1 |
| Imaging/Caching/DiskCacheStore.cs | 132 | 95.5 | 91.8 |
| Imaging/Metadata/ExifQueryInterpreter.cs | 44 | 95.5 | 95.5 |
| App/Localization/TranslationExport.cs | 92 | 95.7 | 97.0 |
| Core/IO/PhysicalJournalCompactionFiles.cs | 50 | 96.0 | 94.3 |
| App/Coordinators/ZoomDetailLoader.cs | 77 | 96.1 | 89.0 |
| Imaging.Raw/Bmff/Cr3ContainerReader.cs | 165 | 97.0 | 98.0 |
| Imaging/Decoding/WpfBitmapImageDecoder.cs | 99 | 97.0 | 94.8 |
| Core/FileActions/FileActionService.cs | 102 | 97.1 | 94.0 |
| Core/Diagnostics/PhotoReviewPerf.cs | 72 | 97.2 | 98.1 |
| Core/FileActions/ActionDestinationPolicy.cs | 37 | 97.3 | 98.9 |
| Platform.Windows/Explorer/ExplorerWindowSelector.cs | 37 | 97.3 | 94.8 |
