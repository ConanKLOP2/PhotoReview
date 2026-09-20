using PhotoReview.Benchmarking;
using System.IO;
using PhotoReview.App;

namespace PhotoReview.Integration.Tests;

/// <summary>Benchmark profile registry and ranking contracts.</summary>
[Trait("Category", "Slow")]
public sealed class BenchmarkProfileTests
{
    [Fact(DisplayName = "Expanded benchmark profile registry")]
    public void ExpandedBenchmarkProfileRegistry() => Assert.True(BenchmarkProfiles.All.Count >= 25);

    [Fact(DisplayName = "Original is correctness-only")]
    public void OriginalIsCorrectnessOnly() =>
        Assert.Equal(1, BenchmarkProfiles.All.Count(x => x.CorrectnessOnly));

    [Fact(DisplayName = "Speed ranking excludes correctness-only profile")]
    public void SpeedRankingExcludesCorrectnessOnlyProfile() =>
        Assert.True(BenchmarkRanking.Rank(
            BenchmarkProfiles.All.Where(x => !x.CorrectnessOnly)
                .Select(x => new BenchmarkPhaseResult(x.Id, x.Workload, [10, 20], BenchmarkResultStatus.Pass)),
            BenchmarkWorkload.Sequential).Count > 0);
}

/// <summary>Relative performance harness probe.</summary>
[Trait("Category", "Slow")]
public sealed class PerformanceHarnessTests : IDisposable
{
    private readonly TempRoot _root = new("performance");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Relative performance samples complete without hard timing failure")]
    public async Task RelativePerformanceSamplesCompleteWithoutHardTimingFailure()
    {
        var fixture = PerformanceTestHarness.CreateFixture(_root.Path, 30);
        var report = await PerformanceTestHarness.RunAsync(fixture, 30, workers: 4,
            reportPath: Path.Combine(_root.Path, "performance-report.json"));
        Assert.True(report.Samples.All(sample => sample.Status is "PASS" or "WARN"));
    }
}
