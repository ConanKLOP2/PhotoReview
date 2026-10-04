using PhotoReview.Benchmarking;

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

/// <summary>R2-F-21: a whole-registry benchmark run must not include profiles that always fail.</summary>
public sealed class BenchmarkRunnableProfileTests
{
    [Fact]
    public void Runnable_ExcludesUnimplementedAndCorrectnessOnlyProfiles()
    {
        Assert.DoesNotContain(BenchmarkProfiles.Runnable, p => p.Id is "explorer-reindex" or "cache-recovery");
        Assert.DoesNotContain(BenchmarkProfiles.Runnable, p => p.CorrectnessOnly);
        Assert.Contains(BenchmarkProfiles.Runnable, p => p.Id == "recommended-auto");
        Assert.Contains(BenchmarkProfiles.All, p => p.Id == "cache-recovery" && BenchmarkProfiles.IsNotImplemented(p));
    }
}
