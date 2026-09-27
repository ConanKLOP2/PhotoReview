# Full-code-review-2026-09-27 misc findings (R06/R07/R11, perf/benchmark-cache-misc)

**R06 (`PreviewImageService` queued-persist memory, no code change):** worst-case retained bitmaps outside the RAM LRU while queued for disk persistence =
`PersistQueueCapacity` (16, drop-when-full) + `PersistWorkerCount` (2 in-flight writes) = 18. Each queued bitmap is at most the app's own "largest sane preview" box
(`RamBudgetPolicy.MinimumBudgetBoxWidth` x `MinimumBudgetBoxHeight` = 3840x2160, 4 bytes/pixel = ~31.64 MiB) since only downscaled previews are ever queued for
persistence (originals are never disk-cached). 18 x 31.64 MiB ~= 569.5 MiB (~0.56 GiB) -- under 4 % of the smallest disk/RAM-derived budget this class configures
and under 4 % of the 16 GiB preview-cache target it is tuned for. Could not construct a realistic slow-disk-write scenario: the persist writer
(`PreviewCacheFile.WriteAtomicallyAsync`) is a static method with no injectable slow-writer test seam, and every existing test double in
`PreviewImageServiceFaultTests`/`PreviewImageServiceTests` only fakes the decoder, not the disk writer. Conclusion: no code change; regression test
(`PreviewImagePersistQueueBoundTests`) pins the two constants so a future change to either is deliberate.

**R07 (`PerformanceTestHarness.MeasureParallelAsync` eager Task creation, fixed):** benchmark tooling only. Before: `files.Select(... => Task.Run(...))` created one
`Task` (one thread-pool work item) per selected file immediately, each then waiting on a `SemaphoreSlim(workers, workers)` internally -- a `take` far larger than
`workers` queued that many work items up front. After: the semaphore slot is acquired in the loop, before that file's `Task` is created, so at most `workers` Tasks
are ever alive at once. `PerformanceHarnessConcurrencyTests` (60 files, 4 workers, `ThreadPool.SetMinThreads` raised so the pool can't itself mask the bug) proves this:
against the old eager-creation code the same test observes up to 19-20 concurrent decodes (mutation-checked); against the fix, never more than 4. Benchmark report
shape (per-file order, P50/P95/stats, JSON) is unchanged -- the existing `BenchmarkReportFormattingTests`/`BenchmarkWorkloadRunnerTests` etc. still pass.

**R11 (`BenchmarkWindow.RunAsync` synchronous folder scan, fixed):** measured the synchronous portion directly (a standalone `Directory.EnumerateFiles(...).Where(...).ToArray()`
+ stat pass over a local NVMe temp folder, same shape as the window's old code): 20,000 top-level entries = 77 ms, 100,000 entries = 203 ms; the image-count-capped stat
pass itself is negligible (single-digit ms). 200 ms is already a visible dialog stall for a large local folder, and this app explicitly supports NAS/network-drive photo
folders (see `../OPEN-DECISIONS.md` Q-R29) where per-call latency is materially higher, so the synchronous scan was moved to a background thread (`BenchmarkWindow.EnumerateAndStat`), with the
run's `CancellationTokenSource` created before the scan (not after) and the token polled every 256 entries so Cancel/Close can interrupt an in-progress scan. Image-count
cap, enumeration order and the stat-sum total are unchanged (`BenchmarkWindowEnumerateAndStatTests`); the folder-missing/no-images/cannot-read-folder status messages,
button re-enable lifecycle and a Cancel-during-scan path are covered by `BenchmarkWindowEnumerationUiTests`.
