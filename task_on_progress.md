# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `b6e5dae` (#38) | **Branch:** `docs/perf-night-2026-09-24`

## Now: perf night 2026-09-24 — PRs #39–#48 open (none merged yet)

- Result (real folder): open folder 489 → 166 ms, burst shown 55 → 200/200, peak WS 5.2 → 1.7 GB, app start → first image 3.2 → 1.8 s. Table: `docs/refactoring/PERF-STATUS.md` ("Perf night").
- **Merge order:** #39 (harness) → #40, #41, #42 → #44, #45 → #46, #48 → then #43 + #47 together (after the zoom decision). Branches after #39 contain earlier PRs' commits (no stacked bases) — merging out of order is safe but brings those commits along.
- **User decisions:** (1) zoom semantics — #43 (decode to viewport box, big win) makes zoom soft unless #47 (option A: "100 %" = 1 source pixel, on-demand original) is merged; #47 also makes wheel zoom from Fit step from the fit size. (2) #46 changes INV-9 timing: the opened photo shows before Explorer order arrives; navigation waits for the order (≤ ~2 s).
- Found + fixed along the way (all lost in T46d): preview decode width (#31), folder trace events (#46), Original loading mode (#48).
- Left for user (visual): Fit first-frame (T89), Settings TurboJPEG greyed without dll, AR04 GUI acceptance, #47 zoom feel. Note: #46 measurement overwrote `%LOCALAPPDATA%\PhotoReview\window-placement.json`.
- **Next:** OC14 (Undo gate → MainViewModel), IO01 (needs decision), DT10.

## Previous: AR00–AR07 done (#23–#37); nav perf pass #20/#21 — details `docs/archive/progress-log-2026-09.md`.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR07 all DONE | GUI acceptance (T89, AR04) by user. |
| ST | ✅ (ST08/09 wait OC14) | ST06 public fields replaced by AR02d (#35). |
| TS | ✅ TS00-07, TS10 | TS08/09 closed (Q-AR5). |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only (kept, Q-AR5) | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | ✅ TC01-TC11 | TC04, TC09 done (#36); TC06/07 kept in Integration.Tests (documented, Q-AR5). |
| **OC** | 🔄 ~65% | OC14 kept, re-scoped to "Undo gate location" (Q-AR5) — blocks ST08/09, OC15-18; no longer blocks WD (Q-AR2=yes). |
| **WD** | ✅ WD01 done (AR04, #37) | WD02-06 closed 2026-09-23 (Q-AR5, no known dialog bug). |
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
