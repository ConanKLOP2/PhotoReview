# T89 Implementation Progress — Fit Layout Convergence

**Date:** 2026-09-20  
**Goal:** Complete T89.1-T89.2 (trace probe + convergence helper)  
**Status:** IN PROGRESS

---

## Work Breakdown

### T89.1 — Layout Trace Probe (Diagnostic)

- [ ] Create `FitLayoutProbe` class to capture state snapshots
- [ ] Capture: zoom, stretch, max-image-size, actual-image-size, viewport, extent, offsets, scrollbar visibility, operation version
- [ ] Call points: before Fit, after ResetFit, after each layout pass, after offset reset
- [ ] Output: structured trace (can print on demand or append to log)
- [ ] No perf impact: diagnostic-only, not enabled by default

### T89.2 — Convergence Snapshot & Epsilon Helper

- [ ] Create `ViewportSnapshot` record (immutable state capture)
  - Zoom, Stretch, MaxImageWidth/Height
  - ActualWidth/Height, ExtentWidth/Height
  - ViewportWidth/Height, HorizontalOffset/VerticalOffset
  - ScrollbarVisibility (Computed)
  
- [ ] Add `ViewportConvergence` helper
  - `IsStableViewport(before, after, epsilon)` → bool
  - `HasMeaningfulChange(...)` → bool
  - `ValidateMeasurements(...)` → ValidationResult
  
- [ ] Unit tests (no WPF required)
  - Unchanged: same snapshot = stable
  - Transition: scrollbar disappear = changed
  - Subpixel noise: 0.5 DIP diff = stable
  - Invalid: NaN/Infinity/Zero = invalid
  - Non-convergence cap: max 3 passes

### T89.3 — Update ApplyFitViewAsync (Fix the Bug)

Current: loops 3x, resets offset, but doesn't check if viewport actually changed
Fix: 
1. Take snapshot before Fit
2. Loop max 3 passes:
   - UpdateLayout + Render
   - Take new snapshot
   - If viewport stable → break early
   - If viewport changed → update MaxImageWidth/Height, continue
3. Reset offset
4. Final validation: offset ≤ 0.5 DIP

### T89.4-T89.9 — Testing & GUI Acceptance

- T89.4: Already done (unify entry points)
- T89.5: Fix InitialViewMode production
- T89.6: SizeChanged control
- T89.7: WPF layout tests
- T89.8: GUI acceptance (manual)
- T89.9: Full validation

---

## Implementation Strategy

1. Write pure helpers first (T89.2 helpers, no WPF dependency)
2. Add unit tests for helpers
3. Add trace probe diagnostic
4. Update `ApplyFitViewAsync` with convergence check
5. Run full test suite
6. Manual GUI verification

---

## Files to Create/Modify

- **New:** `src/PhotoReview.App/MainWindowConvergence.cs` — helpers & probe
- **Modify:** `src/PhotoReview.App/MainWindow.xaml.cs` — update ApplyFitViewAsync
- **New:** `tests/PhotoReview.App.Tests/MainWindowConvergenceTests.cs` — unit tests
- **Modify:** `tests/PhotoReview.App.Tests/ViewerStateTests.cs` — add epsilon tests

---

## Definition of Done

- [ ] T89.2 helpers have unit tests (epsilon, convergence, validation)
- [ ] T89.1 probe can be enabled via conditional output
- [ ] ApplyFitViewAsync uses convergence check
- [ ] One-click Fit converges (test on real images)
- [ ] Two-click Fit shows no visual difference (GUI check)
- [ ] Full test suite PASS (745+)
- [ ] No regression on wheel zoom or pan

---

## Known Risks

- Layout loop: mitigated by max 3 passes + epsilon
- Race with presenter: mitigated by version check + IsLoaded
- Regression zoom: mitigated by keeping wheel path unchanged
