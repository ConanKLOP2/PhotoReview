# T89 — Fit Layout: Double-Click Issue (Summary)

**Status:** Transaction Fit + UI viewport refresh merged to `master` via PR #15 (`feature/Fit-Layout-Status` deleted). Code is on `master`.

**Only GUI acceptance remains** — STA/GUI layout verification still TODO (kept open, Q-AR5 2026-09-23):

- Manual test on real 4K monitor with DPI scaling, resolution changes, window resize, compare panel toggle
- STA layout tests: WPF-specific test harness measuring final position/size with ≤0.5 DIP tolerance
- Merge to `master` is already done; this acceptance just confirms the GUI behavior

Problem statement, root causes (C1–C3), and the full acceptance-criteria list are archived in [`archive/T89-FIT-SUMMARY-detail.md`](archive/T89-FIT-SUMMARY-detail.md). Full plan: [`../archive/historical/T89-FIT-LAYOUT-PLAN.md`](../archive/historical/T89-FIT-LAYOUT-PLAN.md).

## See Also

- **OC07:** Display state refinements (related)
- **ST08/ST09:** Controller extraction (done in #73, not T89)
