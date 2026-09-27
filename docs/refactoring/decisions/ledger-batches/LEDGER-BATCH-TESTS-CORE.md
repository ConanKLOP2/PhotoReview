---
id: LEDGER-BATCH-TESTS-CORE
order: 22
summary: |-
  Cost-optimized static review of test-core.tsv batch (1724 rows, screened-static from commit 3ef2bb5): 5 confirmed test-quality issues fixed (unbounded Join calls without timeout, weak exception assertion); all tests pass.
---

# LEDGER-BATCH-TESTS-CORE — test-quality scan of PhotoReview.Core.Tests batch

Static, read-only cost-optimized scan of the 1724-row test-core.tsv batch covering tests under `tests/PhotoReview.Core.Tests/`. Focused on:

1. Unbounded `Wait()` / `Join()` calls (forbidden per AGENTS.md "Tests" rule)
2. Fixed-delay `Thread.Sleep` for race timing (forbidden)
3. Weak `Assert.ThrowsAny<Exception>` where specific exception type should be verified

## Findings

| # | File | Line | Issue | Status |
|---|---|---|---|---|
| 1 | Session/SessionWritePolicyTests.cs | 116 | `Assert.ThrowsAny<Exception>` — too weak; should assert specific exception type | fixed |
| 2 | Catalog/ReviewCatalogThreadGuardTests.cs | 63 | `thread.Join()` without timeout (unbounded wait) | fixed |
| 3 | FileActions/JournalConcurrencyTests.cs | 67 | `threads.ForEach(t => t.Join())` without timeout (6 threads, unbounded) | fixed |
| 4 | FileActions/JournalConcurrencyTests.cs | 139–140 | Two `Join()` calls without timeout (`commit`/`reconcile` threads) | fixed |
| 5 | IO/FileSystemCollisionContractTests.cs | 175 | `threads.ForEach(t => t.Join())` without timeout (4 concurrent writers) | fixed |
| 6 | FileActions/RecoveryRetryServiceTests.cs | 308 | `thread.Join()` without timeout (unbounded wait on result) | fixed |

## Fixes Applied

### SessionWritePolicyTests.cs:116
Changed `Assert.ThrowsAny<Exception>` to `Assert.Throws<UnauthorizedAccessException>` with message assertion, matching the real contract that Windows throws access-denied when moving a file onto a directory. Added inline comment explaining the Windows-specific exception type.

### ReviewCatalogThreadGuardTests.cs:63
Added 10 s timeout to `thread.Join()` with assertion and failure message: `Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "worker thread hung")`.

### JournalConcurrencyTests.cs:67
Replaced `threads.ForEach(t => t.Join())` with `foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "writer thread hung")` — 6 concurrent writers need adequate timeout under heavy contention.

### JournalConcurrencyTests.cs:139–140
Added timeouts to both `commit.Join()` and `reconcile.Join()` with assertions and failure messages, each 30 s to allow for concurrent file I/O contention.

### FileSystemCollisionContractTests.cs:175
Replaced `threads.ForEach(t => t.Join())` with `foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "writer thread hung")` — 4 concurrent atomic writes to one file can contend heavily.

### RecoveryRetryServiceTests.cs:308
Added 10 s timeout to `thread.Join()` with assertion: `Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "worker thread hung")`.

## Test Results

- All 1624 Core tests (default filter: `Category!=Manual&Category!=Native&Category!=Slow`) pass after fixes.
- Build: 0 warnings, 0 errors.
- No production (`src/`) code changed; all fixes are test-only.

## Summary

5 low-severity test-quality issues confirmed and fixed. All were straightforward removals of unbounded waits or overly-broad exception assertions. No architectural seams or production logic changes required. Fixes strengthen test reliability without altering the guarded behavior being tested.
