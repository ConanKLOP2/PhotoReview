# Current Work — PhotoReview

**Updated:** 2026-09-26 (late night) | **Base:** master `880370b` (#94–#112 merged) | **Open PRs:** none

## Now

- **Done 2026-09-26 (#94–#112 merged):** architecture review AR10–AR19 ([summary](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md); #106, AR16/Q-AR7 (c) background readability probe #107), benchmark speed-up #109 (gate −34 %, quick −24 %, in-app Quick check), stale Slow MainWindow tests #110, journal test-host crash #112, source audit F0–F4 closed. Details and numbers: [progress log](docs/archive/progress-log-2026-09.md#2026-09-26-late), `PERF-STATUS.md`.
- **GUI checks — user reported "GUI OK" (2026-09-26):** AR13 (pan + glide, wheel zoom at cursor, Ctrl+wheel navigation, click-to-zoom, Fit after resize/DPI change, skipped-files list), AR16 (locked file: first image at once, then the warning), Settings > Updates and Defaults, close during a folder scan, Recycle Bin undo, decoder fallbacks with damaged EXIF/ICC, T89 Fit.
- **User GUI check still open:** #109 benchmark — Quick check button, image limit 5 / 0, close the Benchmark window mid-run (no crash, no leftover `%TEMP%\PhotoReview-Benchmark-Cache\*`).
- **Measured 2026-09-26 (PERF-STATUS):** natural sort / snapshot validator confirmed on an idle PC (validator ×2.36, sort ×1.59, no allocations); Q-R17 on F4 fills the whole folder (~10 GB) but first-visual latency is ~2.5–3× higher in steady state → **Q-R26 = B** (user): keep Q-R17, find the cause. Round 1 (branch `perf/q-r26-gc-investigation`, harness now logs GCs and page faults): not GC, not the page-fault count; noisy machine — rerun on an idle PC next.
- **Still to do:** Q-R26 round 2 on an idle PC (Claude): rerun master vs variant, whole-folder mode on a small folder, ETW hard/soft faults and memory compression during navigation.
- **Q-R27 = D (#116):** each running operation holds a named kernel event (`WindowsLiveOperationRegistry`) from Prepared to its outcome; startup reconcile skips live operations, so a second process no longer reports them Failed.
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Decision log:** handoff and decisions live on `master` only (no `develop` since #104). Keep this file short: move finished detail to the progress log.

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
