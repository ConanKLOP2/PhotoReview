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

## R01/R02/R03/R13 UI-thread metadata I/O (2026-09-27; F4 = 2058 files, 18.9 GB; local NVMe, no NAS fixture on this box)

Full review write-up: `git show origin/codex/full-code-review-20260927:docs/refactoring/WORK-FULL-CODE-REVIEW-2026-09-27.md`.
Scope: measure the four confirmed pre-await/UI-continuation synchronous file-metadata reads before deciding whether to
fix any of them. `-Profile quick -FixtureAlias F4` (S2 40-key, `-Repeat` combined to 3 independent `s2-next-slow-quick`
runs) plus one `s4-jump` run for the harness-based numbers; R02/R03/R13 add a direct Stopwatch measurement of the exact
`FileInfo`/`IFileSystem` calls those methods make, run 500x against real F4 files, since the harness has no scripted
Compare or file-action scenario and this box has whole-folder preload (Q-R17) keeping the S2/S4 RAM-hit rate at ~100%,
which starves `ThumbnailCache.GetAsync` of real invocations to instrument end-to-end.

| Finding | What was measured | Local-disk result (median / P95 / worst observed) |
|---|---|---:|
| R01 `ImagePresenter.TryGetFileStat` | `PhotoReviewPerf.Stat` event, 3x `s2-next-slow-quick` runs (N=40 RAM-hit navs each) + 1x `s4-jump` (N=61) | 0.11-0.16 ms / 0.15-0.24 ms / 11.7 ms (nav #62 of 62, session-boundary outlier; every mid-run sample was 0.10-0.22 ms) |
| R02 `ThumbnailCache.BuildKey` | Isolated Stopwatch around `new FileInfo(path)` + `.Length` + `.LastWriteTimeUtc` (same 3 fields `BuildKey` reads), 500 real F4 files | 0.08 ms / 0.13 ms / 10.8 ms (1st file only; files 2-500 were 0.07-0.18 ms) |
| R03 `CompareViewModel.TryGetFileSize` x2 | Isolated Stopwatch around `new FileInfo(path).Exists` + `.Length` (same call `TryGetFileSize` makes), 500 real F4 files, x2 to match the Left+Right calls | 0.16 ms combined (2x0.08 ms) / 0.26 ms / ~21.6 ms worst-case combined |
| R13 `RecoveryRetryService`/`FileActionService`/`UndoService` preflight | Isolated Stopwatch around `File.Exists` + `GetFileStat`-equivalent + `File.Exists` (3 ops, matching `RetryMoveOrCopyAsync`'s source-exists/stat/destination-exists chain), 500 real F4 files | 0.21 ms / 0.29 ms / 19.0 ms (1st file only) |

Key-to-present P50/P95 across the 3 `s2-next-slow-quick` runs (same batch as the R01 Stat samples, for scale): P50 1.79 /
2.07 / 2.24 ms, P95 3.78 / 3.99 / 4.06 ms. R01's ~0.15 ms median stat is 6-8% of P50 and well inside this box's ~5 ms
same-build run-to-run noise (see the file header). `s4-jump`: P50 1.94 ms, P95 3.83 ms, same order.

**What was not measured:** NAS/slow-share timing (Q-R29 asks for this specifically; no network-share or throttled-I/O
fixture exists on this machine, and `work/diag/fixtures.local.json` only defines the local F4 folder) and full
Compare-open / file-action click-to-feedback latency as their own scripted scenarios (the harness has no
`compare-open` or `file-action` scenario yet; the isolated-call numbers above stand in for the metadata-I/O portion
specifically, which is what R01-R13 are about — the surrounding image decode / Move-Copy I/O dwarfs it either way).

**Decision (no code change for any of the four):** every synchronous call these four findings point at costs
0.1-0.3 ms on this box's local NVMe with a warm OS metadata cache, i.e. under 10% of an already-small key-to-present
budget and far below the ~100 ms threshold where UI latency becomes perceptible. The isolated single-outlier spikes
(10-21 ms, always the first file touched in a batch or a run's last navigation) look like one-time NTFS
metadata-cache/session-boundary cost, not a per-navigation steady-state tax — 60/62 `s4-jump` samples and 499/500
isolated-call samples landed in the same tight 0.07-0.3 ms band regardless of position. Per this project's
performance-over-abstraction priority (AGENTS.md "Mandatory Process") and the explicit instruction for this pass, a
negligible, unmeasured-on-slow-storage cost does not justify adding `Task.Run`/async-stat plumbing, a second
`IFileSystem` async surface, or threading a shared stat through `ImagePresenter` into `ThumbnailCache` (the R02
dual-improvement idea from the review) -- that would be speculative complexity against a cost this box cannot show is
real. No test was added for any of the four findings; none of the four code paths changed. This narrows Q-R29
(the NAS-specific stat/preload-bandwidth question) rather than closing it: local-disk cost is now measured and
ruled negligible, but the NAS case Q-R29 asks about is still open and still needs a slow-share/NAS fixture to answer
for real.

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

## Kinetic pan / glide (2026-09-26, no app-side stutter cause found)

`KineticPanFrameMeasurementTests` (Manual): UI cost per frame 0.4-0.8 ms, no GC during glides. The judder is frame pacing (DWM/WPF with a mixed 240/60 Hz, hybrid-GPU setup); a
control scene without the photo behaves the same. Tried without effect: 1 ms timer resolution, thread priorities, extra render ticks. Not done: flip-model D3D11/DirectComposition swap chain (large, risky),
pointer interpolation on drag. Shipped: `KineticGlideSmoothing` (default `Predict`): vblank-aligned steps (`WindowsDisplayClock`, `VBlankEstimator`, `GlideFrameClock`); on a 59.94 Hz monitor speed error RMS
0.69 -> 0.11 and judder 43 -> 12 frames/s, on 240 Hz speed jumps drop but the hold pattern stays. 75/144 Hz only simulated in unit tests.

## Full-code-review-2026-09-27 misc findings (R06/R07/R11, perf/benchmark-cache-misc)

**R06 (`PreviewImageService` queued-persist memory, no code change):** worst-case retained bitmaps outside the RAM LRU while queued for disk persistence =
`PersistQueueCapacity` (16, drop-when-full) + `PersistWorkerCount` (2 in-flight writes) = 18. Each queued bitmap is at most the app's own "largest sane preview" box
(`RamBudgetPolicy.MinimumBudgetBoxWidth` x `MinimumBudgetBoxHeight` = 3840x2160, 4 bytes/pixel = ~31.64 MiB) since only downscaled previews are ever queued for
persistence (originals are never disk-cached). 18 x 31.64 MiB ~= 569.5 MiB (~0.56 GiB) -- under 4 % of the smallest disk/RAM-derived budget this class configures
and under 4 % of the 16 GiB preview-cache target it is tuned for. Could not construct a realistic slow-disk-write scenario: the persist writer
(`PreviewCacheFile.WriteAtomicallyAsync`) is a static method with no injectable slow-writer test seam, and every existing test double in
`PreviewImageServiceFaultTests`/`PreviewImageServiceTests` only fakes the decoder, not the disk writer. Conclusion: no code change; regression test
(`PreviewImagePersistQueueBoundTests`) pins the two constants so a future change to either is deliberate.

**R07 (`PerformanceTestHarness.MeasureParallelAsync` eager Task creation, fixed):** benchmark tooling only. Before: `files.Select(... => Task.Run(...))` created one
`Task` (one thread-pool work item) per selected file immediately, each then waiting on a `SemaphoreSlim(workers, workers)` internally -- a `take` far larger than
`workers` queued that many work items up front. After: the semaphore slot is acquired in the loop, before that file's `Task` is created, so at most `workers` Tasks
are ever alive at once. `PerformanceHarnessConcurrencyTests` (60 files, 4 workers, `ThreadPool.SetMinThreads` raised so the pool can't itself mask the bug) proves this:
against the old eager-creation code the same test observes up to 19-20 concurrent decodes (mutation-checked); against the fix, never more than 4. Benchmark report
shape (per-file order, P50/P95/stats, JSON) is unchanged -- the existing `BenchmarkReportFormattingTests`/`BenchmarkWorkloadRunnerTests` etc. still pass.

**R11 (`BenchmarkWindow.RunAsync` synchronous folder scan, fixed):** measured the synchronous portion directly (a standalone `Directory.EnumerateFiles(...).Where(...).ToArray()`
+ stat pass over a local NVMe temp folder, same shape as the window's old code): 20,000 top-level entries = 77 ms, 100,000 entries = 203 ms; the image-count-capped stat
pass itself is negligible (single-digit ms). 200 ms is already a visible dialog stall for a large local folder, and this app explicitly supports NAS/network-drive photo
folders (see Q-R29) where per-call latency is materially higher, so the synchronous scan was moved to a background thread (`BenchmarkWindow.EnumerateAndStat`), with the
run's `CancellationTokenSource` created before the scan (not after) and the token polled every 256 entries so Cancel/Close can interrupt an in-progress scan. Image-count
cap, enumeration order and the stat-sum total are unchanged (`BenchmarkWindowEnumerateAndStatTests`); the folder-missing/no-images/cannot-read-folder status messages,
button re-enable lifecycle and a Cancel-during-scan path are covered by `BenchmarkWindowEnumerationUiTests`.
