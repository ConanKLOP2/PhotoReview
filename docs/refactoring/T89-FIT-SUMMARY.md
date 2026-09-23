# T89 — Fit Layout: Double-Click Issue (Summary)

**Status:** Transaction Fit + UI viewport refresh implemented on `feature/Fit-Layout-Status` (not on `master`). Acceptance criteria: single Fit idempotent, ≤0.5 DIP error, no STA-blocking timeouts.

**GUI acceptance & STA layout tests still TODO** — must verify before merge to `master`.

Full plan: [`../archive/historical/T89-FIT-LAYOUT-PLAN.md`](../archive/historical/T89-FIT-LAYOUT-PLAN.md)

## Problem

After zoom, **Fit button requires two presses:** first press nearly fits image; second press fits precisely. Should be one press.

## Root Causes

| ID | Cause | Evidence |
|---|---|---|
| **C1** | Scrollbar visibility changes viewport size during layout | `ViewportWidth/Height` depend on scrollbar; initial press uses pre-hidden-scrollbar size; scrollbar vanishes, layout recalculates; second press has correct viewport |
| **C2** | Production initialization callback passes `(0,0)` for viewport | `App.xaml.cs:189` calls `ApplyInitialViewMode(..., 0, 0)` instead of real viewport size; InitialViewMode=Fit doesn't match button Fit contract |
| **C3** | LayoutTransform adds layout cycles | Zoom via `LayoutTransform` requires multiple measure/arrange passes; viewport values may lag |

## Acceptance Criteria

Single Fit press must:

1. Cancel pending wheel offsets
2. Set `Zoom=1, Stretch=Uniform`
3. Use final viewport size (after scrollbars hidden)
4. Image fully fits, no crop, no leftover scroll offset
5. Two consecutive Fit presses give same result (idempotent)
6. Button, key, and InitialViewMode all use same contract
7. **No `Task.Delay` / timespan guesses** — use layout barrier or versioning

## Implementation Status

### ✅ Done

- Transaction Fit protocol (DF00-DF07 merged, PR #14)
- Wheel zoom engine with anchor math
- `LayoutTransform` substituted for `RenderTransform`
- Initial Fit button code (transaction protocol in place)

### 🔄 In Progress / TODO

- **GUI acceptance:** Manual test on real 4K monitor with DPI scaling, resolution changes, window resize, compare panel toggle
- **STA layout tests:** WPF-specific test harness measuring final position/size with ≤0.5 DIP tolerance
- **Merge to `master`:** Blocked until GUI acceptance passed

## See Also

- **OC07:** Display state refinements (related)
- **TS10:** Test audit (must verify T89 test claims before TC starts)
- **ST08/ST09:** Controller extraction (blocked on OC14, not T89)
