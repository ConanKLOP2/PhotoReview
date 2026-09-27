using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R07: <c>PerformanceTestHarness.MeasureParallelAsync</c> used to create one Task per selected file before that
/// file's semaphore slot was acquired, so a `take` far larger than `workers` queued one thread-pool work item per
/// file up front (only `workers` of them could ever run at once). It now acquires the slot in the loop, before
/// creating each file's Task, so at most `workers` Tasks are ever running concurrently.
/// </summary>
[Collection("GlobalState")]
public sealed class PerformanceHarnessConcurrencyTests : IDisposable
{
    private readonly TempRoot _root = new("perf-concurrency");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "MeasureParallelAsync never runs more concurrent decodes than the configured worker count, even with far more files than workers")]
    public async Task RunAsync_ManyMoreFilesThanWorkers_NeverExceedsConfiguredConcurrency()
    {
        const int workers = 4;
        const int fileCount = 60; // far more than the worker count: if slots were still acquired inside a
                                   // pre-created Task (the R07 bug), every file's Task starts immediately and
                                   // this has plenty of room to observe more than `workers` overlapping.
        // Guarantees the thread pool can actually schedule `fileCount` work items close together instead of
        // the test being confounded by the pool's own (much smaller) default minimum thread count.
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, fileCount + 4), minIo);
        var fixture = PerformanceTestHarness.CreateFixture(_root.Path, fileCount);
        var active = 0;
        var maxActive = 0;
        var gate = new object();
        PerformanceTestHarness.ParallelDecodeObserver = _ =>
        {
            var now = Interlocked.Increment(ref active);
            lock (gate) maxActive = Math.Max(maxActive, now);
            // Busy work (no wall-clock sleep/delay) so overlapping observer calls actually overlap in time,
            // giving a buggy implementation a real chance to run more than `workers` at once.
            Thread.SpinWait(2_000_000);
            Interlocked.Decrement(ref active);
        };
        try
        {
            await PerformanceTestHarness.RunAsync(fixture, take: fileCount, workers: workers,
                reportPath: Path.Combine(_root.Path, "report.json"));
        }
        finally
        {
            PerformanceTestHarness.ParallelDecodeObserver = null;
            ThreadPool.SetMinThreads(minWorker, minIo);
        }

        Assert.InRange(maxActive, 1, workers);
    }
}
