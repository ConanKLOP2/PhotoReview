# RAW support — task cards

One card per task. An agent reads only its own card (plus the files it lists). Every card follows
[AGENT-PROTOCOL.md](AGENT-PROTOCOL.md): **commit + push after every numbered step**, progress file
`raw/progress/RAW-NN.md` updated in every commit. "Owns" = files only this task may edit in its wave.
Models follow the user's rule: haiku = mechanical, sonnet = clear spec, opus (strongest) = design-heavy /
data-safety / concurrency, and an opus agent delegates mechanical sub-work to sonnet/haiku.

Legend: **Dep** = must be merged on master first · **Gate** = what the lead re-runs before telling the user.

---

## Wave 0

### RAW-00 — Decisions (user + lead) · no agent
- Lead walks the user through [DECISIONS.md](DECISIONS.md) Q-RAW-01..07, records each answer there.
- Q-RAW-02/05 recommendations are revisited with RAW-01's survey numbers before Wave 5.
- Done when every question says **Decided**; lead then writes `decisions/Q-RAW.md` (frontmatter) and runs
  `tools/generate-open-decisions.ps1` in the next PR.

### RAW-01 — Sample corpus, survey tool, WIC probe · sonnet · Dep: none
Owns: `tools/fetch-raw-samples.ps1`, `tools/raw-samples.txt` (URL, SHA-256, licence URL, camera, format),
`.gitignore` entry for `tests/Fixtures/raw-corpus/`, `tools/PhotoReview.Benchmark.Cli/RawSurvey.cs` (new
`--raw-survey <dir>` command), and a new SURVEY.md next to this file.
Steps:
1. Build the pinned list: ≥2 bodies per format of Q-RAW-05 A (prefer bodies from different generations, e.g.
   old + new Sony ARW) + every body the user names; source raw.pixls.us, confirm each file's CC0 statement.
2. `fetch-raw-samples.ps1`: download to `tests/Fixtures/raw-corpus/`, verify SHA-256, skip existing, fail
   closed on mismatch; idempotent; no admin rights; PowerShell 5.1 compatible (copy `fetch-native.ps1` style).
3. `--raw-survey`: for each file, a throwaway parser (may use WIC metadata + a naive marker scan — this is
   exploration, not production code) prints: format, file size, container orientation, every embedded JPEG
   (offset, length, width×height from SOF, colour space hint), sensor/visible size if cheaply found.
4. WIC probe: for each file, try `WicDirectDecoder.ReadInfo` and a full `Decode` (no target box): record
   success, dimensions, time (3 runs, median), and whether the result equals the embedded preview size
   (i.e. WIC only decoded the preview). Record Windows build and installed "Raw Image Extension" version.
5. Write `SURVEY.md`: one table per format + a summary answering: which bodies have a full-size preview,
   which need full decode for zoom, how fast WIC is where it works, Adobe RGB occurrences.
Tests: script self-test on a 1-file list with a wrong hash → fails; survey command unit test on a synthetic
JPEG. Acceptance: SURVEY.md merged; numbers reproducible by re-running the two commands. Gate: build 0 warn.

## Wave 1

### RAW-10 — Project skeleton + contracts + limits · opus (delegates tests to sonnet) · Dep: RAW-00 answered
Owns: `src/PhotoReview.Imaging.Raw/**` (new), `PhotoReview.slnx`, `tests/PhotoReview.Imaging.Tests/Raw/`
(new folder, test helpers only), Architecture.Tests dependency rules for the new project.
Steps:
1. Create `PhotoReview.Imaging.Raw` (net10.0-windows, same props as `PhotoReview.Imaging.TurboJpeg`),
   reference it from `PhotoReview.App` and the test project; Architecture.Tests: allowed direction
   Raw → Imaging/Core only; nothing references Raw except App/tests/benchmarks.
2. Add every contract type of WORK-RAW-SUPPORT.md §3 exactly as named (enums, records, `IRawHeaderSource`,
   `IRawContainerReader`, `RawFileTypes`, `RawContainerLimits`), XML docs, no logic beyond `RawFileTypes`.
3. `SourceRawHeaderSource : IRawHeaderSource` over `ISourceReader` (priority passed in): 64 KB aligned block
   reads, cache of read blocks for the lifetime of one parse, hard cap `MaxHeaderBytes` total → throws
   `InvalidDataException`; counts bytes read (exposed for tests); INV-8 share flags via `ISourceReader`.
4. `InMemoryRawHeaderSource` (tests) + `SyntheticRawBuilder` test helper: builds TIFF (both byte orders),
   minimal ISO-BMFF boxes and RAF headers around a FixtureGenerator JPEG, fully in memory.
5. `RawContainerReaderRegistry`: picks the reader by `CanRead(first 64 bytes, extension)`; unknown → null.
Tests: header source never exceeds cap, reads only requested blocks, handles EOF; registry routing; builder
round-trips. Acceptance: all contract names match §3; 0 warnings; Architecture tests green.

## Wave 2 (parallel — disjoint files)

### RAW-11 — TIFF-family readers (CR2, NEF, ARW, DNG, ORF, RW2) · sonnet · Dep: RAW-10
Owns: `src/PhotoReview.Imaging.Raw/Tiff/**`, `tests/.../Raw/Tiff/**`, and the extraction of the shared IFD
walker out of `src/PhotoReview.Imaging/Metadata/ExifParser.cs` (behaviour-preserving; ExifParser keeps its
public API and calls the walker).
Steps:
1. Extract `TiffStructure` (byte order, IFD walk with cycle/limit checks, typed value reads) from
   ExifParser; all existing Exif tests stay green unmodified (this is a refactor step, commit alone).
2. CR2 reader per FORMATS.md; 3. NEF (SubIFDs, JpgFromRaw); 4. ARW; 5. DNG (NewSubFileType previews,
   DefaultCropSize); 6. ORF (IIRO magic, MakerNote CameraSettings); 7. RW2 (IIU magic, 0x002E JpgFromRaw).
   One reader per step, each with its synthetic tests from FORMATS.md's matrix, each its own commit.
8. Native-category tests against the RAW-01 corpus: every corpus file of these formats yields the preview
   offsets/sizes recorded in SURVEY.md (skip cleanly when the corpus is absent).
Acceptance: FORMATS.md matrix covered per format; corpus test green; ExifParser behaviour unchanged.

### RAW-12 — CR3 reader (ISO-BMFF) · sonnet · Dep: RAW-10
Owns: `src/PhotoReview.Imaging.Raw/Bmff/**`, `tests/.../Raw/Bmff/**`.
Steps: 1. box walker (32/64-bit sizes, uuid, depth/limit/cycle checks) · 2. Canon uuid + CMT1..4 spans as
`ExifBlock`s, orientation from CMT1 · 3. THMB + PRVW previews · 4. trak1 full JPEG via stsz + stco/co64 ·
5. sensor size · 6. corpus Native test. Each step its own commit. Acceptance: as RAW-11.

### RAW-13 — RAF reader · sonnet (haiku acceptable) · Dep: RAW-10
Owns: `src/PhotoReview.Imaging.Raw/Raf/**`, `tests/.../Raw/Raf/**`.
Steps: 1. fixed header + JPEG span · 2. EXIF block inside the embedded JPEG · 3. sensor size from CFA
header records · 4. corpus Native test. Acceptance: as RAW-11.

### RAW-20 — `DecodeRequest.SourceOrientation` + `ImageCacheKey.SourceKind` · sonnet · Dep: RAW-10
Owns: `DecodeRequest.cs`, `ImageCacheKey.cs`, the orientation code paths in `WicDirectDecoder`,
`WpfBitmapImageDecoder`, `TurboJpegDecoder`, `FallbackImageDecoder` (pass-through), and their tests.
Steps: 1. add `SourceOrientation` (appended, default null) and honour it in all three decoders (skip the
EXIF read when set; still apply when `ApplyOrientation`) · 2. add `SourceKind` to `ImageCacheKey`
(equality/hash/`Create` overloads; default 0) + persisted-preview key/hash includes it only when ≠ 0 so
existing disk caches stay valid · 3. quality-gate test: 8 orientations × 3 backends with the override on a
JPEG carrying a *different* EXIF orientation → override wins. Acceptance: every existing test unchanged
and green; INV-1 test for key inequality across `SourceKind`.

## Wave 3

### RAW-14 — Parser robustness & fuzz · sonnet · Dep: RAW-11, RAW-12, RAW-13
Owns: `tests/PhotoReview.Imaging.Tests/Robustness/RawContainerFuzzTests.cs` (+ helpers).
Steps: 1. mutation fuzz (bit flips, truncation, length/offset rewrites) over synthetic + corpus headers for
each reader, following `DecoderMutationFuzzTests` style (seeded, bounded time) · 2. assert: only
`InvalidDataException`/`NotSupportedException`, no allocation > header cap, header bytes read ≤ cap, parse
time ≤ 5 ms worst case per file on this machine · 3. fix any reader bug found **in a separate commit that
names the owning reader** (coordinate with the lead if that reader's PR is still open).
Acceptance: ≥10k mutations per format, zero unexpected exception types.

### RAW-15 — EXIF summary from RAW containers · sonnet · Dep: RAW-11/12/13 (+ PR #234's `ExifParser.TryParseTiffBlock`)
Owns: `src/PhotoReview.Imaging.Raw/RawExif.cs`, tests.
Steps: 1. build `ExifSummary` from `ExifBlock`s (TIFF blocks via `TryParseTiffBlock`; RAF/RW2 via the JPEG
APP1 path on the preview bytes) · 2. corpus test: camera make/model, date taken, ISO, exposure match
ExifTool's values recorded in SURVEY.md. Acceptance: no second file read (EXIF comes from bytes the
decoder already has).

### RAW-21 — `RawDecoder` + `FormatRoutingDecoder` · opus (delegates tests) · Dep: RAW-11/12/13/20
Owns: `src/PhotoReview.Imaging.Raw/RawDecoder.cs`, `FormatRoutingDecoder.cs`, `PreviewSelector.cs`,
`App/Composition/DecoderProviders.cs` + `ImageDecoderFactory.BuildDecoder` wiring, tests.
Steps:
1. `PreviewSelector`: among JPEG previews, smallest with both sides ≥ requested box (after orientation
   transpose), else largest; unknown sizes resolved by reading ≤ 64 KB SOF of candidates, largest-first,
   stopping as soon as the choice is certain.
2. `RawDecoder.ReadInfo`: container parse only → `ImageInfo(SensorW, SensorH, Orientation)` (Q-RAW-03).
3. `RawDecoder.Decode`: parse → select → read preview range (through `SourceBytesCache` when available,
   key suffix `#preview:<index>`) → inner decode with `Bytes` + `SourceOrientation` + same box/priority →
   wrap result: `OriginalWidth/Height` = sensor size, `Exif` from RAW-15, `Downscaled` true when the output
   is smaller than the sensor, `ActualBackend` = inner backend; `SourceKind = 1`.
4. `IsOriginal` requests: `RawFullDecode = Never` → largest preview; `OnZoom` → full decoder from Q-RAW-02
   (registered in Wave 5; until then largest preview) with `SourceKind = 2`.
5. `FormatRoutingDecoder` wraps the configured chain; RAW ext → `RawDecoder`, else unchanged; only active
   when `RawSupportEnabled`. Failure mapping: container error → `InvalidDataException` with a user-facing
   message key (`image.error.rawUnsupported` / `rawCorrupt`), never falls back to WPF for RAW (WPF cannot).
Tests: disk-read accounting (a 40 MB synthetic RAW reads < header + preview + 64 KB), orientation 1..8,
box selection table, cache key `SourceKind`, cancellation between parse and decode.
Acceptance: first-paint benchmark on the corpus recorded in the progress file; no whole-file read.

### RAW-23 — File types behind the flag · haiku · Dep: RAW-10
Owns: `ImageFileTypes.cs`, `PerformanceTestHarness.cs:20` (use `ImageFileTypes`), `ThumbnailCache.cs:244`
(RAW branch → `RawDecoder` via the factory, not `WpfBitmapImageDecoder`), tests.
Steps: 1. `ImageFileTypes.IsSupported(path, rawEnabled)` overload; callers read the setting (folder load,
sibling folders, drag-drop, benchmark window) · 2. harness list unified · 3. thumbnail branch.
Acceptance: with the flag off, behaviour is byte-identical to today (tests prove RAW files are not listed).

## Wave 4

### RAW-22 — Cache, preload and RAM budget for RAW · opus · Dep: RAW-21
Owns: `RamBudgetPolicy.cs`, `PreloadScheduler.cs` (RAW-specific parts only), `SourceBytesCache` use for
preview ranges, `PreviewImageService` (key `SourceKind` plumbing), tests.
Steps: 1. per-file decoded-cost estimate from the chosen preview's dimensions (cache the parsed
`RawContainerInfo` per (path,len,mtime) in a small bounded LRU so preload does not re-parse); fallback
factor for RAW = preview-size based, never file length · 2. `SourceBytesCache` stores preview ranges, not
whole RAWs; whole-folder-in-RAM rule counts preview bytes · 3. preload never triggers full decode; slow-link
EWMA ignores full-decode timings · 4. `Category=Slow` preload test on a synthetic 200-file RAW folder: RAM
estimate within ±20 % of measured; no file read beyond header+preview.
Acceptance: `PreloadSafetyTests`/`BurstPreloadTests` green; new numbers in `perf/` fragment + one
PERF-STATUS bullet.

### RAW-50 — Settings, UI, i18n · sonnet · Dep: RAW-21
Owns: `AppSettings.cs` (append), `SettingsNormalizer.cs`, `SettingsValidator.cs`, `SettingsWindow.xaml(.cs)`,
`Languages/vi.json` + `en.json` (append keys), info-overlay RAW badge, tests.
Steps: 1. settings + normalizer/validator · 2. Settings window controls (the field-map guard test from PR
#232 must pass — it will fail until the window handles the new properties) · 3. i18n keys vi/en
(`settings.raw.*`, `image.error.raw*`, `overlay.rawPreview`) + `tools/i18n-check.ps1` · 4. info overlay
"RAW · preview W×H" when shown from an embedded preview smaller than the sensor.
Acceptance: UI tests green (not excluded), i18n check green.

### RAW-30 — WIC full-decode path (probe) · sonnet · Dep: RAW-21
Owns: `src/PhotoReview.Imaging.Raw/WicRawFullDecoder.cs`, tests.
Steps: 1. detect per-format WIC codec availability once (cached) · 2. full decode through `WicDirectDecoder`
with `SourceOrientation`, reject results equal to the embedded preview size (codec only returned the preview)
· 3. Native tests on the corpus. Acceptance: availability matrix appended to SURVEY.md.

## Wave 5

### RAW-31 — LibRaw backend (only if Q-RAW-02 = A or C) · opus (delegates) · Dep: RAW-21
Owns: `src/PhotoReview.Imaging.LibRaw/**`, `tools/fetch-libraw.ps1`, `native/libraw.sha256`,
`native/README.md` (append), `release.yml` fetch step, licence files.
Steps: 1. pick an official LibRaw Windows x64 build or build recipe; pin SHA-256; licence texts · 2.
P/Invoke + `SafeHandle` (`libraw_init/open_buffer|open_file/unpack/dcraw_process/dcraw_make_mem_image/
dcraw_clear_mem/recycle/close`), cancellation via the progress callback, orientation handled once ·
3. availability probe + registration in `DecoderProviders` (log once, never crash) · 4. output normalized to
Bgr32 96 DPI (INV-9), `SourceKind = 2` · 5. Native tests: every corpus file decodes; time and peak memory
recorded; `half_size` evaluated for the zoom path.
Acceptance: licence review by the lead; no leak over 200 decodes (private bytes stable).

### RAW-32 — Zoom full decode for RAW · sonnet · Dep: RAW-31 or RAW-30
Owns: `ZoomDetailLoader.cs` (RAW branch), tests.
Steps: 1. when `RawFullDecode = OnZoom` and the needed pixels exceed the preview, request `SourceKind = 2`
original; show a subtle "decoding RAW…" indicator after 300 ms · 2. cancellation on navigation;
one original held at a time (existing rule) · 3. Integration test with a fake full decoder.

### RAW-40 — JPG+RAW pair detection · sonnet · Dep: RAW-23
Owns: `src/PhotoReview.Core/Catalog/CaptureGroup*.cs` (new), `ReviewCatalog` grouping hook, tests.
Steps: 1. group = same folder + same base name (case-insensitive) + one JPEG + one RAW (+ optional `.xmp`)
· 2. `RawPairMode` decides the representative entry; the other member stays reachable (index count counts
groups) · 3. Explorer order + INV-7 unaffected (tests) · 4. 10k-file folder grouping cost < 20 ms.

## Wave 6

### RAW-41 — Pair file actions (journal group transaction) · opus, data-safety review by lead · Dep: RAW-40, journal PR (perf/journal-single-parse-compaction) merged
Owns: `FileActions/**` group support, `UndoService`, `RecoveryWindow` display, tests.
Steps: 1. design note in the progress file first (group id in `JournalEntry`, append-only, recovery
semantics for partial groups, compaction compatibility) — lead approves before code · 2. Move/Copy/Recycle
of a group: prepare all → act each → commit all; partial failure → report which member, offer retry,
never silent · 3. Undo restores the whole group · 4. startup reconcile of a crashed group · 5. crash-point
tests at every step (fault-injecting `IFileSystem`), `Category=Slow` real-FS tests.
Acceptance: every crash point leaves files recoverable and the journal consistent; lead reruns Slow tests.

### RAW-42 — Pair UI · sonnet · Dep: RAW-40
Owns: key binding to toggle the shown member, title-bar/overlay pair badge, compare integration.
Steps: 1. shortcut (default unassigned — user sets it) · 2. badge "JPG+RAW" · 3. compare shows both members.

## Wave 7

### RAW-60 — Benchmark · sonnet · Dep: Wave 5
`--decoder-bench --raw` on the corpus: header parse, preview first paint (FHD/2K/4K box), full decode;
compare with same-size JPEGs; `perf/` fragment + PERF-STATUS bullet.

### RAW-61 — Quality gate · sonnet · Dep: Wave 5
Extend `DecoderQualityGateTests`: per format dims, 8 orientations (synthetic), corrupt/truncated files,
INV-8 handle release, INV-9 pixel format, Adobe RGB conversion (Q-RAW-06).

### RAW-62 — Real-machine check · lead
On the user's own RAW folder: navigation speed, zoom, pair actions + undo, RAM use; screenshots/notes in
the progress file. Only after this may RAW-70 flip defaults.

## Wave 8

### RAW-70 — Enable and document · sonnet (docs by haiku) · Dep: RAW-62
`RawSupportEnabled` default true (and `RawPairMode` default per Q-RAW-04); ADR 0009 (RAW decode) +
ADR 0001 addendum; `architecture.md`, `APP-MECHANISMS-VI.md`, README formats list; one line in
`HISTORY.md`; delete `WORK-RAW-SUPPORT.md`, `raw/TASKS.md`, `raw/FORMATS.md`, progress files (git keeps
them); keep `SURVEY.md` only if an ADR links it.
