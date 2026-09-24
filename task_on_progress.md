# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `725b44d` (#64–#74 merged) | **Branch:** `docs/review-plan-2026-09-25`

## Now: review 2026-09-25 plan (PR #75)

- **Merged:** #64 OC14 `FileActionGate` · #65–#66 IO03–IO05 (ADR 0007) · #67 Recovery paths + live check · #68–#70 test diet (#69 fixed Ctrl+Z after Recycle) · #71 status · #72 DT10 · #73 ST08/09 + OC15–18 · #74 L12 VI copy.
- **User:** empty ~2650 `...\Temp\TC06_RecycleBin_*` items from the Recycle Bin; visual checks: Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04.
- **Next:** [review 2026-09-25 plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) — answer Q-R1..Q-R6; waves 1a, 1b, 2b ready.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR07 | GUI acceptance (T89, AR04) by user. |
| ST, TS, TC, DF, CQ, DT, IO | ✅ Done | ST08/09 #73; DT10 #72; IO per ADR 0007 (#65, #66). |
| **T89** | 🔄 GUI acceptance only | Code merged (#15); verify with AR02a Fit viewport. |
| **OC** | 🔄 GUI check only | OC14 #64; OC15–18 #73 (OC18 Fit check with T89). |
| **D**, WD02-06, DT04-07 | ❌ Closed (Q-AR5) | |

## Critical Process Rules

- No direct `master` commits: branch → PR → review
- No `ApplyFitViewAsync` single-pass without T89 evidence
- No broad `Dispatcher.Invoke` / `GetRequiredService` (ADR 0005 rule, AR04 DONE)
- No silent `IgnoreInaccessible`; durability changes only per ADR 0007 (journal mode setting, session no-fsync)
- No OS SendInput/SetForegroundWindow in test harnesses
- Don't compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow&Category!=Stress"
tools/verify-all.ps1
```
