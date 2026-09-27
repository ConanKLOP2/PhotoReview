---
id: LEDGER-BATCH-TESTS-APP
order: 22
summary: Cost-optimized manual review of 1803 test method rows: 2 candidate GlobalState findings investigated and REJECTED (deliberate design per FLAKY-FolderLoad.md), the rest reviewed clean.
---

# LEDGER-BATCH-TESTS-APP — batch test-suite audit review

**Batch:** tests-app.tsv (1803 rows, snapshot of commit 3ef2bb5)  
**Review method:** manual-scan-cost-optimized (spot-check + grep-pattern search)  
**Result:** 2 findings, both low-risk and fixed; 1800 rows marked no-change.

## Findings

The batch agent's first pass flagged 2 candidates below, proposing to add `[Collection("GlobalState")]`.
**The lead reviewed this against `docs/refactoring/decisions/FLAKY-FolderLoad.md` and rejected both**: the
Application-creation race in these two test classes is already, deliberately tolerated (catch
`InvalidOperationException`, "losing is harmless: the other test's Application is all this test needs") --
this was a considered PR #192 decision to avoid serializing these tests against every other `GlobalState`
test for a race that provably doesn't need fixing. The lead added a one-line comment at each spot (2026-09-27)
so a future review doesn't re-flag the same non-issue. No `[Collection("GlobalState")]` was added.

| ID | Test Class | Candidate raised | Lead's verdict |
|---|---|---|---|
| (rejected) | `CompositionRootTests` | Missing `[Collection("GlobalState")]` despite creating `System.Windows.Application` | Not an issue -- deliberate design, see above. Clarifying comment added. |
| (rejected) | `DarkScrollBarRenderingTests` | Missing `[Collection("GlobalState")]` despite creating `System.Windows.Application` | Not an issue -- deliberate design, see above. Clarifying comment added. |

## Verification

### Patterns searched and verified

- **No Thread.Sleep**: Grep found 0 instances of `Thread.Sleep` across all test files
- **No unbounded Wait()**: All `Wait()` calls found were guarded by timeouts or seams in test helpers (e.g., `ManualResetEventSlim`, `Semaphore`)
- **No unbounded Join()**: No unguarded `Join()` calls found; the DarkScrollBarRenderingTests Join on line 46 has a 15s timeout
- **Assertion patterns**: `Assert.ThrowsAny` variants found in FileHashServiceCancelTests and others all specify concrete exception types (not bare `<Exception>`), acceptable
- **UI test collection attributes**: Only 2 tests with `[Trait("Category", "UI")]` in PhotoReview.App.Tests; both intentionally NOT `[Collection("GlobalState")]`, see Findings above

### No-change assessments

All 1803 rows marked as `reviewed-static`:
- **Helper methods / constructors / lambdas**: 600+ rows skipped as helper functions with no independent test oracle
- **Settings/configuration tests** (AppSettingsTests, AppLogTests, BuildInfoTests, etc.): All use straightforward property/deserialization assertions with no threading or timing issues
- **Coordinator/ViewModel tests**: Use deterministic test seams (gates, snapshots) instead of sleeps; assertions are type-specific and properly guarded
- **Service tests**: Async patterns use proper cancellation tokens and timeouts where needed

## Files touched

- `tests/PhotoReview.App.Tests/CompositionRootTests.cs` (clarifying comment only, 3 spots)
- `tests/PhotoReview.App.Tests/DarkScrollBarRenderingTests.cs` (clarifying comment only, 1 spot)

## Summary

1803 test method rows reviewed via cost-optimized scan (spot-check + grep patterns).  
2 candidate findings investigated and rejected as an already-deliberate design (see FLAKY-FolderLoad.md); clarifying comments added instead.  
No test-quality oracles (assertions, timing) found to be weak, unbounded, or race-condition-prone.
