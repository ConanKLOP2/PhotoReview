# Performance status and baselines

**Updated:** 2026-09-27. Older detail (D-series tasks, AR04 interleaved gate tables, per-PR perf-night notes, benchmark speed-up step tables,
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
