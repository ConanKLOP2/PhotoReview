# Benchmark harness speed-up (#109, 2026-09-26)

`run-matrix.ps1`: one warm-up per (fixture, mode, condition) per batch (`-WarmupEveryCell` restores the old behaviour); `-Profile quick` uses `s2-next-slow-quick` (40 keys);
`gate`/`full` unchanged. F4: gate 393 -> 260 s (-34 %), quick 209 -> 158 s (-24 %). In-app `BenchmarkWindow`: "Quick check" button (fast-sequential, 10 iterations, 64 images) ~3.1x faster than the default run.
