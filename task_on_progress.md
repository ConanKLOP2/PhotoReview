# Current Work — PhotoReview

**Updated:** 2026-09-26 (night) | **Base:** master `36da791` (#94–#105 merged) | **Open:** integration PR `integration/arch-review-2026-09-26`

## Now

- **Architecture review 2026-09-26 — code done, one integration PR:** [summary](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md). Four agent branches merged without conflicts: AR11 (static `AppSettings` persistence removed, Settings round-trip test, Core `File.*` boundary test), AR12 (+AR17a, AR18a: `.gitignore` native rule, Release warnings-as-errors, GC note, TurboJpeg "experimental" label), AR15 (dead overloads, one atomic cache writer, `SourceBytesCache` reads on the calling thread), AR13 (+AR19a, AR14a: `PointerInputController`, `FitViewController` — Fit loop moved verbatim, still ≤ 3 passes; `MainWindow.xaml.cs` 715 → 404 lines; `ShowSkippedFiles` via `IDialogService`). Decisions Q-AR6/8/9/10 = recommended options (user said "run everything"); Q-AR7 pending.
- **Measured 2026-09-26 (PERF-STATUS):** AR15c not worse with the source-bytes cache on (new CLI flag `--source-bytes-cache`); AR16 probe ≈ 114 ms per folder open on F4 (≈ 68 % of the old 166 ms first visual) → **Q-AR7 waits for the user** (recommended: (c) background probe).
- **Still to do:** **user GUI check for AR13**: pan + glide, wheel zoom at cursor, Ctrl+wheel navigation, click-to-zoom, Fit after resize and DPI change, skipped-files list opens. Reported by the AR13 agent, not re-verified by the lead: 5 `Slow` MainWindow integration tests (Actions/FolderSwitch/Explorer) fail identically on master. Also seen once: a `JournalConcurrencyTests` file-lock crash (passed on rerun).
- **User (GUI / real machine), still open from the overnight review:** Settings > General > Updates, Defaults button, close during a folder scan, Recycle Bin undo on a non-English Windows, decoder fallbacks with damaged EXIF/ICC; perf runs for natural sort / snapshot validator and Q-R17 on F4.
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Decision log:** handoff and decisions live on `master` only (no `develop` since #104).

## Status by Group

Group status: see [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md) (single source; do not copy it here).

## Critical Process Rules

- No direct `master` commits: branch → PR → review
- No `ApplyFitViewAsync` single-pass without T89 evidence
- No broad `Dispatcher.Invoke` / `GetRequiredService` (ADR 0005 rule, AR04 DONE)
- No silent `IgnoreInaccessible`; durability changes only per ADR 0007 (journal mode setting, session no-fsync)
- No OS SendInput/SetForegroundWindow in test harnesses
- Never run code that deletes/sweeps the user's real Recycle Bin (tests use fakes)
- Don't compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"
tools/verify-all.ps1
```
