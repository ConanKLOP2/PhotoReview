# Baseline AR02e (production graph, 2026-09-23; F4 = 1625 files, 13.2 GB)

Window 1920x1080, Preview, warm, cache 16 GiB, 8 preload workers, WicDirect, disk cache on. "After" = decode width restored (#31).

| Scenario | Metric | Before | After |
|---|---|---:|---:|
| S1 open folder | first / final visual P50 | 366 / 488 ms | 323 / 421 ms |
| S2 next 1.5 s x100 | key->present P50 / P95 | 2.0-2.5 / 4.8-5.2 ms | 3.6-4.0 / 5.4-7.2 ms |
| S3 burst 30/s x200 | incomplete navigations | 309 / 402 | 44 / 402 |
| S4 jump | P50 / P95 | 1.8 / 4.9 ms | 3.4 / 6.6 ms |

AR04 (UI-thread affinity) perf gate passed on interleaved runs (mean P95 difference <= 1 ms, `CrossThreadPresentCount = 0`).
