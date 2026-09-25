# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `8de2ac4` (#76-#81 merged) | **Branch:** `perf/q-r17-preload-estimate`

## Now: Q-R17 = D on a branch; RAM-cache % setting and full code review in progress

- **Q-R17 (branch `perf/q-r17-preload-estimate`, PR pending):** whole-folder estimate = box bound, then measured preview mean x 1.25; also fixes a preload-loop crash on synchronously completed `Task<bool>` (see [ROUND3-4](docs/refactoring/REVIEW-2026-09-25-ROUND3-4.md)). Perf run on the user machine (fixture F4) still due.
- **In progress:** RAM cache as % of physical RAM (max 90 %, system minimum) on `feat/ram-cache-percent`; docs budget limits; full code review 2026-09-25 (round 7).
- **User:** GUI check of #78 (multi-select open from Explorer), toolbar focus handback (#81), Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04. The real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven).
- **Plan / evidence:** [plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) · [rounds 3-6](docs/refactoring/REVIEW-2026-09-25-ROUND3-4.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

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
