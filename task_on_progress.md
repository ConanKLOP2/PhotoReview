# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `cbd24b8` (v2.0.71) | **Branch:** `docs/review-plan-2026-09-25`

## Now: #64–#71 merged; open: #72 DT10, #73 ST08/09+OC15–18, L12 branch

- **Merged:** #64 OC14 `FileActionGate` · #65 IO03 journal durability setting (ADR 0007) · #66 IO04 session no-fsync + IO05 skip unreadable · #67 Recovery paths + live check · #68–#70 test diet (#69 fixed Ctrl+Z after Recycle) · #71 status docs.
- **User:** empty ~2650 `...\Temp\TC06_RecycleBin_*` items from the Recycle Bin; visual checks: Recovery window, Settings journal option, dark dialogs, language picker, zoom, Fit first frame (T89), AR04.
- **Next:** [review 2026-09-25 plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) — answer Q-R1..Q-R6; waves 1a, 1b, 2b ready (bin leak = TEST-10, wave 2a).

## Previous (merged): i18n · perf night #39-#49 · AR00-AR07 · ADR 0007

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR07 all DONE | GUI acceptance (T89, AR04) by user. |
| ST | ✅ (ST08/09 wait OC14) | ST06 public fields replaced by AR02d (#35). |
| TS | ✅ TS00-07, TS10 | TS08/09 closed (Q-AR5). |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only (kept, Q-AR5) | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | ✅ TC01-TC11 | TC04, TC09 done (#36); TC06/07 live in App.Tests/HotPath (real Recycle Bin / real photos). |
| **OC** | 🔄 ~65% | OC14 gate moved to the ViewModel (#64); ST08/09, OC15-18 unblocked. |
| **WD** | ✅ WD01 done (AR04, #37) | WD02-06 closed 2026-09-23 (Q-AR5, no known dialog bug). |
| **IO** | 🔄 IO01 ADR 0007 | IO03 #65, IO04+IO05 #66 open; IO02/06/07 closed. |
| **D** | ❌ Closed (Q-AR5) | Legacy `--perf-session` numbers; re-open from AR02e baseline if needed. |
| **DT** | 🔄 | DT00-03, 08, 09 done; DT04-07 closed (Q-AR5); DT10 kept. |

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01 (→ replaced by ADR 0005 rule once AR04 is DONE)
- ❌ No silent `IgnoreInaccessible`; durability changes only as decided in ADR 0007 (journal mode setting, session no-fsync)
- ❌ No OS SendInput/SetForegroundWindow in test harnesses
- ❌ Do not compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow&Category!=Stress"
tools/verify-all.ps1
```
