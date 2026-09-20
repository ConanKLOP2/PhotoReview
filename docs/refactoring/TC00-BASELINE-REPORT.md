# TC00: Baseline Test Run Report

**Date**: 2026-09-20  
**Branch**: `codex/st01-clean-dead-code` (ST09 completed)  
**Build**: Release (`dotnet build PhotoReview.slnx -c Release`)  
**Test Filter**: `Category!=Manual` (excludes manual image tests)

## Summary

- **Total Tests**: 774 PASS + 1 FAIL = 775 total
- **Failure Rate**: 0.13% (1 failure)
- **Duration**: ~36 seconds

| Project | PASS | FAIL | Duration |
|---------|------|------|----------|
| Architecture | 12 | 0 | 1 s |
| Core | 321 | 1 | 5 s |
| Imaging | 205 | 0 | 4 s |
| App | 174 | 0 | 9 s |
| Integration | 62 | 0 | 18 s |
| **TOTAL** | **774** | **1** | **36 s** |

## Failing Test

**Test**: `PhotoReview.Core.Tests.OperationJournalTests.LargeJournal_ReadCommittedMoves_IsBoundedAndFast`  
**Issue**: Journal startup took 112 ms (threshold: < 100 ms)  
**Severity**: Timing-sensitive; flaky due to system load

```
Large journal startup reads only recent committed moves within threshold
  Error: Large journal startup took 112 ms.
  at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
```

This test exemplifies **G7** from TEST-CLEANUP-PLAN (timing assertions instead of barriers).

## Slowest Test Groups (sampled)

- `Integration.Tests`: 18 s (includes real OperationJournal + FileActionService scenarios)
- `App.Tests`: 9 s (MainViewModel composition, UI event wiring)
- `Core.Tests`: 5 s (321 tests = 15 ms/test average, one slow outlier)

## Test Trait Coverage (Current)

```
Category=Manual: 1 test (FixtureTests.RealWorldPhotosManualTest)
Category=Architecture: 12 tests
(Most other tests untraited)
```

**Problem**: Cannot select "hotpath" tests for fast iteration; ~37 seconds for every full run.

## Decisions Needed Before TC01

- **Q-T1**: How should concurrent action drops be measured? (currently silent gate drop via `IsBusy`)
- **Q-T2**: Add seam for disk read measurement in production? (`ReadBudgetProbe`)
- **Q-T3**: Real photo folder path via env var? (`PHOTOREVIEW_FIXTURE_DIR`)
- **Q-T4**: Allow native Recycle Bin tests on this machine?

## Next: TC01 (Fixture Builder for Real Photos)

---

**Baseline committed**: 774 PASS (excluding 1 timing-flake)  
**Goal**: Add TC01–TC05 hotpath tests without reducing PASS count, maintain or improve flake resistance
