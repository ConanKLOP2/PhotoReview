# RAW integration: review and fix waves

Status record for PR https://github.com/ConanKLOP2/PhotoReview/pull/239 (`feat/raw-support-integration` -> `master`,
draft). Written 2026-09-30 from the working session's fact sheet, checked against git. Where the two disagreed, git wins
and the difference is stated. Status keys: DONE = merged/pushed on the branch; IN PROGRESS; NOT VERIFIED.

## Git facts (as of tip cd8ff26d)

- `git rev-list --count origin/master..HEAD` = 120 commits. At c1e5c452 (branch tip when the review fixes were merged
  in) it was 91; 29 commits were added after it. The fact sheet said "85 commits before this session"; git shows 91 at
  c1e5c452, because that tip already includes the merged review PRs #241-#245 and the master merge.
- The PRs below were squash-merged, so each has ONE sha; commits after c1e5c452 were pushed directly (user request:
  no more PRs).

## How the review was organised

Five parallel reviewers, one area each, results merged by the lead:

| # | Area |
|---|------|
| 1 | TIFF-family readers (TIFF/DNG/ORF/RW2/NEF/ARW) |
| 2 | CR3, RAF, decoders, cache |
| 3 | Core: file actions, journal, catalog |
| 4 | App layer |
| 5 | LibRaw, native code, tooling |

Result: no data-loss path found. Findings were fixed with failing-first tests (test written first, seen to fail, then
fixed). Some suspected findings turned out not to be bugs (see the last section).

## Wave 0: review PRs into the integration branch (DONE)

| PR | sha | Content | Guarding tests (files) |
|----|-----|---------|------------------------|
| https://github.com/ConanKLOP2/PhotoReview/pull/241 | 6b840e19 | Review fixes of #239: data safety, RAW readers, LibRaw, tooling (120 files changed) | Core: `CaptureGroupActionRollbackTests`, `JournalGroupReconcileTests`, `RecoveryRecycleGroupTests`, `UndoServiceGroupTests`; Imaging: `TiffReaderRegressionTests`, `RawHeaderHostileInputTests`, `LibRawDecoderRegressionTests`, `RawContainerFuzzTests`; App: `FileActionControllerGroupTests`, `LibRawPreviewFallbackTests`; Integration: `ToolScriptTests`, `RawBenchmarkToolTests` |
| https://github.com/ConanKLOP2/PhotoReview/pull/242 | 8f9af342 | Follow-ups: undo-retry regrouping, RAW-toggle folder reload, LibRaw fetch Exec WarnAndContinue | `FileActionControllerGroupTests` (App) |
| https://github.com/ConanKLOP2/PhotoReview/pull/243 | 34ed759c | Tests for #242 | `MainViewModelAdvancedTests` (App), `LibRawBuildTargetTests` (Integration) |
| https://github.com/ConanKLOP2/PhotoReview/pull/244 | c1e5c452 | Merge of master #240 into the branch; fixes 4 rotted `Category=Slow` MainWindow tests, run in CI | existing Slow MainWindow tests |
| https://github.com/ConanKLOP2/PhotoReview/pull/245 | a9fe5f81 | GroupEntries timing test replaced by an allocation guard + allocation trim in `CaptureGroupBuilder`; SEC-01 real-junction tests skip when the temp folder does not allow junction writes | `CaptureGroupBuilderTests` (Core), `SymlinkDestinationEscapeNativeTests` (Core) |

Also on the branch before the waves: 940d3ec4 dropped the wall-clock assert from the CORE-08 Move-sparse journal test
(`OperationJournalTests`).

The squash of #241 hides per-fix shas; ask `git show 6b840e19` for its file list.

## Wave 1: direct commits, six areas plus dead-code removal (DONE)

Each row is one commit; the test file is the one the commit touches.

### App

| sha | Fix | Test |
|-----|-----|------|
| 07037175 | ShowSettings early return; RawPairMode change reloads the folder; Compare pair kept on navigation; presented path restored on failed member decode; StatusText not hidden by "Decoding RAW..."; localization notes | `MainViewModelAdvancedTests.RawSettings.cs` |

### TIFF / RAF / ORF readers

| sha | Fix | Test |
|-----|-----|------|
| 2db2ef11 | RAF raw size from the CFA header (X-T2 6000x4000, X-E2S 4896x3264, X100V 6240x4160) | `RafSensorSizeTests` |
| 10bb4557 | ORF EXIF parser accepts IIRO/IIRS/MMOR magics | `OrfExifMagicTests` |
| 045b70e3 | TIFF type 13 (IFD) SubIFD pointers | `TiffSubIfdTypeTests` |
| c75c7f71 | DNG masks/enhanced/non-raw IFDs excluded from sensor size | `DngPrimaryIfdTests` |
| 5b779e30 | Legacy OLYMP MakerNote at +8, file-absolute offsets | `OrfLegacyMakerNoteTests` |

### Decoder and cache

| sha | Fix | Test |
|-----|-----|------|
| aa3d6124 | Adobe RGB detected via the Interop IFD ('R03') instead of a substring scan; also for previews with declared size | `PreviewSelectorColorSpaceTests`, `PreviewSelectorJpegFrameTests`, `RawDecoderTests` |
| ceaf3bed | RAW without an embedded JPEG falls back to LibRaw | `RawDecoderNoPreviewFallbackTests` |
| b2cccf5f | Canon sRAW/mRAW preview RAM estimate no longer under-estimated | `RamBudgetPolicyRawPreviewTests` |
| a52a53ef | RAW full decode offered on zoom in Original loading mode | `ZoomDetailTests` (App) |

### LibRaw and tooling

| sha | Fix | Test |
|-----|-----|------|
| 232c9005 | Camera white balance written by verified struct offset (read-back guard); insufficient memory mapped to `InvalidOperationException`, not "corrupt" | `LibRawWhiteBalanceAndErrorTests` |
| 9b7af7ca | LibRaw in THIRD-PARTY-NOTICES + NOTICE copyright lines | none (docs) |
| 71634d91 | `fetch-raw-samples` fails when `-FormatFilter` matches nothing | `ToolScriptTests` |

### Core undo / recovery / journal

| sha | Fix | Test |
|-----|-----|------|
| 2ed0edd8 | E1: retry of a partial group undo closes earlier Failed lines | `UndoServiceGroupTests` |
| 37ae2f4c | E2: Recovery retry refuses permanent delete when `AllowPermanentDeleteWithoutRecycleBin` is off (wired in `App.xaml.cs`) | `RecoveryRecycleGroupTests` |
| 90271a85 | E3: partially failed group Recycle registers completed members for Ctrl+Z | `UndoServiceGroupTests`, `FileActionControllerGroupTests` |
| 80492486 | E4: journal lines with a blank member Source are quarantined | `JournalGroupReconcileTests` |

### Core catalog and dead code

| sha | Fix | Test |
|-----|-----|------|
| dd3483af | `RemovePaths` degrades a capture instead of dropping it; cross-volume partial destination cleanup when provably ours; `TryReformGroup` keeps the displayed member | `ReviewCatalogTests`, `CaptureGroupActionRollbackTests` |
| 54a1b1c6 | Removed unused `WicRawFullDecoder`/`WicCodecRegistry` | its test file `WicRawFullDecoderTests` deleted |

## Wave 2 and later commits (DONE)

| sha | Change | Test |
|-----|--------|------|
| eb41bac9 | TIFF EXIF block sized from IFD offsets (was fixed 128 KiB; cap 4 MiB) | `TiffExifBlockPlacementTests` |
| 4ad0c680 | Journal downgrade guard: an older build rewrites group lines without GroupMembers, the new build restores them at startup; Committed with a missing member becomes Failed in Recovery; plus compaction round-trip test | `JournalGroupDowngradeTests` |
| e6576196 | Localized RAW error sentences (`image.error.rawUnsupported`, `rawCorrupt`) via `UserFacingError.Localized`; README downgrade note | `RawDecoderTests` |
| 3d2aafe6 | `image.error.rawNoPreview` sentence for a valid RAW without a JPEG preview | `RawDecoderNoPreviewFallbackTests` |
| 19f3ba29 | Info line shows "RAW preview WxH" (Q-RAW-03) via `IRawPreviewInfo`, hidden after full decode | `ZoomDetailTests`, `ExifFormatterTests`, `ExifLineViewModelTests`, `RawDecoderNoPreviewFallbackTests` |

## Test-hardening commits (pushed WITHOUT a full test run, at the user's request)

| sha | Change |
|-----|--------|
| f8730c11 | Private-bytes leak guard (`LibRawDecoderTests`, Category=Native, not in CI) compares medians of two sample windows (decodes 50-100 vs 150-200) with a 192 MB budget. Reason: private bytes after full GC are bimodal, and the old guard failed on identical runs at c1e5c452. A deliberate 8 MB/decode leak was seen failing it; a 1 MB/decode leak is below its resolution |
| 4f6b0bde | `.github/workflows/raw-corpus.yml`: manual workflow, strict corpus run. NOT RUN yet (see below) |
| a18adf82 | Shared `RawCorpus` helper: corpus tests fail instead of skipping when `PHOTOREVIEW_RAW_CORPUS_STRICT=1` (touches `Cr3AndRafCorpusTests`, `RawCorpusTests`, `LibRawDecoderTests` and others) |
| e4a88645 | Composition tests for the two `App.xaml.cs` wirings (Recovery retry settings delegate; RawDecoder LibRaw no-preview decoder): `ServiceFactoriesTests` |
| cd8ff26d | `JournalGroupOlderBuildFixtureTests` pins the downgrade guard against journal lines taken from origin/master |

## Findings not fixed on purpose

Reasons and decisions are in 03-decisions-and-open-items.md. Short list:

- RawSurvey JPEG item: not a real bug.
- ORF active area: not a bug (AspectFrame is a smaller crop); no change.
- Reconcile Copy mtime comparison: left size-only.
- RW2 1:1 crop and ORF displayed size: kept as-is.

## Test status

- Last full local run (Release, `Category!=Manual`), before the last three test commits: Architecture 64, Core 1911,
  App 1289, Integration 771 pass; Imaging 1267/1268 (the memory test, since made robust). Counts come from the working
  session, not re-run here.
- CI of #239 was green (3/3) at 4f6b0bde. NOT VERIFIED: CI at a18adf82, e4a88645 and cd8ff26d, and any local run of them.
- NOT VERIFIED: the strict corpus workflow (GitHub offers "Run workflow" only once the file is on master).
