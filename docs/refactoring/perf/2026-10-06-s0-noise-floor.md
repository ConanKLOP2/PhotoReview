# S0 noise floor of the device-tuning bench (2026-10-06)

First run of `tools/diag/tune-matrix.ps1` ([plan](PLAN-device-config-bench.md)): stage `s0-noise` (the default config x6, identical), profile `quick`
(S2-quick 40x Next + S3 burst 200 keys), fixture F4, WicDirect/Preview, CLI 2.0.327-dev (`ea40b028`), one warm-up, 30 s cooldown. Not a tuning result.

**Environment (not ideal, recorded as found):** AC on, power plan **Turbo** (not High performance), free RAM 17.9 GB (a browser and other apps open), CPU idle ~80 %.
No agent was building during the batch.

**F4 has grown:** 2743 files, 28.5 GB (docs of 2026-09 say 1625 files / 13.2 GB). It no longer fits RAM, so all runs are window mode (peak WS 14-15 GB).
Numbers are not comparable with earlier baselines.

| Scenario | final-visual P50 (6 runs) | final-visual P95 (6 runs) | non-RAM-hit % |
|---|---|---|---|
| S2-quick | 1.85-3.00 ms (median 2.1) | 4.05-12.80 ms (median 5.57, bootstrap CI 4.44-10.36) | 2.44 (identical every run) |
| S3 burst | 1.38-1.86 ms (median 1.48) | 2.44-4.21 ms (median 3.09, CI 2.45-3.92) | 0.50 (identical every run) |

Findings:
- P95 of one run is **very noisy at the default config** (S2: max/min = 3.2x, S3: 1.7x), P50 is far steadier (1.6x / 1.35x). The planned rule "winner must beat noise x2 and 10 %" cannot be met at 3 repeats using run P95.
  Quick S2 has only 40 keys, so its P95 is the 2nd-worst sample. Adjustments for S1-S9: use the `gate` profile (S2 = 100 keys), repeat >= 8 for decisions, rank on
  P50 and on the P95 of **pooled per-key latencies** (not the median of run P95s), and keep run P95 only as a guard.
- Two of 12 runs broke the GC <= 10 % constraint at the default config (S3 run 5: 17.4 %, run 6: 10.9 % with 4 "preload paused (memory)" events); peak WS was the same 14.8 GB as the clean runs,
  so the cause is memory pressure from other processes at that moment, not the config. Memory-sensitive stages (S4-S6) need a quiet PC (close browsers) and `minAvailRam` sampling.
- The cache-hit shares are deterministic, so they are a safe tie-breaker metric.
- Harness gaps seen: `minAvailRam` and `frameP95` are n/a, the report shows "valid NO" for the default because of the memory-pause run.
