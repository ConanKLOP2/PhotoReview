# Current Work — PhotoReview

**Updated:** 2026-09-22 | **Branch:** `master` | **Last merge:** #15 (Fit-Layout-Status)

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| ST | ✅ Mostly done (ST08/09 blocked) | ST01-07, ST10-12 merged. Blocked on OC14. |
| TS | ✅ TS00-04 done | Gate ~23s (no hang). TS05-10 remain. TS10: re-audit before TC. |
| DF | ✅ Done | PR #14 merged. |
| **T89** | 🔄 IN PROGRESS | GUI/STA acceptance on `feature/Fit-Layout-Status`. Not on `master` yet. |
| **TC** | 🔄 TODO | Blocked on TS10 re-audit. 23 plans per Q-T1..Q-T4. |
| **OC** | 🔄 ~65% done | OC14 (Undo) blocks ST08/09, WD, OC15-18. |
| **WD** | 🔄 TODO | Blocked on OC14. |
| **IO** | 🔄 TODO | Blocked on IO01/02 contract. |
| **D** | 🔄 Mixed | D06 Procmon data needed for D01/02/08/09. |
| **DT** | 🔄 TODO | Docs token diet: DT00 baseline taken, DT01+ in progress. |

## Key Blockers

1. **OC14** (Undo unification) — critical path for ST08/09, WD, OC15-18
2. **T89** GUI acceptance — needed before merging Fit to `master`
3. **IO01/02** journal contract — gates I/O durability work
4. **TS10** audit — required before trusting TC status

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01
- ❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02
- ❌ No OS SendInput/SetForegroundWindow in test harnesses

## Key Links

- **Master plan:** [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md)
- **Structure status:** [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md)
- **Test plan:** [`docs/refactoring/TEST-SPEED-PLAN-2026-09-20.md`](docs/refactoring/TEST-SPEED-PLAN-2026-09-20.md)
- **Fit layout:** [`docs/refactoring/T89-FIT-LAYOUT-PLAN.md`](docs/refactoring/T89-FIT-LAYOUT-PLAN.md)
- **Docs diet:** [`docs/INDEX.md`](docs/INDEX.md)
- **History:** [`docs/archive/progress-log-2026-09.md`](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
# Budget check
tools/docs-budget.ps1 -Check

# Test gate
dotnet test PhotoReview.sln --filter Category=Gate

# Build Release
dotnet build -c Release PhotoReview.sln
```
