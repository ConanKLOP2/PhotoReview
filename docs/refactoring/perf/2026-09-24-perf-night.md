# Perf night 2026-09-24 (PRs #39-#48; F4 = 1841 files, 14 GB)

| Metric | baseline | after #40-#48 |
|---|---:|---:|
| Open folder, first visual (median) | 489 ms | **166 ms** |
| Burst 30 keys/s: images shown /200 | 50-60 | **197-201** |
| Burst key->present P95 | 10.5 ms | **2.8-3.5 ms** |
| Jump P95 / max | 7.6-8.4 / 474-489 ms | **3.8-4.0 / 164-165 ms** |
| Peak WS open / slow-next / burst | 1.6 / 4.2 / 5.2 GB | **0.33 / 1.1-1.3 / 1.7 GB** |
| App start -> first image from Explorer (#46) | 3169 ms | **1817 ms** |

What did it: ICC via WIC color transform, Bgr32/Pbgra32, preview races the thumbnail, embedded EXIF thumbnails, JPEG single-file disk cache, direction-aware
burst preload with viewer-priority decode, decode to the viewport box (#43). Not shipped: SIMD-only TurboJpeg scale factors (slower), non-default GC mode
(AR12c, reason in `architecture.md`); TurboJpeg stays experimental and slower than WicDirect (Q-AR8 a).
