# PhotoReview.Imaging / PhotoReview.Imaging.TurboJpeg — Code Review (2026-09-25)

Scope: `src/PhotoReview.Imaging`, `src/PhotoReview.Imaging.TurboJpeg`. Read-only, branch `master`. Context: `AGENTS.md` (minimize disk reads, maximize RAM, review speed), `docs/architecture.md`, ADR 0001, `docs/refactoring/PERF-STATUS.md`.

## Summary counts

| Severity | Count |
|---|---|
| High | 1 |
| Medium | 5 |
| Low | 5 |

## Top 5 one-liners

1. **IMG-01 (High):** The preview disk cache re-encodes every downscaled preview as JPEG regardless of source format, silently and permanently discarding transparency for PNGs (`PreviewImageService.cs:465-467`, `PreviewCacheFile.cs:114-115,163,201`) — visible on the F4 fixture's 62 PNG files.
2. **IMG-02 (Medium):** `PreviewImageService._originalDimensions` (`PreviewImageService.cs:26`) is an unbounded `ConcurrentDictionary` never trimmed by `EvictCachedPath`/LRU pressure — grows for the life of the process across very large libraries, working against the "maximize RAM, bound usage" goal.
3. **IMG-03 (Medium):** `RunPreloadSchedulerAsync`'s memory-headroom check (`PreloadScheduler.cs:301-316`) is only re-evaluated once per `workers`-sized batch, so a preload burst can commit up to `workers` more decodes past the 80% memory-load line before pausing.
4. **IMG-04 (Medium):** `WicDirectDecoder`'s ICC-failure catch (`WicDirectDecoder.cs:211,398`) is guarded only by `colorChain.IsActive`, so an unrelated `COMException` from format conversion/rotation on an ICC-bearing image is mis-reported as an ICC transform failure and forces a WPF fallback.
5. **IMG-05 (Medium):** No test exercises the actual app-level path where a transparent PNG is decoded, downscaled, persisted to the v5 disk cache and reloaded — the only alpha-related test (`PreviewCacheFileTests.ReadYieldsRenderNativeOpaquePixelFormat`) asserts the alpha-drop as *expected* rather than catching that PNG sources should never reach this path.

---

## High

### IMG-01 — Disk-cached previews of transparent images lose alpha permanently
- **File:line:** `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:465-467`; `src/PhotoReview.Imaging/Caching/PreviewCacheFile.cs:114-115, 163, 201, 215`
- **Problem:** `PreviewImageService.DecodeAndCache` persists any downscaled preview whose `PlatformImage` is a `BitmapSource` to the v5 disk cache, with no check for alpha/format. `PreviewCacheFile.WriteAtomicallyAsync` always encodes the payload as JPEG (`JpegBitmapEncoder`) and hard-codes `header[7] = 0` ("has alpha" = false). On the next read, `PreviewCacheFile.Read` always converts to `PixelFormats.Bgr32` (opaque) per the hard-coded flag. WIC/WPF decode a transparent PNG to `Pbgra32`; JPEG cannot carry alpha, so premultiplied-transparent pixels (RGB ≈ 0,0,0 where alpha = 0) are baked in as opaque black.
- **Why it matters:** Real photo folders mix JPEG and PNG (the F4 perf fixture itself has 62 PNGs). The very first time a transparent PNG preview is downscaled and cached to disk, the image is corrupted for every subsequent view (fresh process, folder revisit, etc.) until `Clear Cache` is used — a direct violation of "don't present wrong pixels" territory for that subset of files, and it is silent (no log, no fallback).
- **Fix steps:**
  1. In `PreviewImageService.DecodeAndCache`, gate the `PersistToDiskCache` call on `decodedImage.PlatformImage is BitmapSource { Format: PixelFormats.Bgr32 }` (opaque only), or
  2. Extend `PreviewCacheFile` to actually honor alpha: derive `hasAlpha` from the source format, and either keep a lossless payload (e.g. PNG) for the alpha case or accept the lossy JPEG-without-alpha but skip persistence for that entry.
- **Test:** Integration test in `PreviewImageServiceTests` (or a new test) that decodes a real ARGB PNG fixture through `PreviewImageService`, forces a disk-cache write + eviction of the RAM entry, re-fetches the preview, and asserts the returned bitmap still carries alpha (or, if the fix is "never persist," asserts no `.pv4` file was written for that key).
- **Effort:** S–M. **Risk:** Low (additive gate; worst case is more re-decodes for PNGs, which is the current safe behavior for other exemptions like `SourceBytesCache`'s PNG fallback).

---

## Medium

### IMG-02 — `_originalDimensions` dictionary grows unbounded for the process lifetime
- **File:line:** `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:26, 449, 624, 649`
- **Problem:** Every decode (viewer, preload, or `GetOriginalDimensionsAsync`) adds an entry keyed by the full-identity `ImageCacheKey` (`CreateOriginal`). `EvictCachedPath` (line 512-521) only removes entries from the bounded `_cache`, never from `_originalDimensions`; only `ClearOriginalDimensions()`/`ClearCache()` (explicit user actions) clear it.
- **Why it matters:** AGENTS.md's RAM priority is about *using* RAM efficiently, not leaking it — a multi-day review session over one or more 15k+/100k+ file libraries (folder switches, re-scans) accumulates one entry per distinct file identity ever seen, each holding a normalized path string plus two ints, with no LRU bound. For very large collections this is a slow, steady leak that competes with the 16 GiB preview/16 GiB source-bytes budgets instead of being counted against them.
- **Fix steps:** Track original-dimension entries per path in `EvictCachedPath`'s `alsoInvalidate` callback (already wired for exactly this purpose from `MainViewModel`/`ImagePresenter` — check current callers), or replace the plain dictionary with a small `BoundedLruCache<ImageCacheKey, (int,int)>` sized independently (entries are tiny, so a large-but-bounded cap, e.g. 200k entries, is cheap and eliminates the leak).
- **Test:** Unit test that decodes N distinct paths, evicts/replaces them via the normal navigation path, and asserts `PreviewImageService`'s internal dimension-cache count stays bounded (expose a `KnownOriginalDimensionsCount` test-only property, mirroring `CacheCount`).
- **Effort:** S. **Risk:** Low.

### IMG-03 — Preload memory-headroom check can overshoot by up to `workers` decodes
- **File:line:** `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs:295-316`
- **Problem:** `HasPreloadHeadroom()` (a `GlobalMemoryStatusEx` syscall) is deliberately checked only when `examinedSinceYield == 0`, i.e. once per `workers`-sized batch (comment at line 299 explains this is to avoid a syscall per candidate). But the inner `while (running.Count < limit && order.MoveNext())` loop can still queue up to `limit` (`workers`, up to `_viewerBusyWorkerLimit` less when the viewer is busy) new decodes in that same pass before the next check, each potentially several MB to tens of MB (full-resolution originals, high-MP previews).
- **Why it matters:** Under AGENTS.md's "prioritize RAM but never cause errors," this is the mechanism meant to keep memory load under 80% (`PreloadMemoryLoadLimit`); PERF-STATUS.md's own burst scenario (S3) shows peak WS spikes are already a tracked concern (6–17 GB observed historically). Overshooting the check by a full worker batch (default 8) right at the 80% line increases OOM/paging risk on lower-RAM machines than the 32 GB dev box, especially combined with `SourceBytesCache` when enabled (16 GiB) plus the 16 GiB preview cache.
- **Fix steps:** Re-check `HasPreloadHeadroom()` after every few queued items within the batch (e.g. every 2), not only at the start of the batch, or make the check cheaper (cache the last `GlobalMemoryStatusEx` result for a short TTL, e.g. 50 ms, instead of gating purely on batch boundaries) so it can be called more often without extra syscall cost.
- **Test:** Extend `PreloadSafetyTests`/`BurstPreloadTests` with a fake memory probe that flips `HasHeadroom` to false mid-batch and assert no more than N (small constant) additional decodes start afterward.
- **Effort:** S. **Risk:** Low.

### IMG-04 — ICC-fallback catch can misattribute unrelated COM failures
- **File:line:** `src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:151-160, 211-217, 396-403`
- **Problem:** `catch (COMException ex) when (colorChain.IsActive)` at line 211 wraps the *entire* `try` block of `DecodeFromStream`, including the unrelated format-converter (line 151-160) and flip/rotator (line 163-172) stages that run after the color chain was built. Any `COMException` thrown by those later stages on an ICC-bearing image is reported as "WicDirect could not transform the embedded ICC profile" and routes to the WPF fallback, masking the real defect (e.g., an unsupported orientation transform combination or a converter format edge case).
- **Why it matters:** The image still renders correctly via the WPF fallback (INV-12 holds), so this is not user-visible today, but it actively hides real WicDirect bugs behind a generic, wrong diagnostic message, making future WicDirect regressions (this is the app's default backend per ADR 0001, ~4–12x faster than WPF) harder to triage from logs alone.
- **Fix steps:** Narrow the `try`/`catch` so only `colorChain.Build(...)` and the subsequent `CopyPixels` (where the transform is lazily evaluated, per the comment at line 212-213) are covered by the ICC-specific catch; let converter/rotator failures propagate as plain `COMException`/surface with their own message (still fallback-eligible via `FallbackImageDecoder.IsFallbackable`, just with an accurate log line).
- **Test:** Unit test that stubs/forces a converter failure unrelated to color management on an ICC image and asserts the resulting exception message does not claim an ICC transform failure (or a test on the log message content if that's easier to assert against).
- **Effort:** S. **Risk:** Low (behavior for the user is unchanged; only diagnostics improve).

### IMG-05 — No test covers the disk-cache alpha loss at the `PreviewImageService` level
- **File:line:** `tests/PhotoReview.Imaging.Tests/Caching/PreviewCacheFileTests.cs:170-184`; missing coverage in `tests/PhotoReview.Imaging.Tests/PreviewImageServiceTests.cs`
- **Problem:** The existing test explicitly documents and accepts "JPEG can never carry alpha, so the round-tripped bitmap must come back as opaque Bgr32" as a *format-level* contract, which is fine for `PreviewCacheFile` in isolation. But no test exists one layer up, at `PreviewImageService`, verifying that a genuinely transparent source is either excluded from persistence or handled specially — which is exactly the gap behind IMG-01.
- **Why it matters:** This is the kind of gap that lets IMG-01 ship and stay shipped; the "quality gate" (ADR 0001 §4, `DecoderQualityGateTests`) checks orientation/ICC/pixel-size/error-handling but not alpha fidelity across the disk-cache round trip.
- **Fix steps:** Add the test described in IMG-01, and add an explicit `QG` (quality-gate style) case for "downscaled transparent PNG preview survives a disk-cache round trip without losing alpha" once IMG-01 is fixed.
- **Effort:** S. **Risk:** None (test-only).

### IMG-06 — `PreviewImageService` and `PreloadScheduler` are doing too much for one class each
- **File:line:** `src/PhotoReview.Imaging/Caching/PreviewImageService.cs` (689 lines); `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs` (519 lines)
- **Problem:** `PreviewImageService` owns: RAM LRU cache, in-flight dedup, cache-epoch/invalidation, viewer decode slots + priority threads, background disk-persist workers (a small actor system in its own right), legacy cache-file cleanup, per-backend decoder caching, original-dimensions tracking, and the "Original mode" zoom decode path. `PreloadScheduler` owns cancellation-lifetime management, worker-slot scheduling, pacing/direction/burst logic, memory-headroom gating, and warmed-key bookkeeping.
- **Why it matters:** Both are central to every documented invariant (INV-1, INV-2, INV-8, INV-9, INV-12) and both are already dense with subtle concurrency reasoning (see the extensive inline comments justifying `Lazy`/`GetOrAdd` races, epoch checks, etc.) — the size and concern-count make it easy for a future change to accidentally violate one of those invariants without noticing, since so much shared mutable state (`_cacheEpoch`, `_cacheLifecycleGate`, `_previewLoads`) is threaded through one file.
- **Fix steps:** Extract the disk-persist worker pool (`_persistQueue`, `RunPersistWorkerAsync`, `PersistToDiskCache`, legacy cleanup) into a dedicated `PreviewDiskPersistWorker` class owned by `PreviewImageService`; extract `_originalDimensions` tracking into its own small bounded-cache type (dovetails with IMG-02's fix). For `PreloadScheduler`, consider extracting the pacing/shape logic (`CurrentShape`, `IsStillWanted`) — already partially isolated in `NavigationPace` — further from the scheduling loop itself.
- **Test:** No new tests required beyond keeping existing ones green; this is a structural refactor.
- **Effort:** L. **Risk:** Medium (touches hot, invariant-critical code; must be done incrementally with the existing test suite as a safety net, per the project's "verify-by-real-build" lesson).

---

## Low

### IMG-07 — Duplicated JPEG marker-walking logic in `TurboJpegDecoder`
- **File:line:** `src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs:312-350` (`HasEmbeddedIccProfile`) and `:352-389` (`ReadExifOrientation`)
- **Problem:** Both methods independently re-implement the same JPEG segment-walking loop (marker/length parsing, SOS/RST short-circuits) to look for different APPn segments.
- **Why it matters:** Maintainability only — a future fix to the marker-walking logic (e.g., a malformed-JPEG edge case) has to be applied twice.
- **Fix steps:** Extract a shared `IEnumerable<(byte Marker, ReadOnlySpan<byte> Payload)> WalkJpegSegments(ReadOnlySpan<byte>)` (or a callback-based walker, since spans can't be captured in iterators) and rebuild both methods on it.
- **Test:** Existing `TurboJpegTests.cs` coverage should be preserved (rerun as regression, no new cases strictly needed).
- **Effort:** S. **Risk:** Low.

### IMG-08 — `ImageDecoderFactory.Create` allocates a fresh `FallbackImageDecoder` (+ primary/fallback pair) per call
- **File:line:** `src/PhotoReview.Imaging/Decoding/ImageDecoderFactory.cs:59-75`
- **Problem:** Every call to `Create(backend)` for a non-Wpf backend builds a brand-new primary decoder, a new Wpf fallback decoder, and a new `FallbackImageDecoder` wrapper. `PreviewImageService` already works around this for its own use via `_decodersByBackend` (comment at `PreviewImageService.cs:38-44` explains exactly this cost), but any other caller of `IImageDecoderFactory.Create` (benchmark CLI, tests, future call sites) pays the allocation cost on every decode.
- **Why it matters:** Minor GC pressure on a hot path if a future caller doesn't know to cache the result; the decoders are documented as stateless/shareable, so the factory could just do the caching itself.
- **Fix steps:** Cache constructed `IImageDecoder` instances per `DecoderBackend` inside `ImageDecoderFactory` itself (a `ConcurrentDictionary<DecoderBackend, IImageDecoder>`), matching the pattern `PreviewImageService` already uses, so every caller benefits without needing to know about the cost.
- **Test:** `FactoryTests.cs` — assert `Create(backend)` returns behaviorally-equivalent (or same-instance, once cached) decoders across repeated calls.
- **Effort:** S. **Risk:** Low.

### IMG-09 — `SourceBytesCache.GetOrRead` blocks synchronously on an async `Lazy<Task<byte[]>>`
- **File:line:** `src/PhotoReview.Imaging/Caching/SourceBytesCache.cs:22-31`
- **Problem:** `GetOrRead` is a synchronous method that does `lazy.Value.GetAwaiter().GetResult()` on a `Task.Run`-backed lazy. It is currently only called from `PreviewImageService.DecodeFromSource`, itself always on a worker/dedicated thread, so no UI-thread deadlock risk today — but the type is public and nothing in its contract documents "never call this from a `SynchronizationContext`-bound thread."
- **Why it matters:** A future caller (e.g., a new UI-thread-adjacent code path) calling this directly would deadlock if awaited synchronously from a context that also owns the thread pool thread the `Task.Run` needs, or at minimum block a caller thread for the duration of a full-file read.
- **Fix steps:** Add an XML-doc warning ("must not be called from a UI/sync-context thread") or, better, expose an async `GetOrReadAsync` and make the sync overload call `Task.Run(...).GetAwaiter().GetResult()` explicitly at the boundary so intent is unambiguous, matching ADR 0005's App/Core threading split.
- **Test:** None strictly required; documentation/API-shape fix.
- **Effort:** S. **Risk:** Low.

### IMG-10 — `DiskCacheStore.PruneDirectory` does a full directory `EnumerateFiles` + sort per prune pass
- **File:line:** `src/PhotoReview.Imaging/Caching/DiskCacheStore.cs:165-194`
- **Problem:** Every coalesced prune pass (triggered per persisted preview/thumbnail, debounced via `SchedulePrune`) lists every file in the cache directory, builds a `FileInfo` for each, sorts by `LastAccessTimeUtc`/`CreationTimeUtc`, and sums sizes — O(n log n) over the whole cache directory (up to the 16 GiB preview / 4 GiB disk-cache / 1 GiB thumbnail budgets, i.e. potentially tens of thousands of files) on every pass.
- **Why it matters:** This runs on a background thread and is already coalesced (`SchedulePrune`), so it is not currently flagged as a measured bottleneck in PERF-STATUS.md, but it is an O(n) rescan per pass with no incremental/streaming size tracking, and `LastAccessTimeUtc` is not reliably updated on NTFS unless "last access time" tracking is enabled (it is disabled by default on many Windows installs since Vista), which could make the "least recently used" ordering effectively "least recently created" in practice.
- **Fix steps:** (a) Track directory size incrementally (increment on write, decrement on delete) to skip full enumeration when clearly under quota; (b) verify/document reliance on `LastAccessTimeUtc` given NTFS's default `NtfsDisableLastAccessUpdate` — consider falling back to `LastWriteTimeUtc` if access-time tracking cannot be assumed enabled.
- **Test:** A benchmark/perf test measuring prune pass duration at a representative file count (thousands of `.pv4`/`.png` files) would validate whether this is worth prioritizing.
- **Effort:** M. **Risk:** Low (perf-only; no correctness change without also fixing the access-time assumption).

---

## Notes / out-of-scope observations

- ADR 0001 (dated 2026-09-18) states embedded-ICC images "also go through fallback" to WPF, but the current `WicDirectDecoder` (per PERF-STATUS.md's #40 perf-night entry, "ICC via WIC color transform") now attempts a direct WIC color transform first and only falls back on failure. The ADR's addendum (§9) was updated for the TurboJpeg registration change but not for this later ICC behavior change — worth a documentation sync pass, flagged here for visibility rather than as a code defect.
- `RamBudgetPolicy`/`PreloadOptions` and `SourceBytesCache`'s independent 16 GiB budget are not cross-checked against each other or the OS-reported physical RAM at startup; on a machine with less than 32 GB, enabling `UseSourceBytesCache` alongside the default 16 GiB preview cache could reserve more virtual budget than physical RAM allows before `IMemoryProbe` ever triggers back-pressure. Not filed as its own IMG item since `AGENTS.md` targets are explicitly tied to the 32 GB dev machine, but worth a startup-time sanity check if the app ships to smaller machines.
