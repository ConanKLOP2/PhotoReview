# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `ebf2bf3` (#82 Q-R17, #83 docs budget, #84 RAM % merged; v2.0.87) | **Branch:** `develop` (decision log + handoff)

## Now: 9 agent branches → one integration PR (read this first after a new session)

- **Handoff:** [WORK-2026-09-25](docs/refactoring/WORK-2026-09-25-ROUND7-FEATURES.md) — the 9 branches (5 features incl. Q-R18, 4 round-7 fix branches), their contracts, the integration + Settings-redesign plan. Agents work in `.claude/worktrees/agent-*`; check `git ls-remote origin` for which branches are pushed.
- **Branch `develop`:** long-lived decision log (OPEN-DECISIONS, this file, WORK docs). Merge to master by PR from time to time; feature/fix branches still start from master.
- **User:** review Q-R19 (defaults chosen by Claude); perf run for Q-R17 on F4; GUI checks listed in the WORK doc plus #78 multi-select open, toolbar focus handback (#81), Recovery, Settings journal option, VI wording, Fit first frame (T89), AR04. The real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven).
- **Evidence:** [archived review plan](docs/archive/historical/REVIEW-2026-09-25-PLAN.md) · [rounds 3-6](docs/refactoring/REVIEW-2026-09-25-ROUND3-4.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

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
