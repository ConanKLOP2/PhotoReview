using System.Globalization;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Diagnostics;

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

    [Fact(DisplayName = "The profile runner the CLI uses times only the measure step, never the iteration's setup")]
    public async Task RunProfileAsync_SamplesExcludeSetupTime()
    {
        var clock = new ManualTimeProvider();
        using var root = new TempRoot("bench-profile-timing");
        var profile = BenchmarkProfiles.Find("action-move")! with { Iterations = 2, WarmupCount = 1 };
        var prepared = 0;

        var report = await BenchmarkWorkloadRunner.RunProfileAsync(root.Path, profile,
            (workload, iteration, _) =>
            {
                Assert.Equal(profile.Workload, workload);
                prepared++;
                clock.Advance(TimeSpan.FromMilliseconds(700)); // scratch copy / cold-cache eviction
                return Task.FromResult<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>>(() =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(20));
                    return Task.FromResult<(bool, ReviewMetricsSnapshot?)>((true, null));
                });
            },
            progress: null, clock, CancellationToken.None);

        Assert.Equal([20d, 20d], Assert.Single(report.Phases).Samples);
        Assert.Equal(3, prepared); // warm-up + 2 timed iterations
    }

    private static ReviewMetricsSnapshot Cumulative(int calls) =>
        new(CacheHits: calls * 2, CacheMisses: calls, SourceBytesRead: calls * 1000, SourceReads: calls * 5,
            DecodeMilliseconds: calls * 7, PresentedImages: 0, PresentMilliseconds: 0)
        {
            PreloadHits = calls * 3,
            DiskCacheHits = calls,
            SourceOpenCount = calls * 5,
            TopSourceOpens = [new SourceOpenEntry("a.jpg", calls * 5)],
        };

    [Fact(DisplayName = "Report metrics cover the measured iterations only, not the warm-up ones")]
    public async Task RunPreparedAsync_MetricsExcludeWarmup()
    {
        using var root = new TempRoot("bench-warmup-metrics");
        var profile = BenchmarkProfiles.Find("action-move")! with { Iterations = 3, WarmupCount = 2 };
        var calls = 0;

        // Like BenchmarkImageExecutor.Metrics, every step reports the executor's cumulative totals.
        var report = await BenchmarkEngine.RunPreparedAsync(root.Path, profile,
            (_, _, _, _) => Task.FromResult<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>>(() =>
            {
                calls++;
                return Task.FromResult<(bool, ReviewMetricsSnapshot?)>((true, Cumulative(calls)));
            }));

        Assert.Equal(5, calls);
        var metrics = Assert.IsType<ReviewMetricsSnapshot>(report.Metrics);
        Assert.Equal(15, metrics.SourceReads);
        Assert.Equal(6, metrics.CacheHits);
        Assert.Equal(3, metrics.CacheMisses);
        Assert.Equal(3000, metrics.SourceBytesRead);
        Assert.Equal(9, metrics.PreloadHits);
        Assert.Equal(3, metrics.DiskCacheHits);
        Assert.Equal(15, metrics.SourceOpenCount);
        Assert.Equal(15, Assert.Single(metrics.TopSourceOpens).Count);
    }

    private sealed class LineLog : ILog
    {
        public List<string> Lines { get; } = [];
        public bool Enabled => true;
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message, Exception? ex = null) => Lines.Add(message);
    }

    [Fact(DisplayName = "Benchmark log lines use invariant decimals under a comma-decimal culture (AGENTS rule 4)")]
    public async Task RunPreparedAsync_LogUsesInvariantDecimals()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
        try
        {
            var clock = new ManualTimeProvider();
            using var root = new TempRoot("bench-log-culture");
            var profile = BenchmarkProfiles.Find("action-move")! with { Iterations = 1, WarmupCount = 0 };
            var log = new LineLog();

            await BenchmarkEngine.RunPreparedAsync(root.Path, profile,
                (_, _, _, _) => Task.FromResult<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>>(() =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(12.5));
                    return Task.FromResult<(bool, ReviewMetricsSnapshot?)>((true, null));
                }),
                timeProvider: clock, log: log);

            Assert.Contains(log.Lines, l => l.Contains("elapsedMs=12.5 ", StringComparison.Ordinal));
            Assert.Contains(log.Lines, l => l.Contains("p50Ms=12.5 p95Ms=12.5 maxMs=12.5", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
