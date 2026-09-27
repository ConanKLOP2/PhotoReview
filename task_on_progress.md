# Current Work — PhotoReview

**Updated:** 2026-09-27 | **Base:** origin/master `fa1b4e6` (merge of `436656e`) | **Open PR:** #197 (R08 fix + R12 doc-comment retarget)

## Now
- **PR #197 open:** the last two open codex-review findings ([WORK report](docs/refactoring/WORK-FULL-CODE-REVIEW-2026-09-27.md)). R08: disconnect-after-accept in `InstanceForwardServer`/`Client` could report `NoInstance` and open a second window behind a live owner; fixed with a one-byte accept marker + regression test. R12: retargeted 8 stale doc-comment references across 6 files (deleted `PERF-DIAGNOSIS-PLAN.md`, wrong `PERF-DIAGNOSIS-TASKS.md` path, dangling AR14/io-decode-split.md); left `AGENTS.md` comment refs alone (file exists, review's scan was wrong there). Full filtered suite + doc checks pass.
- **Review #185 (merged `1c167cd`):** R09/R10 fixed/ruled-out in #192, R01/R02/R03/R13 measured negligible in #191, R08/R12 fixed in #197. Remaining (R05-R07, R11, R14-R18) are conditional perf candidates needing real-machine measurement before any code change; ledger in [FUNCTION-BODY-AUDIT-2026-09-27.tsv](docs/refactoring/FUNCTION-BODY-AUDIT-2026-09-27.tsv) predates #190-#195.
- **GUI check pending (user):** context-menu Zoom and #183 layout/dark style, "Taken:"/"Modified:" labels, info-overlay auto-hide, Zoom card, sort modes, preload-window setting, #181 fade and #177/#178 zoom behavior.
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
