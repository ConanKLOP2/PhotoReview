# Current Work — PhotoReview

**Updated:** 2026-09-23 | **Base:** `master@b2048b6` (#23) | **Branch:** `docs/ar07-docs-repair` (PR #25)

## Now: Q-AR1..5 decided (2026-09-23) → PRs open, none merged

- Decisions (see `docs/refactoring/OPEN-DECISIONS.md`): Q-AR1=A, Q-AR2=yes (ADR 0005 Accepted), Q-AR3=yes (ST06 fields → AR02d), Q-AR4=yes, Q-AR5=AR07 §5 table.
- Open PRs: #24 AR03 · #25 AR07 (this) · #26 AR06 · #27 AR02a (base #24) · #28 AR01 · #29 AR02c, #30 AR02b (base #27). No conflicts between them. Merge: #25, #26, #28, #24 → #27 → #29, #30.
- User, real machine: Fit GUI check (#27 + T89), TurboJPEG check (#28), then **AR02e** baseline → AR02d → AR04.

## Previous: nav hot-path perf pass — DONE (#20 `94c5aeb..20300b4`, fix #21 `9161c35`)

Details: `docs/archive/progress-log-2026-09.md` (2026-09-23 entry). Still deferred, **not fixed**:
- `ConfigureAwait(false)` in App / catalog mutated off UI thread → now **AR04** (ADR 0005).
- RAM-budget accuracy, disk-cache value, `SourceBytesCache` + preview budget > physical RAM, lazy EXIF/TurboJpeg transform → need real-folder numbers from the **AR02e** baseline (old `--perf-session` ran without preload, F2).
- `GetOriginalDimensionsAsync` key reuse: implemented then reverted (race trade-off), keep reverted.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00, AR07; 🔄 PRs #24, #26–#30 | Next: AR02e, AR02d, AR04. |
| ST | ✅ (ST08/09 wait OC14) | ST06 fields to be replaced by AR02d (Q-AR3=yes). |
| TS | ✅ TS00-04, TS10 | TS05-07 open; TS08/09 closed (Q-AR5). |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only (kept, Q-AR5) | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | 🔄 | TC04, TC09 open (kept); TC06/07 kept in Integration.Tests (documented, Q-AR5). |
| **OC** | 🔄 ~65% | OC14 kept, re-scoped to "Undo gate location" (Q-AR5) — blocks ST08/09, OC15-18; no longer blocks WD (Q-AR2=yes). |
| **WD** | ✅ WD01 unblocked (AR04/ADR 0005) | WD02-06 closed 2026-09-23 (Q-AR5, no known dialog bug). |
| **IO** | 🔄 IO01 only (kept) | IO02-07 closed 2026-09-23 (Q-AR5, speculative). |
| **D** | ❌ Closed (Q-AR5) | Legacy `--perf-session` numbers; re-open from AR02e baseline if needed. |
| **DT** | 🔄 | DT00-03, 08, 09 done; DT04-07 closed (Q-AR5); DT10 kept. |

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01 (→ replaced by ADR 0005 rule once AR04 is DONE)
- ❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02
- ❌ No OS SendInput/SetForegroundWindow in test harnesses
- ❌ Do not compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [AR summary](docs/refactoring/ARCH-REVIEW-SUMMARY.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [Structure status](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md) · [T89 summary](docs/refactoring/T89-FIT-SUMMARY.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
tools/verify-all.ps1
```
