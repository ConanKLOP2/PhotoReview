# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `7f4c90b` | **Branch:** `docs/i18n-plan` (L00)

## Now: I18N (group L) — EN + VI, community-editable JSON catalogs

- Plan `docs/refactoring/I18N-PLAN.md`, ADR 0006; Q-L1..Q-L8 all = recommendation (user, 2026-09-24).
- L00 (docs) on `docs/i18n-plan`. Code L01+ goes on a separate branch based on `master` (no stacked PRs).

## Previous: perf night 2026-09-24 — all merged (#39–#49, #53)

- Result (real folder): open folder 489 → 166 ms, burst shown 55 → 200/200, peak WS 5.2 → 1.7 GB, app start → first image 3.2 → 1.8 s. Table: `docs/refactoring/PERF-STATUS.md` ("Perf night").
- **Decisions taken:** zoom = option A (user, 2026-09-24): "100 %" = 1 source pixel; wheel zoom from Fit steps from the fit size. #46 INV-9 change accepted by merge: opened photo shows before Explorer order; navigation waits for it (≤ ~2 s).
- Fixed along the way (all lost in T46d): preview decode width (#31), folder trace events (#46), Original loading mode (#48).
- Left for user (visual): Fit first-frame (T89), TurboJPEG item greyed without dll, AR04 GUI acceptance, #47 zoom feel. CI tags missing for runs cancelled by quick successive merges (e.g. v2.0.44/45/48) — tag job could backfill.
- **Next:** OC14 (Undo gate → MainViewModel), IO01 (needs decision), DT10.

## Also 2026-09-24: Recovery clear — #57 merged (v2.0.59, `3be2644`)

- Recovery window: "Xoá mục đã chọn" (multi-select) + "Xoá tất cả", with confirm. Appends `JournalState.Dismissed` under the same Id (journal stays append-only; latest entry wins) — no file is touched. `OperationJournal.Dismiss`.
- Build 0/0, all tests pass. GUI click-through not done (needs a journal with failed entries).

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
