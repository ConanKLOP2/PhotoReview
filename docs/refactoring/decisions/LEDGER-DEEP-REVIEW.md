# Deep review of the 289 `needs-deep-review` ledger rows (2026-09-27)

Scope: every row in [`FUNCTION-BODY-AUDIT-2026-09-27.tsv`](../FUNCTION-BODY-AUDIT-2026-09-27.tsv) with
`Status=needs-deep-review` (289 rows, all in `src/PhotoReview.Imaging/{Caching,Decoding,Preload}`,
`src/PhotoReview.Imaging.TurboJpeg/Native`, and `tests/PhotoReview.Imaging.Tests/**`). The ledger is a
snapshot of source base `3ef2bb5`; every row was re-located in the current tree by File+Symbol before
review (line numbers had drifted for most rows but every symbol was still found — none were renamed or
removed). Six reviewers (one per pre-split ~48-row batch) each read the actual current body of every
function/test in their batch and judged it for correctness bugs, races, native-handle leaks (TurboJPEG),
UI-thread blocking I/O, wasted disk reads, and — for tests — whether the assertions would actually fail if
the guarded code broke. Findings already tracked as R01-R18 were excluded (none of R01-R18 touch this
Imaging-only slice except R04/R06/R07/R11, whose decision docs were read first and confirmed not to
overlap any of these 289 rows).

**Result: 287 of 289 rows had no actionable issue** (268 `reviewed-static`, 19 `no-change` — code and test
oracles here are unusually well-documented and non-tautological; see per-row `Assessment` cells in the
ledger for the specific reasoning behind each). **2 new, real, previously-unreported issues** were found,
both low severity, both fixed with a mutation-checked regression in this same change:

| ID | File:symbol | Evidence | Severity | Action taken |
|---|---|---|---|---|
| L01 | `tests/PhotoReview.Imaging.Tests/Decoding/WicDirectTests.cs:92-103` — `WicDirectTests.DecodesFromMemoryBuffer()` | The test decodes via `DecodeRequest(path, Bytes: bytes)` but never removes the on-disk file at `path`, which still has byte-identical content. A regression where `WicDirectDecoder.Decode` silently ignored `Bytes` and fell back to opening `Path` would still produce the same 64x48 result, so the test could not catch it. Confirmed by mutation check: temporarily forcing the decoder to always take the file-path branch made the *original* test still pass, but throws a `FileNotFoundException` once the fix below is applied. | Low (weak test oracle; no production bug — the decoder does honor `Bytes` today) | Fixed: the test now deletes the on-disk file right after reading `bytes`, so it can only pass if the decoder actually decodes from the supplied buffer. Verified: reverting `WicDirectDecoder` to prefer `Path` now fails this test (`FileNotFoundException`); with the real decoder, the full `PhotoReview.Imaging.Tests` suite (577 tests) passes. |
| L02 | `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:452-548` — `PreviewImageService.DecodeAndCache` | `GetDiskCachePath(key)` (a `SHA256` hash over a formatted string) ran unconditionally at the top of the method, before the `!_disableDiskCache` check. Its result (`cachePath`) is only ever read from or persisted to inside `!_disableDiskCache` branches, so every preview decode with the disk cache off (`PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1`, a `disableDiskCacheOverride: true` instance, or `diskCacheCapacityBytes<=0`) paid for a hash it threw away. On the hot per-image decode path, which the project's stated priority is to keep minimal. | Low (pure CPU waste, not I/O or a correctness issue) | Fixed: `cachePath` is now computed lazily, only when `!_disableDiskCache`, and both use sites null-check it. Verified: `dotnet build` 0 warnings (the resulting nullable `string?` flows cleanly through the existing try/catch), and the existing behavioral regression tests `ZeroDiskCacheCapacityDisablesDiskCache` / `ZeroDiskCacheCapacityDoesNotWrite` (which already assert nothing is read/written/pruned when the disk cache is off) still pass unchanged — this is a pure internal refactor with no behavior change, so no new test was needed beyond those two existing oracles continuing to pass. |

## Method

- Batches: the 289 IDs were pre-split into 6 files of ~48 rows each (`batch1.tsv`..`batch6.tsv` in the
  session's temp directory); verified to be an exact, non-overlapping partition of the 289
  `needs-deep-review` IDs before dispatch.
- Five batches were reviewed fresh by sonnet sub-agents in this session; one batch's results from an
  earlier interrupted session attempt were reused after the lead re-verified them (including correcting
  one row a stale/glitched worktree in that earlier attempt had wrongly marked "file not found" — the file
  and test exist and pass in the current tree).
- One batch (`batch1`) initially wrote ledger-row *position* (1-49) instead of the ledger's actual stable
  `ID` column into its results file; the lead detected this by cross-checking every batch's result IDs
  against that batch's own input file, and remapped it before merging into the ledger.
- The lead (this change) personally re-read and verified both `issue-` findings above against the current
  source before fixing them, plus spot-checked ledger-column integrity after the merge (row/column counts
  unchanged, exactly 289 rows touched, no duplicate or dropped IDs).
