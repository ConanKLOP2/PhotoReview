# Current Work — PhotoReview

**Updated:** 2026-09-27 | **Base:** origin/master `23230b8` | **Open PR:** #196 (review handoff); #185 merged

## Now
- **Review #185 merged as `1c167cd`:** five docs-only files; [WORK report](docs/refactoring/WORK-FULL-CODE-REVIEW-2026-09-27.md) has 18 findings, [ledger](docs/refactoring/FUNCTION-BODY-AUDIT-2026-09-27.tsv) has 8,586 bodies at source base `3ef2bb5`. 8,096 rows have screening only; 289 need deeper review. #191 measured R01/R02/R03/R13 as negligible on local NVMe (NAS open); #192 fixed R09 and traced R10 as non-issue with a regression test. #190-#192 changed code/tests after the ledger baseline; re-inventory that delta before a current-tree claim.
- **Validation:** Release build on rebased `3ef2bb5` tree passed with 0 warnings; #188's `PerfTraceTests` passed 9/9. The complete filtered suite passed earlier on `f82084d` (Architecture 63, Imaging 576, Core 1593, App 1132 + 1 skipped, Integration 587) but was not rerun after #188. Runtime NAS/GUI measurements are still needed for optimization claims.
- **GUI check pending (user):** context-menu Zoom and new #183 layout/dark style, "Taken:"/"Modified:" labels, info-overlay auto-hide, Zoom card, sort modes, preload-window setting, #181 fade and #177/#178 zoom behavior. Earlier GUI checks (AR13, AR16, T89 Fit, Q-R30 UI feedback, #109) were reported OK by the user on 2026-09-26.
- **Merged (2026-09-27):** #177, #178, #181, #182, #183 and #188 are on fetched master. #188 strengthens two PerfTraceTests fixtures; #183 adds the redesigned right-click menu and Q-R38 settings. #189 updates AGENTS.md.
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
