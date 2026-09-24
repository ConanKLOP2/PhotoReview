# TC — Test Cleanup (Summary)

**Status:** TC01–TC11 DONE (TS10 audit complete 2026-09-22; TC04/TC09 via #36). TC06/TC07 live in `tests/PhotoReview.App.Tests/HotPath/` (real Recycle Bin / real photos; moved from Integration.Tests in `440527f`). Decisions Q-T1..Q-T4 finalized:

| Q | Decision |
|---|---|
| Q-T1 Queue or drop actions during busy? | **Drop** — TC05 locks this via test + metric. |
| Q-T2 Seam for source-read probe? | **Yes** (small seam), no behavior change. |
| Q-T3 Real photo fixture path? | **User-provided** via env var `PHOTOREVIEW_FIXTURE_DIR`. |
| Q-T4 Live Recycle Bin test? | **Yes** (temp files only, Undo cleanup). |

Objective, evidence findings (G1–G8), the full TC00–TC11 task table, and acceptance criteria are archived in [`archive/TEST-CLEANUP-SUMMARY-detail.md`](archive/TEST-CLEANUP-SUMMARY-detail.md). Full plan: [`../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`](../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md).

## See Also

- **OC06:** Native Recycle identity acceptance
- **OC11:** Warm-navigation source-read gate
- **DT07:** Test boilerplate consolidation (TC10 follow-up)
