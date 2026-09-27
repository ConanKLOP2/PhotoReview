---
id: Q-R29
order: 15
summary: |-
  User chose B then C-if-needed (2026-09-27); C part 1 done (stat off UI thread), part 2 preload throttle pending seam --
---

# Q-R29 option C -- navigation stat off the UI thread (part 1) and a preload read-throttle seam (part 2, proposal)

**Decision (user, 2026-09-27):** "B (slow-link simulation) then C if needed". B is PR #202 (`perf/qr29-slow-link-sim`,
`SlowLinkFileSystem` + `--perf-session --slow-link-latency-ms/--slow-link-bandwidth-mbps`): injected per-metadata-call
latency clearly reached the UI thread; its bandwidth cap never reached the image-byte reads. So C is needed, split in two:
part 1 (the measured half) is implemented here; part 2 (preload throttling) needs a read seam first and is only proposed.
Measurements: [perf/2026-09-27-qr29-option-c.md](../perf/2026-09-27-qr29-option-c.md).

## Part 1 -- done: no synchronous file-metadata I/O on the UI thread in the navigation path

Before, every `ImagePresenter.PresentAsync` ran on the UI thread, before its first await:
`TryGetFileStat` (one `IFileSystem.GetFileStat`), then for a cold Preview navigation `ThumbnailCache.GetAsync` ->
`BuildKey` (`new FileInfo` length+mtime, before its RAM lookup), plus -- after the image was up -- `GetOriginalDimensionsAsync(path)`
(`ImageCacheKey.Create(path)` = another `FileInfo`) and a second refresh `TryGetFileStat`. `RemoveMissingCatalogItemAsync`
stat-ed each next file synchronously too. On a NAS each of these is one network round trip (2-30 ms, or a stalled SMB call)
during which the window cannot repaint or take input.

Now (`src/PhotoReview.App/Coordinators/ImagePresenter.cs`, `src/PhotoReview.Core/IO/NavigationStatWorker.cs` -- in Core because it owns a deliberately blocking thread, which ADR 0005 forbids in the UI-affine App layer):

- **Stat on a dedicated worker.** `StatOffUiThreadAsync` runs the unchanged stat logic (`StatNow`, never throws) on
  `NavigationStatWorker`: one process-wide, above-normal-priority background thread with a FIFO queue. Not the thread pool:
  whole-folder preload keeps up to 8 pool threads busy with blocking decodes, and the current image's stat must not queue
  behind them (same reason the viewer decode has its own lane). Completions use `RunContinuationsAsynchronously`, so the
  awaiting navigation resumes on its own context (the Dispatcher in the app), never on the worker.
- **Superseded work is dropped, not paid for.** The stat is queued with the navigation's `viewerDecodeCts` token, which the
  next navigation cancels: a stat still queued when its navigation is superseded completes as cancelled without touching the
  disk (a key-held burst behind one slow stat pays for one stat, not thirty).
- **Generation guards.** After every off-thread stat the navigation token is re-checked before anything touches the catalog,
  the screen or the status: a superseded navigation whose stat reports Missing/Error neither removes the file nor clears the
  image nor shows an error (previously impossible, because the stat ran before any other key could be processed).
- **No second stat for the same navigation.** The thumbnail key reuses this navigation's stat
  (`ThumbnailCache.GetAsync(path, FileStat? knownStat)`; null keeps the old self-stat for other callers; the key string is
  identical, so existing disk thumbnails still hit). Original dimensions reuse `currentKey`
  (`GetOriginalDimensionsAsync(path, currentKey)`, already the documented stat-free overload; the decode seeds that entry, so
  it is normally a pure RAM lookup). An entry that left the catalog meanwhile gets its preview key from the same stat, not a
  fresh `FileInfo`. Net effect is also one to two fewer metadata calls per cold navigation (priority 1: fewer disk reads).
- The post-present refresh stat and `RemoveMissingCatalogItemAsync`'s skip-loop stats go through the same worker and re-check
  the token after each await.

**Invariants kept (and tested, `ImagePresenterTests.StatOffUiThread.cs`, delayed-stat `IFileSystem` fake + per-test worker):**

| Invariant | How |
|---|---|
| Missing vs unreadable | `StatNow` is the old `TryGetFileStat` body: only "not there"/unusable path = Missing (dropped from catalog, next shown); any other failure = Error (kept, error status). |
| Freshness: a changed file is never shown stale | No cache is looked at before the stat returns; the R7-1 refresh (catalog metadata vs stat, evict old RAM entries) runs on that stat; thumbnail/preview/dimension keys are all built from it. Test: file rewritten while its stat is held -> new pixels, stale RAM image never set. |
| Deleted while navigating | Test: file deleted while its stat is held -> removed, next photo shown, its cached preview never set. |
| Navigation generation (INV-1) | Token re-checked after every off-thread stat; test: superseded navigation's failing stat leaves image/status/catalog alone. Stale queued stats skipped (test). |
| Crossfade pre-change event | Unchanged: `UpdateCurrentImage` (which decides `isFileChange` and raises `ImageChanging` via the sink) still runs on the UI context after the await; `_presentedFilePath` logic untouched. |
| Photo info line never shows the previous image's EXIF | `CurrentPhotoInfo` is cleared synchronously when the navigation starts (before, the synchronous stat made that happen before the handler returned); guarded by the existing `ExifLineViewModelTests.LineFollowsPresentedImageAndSettings`, which failed without it. |
| UI thread never blocked by the stat | Test: `PresentAsync` returns to its caller while the stat is held, and no stat ever runs on the caller's thread. |

**Accepted trade-offs.** (1) Every navigation, including a RAM hit, now has one worker hop + one Dispatcher post before the
image is set (tens of microseconds when idle, Normal priority so it still lands before the next render); measured in the perf
fragment. (2) One worker thread means a stat hung on a dead share delays later navigations' stats (the UI stays responsive;
before, the whole window froze). (3) While the stat is pending the previous image and status stay on screen (no "loading"
flash for the common fast case).

**Mutation-checked:** synchronous stat (M1), missing token re-check after the stat (M2), R7-1 refresh disabled (M3), queued-
stat cancellation removed (M4), Missing treated as Error (M5), thumbnail ignoring `knownStat` (M6), synchronous continuations
on the worker (M7, `NavigationStatWorkerTests`) -- all killed; details in the perf fragment.

**Result (simulated link, F4):** UI-thread time per key 15/45 ms (10/30 ms link) -> ~0.4 ms; key-to-present unchanged within
noise (the stat stays on the critical path by design). Note: `ReviewMetrics.PresentedImages` is recorded after the
post-present refresh stat, so in a fast burst it undercounts shown images (superseded before that stat returned).

## Part 2 -- proposal only (no behavior change): a throttle-able seam for preload byte reads

**Problem (from #202):** image bytes never go through `IFileSystem.OpenReadShared`; each reader opens its own `FileStream`:
`SourceBytesCache.ReadAndCache`, `WicDirectDecoder` (two sites), `WpfBitmapImageDecoder` (two sites), `TurboJpegDecoder`
(`File.ReadAllBytes` + two `FileStream`s), `EmbeddedThumbnailReader`, `PreviewImageService.DecodeFromSource` (diag pre-read),
`FileHashService` (compare hash). So neither a bandwidth cap (harness) nor a foreground-first policy (product) can be applied,
and Q-R29's "preload competes with the photo on screen for the link" half stays unmeasured.

**Proposed seam (one choke point, priority carried from the caller):**

1. `PhotoReview.Core.Abstractions.ISourceReader` (or an `IFileSystem` extension) with
   `Stream OpenSource(string path, SourceReadPriority priority)` where `SourceReadPriority` is `Viewer | Preload | Background`.
   Default implementation = today's `FileStream(path, Open, Read, ReadWrite|Delete, 1 MiB, SequentialScan)` -- byte-for-byte the
   current behavior.
2. `DecodeRequest` gains `SourceReadPriority Priority` (default `Viewer`), set in `PreviewImageService.DecodeAndCache` from the
   lane that started the decode (`DecodeForViewerAsync` = Viewer; `DecodeAndCacheAsync` from preload = Preload; compare = Viewer).
   `SourceBytesCache.GetOrRead(path, priority)` and every decoder's `new FileStream` call `ISourceReader.OpenSource` instead
   (constructor-injected; decoders are built by `ImageDecoderFactory`, so one registration covers all backends).
3. A `ThrottledSourceReader` decorator implements the policy: Viewer reads never wait; Preload reads draw from a token bucket
   whose rate drops while a Viewer read is in flight (or recently measured slower than N MB/s) and preload has not reached
   that file -- the "de-prioritize, don't disable" rule #202's lat10-bw20 run supports (preload OFF was ~8x slower per
   navigation). Optional OS hint: `SetFileInformationByHandle(FileIoPriorityHintInfo, IoPriorityHintLow)` on Preload handles
   (helps local disks; not carried over SMB, so the bucket is still needed for a NAS).
4. Harness: #202's `SharedBandwidthLimiter` plugs into the same seam (`--slow-link-bandwidth-mbps` wraps `ISourceReader`
   instead of only `IFileSystem.OpenReadShared`), which finally makes the bandwidth half measurable -- measure
   `s3-next-burst`/`s4-jump` (bursts outrunning preload) at 5/20/60 MB/s before choosing the bucket policy.

**Cost/risk of the seam:** touches the decode hot path of every backend (constructor signatures, `ImageDecoderFactory`, tests
that construct decoders directly); must keep one open per file (priority 1) and must not add an allocation per read. Needs its
own PR + gate perf run (decode P50/P95 unchanged with the pass-through reader) before any throttling policy is enabled.
**State:** pending -- not started; open a separate task when the user wants part 2.
