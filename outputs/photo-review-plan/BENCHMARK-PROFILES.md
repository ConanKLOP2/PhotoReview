# Benchmark profiles

The in-app benchmark compares the configured loading and scheduling strategies against the same local image folder. `Original Correctness` is deliberately excluded from speed ranking: it verifies full quality and cache isolation only.

## How to read results

Each run records a stable `runId`, UTC start time, full folder path, profile, workload, iteration, elapsed milliseconds, correctness, and final P50/P95/P99/max. P95 is the primary responsiveness measure because it exposes occasional stalls. A `FAIL` correctness result always outranks a fast result as a failure and must be investigated first.

Profiles are grouped by workload: first frame/navigation, preview quality, preload/cache, storage/RAM, file-action races, and diagnostics. Run cold and warm phases separately; a warm result is not evidence that a cold folder open is fast. Action profiles operate on temporary copies, never the selected source folder, and do not retry a failed filesystem operation.

## Debug workflow

1. Run one profile at a time to establish a reproducible baseline.
2. Compare P95 and the `Benchmark sample` log lines for the first outlier.
3. Check `Benchmark phase failed`, cache epoch/generation, queue wait, and file-operation timestamps in `app.log`.
4. Repeat with `Logging Off` to estimate logging overhead.
5. Use `Original Correctness` after any cache or decoder change.

Reports are JSON and should include the machine and folder metadata. Do not publish reports containing private image paths without removing the folder field.

