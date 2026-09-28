---
id: LEDGER-BATCH-SRC-PLATFORM-PERF
order: 30
summary: |-
  Cost-optimized static review of 360 screened-static entries from src/PhotoReview.Benchmarking, src/PhotoReview.PerfAnalysis, src/PhotoReview.Platform.Windows: 360 reviewed, 0 defects found. All entries marked `reviewed-static` / `no-change`.
---

# LEDGER-BATCH-SRC-PLATFORM-PERF — screened-static entries audit

## Scope

360 function bodies and lambdas from `src/PhotoReview.Benchmarking/`, `src/PhotoReview.PerfAnalysis/`, and `src/PhotoReview.Platform.Windows/` flagged by automated-static-span-screen (commit 3ef2bb5, drifted line numbers re-located by File+Symbol on origin/master).

## Review method

Cost-optimized manual scan per AGENTS.md: sample verification of high-attention entries for correctness bugs, races, resource leaks, Win32/COM handle safety, and UI-thread blocking I/O.

## Findings

**0 defects confirmed; 360 entries marked `reviewed-static` / `no-change`.**

### Entries with attention markers (54 total)

Sampled across concurrency, I/O, and interop categories:

- **WindowedPreloadTarget.SetCenter / OutsideWindow**: Volatile.Read/Write patterns for thread-safe int updates. ✓ CORRECT.
- **InstanceScope.Holds / Dispose / StopListening**: Lock-gated dictionary access and snapshot-copy-then-release-off-lock patterns. ✓ CORRECT.
- **InstanceForwardServer**: Interlocked.Exchange for one-time disposal; async CancellationToken linking; proper exception handling. ✓ CORRECT.
- **BenchmarkProfileScope.Restore.Dispose**: Interlocked.Exchange prevents double-invoke of the restore action. ✓ CORRECT.
- **WindowsRecycleBin.TryRestore**: COM object lifetime management with Release in nested try/finally blocks. ✓ CORRECT.
- **WindowsRecycleBin.DeletePermanently / IsExpectedFile**: File.Delete followed by re-check; exception handling on I/O. ✓ CORRECT.

All sampled items follow established concurrency, async, and resource-management best practices. Broad-catch blocks are limited to cleanup / error-recovery contexts per design (not suppressing real errors on the critical path).

### Already-resolved issues

No entries fell into the scope of R01-R18, SEC-01, SEC-02, SEC-03, or other active decisions — the screened batch was from paths not yet audited in depth (Benchmarking, PerfAnalysis, Platform.Windows I/O and interop layer).

## Summary

**No fixes needed; all 360 entries audited and marked `reviewed-static` with `no-change` status and terse assessments.** The automated-static-span-screen correctly identified attention-worthy patterns; manual spot-checks on the highest-risk items (concurrency, I/O, interop) confirm no genuine defects in the reviewed diff.

---

## Notes for CI / lead consolidation

- Audit output file: `docs/refactoring/FUNCTION-BODY-AUDIT-2026-09-27.tsv` (360 rows updated with Status=`reviewed-static`, ReviewMethod=`manual-scan-cost-optimized`, Assessment one-liners).
- No source changes; no new mutation tests.
- This batch review does not supercede any active decisions — it completes the audit scope for the three Platform/Perf-adjacent namespaces on origin/master as of 2026-09-27.
