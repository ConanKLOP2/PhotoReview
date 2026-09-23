# Current Work — PhotoReview

**Updated:** 2026-09-23 | **Base:** `master@4cf6d6d` (#33) | **Branch:** `fix/land-ar02bc-and-31`

## Now: AR01, AR02a/b/c, AR02e, AR03, AR06, AR07 DONE (merged 2026-09-23)

- Merged: #24 AR03 · #25 AR07 · #26 AR06 · #27 AR02a · #28 AR01 · #32 TC09/TS02 · #33 enum dict keys. #29 AR02c, #30 AR02b, #31 were merged into `refactor/ar02a-apphost` *after* #27, so they reached master only via `fix/land-ar02bc-and-31` (this PR). Decisions: `docs/refactoring/OPEN-DECISIONS.md`.
- **#31 fixed a regression since T46d**: Preview decoded at full size. **AR02e baseline** (real folder, 1625 files, 13.2 GB) in `docs/refactoring/PERF-STATUS.md`: S3 burst images shown 27–47 → 145–175 /200, peak WS 16.9 → 7.0 GB.
- TurboJPEG checked headless (registered; missing dll → not registered). On this set it is **slower** than WicDirect at 2190 px (P50 590 vs 368 ms) — keep WicDirect default.
- Left for user (visual): Fit first-frame (T89), Settings TurboJPEG item greyed without dll. **Next:** AR02d, then AR04 (gate = PERF-STATUS "After").

## Previous: nav hot-path perf pass — DONE (#20 `94c5aeb..20300b4`, fix #21 `9161c35`)

Details: `docs/archive/progress-log-2026-09.md`. Deferred: App `ConfigureAwait(false)` → AR04; RAM budget / disk-cache value / lazy EXIF → tune from AR02e numbers; `GetOriginalDimensionsAsync` key reuse stays reverted.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR03, AR02e, AR06, AR07 | Next: AR02d, AR04. |
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
