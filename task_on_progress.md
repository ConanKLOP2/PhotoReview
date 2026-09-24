# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master `cbd24b8` (#64–#71 merged) | **Branch:** `i18n/l12-vi-copy`

## Now: L12 Vietnamese copy polish — PR open, needs user wording review

- `vi.json`: 236 values changed (keys unchanged): one term per concept (thư mục, tệp, Thùng rác, hành động = user action,
  thao tác = operation, Di chuyển/Sao chép, thử lại, hoàn tác, Phục hồi, ảnh xem trước, Vừa khung), typo "bấp", `Xoá`→`Xóa`,
  English leftovers (Recovery/Diagnostics/enums). Glossary added to `docs/TRANSLATING.md`. Tests pinning old VI text updated.
- Validation: i18n-check PASS, build 0 warnings, fast suite 0 failures. Every changed string is listed in the PR body.

## Previous: PR batch #64–#70 (all merged)

- **Chain (each branch contains the previous):** #64 OC14 `FileActionGate` in the ViewModel → #65 IO03 journal durability setting (Fast default / power-loss safe, ADR 0007) → #66 IO04 session no-fsync + IO05 skip unreadable files with a warning → #67 Recovery: source/destination paths, live validity check on open, verdicts.
- **Test diet (any order):** #68 Imaging 339→305 · #69 App/Integration + TC06 fix · #70 Core/Architecture; removals have mutation evidence.
- **#69 also fixes a real bug:** Ctrl+Z after Recycle never restored (shell mtime is whole-second UTC, parsed as local ⇒ 7 h off). TC06 really verifies restore now and cleans its own Recycle Bin items.
- **User:** empty the ~2650 test items (original location `...\Temp\TC06_RecycleBin_*`) from the Recycle Bin; visual checks: Recovery window, Settings (journal option), dark dialogs, language picker, zoom, Fit first frame (T89), AR04.
- **Next:** DT10 · ST08/09, OC15–18 · benchmark `action-delete` leaves ~2 items/run in the real bin.

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
