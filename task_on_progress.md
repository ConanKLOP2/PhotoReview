# Current Work — PhotoReview

**Updated:** 2026-09-26 | **Base:** master `3ea2555`+ (PRs #94-#96 open) | **Branches:** `integration/review-2026-09-26` (PR #96), `integration/wave5-2026-09-26` (everything, incl. waves 3-5)

## Now: overnight review 2026-09-26 (Q-R25)

- **What:** 7 lane review branches (fileactions, core, imaging, platform, app, tools/CI, test quality) = PR #96; ~20 more small-agent branches (`opt/*`, `fix2/*`) merged into `integration/wave5-2026-09-26`: bug fixes, dead-code removal, measured optimisations, cross-review fixes. Gate on wave5: build 0 warnings, default tests green, `i18n-check` PASS. PR order: #94 (flaky fix), #95 (update button), #96, then a PR from `integration/wave5-2026-09-26`.
- **User:** merge order above; GUI checks: Settings > General > Updates, Defaults button (WIC, EXIF off), close during a folder scan, Recycle Bin undo on a non-English Windows (`undelete` fallback is unverified), decoder fallbacks with damaged EXIF/ICC; real-machine perf run for the measured optimisations (natural sort, snapshot validator) on F4.
- **Decisions needed:** see Q-R25 (APP-03, Enter/Return, DuplicateCleanup cancellation, journal ownership marker, Actions pinned by SHA).

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
