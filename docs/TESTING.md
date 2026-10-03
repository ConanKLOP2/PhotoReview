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

## Mutation tests

[Stryker.NET](MUTATION-TESTING.md) measures how well the tests pin behaviour (manual, not part of CI): how to run it safely and the 2026-10-03 baseline.

## Timing tests

Never assert a wall-clock budget in a gated test: a full parallel run on a shared runner makes it flake (`GroupEntries` 10k paths: 4-7 ms alone, 27-65 ms inside the full `Core.Tests` run). Guard hot paths with a deterministic proxy instead (`GC.GetAllocatedBytesForCurrentThread()` bound, operation counts) and keep stopwatch numbers in a `Category=Manual` report test (example: `CaptureGroupBuilderTests`).

## RAW corpus tests (manual CI)

The regular CI never fetches the RAW sample corpus (`tools/fetch-raw-samples.ps1`, 23 CC0 files, ~590 MB), so tests that need a real camera file return early there. Run the manual workflow **RAW corpus tests (manual)** (`.github/workflows/raw-corpus.yml`, Actions > Run workflow; the file must exist on the default branch) to fetch the corpus and run `Imaging.Tests` (including `Category=Native`) with `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`: a missing sample or `libraw.dll` then fails the test instead of skipping it. Locally, set the same variables after `fetch-raw-samples.ps1` to reproduce.
