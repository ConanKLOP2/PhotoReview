# Performance status and baselines

**Updated:** 2026-10-01. Older detail (D-series tasks, AR04 interleaved gate tables, per-PR perf-night notes, benchmark speed-up step tables,
kinetic-pan harness tables): `git show 1de561c:docs/refactoring/PERF-STATUS.md`.
Fixture F4 = a real folder of portrait 20-30 MP JPEGs (1841-2058 files, 13-19 GB, grows over time); paths are machine-specific (`CLAUDE.local.md`).
Harness: `tools/diag/run-matrix.ps1 -Profile quick|gate|full` (`--perf-session` on the production graph via `AppHost`). Compare only within
the same folder state, same build family and interleaved runs (same-build P95 noise is ~5 ms).

**Do not compare numbers before the AR02c boundary:** the D-series / T66 numbers came from the legacy `--perf-session` graph (no preload, 64 MiB cache).

**Recording a new measurement (avoid merge conflicts across parallel PRs):** put ALL detail (methodology, tables, numbers,
reasoning) in a new file under [`perf/`](perf/), named `<date>-<topic>.md` (ISO date so filenames sort chronologically) --
never write the actual measurement content here. Add exactly ONE new bullet to the list below, at the end. Two PRs
measuring different things on the same day may still collide on that one new bullet, but resolving a one-line insert is
trivial -- it's the growing multi-paragraph sections that caused repeated multi-round merge conflicts on 2026-09-27.

See [`perf/`](perf/) for every measurement pass, oldest first by filename:

- [2026-09-23 -- Baseline AR02e](perf/2026-09-23-baseline-ar02e.md)
- [2026-09-24 -- Perf night (#39-#48)](perf/2026-09-24-perf-night.md)
- [2026-09-26 -- AR16 readability probe](perf/2026-09-26-ar16-readability-probe.md)
- [2026-09-26 -- Whole-folder preload (Q-R17)](perf/2026-09-26-whole-folder-preload-qr17.md)
- [2026-09-26 -- Benchmark harness speed-up](perf/2026-09-26-benchmark-harness-speedup.md)
- [2026-09-26 -- Kinetic pan / glide](perf/2026-09-26-kinetic-pan-glide.md)
- [2026-09-27 -- Image crossfade (PR-D)](perf/2026-09-27-image-crossfade.md)
- [2026-09-27 -- R01/R02/R03/R13 metadata I/O](perf/2026-09-27-r01-r02-r03-r13-metadata-io.md)
- [2026-09-27 -- R04 preload contention / R14 startup sweep](perf/2026-09-27-r04-preload-contention-r14-startup-sweep.md)
- [2026-09-27 -- R06/R07/R11 benchmark/cache misc](perf/2026-09-27-r06-r07-r11-benchmark-cache-misc.md)
- [2026-09-27 -- Q-R29 slow-link simulation (option B)](perf/2026-09-27-qr29-slow-link-sim.md)
- [2026-09-27 -- R15/R16/R17/R18 hash/catalog/drag-drop/diagnostics costs](perf/2026-09-27-r15-r18.md)
- [2026-09-27 -- Q-R29 option C: navigation stat off the UI thread](perf/2026-09-27-qr29-option-c.md)
- [2026-09-28 -- Q-R29 option C-2: ISourceReader seam, preload/viewer bandwidth contention](perf/2026-09-28-qr29-c2-preload-throttle.md)
- [2026-09-28 -- IMG-07 single JPEG header marker walk; title-bar/EXIF-line rebuild cost check (no change)](perf/2026-09-28-img07-single-header-walk.md)
- [2026-09-29 -- RAW-22 cache/preload/RAM estimate](perf/2026-09-29-raw-22-cache-preload.md)
- [2026-09-29 -- RAW-60 decoder benchmark](perf/2026-09-29-raw-60-decoder-bench.md)
- [2026-09-29 -- RAW-31 LibRaw `half_size` evaluation](perf/2026-09-29-raw-31-half-size-evaluation.md)
- [2026-10-06 -- PLAN: bench many configurations for this device (not yet run)](perf/PLAN-device-config-bench.md)
- [2026-10-06 -- S0 noise floor of the device-tuning bench (F4 grew to 28.5 GB; run P95 too noisy at 3 repeats)](perf/2026-10-06-s0-noise-floor.md)
- [2026-10-07 -- Device-tuning bench results S0-S9 plus completion round (S4/S5/S6): no winner, defaults kept](perf/2026-10-07-device-tuning-results.md)
- [2026-10-07 -- TieredPGO=0 confirmation (48 runs): P50 -8.6 %, P95 +8 %, not adopted](perf/2026-10-07-tieredpgo-confirmation.md)
- [2026-10-10 -- Mở ảnh từ Explorer: ảnh đầu, khung trắng lúc khởi động, so sánh v2.0.200-380 (không hồi quy ở v240)](perf/2026-10-10-startup-first-image.md)
- [2026-10-10 -- P-1 "WPF nhanh": ảnh đầu từ Explorer R2R 1127 -> 782 ms, build 1321 -> 915 ms (chưa đạt NW-5 <= 600 ms); thí nghiệm self-contained + composite R2R -140..-160 ms](perf/2026-10-10-p1-startup-in-wpf.md)
- [2026-10-10 -- WP-05 TurboJpeg ra PixelBuffer (fine-scale vs WIC Fant)](perf/2026-10-10-wp05-native-pixelbuffer.md)
