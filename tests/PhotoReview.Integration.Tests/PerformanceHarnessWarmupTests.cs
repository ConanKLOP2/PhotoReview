using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>TOOL-02: cold-read timing must not include first-call warm-up cost.</summary>
[Collection("GlobalState")]
public sealed class PerformanceHarnessWarmupTests : IDisposable
{
    private readonly TempRoot _root = new("perf-warmup");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Cold-read runs exactly one untimed warm-up decode before the first timed sample")]
    public async Task ColdReadRunsExactlyOneUntimedWarmupDecodeBeforeFirstTimedSample()
    {
        var fixture = PerformanceTestHarness.CreateFixture(_root.Path, 3);
        var events = new List<bool>();
        PerformanceTestHarness.DecodeObserver = timed => { lock (events) events.Add(timed); };
        try
        {
            await PerformanceTestHarness.RunAsync(fixture, 3, workers: 1,
                reportPath: Path.Combine(_root.Path, "report.json"));
        }
        finally { PerformanceTestHarness.DecodeObserver = null; }

        Assert.Equal([false, true, true, true], events);
    }
}
