# TieredPGO=0 confirmation (2026-10-07)

Question: does `DOTNET_TieredPGO=0` (candidate from S8/S9 of the device bench, [results](2026-10-07-device-tuning-results.md)) hold up in a larger A/B?
Setup: `tune-matrix.ps1`, default vs pgo-0 (both WicDirect/Preview), F4 (2743 files, 28.5 GB), scenarios s2-next-slow-quick, s3-next-burst, s4-jump, Repeat 8 (24 runs per config, 0 failed),
build 2.0.350+57c39b4c, AC power, plan Turbo (not High performance), free RAM 20 GB, CPU idle 71 % at start (the user was working on the PC).

A/A noise, estimated from the 8 default repeats (halves of the pooled samples): P50 5.5 %, P95 19.7 % (S2 P95 37.7 %, S3 11.3 %, S4 10.2 %).

| Scenario | P50 default / pgo-0 (ms) | P50 ratio | P95 default / pgo-0 (ms) | P95 ratio |
|---|---|---|---|---|
| S2 (slow Next, 328 samples) | 2.19 / 1.98 | 0.904 | 8.32 / 10.35 | 1.244 |
| S3 (burst, 1608 samples) | 1.39 / 1.33 | 0.952 | 2.50 / 2.23 | 0.894 |
| S4 (jump, 496 samples) | 1.60 / 1.42 | 0.888 | 2.95 / 3.35 | 1.133 |
| Geometric mean over S2-S4 | | **0.914** | | **1.080** |

No hard-constraint violation (GC median 1.6 %, peak WS 14.83 GB, min available RAM 5.2 GB, 0 failed runs).

Reading: pgo-0 is consistently a little faster at the median (P50 -5 to -11 % in all three scenarios, CIs mostly disjoint), but the geometric P50 gain (8.6 %) is below the pre-registered 10 % bar,
and its P95 is WORSE in two of three scenarios (geo 1.080, inside the 19.7 % noise). Earlier S8/S9 batches had pgo-0 better on P95; the larger batch does not reproduce that, so the earlier P95 gain was noise.
Conclusion: **not adopted**. A median gain of about 5-10 % at no tail benefit does not justify a runtimeconfig switch; revisit only with a High-performance-plan re-run and a quiet PC if the median ever matters.
