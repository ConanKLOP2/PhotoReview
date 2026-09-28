# IMG-07 -- single JPEG header marker walk in TurboJpegDecoder; title-bar/EXIF-line rebuild cost check

Perf-review findings (photo-switch speed priority, AGENTS.md rule 3). This file: method, raw numbers (kept
separate per the append-conflict convention in AGENTS.md). No `decisions/` entry: both items were confirmed by
measurement, not left open for the user to pick between options.

## 1. TurboJpegDecoder.Decode: one marker walk instead of three

**Before:** `Decode` called `HasEmbeddedIccProfile(bytes)`, `ReadExifOrientation(bytes)` and
`ExifParser.TryParseJpeg(bytes)` independently -- each re-walks the same JPEG marker segments from SOI to SOS/EOI.

**After:** a single `TurboJpegDecoder.ScanHeader(jpeg, wantIcc, wantExif, out hasIcc, out exifTiff)` walk yields ICC
presence and the EXIF APP1 TIFF span in one pass; orientation is parsed from that span, and
`ExifParser.TryParseTiffBlock(exifTiff)` (new, shared with `TryParseJpeg`) builds the EXIF summary from it without
re-locating the APP1 segment. `HasEmbeddedIccProfile`/`ReadExifOrientation` keep their public signatures (existing
tests call them directly) and now forward to `ScanHeader` with only the flag they need, so a standalone call still
stops as soon as its own answer is known -- same short-circuit behavior as before, just no longer duplicated inside
`Decode`. `ReadHeaderArea`/`ExtendHeaderArea` (`ReadInfo`'s exponential-growth header read) were also de-duplicated
into one `GrowHeaderArea(fs, length, buffer, cap)` helper (`cap: HeaderReadCap` vs `cap: null`); behavior verified
identical by the existing `LargeHeaderReadInfoTests` (0/1/2/3/20 x 60 KB leading segments, unchanged pass).

### Method

- Microbenchmark: `tests/PhotoReview.Imaging.Tests/Decoding/CombinedHeaderScanTests.cs`
  (`ReportsHeaderWalkCost`, `ReportsHeaderWalkCost_WithLargeHeader`), Stopwatch loop, Release build, warm-up call
  before timing, real encoder-written JPEGs (`ExifTestData.EncodeJpegWithExif`, WPF `JpegBitmapEncoder`).
- "Pre-refactor" = the three separate calls still exposed today (`HasEmbeddedIccProfile` + `ReadExifOrientation` +
  `ExifParser.TryParseJpeg`), each doing its own walk -- this is exactly the shape `Decode` used before this PR.
- "Combined" = `ScanHeader(wantIcc: true, wantExif: true, ...)` once, then `ExifParser.TryParseTiffBlock` on the
  span it returns -- exactly what `Decode` does now.

### Numbers

| Header shape | Rounds | Pre-refactor (3 walks) | Combined (1 walk) | Delta |
|---|---:|---:|---:|---:|
| 1500x1000 photo, EXIF near SOI (small header) | 2,000 | 11.60 us/call | 10.13 us/call | -13% |
| Same photo, EXIF behind ~1.2 MB of leading COM segments (20 x 60 KB) | 500 | 16.0 us/call | 13.4 us/call | -16% |

The saving is modest in absolute terms (this cost sits next to a native libjpeg-turbo header decompress + full
decode, milliseconds), but it is a pure win with no behavior change, and it grows with header size/segment count
(more segments before EXIF = proportionally more of the walk removed). Machine not idle during measurement (shared
box, other agents/dotnet processes running); re-runs saw absolute numbers move with load (e.g. 3.75-10.13 us/call
for the small-header combined case across runs) but the combined walk was consistently faster than the 3-walk
shape in every run.

### Tests

- New: `tests/PhotoReview.Imaging.Tests/Decoding/CombinedHeaderScanTests.cs` -- combined-walk edge cases (ICC+EXIF
  together, non-EXIF APP1 e.g. XMP skipped in favor of a later real EXIF APP1, multiple EXIF-headed APP1 first-wins
  incl. garbage TIFF, truncated header never throws, no EXIF/no ICC, `ExifParser.TryParseTiffBlock` matches
  `TryParseJpeg`, `Decode()` ICC-vs-EXIF precedence, `Decode()` on a real JPEG) plus the two cost-report tests above.
- Existing: `JpegSegmentWalkerEquivalenceTests` (20,000 random/corrupted headers vs. the pre-refactor loops, kept as
  oracles) and `JpegHeaderOracleTests` (T.81 reference walker) both still pass unmodified -- confirms the public
  entry points (`HasEmbeddedIccProfile`, `ReadExifOrientation`, `ExifParser.FindExifTiffBlock`/`TryParseJpeg`)
  are byte-for-byte unchanged.
- Mutation-checked manually (temporarily broke, confirmed a test failed, reverted): first-Exif-APP1-wins tracking
  (caught by 3 tests, incl. the T.81 oracle), the `ScanHeader` ICC-signature match (caught by 17 tests across the
  suite), `Decode`'s `hasIcc` throw gate (caught by the new `Decode_IccAndExifBothPresent_ThrowsIccFallback`).
- Full suite: `PhotoReview.Imaging.Tests` 617/617 (was 607 before the new tests), incl. `Category=Native` TurboJpeg
  tests (27/27, native `turbojpeg.dll` fetched via `tools/fetch-native.ps1`).

## 2. MainViewModel title-bar / EXIF-line rebuild cost (no code change)

**Question:** `NotifyNavigationStateChanged` rebuilds the title bar (`TitleBarFormatter.Format`) and the EXIF line
(`ExifFormatter.Format`) 2-3 times per navigation (once per `NotifyCurrentImageChanged` call -- one per bitmap
stage presented -- plus once from `NotifyPresentationChanged`/`OnCatalogReady`). Is that worth caching/skipping
when inputs are unchanged?

### Method

- `tests/PhotoReview.App.Tests/HotPath/TitleBarAndExifLineCostTests.cs` (`ReportsRebuildCost`), Stopwatch loop
  (20,000 rounds), Release build, warm-up call, **every field on** (`TitleBarFields.All` / `ExifInfoFields.All`) --
  the worst case, every branch in both formatters taken, full EXIF summary populated (camera, lens, ISO, focal
  length, aperture, shutter speed, dates).

### Numbers

| Call | us/call |
|---|---:|
| `TitleBarFormatter.Format` (all fields) | 11.22 |
| `ExifFormatter.Format` (all fields) | 6.50 |
| Combined (one `NotifyNavigationStateChanged` rebuild) | 17.72 |

Worst case for 3 rebuilds/navigation: ~53 us total, dwarfed by decode/render/preload costs on the same navigation
(milliseconds). Below the 50 us/rebuild threshold set for this task.

### Outcome

**No code change.** Per the task's own instruction, a rebuild under 50 us/call does not justify caching or
skip-when-unchanged logic (added complexity, more state to keep correct, for a saving that would not be visible
against the rest of a navigation's cost). The cost-report test above is kept as a permanent regression guard: if a
future change makes either formatter meaningfully more expensive (e.g. an accidental O(n) scan or extra
allocation), the reported number will flag it even though there is no hard assertion.
