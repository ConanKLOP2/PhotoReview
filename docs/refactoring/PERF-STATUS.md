# Performance Diagnosis Status (D Series)

**Last updated:** 2026-09-22  
**Overall status:** Most D00-D07/D10-D12 complete; D01/D02 blocked on D06 data; D08/D09 pending

## Current Status

| ID | Task | Status | Blocker |
|---|---|---|---|
| D00 | Setup, fixtures, tools | ✅ DONE | — |
| D03-D07, D10-D12 | Instrumentation, scenarios, analysis | ✅ DONE | — |
| D01, D02 | AppLog/File-access analysis | 🔄 BLOCKED | D06 (Procmon data) |
| D08, D09 | ETW, GC profiler deep-dive | 🔄 TODO | D07 complete, tools pending |

## Full Task History

Detailed task tracking archived at: [`docs/archive/historical/PERF-DIAGNOSIS-TASKS.md`](../archive/historical/PERF-DIAGNOSIS-TASKS.md) and [`PERF-DIAGNOSIS-PLAN.md`](../archive/historical/PERF-DIAGNOSIS-PLAN.md)

## Key Conclusions

- D06 Procmon scenario driver established baseline (_perf-session_ mode)
- EventSource instrumentation and measurement points in place (D03-D04)
- Scenario matrix (D07) run; analysis pending profiler data (D08/D09)

Next: D01/D02 completion requires Procmon session; D08/D09 require ETW trace analysis.

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

Same fixture/config as AR02e. A first batch (master e1375cc vs AR04, 3 runs/cell) looked like a regression (S2 P95 7.2 -> 11.1 ms) but master itself was degraded in that batch (S2 hit rate 37.7 %, preload did not start in S1 — consistent with the off-UI-thread catalog race ADR 0005 removes), so machine state was not comparable. Gate decided on **interleaved** runs (master, AR04, master, AR04; S2 + S4, warm):

| Round | master S2 P50/P95 | AR04 S2 P50/P95 | master S4 P50/P95 | AR04 S4 P50/P95 |
|---|---:|---:|---:|---:|
| 1 | 6.5 / 12.4 | 7.9 / 12.4 | 5.6 / 11.0 | 4.5 / 11.9 |
| 2 | 4.8 / 6.9 | 4.7 / 7.7 | 4.5 / 9.3 | 4.3 / 6.3 |
| mean P95 | 9.7 | 10.1 (+0.4) | 10.2 | 9.1 (-1.1) |

- **Passed** (<= 1 ms): same-build run-to-run P95 noise is ~5 ms, larger than the difference. Hit rate 99.0 % / 98.4 % both; peak WS equal; `CrossThreadPresentCount = 0` in every AR04 run. All values < 1 frame (16.7 ms).
- AR04 was stable in all 20 runs; master lost preload in the first batch (all 3 S1 runs, 2 of 3 S2 runs), not in the interleaved rounds.
