# Performance Diagnosis Status (D Series)

**Last updated:** 2026-09-24  
**Overall status:** D series closed 2026-09-23 (Q-AR5) — superseded by the AR02e production-graph baseline below

## Current Status

| ID | Task | Status | Notes |
|---|---|---|---|
| D00 | Setup, fixtures, tools | ✅ DONE | — |
| D03-D07, D10-D12 | Instrumentation, scenarios, analysis | ✅ DONE | — |
| D01, D02, D08, D09 | AppLog/File-access analysis, ETW, GC profiler deep-dive | ❌ Closed 2026-09-23 (Q-AR5) | Numbers were from the legacy `--perf-session` graph (no preload); re-open any item from the AR02e baseline if a bottleneck shows |

## Full Task History

Detailed task tracking archived at: [`docs/archive/historical/PERF-DIAGNOSIS-TASKS.md`](../archive/historical/PERF-DIAGNOSIS-TASKS.md) and [`PERF-DIAGNOSIS-PLAN.md`](../archive/historical/PERF-DIAGNOSIS-PLAN.md)

## Key Conclusions

- D06 Procmon scenario driver established baseline (_perf-session_ mode); superseded by the AR02e production-graph baseline (see below)
- EventSource instrumentation and measurement points in place (D03-D04)
- Scenario matrix (D07) run; D series closed 2026-09-23 (Q-AR5) instead of profiler deep-dive — see `docs/ACTIVE-TASKS.md`

See [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) for current work status.

## Baseline AR02e (production graph) — 2026-09-23

Harness: `run-matrix.ps1` → `--perf-session` via `AppHost` (AR02c), window 1920×1080, Preview, `warm` (1 warm-up + 2 recorded runs/cell). Effective config: cache 16 GiB, 8 preload workers, WicDirect, SourceBytesCache off, disk cache on. Fixture F4 = real folder, 1625 files (1563 JPG + 62 PNG), 13.2 GB. Build = master `b2048b6` + #25–#30 + #33 + metrics fix `9849c8b`; **after** adds `bb87839` (#31, decode width restored: 0 → 2190 px).

| Scenario | Metric | Before (width 0, full-size decode) | After (#31) |
|---|---|---:|---:|
| S1 open folder | first / final visual P50 | 366 / 488 ms | 323 / 421 ms |
| S1 | peak working set | 3.4 GB | 1.5 GB |
| S2 next 1.5 s ×100 | key→present P50 / P95 / max | 2.0–2.5 / 4.8–5.2 / 738–822 ms | 3.6–4.0 / 5.4–7.2 / 347–440 ms |
| S2 | peak WS · decode total · source reads | 12.2 GB · 66 s · 127 | 4.1 GB · 41 s · 53 |
| S3 burst 30/s ×200 | incomplete navigations | 309 / 402 | 44 / 402 |
| S3 | P95 · images shown · peak WS | 40.3 ms · 27–47/200 · 16.6–16.9 GB | 12.7 ms · 145–175/200 · 6.0–7.0 GB |
| S4 jump | P50 / P95 / max · peak WS | 1.8 / 4.9 / 503 ms · 5.6 GB | 3.4 / 6.6 / 400 ms · 2.3 GB |

- **AR04 gate = the "After" column.** Older D-series / T66 numbers are legacy graph (no preload, F2) — not comparable.
- Open questions: RAM-hit P50 is ~1.5 ms higher after (both < 1 frame; render-dominated); S3 records 85 decoder fallbacks after vs 14 before (WicDirect with a target width?) — investigate before tuning.

## AR04 perf gate (UI-thread affinity) — 2026-09-24

Same fixture/config as AR02e. A first batch (master e1375cc vs AR04, 3 runs/cell) looked like a regression (S2 P95 7.2 -> 11.1 ms) but the fixture folder was being modified during that batch (files grew 1689 → 1841 while photo sets were copied in), so master looked degraded (S2 hit rate 37.7 %) — not comparable. Gate decided on **interleaved** runs (master, AR04, master, AR04; S2 + S4, warm):

| Round | master S2 P50/P95 | AR04 S2 P50/P95 | master S4 P50/P95 | AR04 S4 P50/P95 |
|---|---:|---:|---:|---:|
| 1 | 6.5 / 12.4 | 7.9 / 12.4 | 5.6 / 11.0 | 4.5 / 11.9 |
| 2 | 4.8 / 6.9 | 4.7 / 7.7 | 4.5 / 9.3 | 4.3 / 6.3 |
| mean P95 | 9.7 | 10.1 (+0.4) | 10.2 | 9.1 (-1.1) |

- **Passed** (<= 1 ms): same-build run-to-run P95 noise is ~5 ms, larger than the difference. Hit rate 99.0 % / 98.4 % both; peak WS equal; `CrossThreadPresentCount = 0` in every AR04 run. All values < 1 frame (16.7 ms).
- Interleaved rounds all ran on a stable folder (1841 files). The earlier "master lost preload" was the folder changing, not evidence of the catalog race. Note: AR02e baseline used 1625 files — compare only within the same folder state.

## Perf night 2026-09-24 — PR series #39–#48

Real folder F4 (1841 files ≈ 14 GB, mostly portrait 20–30 MP JPEG), production graph, Preview, harness from #39 (event-driven pacing, short warm-up) + batch-isolated caches (#42). Interleaved baseline/new, 2 rounds × 2 runs, fixture unchanged. Baseline = master `b6e5dae` + #39 + cache isolation; "all" = + #40–#48.

| Metric | baseline | all |
|---|---:|---:|
| Open folder, first visual (median, n=4) | 489 ms | **166 ms** |
| Burst 30 keys/s: images shown /200 | 50–60 | **197–201** |
| Burst incomplete navigations | 275–292 / ~400 | **0–1 / 402** |
| Burst key→present P95 | 10.5 ms | **2.8–3.5 ms** |
| Jump P95 / max | 7.6–8.4 / 474–489 ms | **3.8–4.0 / 164–165 ms** |
| Slow-next P95 / max | 3.1–3.4 / 503–523 ms | 3.1–3.2 / 156–174 ms |
| Peak WS open / slow-next / burst | 1.6 / 4.2 / 5.2 GB | **0.33 / 1.1–1.3 / 1.7 GB** |
| App start → first image from Explorer (#46, 11 runs) | 3169 ms | **1817 ms** |

Per PR (measured as it landed): #40 ICC via WIC color transform + Bgr32/Pbgra32 + worker-side materialization; #41 preview races the thumbnail, embedded EXIF thumbnails; #42 JPEG single-file disk cache (fallback previews cached); together open 453→180 ms, burst incomplete 250→57. #44 original dims from the decode (first made open +20 ms → fixed by yielding before bookkeeping); #45 direction-aware burst preload, viewer-priority decode (worst burst final 1.9–3.9 s → 0.36–1.1 s). #43 decode to the viewport box (portraits −85 % pixels): burst incomplete 22–40 → 0–2, peak WS −70 % — zoom decision made (option A), #43 and #47 merged. #46 startup: Explorer order batched (1.1 s → ~0.1 s), first image before Explorer order (INV-9 behaviour change). #48 Original mode wiring (lost in T46d).

- New harness: `run-matrix.ps1 -Profile quick|gate|full` (gate ≈ 7 min vs ~30 min), `-ColdDiskCache`, fixture-change guard; `--perf-analyze` reports `renderedFrame` (2nd Rendering tick) and Startup/Folder phases again.
- Not shipped: SIMD-only TurboJpeg scale factors (measured slower). TurboJpeg remains slower than WicDirect on this set.
- Not shipped: GC mode — giữ mặc định (AR12c), lý do trong architecture.md.
- Not shipped: TurboJpeg giữ trạng thái thử nghiệm, không đầu tư thêm (Q-AR8 a).

## Architecture review 2026-09-26 (AR15c, AR16 step 1)

| Measurement | Result |
|---|---|
| AR16 probe (F4, 1841 files, warm OS cache, 1 warm-up + 7 alternating runs, scratch console app mirroring `EnumerateReadableFilesWithStat`) | enumerate + filter 4.0 / 5.1 ms (median / P90); + per-file open probe 117.4 / 140.9 ms; **probe cost 113.6 / 136.8 ms = 61.7 µs per file ≈ 68 % of the 166 ms first-visual baseline**. The probe (IO04, #66) landed after that baseline (#39–#48), so current open-folder first visual was never re-measured. Q-AR7 pending. |
| AR15c `SourceBytesCache` read on the calling thread, `UseSourceBytesCache=true` via new `--source-bytes-cache on` (quick profile, interleaved, 2 rounds, variant = AR15c reverted) | S2 next-slow P50/P95 15.2/30.6 ms (variant) vs 15.2/30.5 ms (AR15c); peak WS 15.7 vs 15.2 GB; S3 burst 200/200 shown, 0 incomplete in all runs, peak WS < 1 % apart. **Not worse.** |


## AR16 (c) readability probe moved off the first-visual path — 2026-09-26

F4 (1841 files), Preview, warm, production graph, `run-matrix.ps1 -Scenarios s1-open-folder,s2-next-slow,s3-next-burst -Repeat 3`, interleaved master `f2661fc`, branch `perf/ar16-background-probe`, master, branch (+1 extra S3 round each). Medians over 6 runs per build; S2/S3 P95 per batch.

| Metric | master | AR16 (c) |
|---|---:|---:|
| S1 T1 catalog ready (Folder trace) | 305 ms | **88 ms** |
| S1 T0→T2 first present (Folder trace) | 603 ms | **353 ms** |
| S1 first / final visual (nav metric) | 291 / 291 ms | **262 / 262 ms** |
| S2 next-slow P95 | 10.2 / 10.3 ms | 7.8 / 11.1 ms |
| S3 burst P95 (incomplete) | 12.5 (94) / 7.8 / 23.7 ms (0) | 16.9 / 13.2 / 20.7 ms (0) |
| Peak WS S1 / S2 / S3 | 7.8–10.1 / 10.2 / 10.7 GB | 8.8–10.2 / 10.2 / 10.7 GB |

- The listing no longer opens each file: catalog-ready drops ~217 ms, first present ~250 ms (Folder trace). S2/S3 P95 differences are inside same-build run-to-run noise (S3 master alone spans 7.8–23.7 ms); one branch S3 run had a 1.5 s max outlier not seen in the extra round. Peak WS unchanged. Raw data is not committed.

## Benchmark harness/window speed-up (benchmark speed-up) — 2026-09-26

Goal: make the benchmark tools (CLI harness `run-matrix.ps1`, in-app `BenchmarkWindow`) themselves faster to run, not the app under test. Branch `perf/benchmark-speed`, base `f2661fc`.

**Step 1 — where a `gate` run spends time** (F4, 1841 files, `-Profile gate -SkipBuild`, before any code change): total batch 393 s.

| Scenario/cell | warm-up | run 1 | run 2 |
|---|---:|---:|---:|
| s2-next-slow | 127.2 s | 105.0 s | 15.3 s |
| s3-next-burst | 9.8 s | 78.9 s | 16.6 s |
| s4-jump | 9.0 s | 14.7 s | 16.1 s |

Warm-up total 146.0 s / 392.6 s = **37.2 % of wall time** — well above the 10 % threshold, so **Step 3a (batched warm-up) was implemented**. `dotnet build PhotoReview.slnx -c Release` (full solution, from clean `obj`/`bin` after `git switch -c`): 60.7 s, 0 warnings.

**Step 3a** — `run-matrix.ps1` now runs one warm-up per (fixture, mode, condition) combo per batch (scenario loop moved innermost) instead of once per scenario/fixture/mode/condition cell; `-WarmupEveryCell` restores the old per-cell behavior (`-FullWarmup` implies it, since a full warm-up only makes sense per scenario).

**Step 3b** — new scenario `s2-next-slow-quick.json` (same as `s2-next-slow`, `repeat: 40` instead of 100); `-Profile quick` now uses it. `-Profile gate`/`full` are unchanged (100 keys) so decisions stay comparable to earlier AR entries in this file. `PerfAnalyze.IsBurstScenario` and the rest of the analyze/report tooling key off the scenario's `name` field ("S2-next-slow", unchanged) and an S3/S4 prefix check, not the file name, so no other change was needed there.

**Step 2** — `BenchmarkWindow`: "Quick check" button (fast-sequential, 10 iterations, its default 1 warm-up) via `BuildQuickCheckProfile`; image-limit control (default 64, 0 = all) via `ApplyImageLimit`, mirroring the CLI's `--benchmark-all` `Take(64)`; `BenchmarkImageExecutor.DisposeAsync` no longer awaits the disk-cache prune pass (was bounded to 5 s) before returning — the wait + directory delete move to a background task (`TeardownBackgroundTask`) so a slow prune cannot hold up the next profile in a sequential run, while still guaranteeing the scratch directory is removed once the prune settles.

**Step 4 — before/after wall-clock (F4, 1841 files, same machine, interleaved commit range `f2661fc`→`984b55d`, -SkipBuild):**

| Run | Before | After | Change |
|---|---:|---:|---:|
| `-Profile gate` (S2+S3+S4, repeat 2) | 393.0 s | 260.3 s | **-33.8 %** |
| `-Profile quick` (S2 100 keys + S3, repeat 1, `-WarmupEveryCell` = old default) → new quick (S2-quick 40 keys + S3, repeat 1, batched warm-up) | 208.5 s | 157.9 s | **-24.3 %** |

In-app `BenchmarkWindow` (headless probe: `BenchmarkEngine.RunPreparedAsync` + `BenchmarkWorkloadRunner.PrepareIterationAsync` through a real `BenchmarkImageExecutor`, same F4 folder, same recipe the window uses):

| Run | Files | Iterations | Wall time |
|---|---:|---:|---:|
| Default run today (fast-sequential, no cap — pre-Step-2b) | 1841 | 30 | 7875 ms |
| Default run with the new 64-image cap | 64 | 30 | 8157 ms |
| **Quick check** (fast-sequential, 64 images, 10 iterations) | 64 | 10 | **2620 ms** |

The image cap alone barely moves fast-sequential's wall time on this fixture: its `ImagesPerSample = min(Workers, fileCount) = 8` either way once the folder has ≥8 files, so decode volume is unchanged — the cap's real payoff is keeping setup (file enumeration, `totalSourceBytes`, the preload-window index) and `FullFolder` profiles (e.g. `full-folder-warm`, `ram-maximizer`) cheap on folders with tens of thousands of files, not measurable on this 1841-file fixture. "Quick check" itself is the big win: **~3.1x faster than the default run** (10 vs 30 timed iterations, same 1 warm-up).

Gate: `dotnet build PhotoReview.slnx -c Release` 0 warnings/0 errors; `dotnet test --filter "Category!=Manual&Category!=Native&Category!=Slow"` 3352 passed, 1 skipped (pre-existing, unrelated), 0 failed; `docs-budget.ps1 -Check`, `check-doc-links.ps1`, `i18n-check.ps1` all PASS. Mutation checks (reverted after each): `BenchmarkWindow.ApplyImageLimit` cap removed → red; `BuildQuickCheckProfile` iterations override ignored → red; `BenchmarkImageExecutor` teardown cleanup skipped → red (this last one needed a test fix — the first version of the teardown test was a false green because the RAM cache never evicted the 5 tiny test images, so nothing was ever persisted to disk to clean up; fixed by seeding the scratch directory directly).

## Q-R17 preload estimate and natural sort / snapshot validator — measured 2026-09-26

**Q-R17 (merged #82, never measured before).** F4 (1841 files, 13.2 GB), Preview, warm, `-Profile gate` (S2+S3+S4, repeat 2), production graph, cache 16 GiB, 8 workers, WicDirect, decode box 2304×1280. Interleaved master `868275b` / variant (same commit, `EstimateFolderPreviewBytes` forced to the pre-Q-R17 `compressed × 10`) / master / variant; `--perf-analyze` first-visual ms.

| | master (Q-R17) run 1 / run 2 | variant (old estimate) run 1 / run 2 |
|---|---|---|
| Preload mode | whole folder: 1840 `PreloadItem` per run | window only: ~70–240 per run |
| Peak working set | 9.9–10.7 GB | 0.7–1.8 GB |
| S2 next-slow P50 / P95 | 4.4 / 8.6 · 5.0 / 9.6 | 1.6 / 2.8 · 1.8 / 3.8 |
| S3 next-burst P50 / P95 | 3.1 / 5.5 · 3.6 / 8.3 | 1.7 / 3.0 · 1.7 / 2.7 |
| S4 jump P50 / P95 | 3.9 / 11.1 · 4.2 / 10.2 | 2.0 / 3.5 · 2.1 / 3.7 |
| Hit rate / kinds | 98.4–99.5 %, all RamHit (+2 disk) | identical |
| Batch wall time | ~4.5 min (fill per run) | ~1.5 min |

Q-R17 works as designed (F4 now keeps the whole folder in ~10 GB of RAM, AGENTS.md rule 2), but first-visual latency is ~2.5–3× higher (still < 12 ms P95, under one 60 Hz frame). It is **not** decode contention: the scenario waits for idle, and the fill had finished before the first key (54 s cold, 3.7 s with a warm disk cache; `PreloadItem` vs `KeyInput` timestamps). (`process.json` GC totals — heap 532 vs 37 MB, gen2 149 vs 69 — cover the whole iteration including the fill, not the navigations; see the follow-up below.) The scenarios stay inside the preload window, so the extra RAM brings no extra hits here; the benefit (far jumps/revisits all RAM hits) is not exercised by S2–S4. Q-R26 = B (user): keep Q-R17, find the cause.

**Natural sort / Explorer snapshot validator (`83077cf`, first measured on a busy PC).** Re-run on the idle machine (`Category=Manual` `NaturalKeyBenchmarkTests`, `ExplorerSnapshotValidatorBenchmarkTests`, old = `*Reference` oracles, median): `TryValidate` 50k 111.3 → 47.1 ms, 9.6 → 6.1 MB (×2.36); `BuildNaturalKey` ×50k 54.9 → 49.0 ms, 38 → 7.8 MB; `Compare` ×500k 427 → 255 ms, 763 MB → 0; `Array.Sort` 50k 655 → 412 ms, 1.3 GB → 390 KB. Confirms the commit numbers; no action.

**Q-R26 follow-up (same day, branch `perf/q-r26-gc-investigation`).** perf-session now writes `gc-events.csv` per iteration (every GC: time, generation, reason, EE pause; `GcEventRecorder`) and `pageFaults=` per step (`ProcessPageFaults`). Two more interleaved rounds (master / variant, S2 + S4, repeat 2), with GCs matched to navigations (`RenderedFrame` window):
- **GC ruled out.** During the key phase master has only 4–8 small gen0 GCs (6–27 ms pause in total); navigations that overlap no GC are just as slow (e.g. 9.2 ms median without vs 10.5 with). The gen2 `InducedNotForced` GCs (WPF bitmap memory pressure) happen during the fill. The variant, which keeps decoding in its window, has more GCs in the key phase (62–63 gen2) and is still faster.
- **Page-fault count ruled out as the simple explanation:** master ~127 k per 100 navigations, variant 196–250 k.
- **Noisy machine:** other sessions used ~3.6 of 12 logical cores; master's warm P50 drifted 4.1 → 9.3 → 14.1 ms across rounds while the variant stayed at 3–6 ms. Not decisive.
- ~~**Next:** repeat on an idle machine; test "cold bitmap memory" directly (whole-folder mode on a small folder vs F4; ETW hard/soft faults and memory-compression activity during navigation).~~ → round 2 below.

**Q-R26 round 2 — cause found and fixed (2026-09-26, idle PC, F4 now 1895 files / 15.9 GB).** Interleaved master `3d57e66` (M) / variant (V) / M / V, S2 + S4, repeat 2: M S2 first P50/P95 6.4/16.0 · 4.5/10.0 ms, V 1.8/5.0 · 1.8/4.2 — reproduces on an idle machine.
- **Not memory:** system counters sampled every second during the key phase: no hard faults (Page Reads ≈ 0), Memory Compression working set flat (~830 MB), transition faults no higher in M; both are dominated by demand-zero faults. "Cold bitmap memory" rejected.
- **Cause:** first-visual ends at the first `Rendering` tick after the assign, so it includes the synchronous `preloadKick` that runs right after it on the UI thread. With the whole folder cached, the scheduler pass queued nothing, so it never reached its yield and built a cache key + looked up **every image in the folder** on the UI thread: `preloadKick` P50/P95 M 2.0–2.8 / 4.6–8.3 ms vs V 0.25–0.37 / 0.7–0.8 ms — most of the gap.
- **Fix** (`perf/q-r26-preload-kick-off-ui`): the loop force-yields to the thread pool at the first candidate outside the 32/8 window while still on the caller's thread (test `PreloadKickOffCallerTests`). Re-measured F (fix) / V / F / V: `preloadKick` P50 0.13–0.26 ms; S2 first P50/P95 F 2.0/4.9 · 2.1/5.1 vs V 1.9/3.6 · 2.1/5.3; S4 F 2.4/5.7 · 2.8/9.6 vs V 2.3/6.6 · 2.3/4.1. Whole folder still preloaded (peak working set 10.8 GB). Q-R26 closed.

**Kinetic pan stutter (2026-09-26, branch `perf/kinetic-pan-stutter`, no code fix).** In-process harness `KineticPanFrameMeasurementTests` (Manual; drives `MainWindow.PointerInput`, no OS input; `PHOTOREVIEW_KINETIC_IMAGE`=F4 photo 3809x5712 at 100 %, primary 240 Hz panel, DPI 1.0; second monitor Dell 59.94 Hz) plus a control scene (same `KineticScroller`, a 200 px rectangle, no ScrollViewer/photo). PC never below ~20-50 % CPU (other agents); numbers repeated 6x.
- **Not the app:** UI cost per frame (Rendering + layout + render walk, dispatcher hooks) mean 0.4-0.8 ms, p95 < 1.7 ms; no GC during glides; duplicate Rendering ticks (same RenderingTime) ~0 during the glide and already skipped; steps follow RenderingTime correctly.
- **Frame pacing is the limit:** glide 95-150 fps on the 240 Hz panel, 30-45 % of frames arrive after a missed vsync (gaps 8-17 ms), judder 50-75 frames/s. The **control scene is the same** (94-153 fps, judder 52-74/s). The gaps are phase-locked to the 59.94 Hz monitor (phase-lock R 0.31-0.76 at 16.68 ms vs <= 0.22 at 16.2/17.2 ms): DWM/WPF pacing with a mixed 240/60 Hz, hybrid-GPU setup. On the Dell itself frames come at 4-17 ms intervals on WPF's 240 Hz clock.
- **Tried, no effect** (interleaved A/B, same harness): 1 ms timer resolution, process AboveNormal + UI thread Highest, keeping the UI thread busy, extra render ticks.
- **Drag:** a 125 Hz mouse on a 240 Hz display leaves ~40 % of frames with no movement (inherent to input rate).
- **Options (not implemented):** render the viewer through a flip-model D3D11/DirectComposition swap chain (large, risky); frame-synchronised drag with pointer interpolation (+1 input interval latency); user-side check: disconnect/disable the 60 Hz monitor or set both to the same rate and compare.
