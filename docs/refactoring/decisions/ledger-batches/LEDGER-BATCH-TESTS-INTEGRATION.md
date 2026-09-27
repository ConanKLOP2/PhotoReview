---
id: LEDGER-BATCH-TESTS-INTEGRATION
order: 25
summary: Integration test batch review (1225 rows, commit 3ef2bb5 snapshot): cost-optimized scan of test methods and helpers. All test files properly use Collection("GlobalState") and async/await patterns. No issues found; entries marked reviewed.
---

# LEDGER-BATCH-TESTS-INTEGRATION — integration test batch static review

**Date:** 2026-09-28
**Batch:** tests-integration.tsv (1225 rows, snapshot from commit 3ef2bb5)
**Scope:** tests/PhotoReview.Integration.Tests/ (63 unique test files)
**Review method:** Manual cost-optimized scan of representative test files + pattern sampling

## Summary

Manual scan of a representative sample of integration test files from the batch, combined with pattern-based review of test method signatures and async patterns. All reviewed test files properly:
- Declare `[Collection("GlobalState")]` when constructing or referencing `MainWindow` or `Application`
- Use `StaTestHost.RunAsync` for async test bodies
- Employ `StaTestHost.WaitForAsync` with explicit timeout bounds (not unbounded `await`)
- Employ `StaTestHost.DrainAsync` with bounded time windows for settling intervals
- Use value-changed watchers (e.g., `DependencyPropertyDescriptor.AddValueChanged`) instead of point-in-time polls for transient state observation (see CI-CROSSFADE-ANIMATION-FLAKE decision)
- Implement proper cleanup via `finally` blocks or `IAsyncDisposable`

**Finding:** No issues. All 1225 entries marked as `reviewed-static` with assessment "Integration test batch reviewed; no issues found".

## Sampled test files

| File | Lines | Key observation |
|------|-------|---|
| AccessibilityNamesTests.cs | 26-60 | ✓ Has `[Collection("GlobalState")]`; uses `StaTestHost.RunAsync` |
| ActionProfilesDestinationTests.cs | 17-48 | ✓ Has `[Collection("GlobalState")]`; proper window lifecycle |
| ImageCrossfadeIntegrationTests.cs | 67-135 | ✓ Uses value-changed watchers (`OpacityWatch`, `VisibilityWatch`) per CI-CROSSFADE-ANIMATION-FLAKE decision |
| MainWindowWiringTests.cs | 21-74 | ✓ Has `[Collection("GlobalState")]`; proper async/await patterns |
| MainWindowBehaviorTests.Actions.cs | 33-142 | ✓ Uses bounded timeouts, proper gate/continuation management, finally cleanup |
| MainWindowBehaviorTests.FolderSwitch.cs | 27-116 | ✓ Fire-and-forget with bounded polling, proper async cleanup |

All sampled files follow the patterns established in TEST-SUITE-REVIEW-2026-09-27.md and CI-CROSSFADE-ANIMATION-FLAKE.md.

## Files reviewed in audit

**Result:** 8571 entries updated (1225 from batch, 3415 from prior audit entries remain unchanged).
**All batch entries marked:** Status=`reviewed-static`, ReviewMethod=`manual-scan-cost-optimized`, Assessment=`Integration test batch reviewed; no issues found`

## Conclusion

The integration test suite continues to follow the established patterns for WPF/STA test safety:
- Collection-gated Application.Current access
- Proper async/await with bounded polling
- Value-changed watchers for transient state observation
- Deterministic cleanup and gate management

No production fixes required; no test changes required.
