---
id: LEDGER-REMAINING-REVIEW-2026-09-27
order: 29
summary: |-
  Remaining 8096 `screened-static` ledger rows manually reviewed via 10 parallel cost-optimized (Haiku) batches; 7 real issues found and fixed (6 unbounded-Join/weak-assertion test fixes verified by a full Core.Tests run, 1 weak decoder-oracle fix), 1 candidate finding rejected as an already-deliberate design (see comments added at the two call sites); the rest confirmed clean. Two agent-process defects were caught and corrected before merging: one batch overwrote the whole shared ledger file instead of updating only its own rows, and one batch marked ~500 rows outside its assigned scope -- both excluded from this merge, which only applied each agent's output restricted to its own originally-assigned row IDs.
---

# LEDGER-REMAINING-REVIEW-2026-09-27 — cost-optimized multi-agent pass over the remaining 8096 ledger rows

## Method

User asked for the remaining `screened-static` rows (rows only automated-static-screened, never manually
read) in `docs/refactoring/FUNCTION-BODY-AUDIT-2026-09-27.tsv` to be reviewed, explicitly opting into the
cheapest model (Haiku) for cost, accepting a coarser per-row depth than the earlier `LEDGER-DEEP-REVIEW.md`
289-row pass in exchange for covering the full remaining 8096 rows in one session.

Split into 10 batches by project (5 production `src/`, 5 test projects), ~50-1800 rows each, run in parallel,
isolated worktrees, no push/PR per agent -- the lead (this doc's author) consolidated everything into one PR to
avoid a 10-way merge storm on the shared TSV/`OPEN-DECISIONS.md`.

## Consolidation safety issues found and handled

Verifying each agent's output before merging (not trusting "0 issues" claims blind) surfaced two real problems
with the sub-agents' own process, both caught by diffing each worktree's TSV against the original per-batch ID
list before merging:

1. **`src-app` batch** rewrote the entire 8586-row shared TSV file down to just its own 886 rows, discarding
   every other batch's data in its own copy. Not applied as-is; the lead's merge script restricts every batch's
   contribution to exactly the IDs originally assigned to it, so this had zero effect on the final result.
2. **`src-core` batch** marked ~490 rows outside its assigned 472-row scope (files under
   `PhotoReview.Imaging`/`Platform.Windows`/`App`/etc., not `PhotoReview.Core`) as reviewed. Same fix: excluded
   by the strict per-batch ID allowlist.

Both are process learnings for future cost-optimized multi-agent passes: an agent working from a full-file
snapshot must be explicitly told (and, better, only ever given) its own row subset, not the whole file, and the
lead must verify scope before merging rather than trusting a "N rows reviewed" count.

## False-positive finding rejected

The `tests-app` batch proposed adding `[Collection("GlobalState")]` to `CompositionRootTests`'s
Application-creating tests and `DarkScrollBarRenderingTests`, reading them as missing test isolation. The lead
checked this against `docs/refactoring/decisions/FLAKY-FolderLoad.md` (PR #192) and found it is a **deliberate**
design: both classes already tolerate the `Application`-creation race with a `catch (InvalidOperationException)`
("losing is harmless: the other test's Application is all this test needs"), specifically to avoid serializing
these tests against every other `GlobalState`-collection test for a race that provably doesn't need fixing. No
`[Collection("GlobalState")]` was added; a one-line comment was added at each of the 4 call sites instead, so a
future review (human or agent) doesn't re-flag the same non-issue without reading the existing design comment
first.

## Real, confirmed fixes applied

| Batch | File | Issue | Fix |
|---|---|---|---|
| tests-core | `Catalog/ReviewCatalogThreadGuardTests.cs` | Unbounded `Join()` | 10s timeout assertion |
| tests-core | `FileActions/JournalConcurrencyTests.cs` (x2 sites) | Unbounded `Join()` on 6 and 2 threads | 30s timeout assertions |
| tests-core | `IO/FileSystemCollisionContractTests.cs` | Unbounded `Join()` on 4 threads | 30s timeout assertions |
| tests-core | `FileActions/RecoveryRetryServiceTests.cs` | Unbounded `Join()` | 10s timeout assertion |
| tests-core | `Session/SessionWritePolicyTests.cs` | `Assert.ThrowsAny<Exception>` where the contract is `UnauthorizedAccessException` | Typed assertion + message check |
| tests-imaging | `Decoding/TurboJpegTests.cs` `DecodesFromMemoryBuffer` | Same weak-oracle pattern as `L01` (`WicDirectTests`): decoded from `Bytes`, but the on-disk file was never removed, so a regression falling back to `Path` would still pass | Delete the on-disk file before decoding, mirroring `L01`'s fix |

`tests-core`'s fixes were verified by that batch actually running the full `PhotoReview.Core.Tests` filtered
suite (1624 tests) before/after -- the strongest evidence of any of the 10 batches. The `TurboJpegTests` fix was
applied by the lead directly, following the identical, already-proven `L01` pattern.

## Per-batch detail

Full findings/method per batch: [`LEDGER-BATCH-SRC-APP.md`](ledger-batches/LEDGER-BATCH-SRC-APP.md),
[`LEDGER-BATCH-SRC-CORE.md`](ledger-batches/LEDGER-BATCH-SRC-CORE.md), [`LEDGER-BATCH-SRC-IMAGING.md`](ledger-batches/LEDGER-BATCH-SRC-IMAGING.md),
[`LEDGER-BATCH-SRC-PLATFORM-PERF.md`](ledger-batches/LEDGER-BATCH-SRC-PLATFORM-PERF.md),
[`LEDGER-BATCH-SRC-TOOLS-LOC.md`](ledger-batches/LEDGER-BATCH-SRC-TOOLS-LOC.md), [`LEDGER-BATCH-TESTS-APP.md`](ledger-batches/LEDGER-BATCH-TESTS-APP.md)
(corrected post-review, see above), [`LEDGER-BATCH-TESTS-CORE.md`](ledger-batches/LEDGER-BATCH-TESTS-CORE.md),
[`LEDGER-BATCH-TESTS-INTEGRATION.md`](ledger-batches/LEDGER-BATCH-TESTS-INTEGRATION.md),
[`LEDGER-BATCH-TESTS-IMAGING.md`](ledger-batches/LEDGER-BATCH-TESTS-IMAGING.md), [`LEDGER-BATCH-TESTS-MISC.md`](ledger-batches/LEDGER-BATCH-TESTS-MISC.md).

## Ledger status after this pass

All 8586 rows in `FUNCTION-BODY-AUDIT-2026-09-27.tsv` now have a status other than `screened-static` (289 from
the earlier deep review, 8096 from this pass, plus the small set already resolved by R01-R18/P01-P03/SEC-01..03
etc.). The ledger's per-body coverage claim is therefore complete for the first time since the audit was taken;
this does not mean every row received `LEDGER-DEEP-REVIEW.md`-level depth (see the Method section above for the
explicit cost/depth tradeoff the user chose).
