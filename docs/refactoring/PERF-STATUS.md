# Performance status and baselines

**Updated:** 2026-09-27. Older detail (D-series tasks, AR04 interleaved gate tables, per-PR perf-night notes, benchmark speed-up step tables,
kinetic-pan harness tables): `git show 1de561c:docs/refactoring/PERF-STATUS.md`.
Fixture F4 = a real folder of portrait 20-30 MP JPEGs (1841-1895 files, 13-16 GB); paths are machine-specific (`CLAUDE.local.md`).
Harness: `tools/diag/run-matrix.ps1 -Profile quick|gate|full` (`--perf-session` on the production graph via `AppHost`). Compare only within
the same folder state, same build family and interleaved runs (same-build P95 noise is ~5 ms).

**Do not compare numbers before the AR02c boundary:** the D-series / T66 numbers came from the legacy `--perf-session` graph (no preload, 64 MiB cache).

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
