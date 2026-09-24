# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `67f5aae` (v2.0.64) | **Branch:** `chore/post-i18n-cleanup`

## Now: i18n merged (#61, #55); IO01 decided (ADR 0007, PR #62)

- **i18n (group L) done L00–L11:** EN + VI, community JSON catalogs, `Tr`/`{loc:Tr}`, `tools/i18n-check.ps1` in CI, `docs/TRANSLATING.md`, ADR 0006, plan `docs/refactoring/I18N-PLAN.md`. Open: **L12** Vietnamese copy polish (user reviews wording).
- **IO01 decided (user, 2026-09-24) — ADR 0007:** journal durability = setting, **Fast (no fsync, default)** or **Power-loss safe** (WriteThrough+Flush, written off the UI thread); session no fsync (atomic kept, corrupt = no session), settings unchanged; unreadable files skipped **with a visible warning**. Implement **IO03/IO04/IO05** next (i18n no longer blocks; new UI strings go through catalogs).
- Cleanup after i18n: removed obsolete `MainViewModel.UndoLastAsync` / `FileActionController.UndoAsync`; status docs refreshed.
- **Next:** IO03–IO05 → OC14 (Undo gate → MainViewModel; unblocks ST08/09, OC15–18) → DT10.
- Left for user (visual): Fit first-frame (T89), TurboJPEG item greyed without dll, AR04 GUI acceptance, zoom feel (#47), new dark dialogs + language picker.

## Previous (all merged): perf night #39-#49 (see `docs/refactoring/PERF-STATUS.md`) · AR00-AR07 · Recovery clear #57 · dark dialogs #53-#59

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR07 all DONE | GUI acceptance (T89, AR04) by user. |
| ST | ✅ (ST08/09 wait OC14) | ST06 public fields replaced by AR02d (#35). |
| TS | ✅ TS00-07, TS10 | TS08/09 closed (Q-AR5). |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only (kept, Q-AR5) | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | ✅ TC01-TC11 | TC04, TC09 done (#36); TC06/07 live in App.Tests/HotPath (real Recycle Bin / real photos). |
| **OC** | 🔄 ~65% | OC14 kept, re-scoped to "Undo gate location" (Q-AR5) — blocks ST08/09, OC15-18; no longer blocks WD (Q-AR2=yes). |
| **WD** | ✅ WD01 done (AR04, #37) | WD02-06 closed 2026-09-23 (Q-AR5, no known dialog bug). |
| **IO** | 🔄 IO01 decided: ADR 0007 (2026-09-24) | Implement IO03 journal setting, IO04 session, IO05 skip+warn; IO02/06/07 stay closed. |
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
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
tools/verify-all.ps1
```
