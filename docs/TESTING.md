# Testing

Detail behind [`AGENTS.md` > Tests](../AGENTS.md#tests): categories, parallel local runs, and the hang guard.

## Test projects

Five xUnit projects run by `tools/verify-all.ps1` and CI: `Architecture.Tests`, `Core.Tests`, `Imaging.Tests`, `Integration.Tests`, `App.Tests` (all under `tests/PhotoReview.*`). Two helper libraries are not test projects: `PhotoReview.TestSupport` (net10.0: `TempRoot`, `Wait`, `FakeMemoryProbe`, `RawCorpus`, ...) and `PhotoReview.TestSupport.Windows` (WPF-dependent fixtures such as `InMemoryRawHeaderSource`). Test-only fakes live there, not in `src/`.

## Categories

`HotPath` (fast unit tests), `Slow`, `Architecture`, `Integration`, `Manual`, `Native`, and `UI` (real WPF `Application`/STA-dispatcher tests — mostly `PhotoReview.Integration.Tests`, plus a few in `PhotoReview.App.Tests`; always paired with `[Collection("GlobalState")]`). Filter on any of these with `dotnet test --filter "Category=X"`.

## Parallel local runs

`tools/verify-all.ps1 -Parallel` runs the 5 test projects concurrently (one `dotnet test` process each) — ~2.1x speedup (170s -> 81s, default filter); doesn't change xUnit's in-assembly `[Collection("GlobalState")]` serialization. Add `-Hidden` to keep UI-test windows off the shared desktop.

## Hidden desktop (local only)

`tools/run-tests-hidden.ps1 [dotnet-test-args]` (or `verify-all.ps1 -Hidden`) runs `dotnet test` on a private, non-interactive Win32 desktop (`CreateDesktop`): `Category=UI` real-WPF windows and `MessageBox`es never appear on your desktop or steal focus. Defaults to the CI filter and hang flags; exit code, output (also logged under `TestResults\hidden-desktop-runner\`) and the hang guard behave exactly like a normal run. CI is unchanged.

## Hang guard

`tests/test.runsettings` caps `dotnet test` at 120s/test, 20min/session, no flags needed; prefer `verify-all.ps1` ([detail](refactoring/decisions/TEST-HANG-GUARD.md)).

## Fuzz tests

`tests/PhotoReview.Imaging.Tests/Robustness/BinaryFuzz.cs` is the shared harness for `BinaryReaderFuzzTests` (binary readers fed untrusted bytes): a bounded, reproducible mutant corpus (truncation, length-field overwrites, seeded bit flips/splices) run under a wall-clock bound. It asserts only "no hang" and "every failure is the reader's documented clean failure"; a failing case prints a label that reproduces it.

## Property tests

`tests/PhotoReview.TestSupport/PropertyRunner.cs` runs seeded random properties (`Properties/` folders of Core/Imaging tests; no FsCheck, the offline restore has no such package). CI uses fixed seeds 1..3. Set `PHOTOREVIEW_PROP_SEED=<int>` to replay one seed or `random` for one fresh seed; every failure message names the seed, the iteration and this variable.

## Mutation tests

[Stryker.NET](MUTATION-TESTING.md) measures how well the tests pin behaviour (manual, not part of CI): how to run it safely and the 2026-10-03 baseline.

## Timing tests

Never assert a wall-clock budget in a gated test: a full parallel run on a shared runner makes it flake (`GroupEntries` 10k paths: 4-7 ms alone, 27-65 ms inside the full `Core.Tests` run). Guard hot paths with a deterministic proxy instead (`GC.GetAllocatedBytesForCurrentThread()` bound, operation counts) and keep stopwatch numbers in a `Category=Manual` report test (example: `CaptureGroupBuilderTests`).

## RAW corpus tests (manual CI)

The regular CI never fetches the RAW sample corpus (`tools/fetch-raw-samples.ps1`, 23 CC0 files, ~590 MB), so tests that need a real camera file return early there. Run the manual workflow **RAW corpus tests (manual)** (`.github/workflows/raw-corpus.yml`, Actions > Run workflow; the file must exist on the default branch) to fetch the corpus and run `Imaging.Tests` (including `Category=Native`) with `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`: a missing sample or `libraw.dll` then fails the test instead of skipping it. Locally, set the same variables after `fetch-raw-samples.ps1` to reproduce.

## Measuring coverage

`powershell -ExecutionPolicy Bypass -File tools/coverage.ps1` (~6 min; `-SkipBuild`, `-Project <name>`, `-ReportOnly`, `-MinLines`, `-Top`) runs each of the 5 test projects through `run-tests-hidden.ps1` (hidden desktop, `Category=UI` never skipped, CI filter, blame-hang 120s + 20 min session cap) with the VSTest **Code Coverage** collector, merges with `dotnet-coverage` and writes `merged.cobertura.xml`, `summary.json` and `coverage-report.md` (per assembly and per file, lowest first) to `work\coverage\` (git-ignored, never committed).

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
