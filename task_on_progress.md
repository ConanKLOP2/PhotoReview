# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `fa035e6` | **Branch:** `integration/2026-09-25-features` (one PR)

## Now: integration PR for review round 7 + viewer features + Settings redesign

- **What:** [WORK-2026-09-25](docs/refactoring/WORK-2026-09-25-ROUND7-FEATURES.md) — 9 agent branches + Settings redesign + strict i18n-check + flaky-test fix, all merged into `integration/2026-09-25-features`; gate green (1779 tests).
- **User:** review Q-R19 (defaults chosen by Claude, see OPEN-DECISIONS); GUI checks listed in the WORK doc (Settings pages, click-zoom/kinetic, EXIF line, folder info, Explorer double-click in both instance modes, Recycle restore with hidden extensions, close during a cross-drive Move); perf run for Q-R17 on F4; Native `NativeRecycleBinTests` only if you want (touches the real bin).
- **After merge:** fast-forward `develop` to master; delete merged agent branches; Release build per CLAUDE.local.md.

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
