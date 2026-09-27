# Performance status and baselines

**Updated:** 2026-09-27. Older detail (D-series tasks, AR04 interleaved gate tables, per-PR perf-night notes, benchmark speed-up step tables,
kinetic-pan harness tables): `git show 1de561c:docs/refactoring/PERF-STATUS.md`.
Fixture F4 = a real folder of portrait 20-30 MP JPEGs (1841-1895 files, 13-16 GB); paths are machine-specific (`CLAUDE.local.md`).
Harness: `tools/diag/run-matrix.ps1 -Profile quick|gate|full` (`--perf-session` on the production graph via `AppHost`). Compare only within
the same folder state, same build family and interleaved runs (same-build P95 noise is ~5 ms).

**Do not compare numbers before the AR02c boundary:** the D-series / T66 numbers came from the legacy `--perf-session` graph (no preload, 64 MiB cache).

## PR-D image-crossfade (2026-09-27; F4 = 2058 files, 18.9 GB; `-Profile quick -FixtureAlias F4 -Repeat 3`)

`ImageTransition` has no CLI override in the harness, so `ImageTransitionMs`/`ImageTransition` were set in the real
`%LOCALAPPDATA%\PhotoReview\config.json` between runs (restored after). Same branch build both times (only the setting
differs), so this compares `None` (branch, functionally the same code path as master) against `Fade` 120 ms; both runs
were after the full test-suite/other perf runs on this box had fully exited (an interleaved first attempt showed P95 in
the 1500 ms range with dozens of timeouts -- CPU contention, discarded).

| Scenario | Metric | `None` | `Fade` 120 ms |
|---|---|---:|---:|
| S2 next (40 keys, realistic pace) | key→present P50 (3 runs) | 14.4 / 14.7 ms (run 1 discarded: warm-up P95 outlier) | 10.5 / 10.8 ms |
| S2 next | key→present P95 (2 clean runs) | 24.9 / 97.6 ms | 19.7 / 20.9 ms |
| S3 burst (30/s x200, holds a key) | avg `PresentMilliseconds`/image (3 runs) | 3.15 / 3.55 / 3.60 ms | 7.39 / 8.27 / 8.39 ms |

**Reading this:** at a realistic browsing pace (S2), `Fade` shows no regression -- P50/P95 are the same or lower than
`None`, within this box's run-to-run noise (see the discarded outlier). At a sustained 30 keys/s burst (S3, i.e. key
auto-repeat held down), each `Fade` present costs ~4-5 ms more on average: `StartImageFade` (`MainWindow.xaml.cs`) runs
synchronously inside `PresentAsync`'s call stack (reading `MainImage.ActualWidth`/`ImageScroll` offsets, allocating a
`DoubleAnimation`, one `BeginAnimation` call) every time the photo changes while `Fade` is on. This does not delay
presentation of the new image itself (the swap to the new bitmap is unchanged; only the outgoing layer is animated
afterwards, off the critical path for what the user sees next) and `HandoffBehavior.SnapshotAndReplace` still cancels
a running fade instantly under this exact burst -- but the extra per-present setup cost is real and, being
inline/synchronous, shows up in `PresentMilliseconds`. Acceptable because `Fade` is opt-in (default `None`) and S3's
30/s auto-repeat is an extreme case; a future optimization (reusing one `DoubleAnimation` instance instead of
allocating one per navigation) could reduce this further if it matters in practice.

`None` was not compared against unmodified `master` directly (git checkout of a second build was out of scope for
this measurement pass); `None`'s code path adds one cheap `ImageTransitionDecision.ShouldTransition` bool check per
`UpdateCurrentImage` call and returns before touching any WPF element (`OutgoingImage` stays `Visibility.Collapsed`,
no animation clock), so no measurable difference from `master` is expected and none was observed against noise.

## Baseline AR02e (production graph, 2026-09-23; F4 = 1625 files, 13.2 GB)

Window 1920x1080, Preview, warm, cache 16 GiB, 8 preload workers, WicDirect, disk cache on. "After" = decode width restored (#31).

| Scenario | Metric | Before | After |
|---|---|---:|---:|
| S1 open folder | first / final visual P50 | 366 / 488 ms | 323 / 421 ms |
| S2 next 1.5 s x100 | key->present P50 / P95 | 2.0-2.5 / 4.8-5.2 ms | 3.6-4.0 / 5.4-7.2 ms |
| S3 burst 30/s x200 | incomplete navigations | 309 / 402 | 44 / 402 |
| S4 jump | P50 / P95 | 1.8 / 4.9 ms | 3.4 / 6.6 ms |

AR04 (UI-thread affinity) perf gate passed on interleaved runs (mean P95 difference <= 1 ms, `CrossThreadPresentCount = 0`).

## Perf night 2026-09-24 (PRs #39-#48; F4 = 1841 files, 14 GB)

| Metric | baseline | after #40-#48 |
|---|---:|---:|
| Open folder, first visual (median) | 489 ms | **166 ms** |
| Burst 30 keys/s: images shown /200 | 50-60 | **197-201** |
| Burst key->present P95 | 10.5 ms | **2.8-3.5 ms** |
| Jump P95 / max | 7.6-8.4 / 474-489 ms | **3.8-4.0 / 164-165 ms** |
| Peak WS open / slow-next / burst | 1.6 / 4.2 / 5.2 GB | **0.33 / 1.1-1.3 / 1.7 GB** |
| App start -> first image from Explorer (#46) | 3169 ms | **1817 ms** |

What did it: ICC via WIC color transform, Bgr32/Pbgra32, preview races the thumbnail, embedded EXIF thumbnails, JPEG single-file disk cache, direction-aware
burst preload with viewer-priority decode, decode to the viewport box (#43). Not shipped: SIMD-only TurboJpeg scale factors (slower), non-default GC mode
(AR12c, reason in `architecture.md`); TurboJpeg stays experimental and slower than WicDirect (Q-AR8 a).

## AR16 readability probe (2026-09-26)

The per-file open probe cost ~114 ms per folder open on F4 (~68 % of the 166 ms first visual). Moved to a background pass (Q-AR7 c, #107). Interleaved master vs branch,
6 runs: catalog ready 305 -> 88 ms, first present 603 -> 353 ms (Folder trace), first visual 291 -> 262 ms; S2/S3 P95 and peak WS unchanged (within noise).
AR15c (`SourceBytesCache` read on the calling thread) is not worse with the cache on (CLI flag `--source-bytes-cache`).

## Whole-folder preload on F4 (Q-R17, Q-R26; 2026-09-26)

Q-R17 keeps the whole 13-16 GB folder in ~10 GB of RAM (AGENTS.md rule 2). First-visual P50/P95 was 2.5-3x the window mode (4.4/8.6 vs 1.6/2.8 ms). Ruled out: GC,
page faults, memory compression, decode contention. **Cause:** the synchronous `preloadKick` after each assign built a cache key and looked up every image in the
folder on the UI thread (P50 2.0-2.8 ms vs 0.25-0.37 ms). **Fix:** yield to the thread pool at the first candidate outside the 32/8 window (`PreloadKickOffCallerTests`);
`preloadKick` P50 0.13-0.26 ms, S2 first P50/P95 2.0/4.9 vs 1.9/3.6 ms in window mode, whole folder still preloaded (peak WS 10.8 GB).

Natural sort / Explorer snapshot validator (idle PC, old = `*Reference` oracles): `TryValidate` 50k 111 -> 47 ms (x2.36), `Array.Sort` 50k 655 -> 412 ms (x1.59), allocations -50 % to -99 %.

## Benchmark harness speed-up (#109, 2026-09-26)

`run-matrix.ps1`: one warm-up per (fixture, mode, condition) per batch (`-WarmupEveryCell` restores the old behaviour); `-Profile quick` uses `s2-next-slow-quick` (40 keys);
`gate`/`full` unchanged. F4: gate 393 -> 260 s (-34 %), quick 209 -> 158 s (-24 %). In-app `BenchmarkWindow`: "Quick check" button (fast-sequential, 10 iterations, 64 images) ~3.1x faster than the default run.

## R04/Q-R29 preload contention -- local-disk evidence, and R14 startup sweep (2026-09-27; F4 = 2058 files, 17.6 GB)

**Code-reading (no live measurement needed): the worker-count reduction while a viewer decode is active
(`PreloadScheduler.cs:88-97,343-345`) only throttles NEW preload starts to `_viewerBusyWorkerLimit`
(>=2); a preload read already in flight is never interrupted (its own comment says so, `:338-342`) --
but this is not a software synchronization gap.** The viewer's own decode never queues behind preload
workers at all: `PreviewImageService.DecodeForViewerAsync` takes its own `_viewerSlots` gate
(`ViewerDecodeSlots = 2`, separate from preload's `_preloadSlots` semaphore) and runs the actual decode on
a dedicated `TaskCreationOptions.LongRunning` thread raised to `ThreadPriority.AboveNormal`
(`PreviewImageService.cs:412-450`), specifically so neither preload workers nor thread-pool growth delays
it (comment at `:415-421`, from the #39-#48 perf night). `SourceBytesCache.GetOrRead` dedups per-key
(`ConcurrentDictionary<Key, Lazy<byte[]>>`) and only locks (`_publishGate`) around cheap in-memory
bookkeeping, never around the actual `FileStream` read (`SourceBytesCache.cs`), so a preload read of file A
cannot block a viewer read of file B behind a shared lock. `WicDirectDecoder.CreateFactory()` builds a
fresh `IWICImagingFactory` per call (no cached/shared COM object either). **No shared semaphore, lock, or
decoder instance was found that a preload read could hold while a foreground decode waits on it** --
the only channel left for one to slow the other is the physical device (disk queue depth / network
bandwidth), which is exactly Q-R29's NAS hypothesis, not a fixable ordering bug.

Local measurement (F4, real NVMe/SSD-class disk; harness: `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0|8` env
override + `run-matrix.ps1 -Scenarios s2-next-slow-quick -Conditions warm -Repeat 2`), run on a shared dev
box with 20-40 other `dotnet.exe` processes belonging to other concurrent sessions (`Get-Counter
'\Processor(_Total)\% Processor Time'` read 85-100% during parts of this run, ~7-24% during others --
confirmed with the counter, not guessed):

| Preload | Run | key-settle P50 | P95 | timeouts/40 | System load at the time |
|---|---|---:|---:|---:|---|
| 8 workers (default) | cold cache | 19.1 ms | 1528.8 ms | 13 | ~85-100% CPU (other sessions) |
| 8 workers (default) | warm cache (2nd run) | 14.8 ms | 31.6 ms | 0 | quiet |
| 0 workers (disabled) | cold cache | 297.1 ms | 343.9 ms | 0 | ~85-100% CPU (other sessions) |
| 0 workers (disabled) | warm cache (2nd run) | 14.5 ms | 31.6 ms | 0 | quiet |

Reading this: cold-cache navigation with preload OFF is far slower (P50 297 ms) than with it ON (19 ms)
-- expected, since every image must then decode from source on the UI's own request instead of already
being warm. The P95 spike (1528.8 ms, 13/40 timeouts) happened on the 8-worker cold-cache run specifically
while this shared box was near 100% CPU from other sessions' processes; the very next run (same code,
quieter box) landed at P95 31.6 ms with zero timeouts -- and the 0-worker run's own warm-cache pass landed
at the *identical* 14.5/31.6 ms once its disk cache was warm too (populated by ordinary viewer decodes,
which write to the disk cache regardless of the preload worker count). **The tail correlates with
external CPU contention on the shared box, not with the preload-worker setting**: turning preload off
did not remove the tail (it just wasn't present in that quieter run either), and turning preload on did
not introduce a tail once the box was quiet. This is consistent with the codebase's own prior finding
(`PR-D image-crossfade` above: "an interleaved first attempt showed P95 in the 1500 ms range... CPU
contention, discarded") -- the same artifact reproduced here independently.

**Conclusion for Q-R29:** on local disk, no evidence of a software synchronization/starvation gap was
found (code-level: viewer decode is fully decoupled from preload's worker pool and locks; empirically: the
P95 tail tracked system-wide CPU load, not the preload on/off setting). This narrows Q-R29 to what it
already suspected: whether whole-folder preload starves the next decode is a **bandwidth-only** question
specific to a slow link (NAS/Wi-Fi), which cannot be reproduced or ruled out on this local-disk box --
it still needs a real NAS/slow-link measurement before deciding whether to add I/O throttling. See
`OPEN-DECISIONS.md` Q-R29.

**R14 startup sweep:** `SessionStore`'s constructor ran `SweepStaleTempFiles` synchronously (resolved on
the UI thread at startup via `MainViewModelCompositionRoot`). Measured on real disk (temp directory, not
the user's real session folder): cost scales ~linearly with the stale-`*.tmp`-file count -- 100 files ~24
ms, 500 ~120 ms, 1000 ~282 ms, 3000 ~700 ms-1.3 s (`SessionStoreSweepPerfTests`, `Category=Native`). This
is material against the ~1.8 s app-start-to-first-image budget (perf night table above) for a plausible
worst case (R2-F-34: one leftover `*.tmp` per crash between an atomic write's temp-create and rename;
years of crashes/kills could plausibly leave hundreds to low thousands). **Fix:** the sweep now runs on a
background `Task` (`SessionStore.StartupSweepTask`, test-seam only) instead of blocking the constructor.
Proven safe: `Load`/`Save` never touch `*.tmp` files (`Load` reads the real `*.json` path; `Save`'s own
temp file is renamed away, or cleaned up by `WriteAllTextAtomic` itself on failure), and the existing
1-day age guard means a background sweep can never delete a temp file a concurrent `Save()` is still
writing (that file is milliseconds old, never "stale") -- confirmed by a new test that gates the sweep
mid-flight and asserts `Save`/`Load` still complete immediately instead of waiting for it
(`SaveAndLoad_WhileStartupSweepStillRunning_AreUnaffected`). After the fix, construction time is 0.0-0.2 ms
regardless of stale-file count (same test file, mutation-checked: reverting to the synchronous call makes
the assertion fail as expected).

## Kinetic pan / glide (2026-09-26, no app-side stutter cause found)

`KineticPanFrameMeasurementTests` (Manual): UI cost per frame 0.4-0.8 ms, no GC during glides. The judder is frame pacing (DWM/WPF with a mixed 240/60 Hz, hybrid-GPU setup); a
control scene without the photo behaves the same. Tried without effect: 1 ms timer resolution, thread priorities, extra render ticks. Not done: flip-model D3D11/DirectComposition swap chain (large, risky),
pointer interpolation on drag. Shipped: `KineticGlideSmoothing` (default `Predict`): vblank-aligned steps (`WindowsDisplayClock`, `VBlankEstimator`, `GlideFrameClock`); on a 59.94 Hz monitor speed error RMS
0.69 -> 0.11 and judder 43 -> 12 frames/s, on 240 Hz speed jumps drop but the hold pattern stays. 75/144 Hz only simulated in unit tests.
