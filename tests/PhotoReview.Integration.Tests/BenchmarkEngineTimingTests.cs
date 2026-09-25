using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>R2-F-14: a benchmark sample must contain only the measured step, never its own setup I/O.</summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkEngineTimingTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact(DisplayName = "Samples exclude the prepare step and contain only the measure step")]
    public async Task RunPreparedAsync_SamplesExcludePrepareTime()
    {
        var clock = new ManualTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "PhotoReviewBenchTiming_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var profile = BenchmarkProfiles.Find("action-move")! with { Iterations = 3, WarmupCount = 1 };
            var order = new List<string>();

            var report = await BenchmarkEngine.RunPreparedAsync(folder, profile,
                (_, _, _, _) =>
                {
                    order.Add("prepare");
                    clock.Advance(TimeSpan.FromMilliseconds(500)); // setup I/O: must not show up in the sample
                    return Task.FromResult<Func<Task<(bool Correct, PhotoReview.Core.Diagnostics.ReviewMetricsSnapshot? Metrics)>>>(() =>
                    {
                        order.Add("measure");
                        clock.Advance(TimeSpan.FromMilliseconds(10));
                        return Task.FromResult<(bool, PhotoReview.Core.Diagnostics.ReviewMetricsSnapshot?)>((true, null));
                    });
                },
                timeProvider: clock);

            var phase = Assert.Single(report.Phases);
            Assert.Equal([10d, 10d, 10d], phase.Samples);
            Assert.Equal(BenchmarkResultStatus.Pass, phase.Status);
            // warm-up + 3 timed iterations, each strictly prepare then measure
            Assert.Equal(["prepare", "measure", "prepare", "measure", "prepare", "measure", "prepare", "measure"], order);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
