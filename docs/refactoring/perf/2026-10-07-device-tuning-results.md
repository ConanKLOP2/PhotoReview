# Device tuning bench: overnight results (2026-10-07, snapshot 02:05)

Results of the unattended run of [the plan](PLAN-device-config-bench.md) with `tools/diag/tune-matrix.ps1` + `tools/diag/tune-rank.ps1`.
Earlier pilot: [S0 noise floor](2026-10-06-s0-noise-floor.md). **Snapshot:** only S0 (A/A) and S2 (workers) had finished when this page was written; the
rest of the plan had not started. Rerun this page's tables when later stages finish. Nothing here changes a default.

## Methodology

- Device: i7-9750H 6C/12T laptop, 31.9 GB RAM, one NVMe (see the plan, section 0). AC on, power plan **Turbo** (not High performance), free RAM 22 GB, CPU idle 91-94 %.
- Build: CLI 2.0.342+`0c3e5ff0`. Fixture F4 = 2743 files, 28.5 GB (fingerprint unchanged during both batches), so every run is window mode (peak WS about 14.3-14.4 GB).
- Scenarios: S2 (slow Next, quick profile, 40 keys), S3 (burst, 200 keys), S4 (jump). Metric: final-visual P95 per run, median over runs, bootstrap 95 % CI.
  Objective: geometric mean of the ratio vs baseline over S2, S3, S4. Cooldown 20 s, one warm-up, no cold disk cache.
- Winner rule: >= 10 % better than the baseline AND better than 2x the noise, no hard-constraint violation (0 failed runs, GC <= 10 %, peak WS <= 20 GB, no
  "preload paused (memory)" event). Otherwise keep the default.
- Not evaluated by the harness: minimum available RAM (n/a), frame P95 (n/a), visual-output identity vs baseline.

## Noise floor (S0 A/A: 12 identical default runs per scenario, run dir `work\diag\tune-runs\tune-20261007-005525`)

| Scenario | P95 median (CI) ms | CV of run P95 | CV of run P50 |
|---|---|---|---|
| S2 | 3.73 (2.82-4.47) | 27 % | 13 % |
| S3 | 2.10 (2.07-2.18) | 5 % | 2 % |
| S4 | 2.99 (2.80-3.23) | 12 % | 3 % |

All ratios 1.000, no violations (GC max 6.8 %, peak WS 14.3 GB, 0 paused events). The recorded `noisePct` is 27 (S2 P95, conservative). On S2-like metrics a challenger
would need > 54 % to win; S3 and S4 have a much tighter floor, so per-scenario CV is the realistic yardstick.

## S2: preload worker count (baseline w8 = default, Repeat 3, run dir `work\diag\tune-runs\tune-20261007-012600`)

| Config | Geo ratio vs w8 | S2 P95 ms (ratio) | S3 P95 ms (ratio) | S4 P95 ms (ratio) | Peak WS GB | GC % med |
|---|---|---|---|---|---|---|
| w12 | 0.923 | 2.49 (0.861) | 2.11 (0.976) | 2.75 (0.937) | 14.44 | 1.5 |
| w4 | 0.961 | 3.06 (1.056) | 1.97 (0.912) | 2.70 (0.921) | 14.28 | 1.2 |
| w2 | 0.971 | 2.69 (0.928) | 2.12 (0.981) | 2.94 (1.004) | 14.29 | 0.8 |
| w8 (default) | 1.000 | 2.89 | 2.16 | 2.93 | 14.30 | 1.5 |
| w16 | 1.031 | 3.51 (1.212) | 2.15 (0.995) | 2.66 (0.909) | 14.30 | 1.7 |

Decision: **no winner, keep `PreloadWorkerCount = 8`.** Every difference is under 10 % overall and inside the noise; per-scenario CIs overlap, and the ordering is not
monotonic (w16 is worst on S2 yet best on S4). No violations. Conclusion: worker count between 2 and 16 does not measurably change review latency on this PC.

## Adopted config for this PC

**Default unchanged** (cumulative adopt = none). Confidence: medium-high for "workers do not matter", because three repeats per config is thin and S2 P95 is noisy;
no evidence supports changing it. Caveats: Turbo power plan, 22 GB free RAM (below the 24 GB target), one commit, one fixture.

## Stages not run (as of this snapshot)

S1 (decoder x mode), S3 (preload window), S4 (RAM share, small/mid fixtures), S5 (caches), S6 (memory pressure), S7 (crossed finalists), S8 (runtime flags),
S9 (A/B/A/B confirmation). Reasons: the run only reached S2 within the first hours (PR wave and fixers occupied the machine until about 00:40; each batch needs
a quiet PC); S6 additionally needs a balloon tool and >= 30 GB free RAM, which this PC (22 GB free) does not have, so it is skipped by rule.
If a later batch finishes, its results belong in a follow-up page, not here.

## What the user must verify

- Switch the power plan to High performance and close browsers, then rerun S0/S2 if a decision is wanted; the current numbers are valid only for Turbo.
- Visual F11 check on the portrait monitor with an always-visible taskbar (PR #351, not measurable by the bench).
- Decide the bench plan (A/B/C/D) in the OPEN-DECISIONS flow after S9 exists; until then defaults stay.
