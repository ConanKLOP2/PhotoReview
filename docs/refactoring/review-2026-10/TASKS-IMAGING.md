# Review 2026-10: Imaging tasks (RV-I*)

Conventions, gates and PR mapping: [`PLAN.md`](PLAN.md). Paths under `src/PhotoReview.Imaging/` or
`src/PhotoReview.Imaging.TurboJpeg/`; tests under `tests/PhotoReview.Imaging.Tests/`. Line numbers at `151f4964`.
Hostile-input tests build tiny synthetic files in memory/temp (no new binary fixtures unless unavoidable).

---

## PR 4 `fix/rv-decoding-hardening` (sonnet) — RV-I01, I03..I08

### RV-I01 — Embedded thumbnail buffer sized from an untrusted header · MED · CONFIRMED
- **Where:** `Decoding/EmbeddedThumbnailReader.cs:43-56` (`stride = width * 4; new byte[stride * height]`, unchecked).
- **Problem:** the EXIF APP1 thumbnail (≤ 64 KB of data) can claim e.g. 20000×20000 → ~1.6 GB allocation
  (or `OutOfMemoryException`, which is not in the catch list at `:76-81`; `ThumbnailCache` does not catch around it).
  30000×30000 overflows to a negative size → `OverflowException` (caught, harmless).
- **Step 1:** `Robustness/EmbeddedThumbnailMutationTests.cs` (or new `Decoding/EmbeddedThumbnailReaderTests.cs`):
  JPEG whose APP1 thumbnail SOF claims 20000×20000 and 65535×65535 with truncated data → `TryRead` returns null and
  `GC.GetAllocatedBytesForCurrentThread()` delta < 64 MB (deterministic proxy, no timing).
- **Step 2:** before allocating: `if (width > MaxThumbnailSide || height > MaxThumbnailSide) return null;` with
  `MaxThumbnailSide = 1024` (EXIF thumbnails are ≤ 160×120 by spec; 1024 leaves room for vendor variants), then
  `checked(width * 4)` / `checked(stride * height)`.
- **Step 3:** guard test that a normal 160×120 thumbnail still decodes with correct orientation (existing fixture).

### RV-I03 — Header grow stops at SOS without checking its payload · LOW · CONFIRMED
- **Where:** `TurboJpegDecoder.cs:303` (`GrowHeaderArea`), `:314-322` (`HeaderNeedsMoreData`), `:466,473` (`TryReadSegment`).
- **Problem:** the 64 KB read cut can fall inside the ~14-byte SOS segment → `tj3DecompressHeader` fails →
  `InvalidDataException` → spurious WIC fallback (correct result, but slower + warning log).
- **Step 1:** `Robustness/LargeHeaderReadInfoTests.cs`: pad APP segments so the SOS marker starts at byte 65530 →
  `ReadInfo` returns the right size via TurboJpeg (assert backend, no fallback).
- **Step 2:** in `TryReadSegment`, at SOS require `offset + 2 + length <= jpeg.Length` (marker 2 bytes + declared
  length), else set `needsMoreData`.

### RV-I04 — Second EXIF orientation parser in TurboJpeg diverges · LOW · CONFIRMED
- **Where:** `TurboJpegDecoder.cs:555-587` (`ParseTiffOrientation`) vs `Imaging/Metadata/ExifParser.TryReadOrientation`.
- **Differences:** ignores type/count, accepts `ifd0Offset < 8`, continues past an invalid value to a later duplicate tag 274.
- **Step 1:** parity theory (new `Metadata/OrientationParityTests.cs`): IFD0 with (a) duplicate 274 = 0 then 6,
  (b) 274 as BYTE, (c) `ifd0Offset = 4`, (d) normal 6 → TurboJpeg path and `ExifParser` return the same value.
- **Step 2:** replace the body with `ExifParser.TryReadOrientation(tiff) ?? 1` (check the project reference direction:
  TurboJpeg already references Imaging; if not, move the shared parser rather than duplicating).

### RV-I05 — `LoadBytes` assumes stable length and < 2 GB · LOW · CONFIRMED
- **Where:** `TurboJpegDecoder.cs:377-378` (`new byte[fs.Length]` + `ReadExactly`), `:264-265` (`(int)Math.Min(...)`).
- **Step 1:** `Decoding/TurboJpegTests.cs`: (a) zero-byte file, (b) file truncated mid-scan, (c) stream that reports
  a Length larger than it yields (test stream seam if one exists; otherwise a file shrunk between open and read is not
  deterministic → use the seam) → `NotSupportedException`/`InvalidDataException` carrying the source bytes
  (`DecodeFailureSourceBytes.TryGet` true) so the fallback reuses them.
- **Step 2:** read until EOF into a buffer sized from `Length` but tolerant of short reads (`ReadAtLeast`); reject
  files > `int.MaxValue - 64` with `NotSupportedException` (falls back to WIC, which streams).

### RV-I06 — No decompression-bomb / scan-limit guard · LOW · PLAUSIBLE
- **Where:** `TurboJpegDecoder.cs:133-149` (`Decode`, unscaled path), `CalculateOutputBuffer` overflow branch (untested).
- **Step 1:** `TurboJpegTests`: tiny solid-colour JPEG declaring 30000×30000 decoded unscaled → controlled
  `UserFacingError` (`ErrDecoderOutputTooLarge` or the existing equivalent), no OOM; allocation proxy < 64 MB.
  Progressive JPEG with > 500 scans → controlled failure.
- **Step 2:** reuse `DecodeMemoryGuard` (from Imaging.LibRaw — if the reference direction forbids it, move the guard
  to `PhotoReview.Imaging` and reference it from both) to cap output bytes before `AllocHGlobal`; set
  `tj3Set(handle, TJPARAM_SCANLIMIT, 500)` if the pinned libjpeg-turbo exposes it (check `TurboJpegNative` param enum;
  if absent, document and skip that half).
- **Note:** the cap must not reject legitimate 100+ MP originals the app supports: compute it from the RAM budget the
  same way LibRaw does, not a fixed small number. Add a test that a 12000×9000 declared size is still allowed.

### RV-I07 — `ImageDecoderFactory.Create` may build a decoder twice · LOW · PLAUSIBLE
- **Where:** `Decoding/ImageDecoderFactory.cs:66` (`GetOrAdd(backend, BuildDecoder)`).
- **Step 1:** `Decoding/FactoryTests.cs`: 16 threads start on a `Barrier`, call `Create(backend)` → factory delegate and
  decorator each ran once; all got the same instance.
- **Step 2:** store `Lazy<IImageDecoder>` (ExecutionAndPublication) in the dictionary.

### RV-I08 — `TiffStructure.TryGetValueSpan` reads offset 0 on a short entry · LOW · PLAUSIBLE
- **Where:** `Metadata/TiffStructure.cs:90-94` (`ReadU32` returns 0 when out of range).
- **Step 1:** `Raw/TiffStructureValueSpanTests.cs`: entry at the very end of the block (`entryOffset + 8 > len - 4`) → false.
- **Step 2:** `if (entryOffset < 0 || entryOffset > tiff.Length - 12) return false;` at the top.

---

## PR 6 `fix/rv-cache-preload` **[strong]** — RV-I02, I09..I16

### RV-I02 — Thumbnail persist catches only IO errors · MED · PLAUSIBLE
- **Where:** `Caching/ThumbnailCache.cs:206-225` (persist block of `LoadOrCreateAsync`).
- **Step 0:** read `DiskCacheStore.WriteAtomicallyAsync` and list which exceptions the PNG encoder / `File.Move` can
  throw (NotSupportedException, ArgumentException, InvalidOperationException, COMException). If none can escape it,
  close as NOT-A-BUG with that list.
- **Step 1:** `Caching/ThumbnailCacheLifecycleTests.cs`: disk store fake throws `InvalidOperationException` and
  `COMException` → `GetAsync` returns the embedded image; a second call does not repeat a faulted Lazy.
- **Step 2:** `catch (Exception ex) when (ex is not OperationCanceledException)` → log, return `embedded`
  (same policy as `PreviewImageService.RunPersistWorkerAsync`).

### RV-I09 — Preload direction flips on Home/End jumps · LOW · CONFIRMED
- **Where:** `Preload/NavigationPace.cs:63` (`Record`).
- **Step 1:** `BurstPreloadTests.cs` (holds the `NavigationPace` tests): `Record(500)` then `Record(0)` → direction
  stays +1 (forward is the review default after a jump); `Record(0)`→`Record(1)` → +1; `Record(5)`→`Record(4)` → -1.
- **Step 2:** update `_direction` only when `Math.Abs(delta) == 1`; on a jump keep the previous direction (initial +1).
- **Check:** `PreloadOrderService.Build` tests still green.

### RV-I10 — Thumbnail load exception can go unobserved · LOW · CONFIRMED
- **Where:** `Caching/ThumbnailCache.cs:132-152` (`AwaitAndCacheAsync`, `ContinueWith` cleanup).
- **Step 1:** caller cancels, then the shared load faults → no `TaskScheduler.UnobservedTaskException` after
  `GC.Collect(); GC.WaitForPendingFinalizers();` (subscribe in the test, `[Collection("GlobalState")]`).
- **Step 2:** in the `ContinueWith`, read `_ = t.Exception;` before removing the entry.

### RV-I11 — Dead `.jpg` branch in `ThumbnailCache.DecodeAsync` · LOW · CONFIRMED
- **Where:** `Caching/ThumbnailCache.cs:248` (`DecodeAsync` is only called with the `.png` cache path).
- **Fix:** remove the branch and the unused `_sourceBytesCache` field/ctor parameter (update composition and tests
  that pass it). Model: haiku-sized change, but inside this PR.

### RV-I12 — Scheduler exit race drops one navigation · LOW · PLAUSIBLE
- **Where:** `Preload/PreloadScheduler.cs:259-276` (`PreloadAroundAsync`) vs `:471-476` (loop exit).
- **Step 1:** `PreloadSafetyTests.cs`: test seam hook between the loop's final version check and task completion;
  inside it call `NotifyNavigation` + `PreloadAroundAsync(newCenter)` → the new center is eventually preloaded
  (await a TCS completed by the fake target for that path; hang-guarded by the run settings).
- **Step 2:** do the final version check and mark the scheduler "finished" under `_preloadCtsGate`; `PreloadAroundAsync`
  reads the flag under the same gate and starts a new lifetime when finished.

### RV-I13 — Running scheduler keeps a stale entries snapshot · LOW · PLAUSIBLE
- **Where:** `Preload/PreloadScheduler.cs:259-273`.
- **Step 0:** check every caller: does a catalog change (delete/rename/add/reload) always call `Cancel()` first? If yes,
  close as NOT-A-BUG and add a test that pins "catalog change → Cancel".
- **Otherwise:** compare the snapshot array reference in `PreloadAroundAsync`; restart the lifetime when it changed.
  Test: change entries without Cancel → the next preloaded path comes from the new array.

### RV-I14 — Foreign `OperationCanceledException` ends the preload lifetime · LOW · PLAUSIBLE
- **Where:** `Preload/PreloadScheduler.cs:482-498`.
- **Step 1:** fake target throws `OperationCanceledException` with an unrelated token for item 2 → items 3+ still
  preloaded, no "Preload scheduler failed" error log.
- **Step 2:** in `PreloadOneAsync`: `catch (OperationCanceledException) when (!token.IsCancellationRequested)` → mark Failed, continue.

### RV-I15 — `_preloadedKeys` grows across box changes · LOW · PLAUSIBLE
- **Where:** `Preload/PreloadScheduler.cs:70`, `:637`.
- **Fix:** clear `_preloadedKeys` when the target box changes (keys embed `TargetBox`, old ones can never match).
- **Test:** preload 3 items, change box, assert the internal count is 0 (internal test seam) and items re-preload.

### RV-I16 — `SourceBytesCache._pathVersions` never pruned · LOW · CONFIRMED
- **Where:** `Caching/SourceBytesCache.cs:197` (`Evict`).
- **Fix:** clear `_pathVersions` in `Clear()` together with the generation bump (a Clear already invalidates every
  in-flight read, so per-path versions are no longer needed).
- **Test:** `SourceBytesCacheRobustnessTests`: evict 3 paths, `Clear()`, count 0; an in-flight read started before
  `Clear` is still not cached afterwards.
