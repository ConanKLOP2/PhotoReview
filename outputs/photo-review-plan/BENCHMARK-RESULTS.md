# Benchmark results

This file is reserved for measured runs on the local machine. Raw reports are kept beside the application data under the benchmark result directory and are not checked into source control by default.

Required evidence for a release run:

| Field | Required |
|---|---|
| Run ID and UTC time | Yes |
| Profile and workload | Yes |
| Folder file count and total bytes | Yes |
| Iterations and warmup count | Yes |
| P50/P95/P99/max | Yes |
| Cache hits/misses and bytes read | Yes when available |
| Working set and memory pressure | Yes when available |
| Correctness and error stage | Yes |
| Action navigation-before-operation evidence | Action profiles |

Results must state whether the folder was cold or warm and whether the profile ran read-only or against temporary action copies.

## Measured local run — 2026-09-15

Source folder (read-only): `C:\Xiuren\[[DONE]\Machi馬吉 cosplay Sparkle (Hanabi) - HonkaiStar Rail`.

| Profile / probe | Workload measured | Samples | P50 | P95 | Max | Correctness | Notes |
|---|---|---:|---:|---:|---:|---|---|
| Instant Review | CLI real-image read/decode probe | 30 | 26.4 ms | 34.8 ms | 37.1 ms | PASS | First-frame oriented |
| Fast Sequential | CLI real-image read/decode probe | 30 | 11.4 ms | 16.9 ms | 17.3 ms | PASS | Best measured sequential CLI profile |
| Random Navigation | CLI real-image read/decode probe | 30 | 14.1 ms | 20.8 ms | 28.2 ms | PASS | Random sample pattern |
| No Preload Baseline | CLI real-image read/decode probe | 30 | 12.3 ms | 18.7 ms | 19.4 ms | PASS | Baseline |
| Full Folder Warm | CLI real-image read/decode probe | 30 | 10.2 ms | 15.0 ms | 15.5 ms | PASS | Warm/cache-oriented |
| Huge Image Safe | CLI real-image read/decode probe | 30 | 9.7 ms | 14.3 ms | 17.0 ms | PASS | Lower concurrency |
| UI warm Next | WPF navigation probe | 30 | 12 ms | 21 ms | 21 ms | PASS | 30/30; no skip/double navigation; 30/98 |

Folder metadata: 98 images, `773,065,292` bytes (~737.4 MiB), largest file `9,884,323` bytes. The CLI profile values above are the first real-image baseline and should not be treated as a complete ranking for profiles whose workload was not run. `Original Correctness` is intentionally absent from this speed table.

Earlier decoder preload matrix on the same real-image set:

| Workers | Preload total | Decode median | Decode P95 | Cache | Working set |
|---:|---:|---:|---:|---:|---:|
| 2 | 27.9 s | 1,836 ms | 2,110 ms | 673 MB | 720 MB |
| 4 | 18.9 s | 2,372 ms | 2,844 ms | 673 MB | — |
| 8 | 15.3 s | — | — | 673 MB | 723 MB |

Interpretation: increasing workers reduced total preload time in this run, while per-image decode latency was not monotonic. Warm UI navigation is already fast; remaining work should focus on first-frame/cold decode and ensuring every profile uses its declared preload and worker settings.
