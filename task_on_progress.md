# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `2a76263` (v2.0.x, #64–#75 merged) | **Branch:** `review/2026-09-25-integration`

## Now: review 2026-09-25 — waves implemented on the integration branch, PR pending

- **Done on the branch (cherry-picked, all gates green):** W1a (retry off UI, ConfigureAwait, SessionWriter 2 s, COM) · W1b (alpha previews not cached, preload headroom) · W1c (destination policy) · W2a (CA2007, CI Integration step, TestIsolation rule) · W2b (TEST-04/05/06; TEST-03 sweep declined, Q-R9) · W3 docs · W4 a11y + `Dark.*` · W5a/b/c · round 2: R2-A fixes, R2-F-01/02/03/04/05/06/07/08/14/15/18.
- **In progress:** round-2 mediums/lows (branches `review/r2-f3` App, `review/r2-f4` non-App) — then PR from the integration branch.
- **User:** answer Q-R7..Q-R9 ([OPEN-DECISIONS](docs/refactoring/OPEN-DECISIONS.md)); the real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven); visual checks: Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04.
- **Plan / evidence:** [plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

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
