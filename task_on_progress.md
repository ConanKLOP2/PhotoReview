# Current Work — PhotoReview

**Updated:** 2026-09-26 (evening) | **Base:** master `985d524` (PRs #94–#103 merged) | **Open PRs:** none known besides this docs PR

## Now

- **Branch model change (this PR):** `develop` is retired. The handoff (`task_on_progress.md`), `docs/refactoring/OPEN-DECISIONS.md` and `docs/INDEX.md` live on `master` only; a PR that changes project state updates them in the same PR, and a decision taken outside a code PR gets a small docs PR. Reason: `develop` fell 170 commits behind, was merged once (#85), and master's copy of the handoff went stale — two sources of truth.
- **Merged to master 2026-09-26:** #94–#97 (overnight review + wave5), #98 (AGENTS decision format), #99/#100 (Actions pinned by SHA + Dependabot), #101 (Codex full-source audit, F2 by design), #102 (architecture review plan), #103 (Q-R25 policy decisions + audit fixes: Undo after folder change/APP-03, canonical shortcut keys Enter/Return, Esc cancels duplicate-check hashing, F0/F1/F3/F4, thread-pool pre-warm in tests). All their branches and worktrees can be deleted.
- **Architecture review plan (#102):** [ARCH-REVIEW-2026-09-26-SUMMARY](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md) — tasks AR10–AR19 all TODO. With #103 merged nothing blocks Wave 1 (AR11, AR12, AR15). Q-AR6..Q-AR10 wait for the user (options + recommendation in each `arch-review/AR1x` file).
- **User (GUI / real machine), still open from the overnight review:** Settings > General > Updates, Defaults button (WIC, EXIF off), close during a folder scan, Recycle Bin undo on a non-English Windows (`undelete` fallback unverified), decoder fallbacks with damaged EXIF/ICC; real-machine perf run for natural sort / snapshot validator on F4; Q-R17 preload-estimate perf run.
- **Caution (from the 2026-09-25 audit):** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it until isolated.
- **Local build:** `src/PhotoReview.App/bin/Release/net10.0-windows` last rebuilt from master `fac8347` (2.0.106); rebuild after #103.

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
