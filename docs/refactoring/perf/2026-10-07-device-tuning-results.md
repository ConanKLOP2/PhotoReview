# Device-tuning bench results for this PC (overnight 2026-10-06/07)

Runs of `tools/diag/tune-matrix.ps1` ([plan](PLAN-device-config-bench.md), [S0 pilot](2026-10-06-s0-noise-floor.md)). Result: **no challenger beat the default in any stage; the default config is kept.**
No app setting was changed. Run dirs are under `work\diag\tune-runs\` (untracked, local to this PC).

## Methodology

- Fixtures: F4 (`C:\Xiuren\[[WALLPAPER]`, 2743 files, 28.5 GB, window mode only), F-mid (987 files, 10.7 GB, hard-link subset `C:\Xiuren\_tune\mid`; S4 only, fits RAM).
- Scenarios: S2 (slow Next), S3 (burst Next), S4 (jump). Metric: final-visual latency. Ranking (S1+): P50 and P95 of the pooled per-navigation latencies of all runs of a config,
  bootstrap 95 % CI (2000 resamples), ratio vs `default`, geometric mean over the three scenarios; run-P95 is only a guard (> 1.5x baseline disqualifies). S2 stage used run-P95 median (older ranking).
- Repeat 3 per config, randomised round-robin order, 20 s cooldown, one warm-up. Hard constraints: no failed run, GC <= 10 %, min available RAM >= 4 GB, no "preload paused (memory)".
- Winner rule: geo P95 ratio at least 10 % better **and** beyond 2x the noise floor, valid. Noise floor used: 27 % (S2 run-P95 CV from S0).
- Build: S0/S2 CLI 2.0.342 (`0c3e5ff0`); S1/S4/S5/S8 CLI 2.0.350 (`57c39b4c`). AC on, power plan **Turbo** (not High performance), free RAM about 22 GB (< 24 GB target), CPU idle 93-96 %.

## S0 noise floor (A/A, 12 identical default runs, F4)

| Scenario | P95 median (CI) | CV of run P95 | CV of run P50 |
|---|---|---|---|
| S2 | 3.73 ms (2.82-4.47) | 27 % | 13 % |
| S3 | 2.10 ms (2.07-2.18) | 5 % | 2 % |
| S4 | 2.99 ms (2.80-3.23) | 12 % | 3 % |

No violations (GC max 6.8 %, peak WS 14.3 GB, 0 paused events). S2 P95 differences below about 27 % cannot be told from noise at 3 repeats (a win needs about 54 %).

## Per-stage results (geo P95 ratio vs default, < 1 is faster)

| Stage | What | Config: ratio | Valid | Decision |
|---|---|---|---|---|
| S2 | PreloadWorkerCount, baseline w8, F4, 45 runs | w12 0.923, w4 0.961, w2 0.971, w16 1.031 | all | Keep 8 workers |
| S1 | decoder x mode, F4, 27 runs | wic-fast 0.886, tj-preview 1.188 (S2 1.46) | all | Keep WicDirect/Preview |
| S4 | ImageCacheRamPercent 25/35/65/75 vs 50, F-mid, 45 runs | r75 1.021, r35 1.083, r25 1.138, r65 1.206 | all | Keep 50 % |
| S5 | caches, F4, 27 runs | pd-0 0.952, sb-8g 0.867 | sb-8g **invalid** (min avail RAM 3.1 GB, 1 memory pause, peak WS 18.95 GB) | Keep defaults (preview disk cache 4 GB, source-bytes cache off) |
| S3 | preload window fwd/bwd, F4, 72 runs | f64-b16 0.817, f64-b4 0.836, f16-b4 0.845, f8-b0 0.858, f8-b16 0.884, f128-b0 0.961, f128-b16 0.963 | all | Keep 32/8 (the gain is S2/S4 noise, see below) |
| S9 | confirmation default vs `DOTNET_TieredPGO=0`, F4, 24 runs | pgo-0 geo P95 0.866, geo P50 0.898 | both | Candidate, not adopted (see below) |
| S8 | runtime env, F4, 45 runs | pgo-0 0.799, gen0-256m 0.861, gen0-64m 0.915, gcserver-1 1.079 | all | Keep runtime defaults |

Details: S2 per-scenario CIs overlap for every worker count (S2 scenario alone: w12 0.861, w16 1.212). S1 wic-fast: S2 0.677, S3 0.981, S4 1.047, P50 about 1.00, S2 CI 2.5-187 ms; no violations.
S4: no violations (peak WS 5.64 GB, far from the RAM limit even at 75 %). S5 pd-0: P50 ratios 0.97-1.01. S8 pgo-0: S2 0.736, S3 0.888, S4 0.779, P50 0.89/0.99/0.83; only S3 beats its own CV (5 %)
and the CIs touch the default; no violations in any S8 config (GC med <= 2.8 %, peak WS <= 14.41 GB).

S3 detail: the geo P95 ratios come from the short S2/S4 scenarios (123/186 pooled samples, P95 CIs reach 125-198 ms); in the S3 burst scenario (603 samples, the only one with tight CIs) NO smaller window is faster (P50 ratios 0.99-1.01 for 8/0, 8/16, 16/4) and the 128-image windows are 6-9 % SLOWER (P50 1.09, P95 1.06-1.08). A/A noise of this batch: P50 2.8 %, P95 21.5 % (S2 42.9 %). Conclusion: keep 32/8.
S9 detail (12 runs per config, A/A noise P50 2.3 %, P95 15.0 %): pgo-0 vs default: S2 P50 1.50 vs 1.79 ms (ratio 0.837, 95 % CIs 1.45-1.54 vs 1.71-1.83 do not overlap), S2 P95 0.718 (CIs overlap), S3 P50 0.987 / P95 0.950, no violations, peak WS 14.29 GB, min avail RAM 7.7 GB. S8 and S9 are two independent batches and both rank pgo-0 first, so a small real gain (about 10 % on the early/slow-navigation latency, none on the burst scenario) is plausible; it is still below the 27 % bar derived from the noisiest scenario (S2 P95).

## Adopted config for this PC

`{"decoder":null,"mode":null,"set":{},"env":{}}` = **all defaults** (WicDirect/Preview, PreloadWorkerCount 8, ImageCacheRamPercent 50, source-bytes cache off, preview disk cache 4 GB, stock .NET GC/PGO).

- Confidence: medium-low. It means "no tested alternative is measurably better than the default", not "the default is optimal"; with 27 % S2 noise and 3 repeats only very large gains were detectable.
- Caveats: Turbo power plan and about 22 GB free RAM throughout; F4 only exercises window mode; F-mid only S4; frame P95 not measured (and min available RAM n/a in S2); S0 noise is from CLI 2.0.342, later stages from 2.0.350.
- Candidate: `DOTNET_TieredPGO=0` (S8 geo 0.799, S9 geo 0.866 / P50 0.898; two batches agree; S2 P50 gain 16 % with disjoint CIs). It is a runtime switch, not an app setting: it would be applied through `System.Runtime.TieredPGO: false` in the app's runtimeconfig. Not adopted tonight (below the pre-registered 2x-noise bar on P95); one more confirmation on the High performance power plan, then it is a low-risk one-line change.

## Stages that did not run

- S3 and S9 were first skipped by the stage runner (C: 11.07 GB free < 12 GB; free RAM 21.1 GB < 22 GB; both thresholds were arbitrary and were lowered to 10 GB / 20 GB) and then run by hand at 04:39-05:40 (S3 72 runs, S9 24 runs, results above). The superseded pilot dir `tune-20261006-195359` was found deleted afterwards; the stage runner's delete was not retried after the classifier denial.
- S6 (memory pressure): no result reported, treated as not run.
- Earlier first attempts at S1/S3/S4/S5/S8/S9 failed only because the stage files were missing before PR #358 merged; S1/S4/S5/S8 were re-run afterwards.

## What the user must verify

1. Set the High performance power plan and close other apps (aim for >= 24 GB free RAM); re-run S2 with the `gate` profile and Repeat >= 8 if a decision on worker count or PGO is wanted.
2. Re-test `DOTNET_TieredPGO=0` once more on the High performance plan (Repeat >= 8, gate profile); if it holds, add `System.Runtime.TieredPGO: false` to the app's runtimeconfig (decision for the user).
3. Choose the option in [PLAN-device-config-bench.md](PLAN-device-config-bench.md); recommendation after tonight: (D) keep the defaults for this PC (no measurable winner for decoder, mode, workers, window, RAM share or caches), plus the optional PGO switch.
4. C: is 98 % full (about 11-12 GB free): the bench writes 1-2 GB per stage into `work/diag/tune-runs`; clean old run dirs by hand when needed.
