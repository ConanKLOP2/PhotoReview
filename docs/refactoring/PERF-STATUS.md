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

