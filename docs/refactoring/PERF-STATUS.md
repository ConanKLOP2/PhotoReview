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
