# RAW progress 05 - Multi-agent review of the whole PR (2026-10-01)

Review of every file in `origin/master...HEAD` of [PR #239](https://github.com/ConanKLOP2/PhotoReview/pull/239), run at tip
`e537863f`, fixes pushed up to `7a5109fe`. Facts come from the workflow journal and git; the verdicts are agent opinions
that were re-read before acting.

## 1. How it ran

- 10 read-only area reviewers (RAW parsers, LibRaw/native/tooling, imaging, file actions + journal, core, app/UI, Imaging tests,
  other tests, docs, cross-cutting), each limited to concrete defects with a failure scenario.
- Each claimed defect was checked by 2 independent agents told to refute it; a finding counts as confirmed only when both agree.
- Result: 27 claimed, 18 confirmed by both, 5 rejected by both, 4 split (one agent real, one not).
- Correction: the first summary of the run listed 15 confirmed. Three confirmed findings were dropped by a label-matching bug in
  the summary script (not by the reviewers) and were found only when the rejected list was re-read. Lesson: re-read the journal
  instead of trusting a derived summary.

## 2. Confirmed and fixed (18)

| Finding | Fix | Guard |
|---|---|---|
| Group Recycle / permanent-delete confirmation had its message arguments in the wrong order | argument order fixed in `FileActionController` | tests assert the real arguments (the old test repeated the same wrong call) |
| Group retry / undo verification threw bare `IOException(<code>)`: Recovery showed the identifier, no `ErrorCode` journaled | `JournalCodedException` | `RecoveryRecycleGroupTests` (fails on the old code) |
| CR2 IFD0 strip length narrowed to `int` (strip over 2 GiB: negative count, uncaught `ArgumentOutOfRangeException`) | `StartsWithSoi` | `TiffStripPreviewTests` (fails on the old code) |
| DNG/NEF/CR2 strip previews preferred the declared IFD size over the JPEG frame size | probed frame size wins | `TiffStripPreviewTests` |
| `ThumbnailCache` opened every RAW through WIC for a placeholder thumbnail (extra disk read, wrong size) | RAW skipped | `ThumbnailCacheLifecycleTests` |
| Preload busy backoff never reset when the path was cached by another route; permanent skip after eviction | `RecordSuccess` when seen cached | `PreloadDecoderBusyTests` (fails on the old code) |
| `RestoreMembers` ignored `RawSupportEnabled` | `rawEnabled` parameter | `ReviewCatalogTests` |
| `.raf` missing from the RAW settings label (en/vi/XML doc) | added | none (text) |
| Vietnamese README and DECISIONS said the repair dialog repeats each launch | corrected | none (docs) |
| `ComputeExifBlock` grew to the 4 MiB cap when the IFD could not fit anyway | keeps the default block | `TiffExifBlockPlacementTests` |
| Compare of a JPEG+RAW capture hashed both whole files (they can never match) | no hashing for a RAW/non-RAW pair | `CompareViewModelTests` |
| Corpus tests: inverted `Assert.Subset`; second strict switch; silent pass in strict mode (`CaptureGroupActionNativeTests`, `LibRawPreviewFallbackTests`); hard-coded corpus counts failing on a partial corpus; tautological survey assertion | shared `RawCorpus` strict mode, counts enforced only in strict mode | the tests themselves |

Also fixed before the review, from the first full run: `PhysicalFileSystem.TryCopyNew` swallowed a directory destination as "exists" on
Windows 11 (error 80); it now rethrows (`14788161`, `1319d6b0`).

## 3. Split verdicts (one agent real, one not)

| Finding | Decision |
|---|---|
| ADR 0003 cites line numbers that drift | fixed: symbol names instead |
| `LibRawBuildTargetTests` stub breaks if the temp path contains an apostrophe | fixed: escaped |
| UNDEFINED TIFF text values were read up to 4096 bytes (master read 1 byte) | fixed: clamped to the 256-byte text cap; `CleanText` already capped the display |
| LibRaw probed and `LibRawDecoder` created at startup for every user | NOT changed: the probe is memoized, runs once, and `DecoderProviders` needs the result anyway; revisit if startup time becomes a concern |

## 4. Rejected by both agents (not changed)

| Claim | Why it was rejected |
|---|---|
| Memory-leak tests are noisy and return early without the corpus | documented design: medians against a 192 MB budget; strict mode and the manual corpus workflow make a missing sample fail |
| Corpus tests pass silently instead of skipping | deliberate (no `Assert.Skip` in xUnit 2.9.3); `RawCorpusTests` covers both modes; `raw-corpus.yml` runs strict |
| Ignored `Wait` results in test gates (`PreviewImageService*` tests) | a missed release cannot change the outcome; the test-side awaits are bounded and would fail |
| Junction probe turns a precondition into a silent skip | host precondition, documented; the probe does not exercise the service |
| `ChooseSidecar` shares a key space between member and base sidecars | `IMG.CR3.xmp` is correctly the base sidecar of `IMG.CR3.*`; the ambiguity is inherent to the naming, a split would not resolve it |

## 5. OC14 flake

Found and fixed in the same session, see [file 04 section 2.1](04-in-progress-and-resume.md). Not part of the review findings.

## 6. Verification

- Full local gate `verify-all.ps1 -All -Hidden` (includes Native and Slow), Release, 0 warnings, all green at `c624d083`:
  Architecture 66, Core 2025, Imaging 1643, Integration 794, App 1395.
- Strict corpus run (`PHOTOREVIEW_RAW_CORPUS_STRICT=1`, `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`, 23 samples fetched and SHA-256
  verified): Imaging 1651 of 1651, plus the two App/Core corpus tests that were rewritten.
- NOT VERIFIED: a full gate run after the last commit (`7a5109fe`); only the touched test classes were run.
