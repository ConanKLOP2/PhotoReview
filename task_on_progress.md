# Current Work — PhotoReview

**Updated:** 2026-09-27 | **Base:** master `1de561c` (#134, tag `v2.0.137`) | **Open PRs:** none (last merged: #128-#134)

## Now

- **Nothing in progress.** Everything up to #134 is merged; the 2026-09-27 docs cleanup removed finished plans/evidence (see [HISTORY](docs/refactoring/HISTORY.md)).
- **GUI check pending (user):** context-menu Zoom (Fit, 200/300/400 %, Zoom levels submenu arrow), "Taken:"/"Modified:" labels (info line + title bar), info-overlay auto-hide (Q-R34), Zoom card in Settings (level, click-to-zoom, arrow step %, shortcuts), sort modes Default / Name A→Z / Z→A (Q-R33), preload window setting (Q-R31). Earlier GUI checks (AR13, AR16, T89 Fit, Q-R30 UI feedback, #109) were reported OK by the user on 2026-09-26.
- **Merged (2026-09-27):** `feat/zoom-key-anchor-kinetic-arrows` (PR-A, #177), `feat/fit-width-height-keep-zoom` (PR-B, #178). Master is at v2.0.184.
- **Open PR (2026-09-27):** `feat/image-crossfade` (PR-D, #181, Q-R37): optional fade when the current photo changes (`ImageTransition` None/Fade, default None, `ImageTransitionMs` default 120 ms 40-400); never on same-image thumbnail/preview/original upgrades. Awaiting review; not merged.
- **Open PR (2026-09-27):** `fix/docs-conflict-markers` (#182): PR #178's merge of origin/master into its own branch left literal `<<<<<<<`/`=======`/`>>>>>>>` conflict markers in four docs files on master; fix removes them. Awaiting review; not merged.
- **Open decision:** Q-R29 (NAS: UI-thread stat in `ThumbnailCache.BuildKey`; no I/O cap on whole-folder preload) — awaiting user, see [OPEN-DECISIONS](docs/refactoring/OPEN-DECISIONS.md).
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Releases are manual (Q-R24):** CI only tags `v2.0.N`; a human runs Actions > Release > Run workflow (tag input). Updates in the app: manual "Check for updates" only (Q-R23).
- **Decision log:** handoff and decisions live on `master` only (no `develop`). Keep this file short: move finished detail to `docs/refactoring/HISTORY.md`.

## Status by Group

Open work: [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md) (single source; do not copy it here). Finished groups: [HISTORY](docs/refactoring/HISTORY.md).

## Critical Process Rules

- No direct `master` commits: branch → PR → review
- No `ApplyFitViewAsync` single-pass without T89 evidence
- No broad `Dispatcher.Invoke` / `GetRequiredService` (ADR 0005 rule)
- No silent `IgnoreInaccessible`; durability changes only per ADR 0007 (journal mode setting, session no-fsync)
- No OS SendInput/SetForegroundWindow in test harnesses
- Never run code that deletes/sweeps the user's real Recycle Bin (tests use fakes)
- Don't compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/refactoring/HISTORY.md) · [Perf](docs/refactoring/PERF-STATUS.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"
tools/verify-all.ps1
```
