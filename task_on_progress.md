# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `2d52f6d` (#76 merged) | **Branch:** `docs/decisions-q-r7-r11`

## Now: round-2 decisions implemented, PRs #77 #78 #79 open

- **User:** review + merge #77 (Q-R7), #78 (Q-R10, needs a GUI check: multi-select open from Explorer), #79 (Q-R8); the real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven); visual checks: Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04.
- **Plan / evidence:** [plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

## Review rounds 3+4 (2026-09-25, branch `review/2026-09-25-round3`, PR pending)

- Q-R12..Q-R16 decided and implemented; round-3/4 fixes, deferred items and gate results: [ROUND3-4](docs/refactoring/REVIEW-2026-09-25-ROUND3-4.md). Gate green (0 warnings, non-Manual/Native/Slow tests).

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
