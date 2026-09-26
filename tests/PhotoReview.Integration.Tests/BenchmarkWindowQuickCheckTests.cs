using PhotoReview.App;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// perf(bench-window): "Quick check" (fast-sequential, 10 iterations) and the image-limit control both go
/// through small static test seams on <see cref="BenchmarkWindow"/> so the exact production wiring is
/// asserted, not a re-derived expression -- see <see cref="BenchmarkWindow.BuildQuickCheckProfile"/> and
/// <see cref="BenchmarkWindow.ApplyImageLimit"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkWindowQuickCheckTests
{
    [Fact(DisplayName = "Quick check always runs fast-sequential with 10 iterations, keeping its default (1) warm-up")]
    public void BuildQuickCheckProfile_IsFastSequentialWithTenIterations()
    {
        var profile = BenchmarkWindow.BuildQuickCheckProfile();

        Assert.NotNull(profile);
        Assert.Equal("fast-sequential", profile!.Id);
        Assert.Equal(10, profile.Iterations);
        Assert.Equal(BenchmarkProfiles.Find("fast-sequential")!.WarmupCount, profile.WarmupCount);
    }

    [Theory(DisplayName = "The image-limit cap keeps only the first N files, in the same order, and 0/oversized limits mean 'all'")]
    [InlineData(64, 10, 10)]   // limit bigger than the folder: unchanged (mirrors today's un-capped behavior)
    [InlineData(3, 10, 3)]     // the actual cap: first 3 of 10
    [InlineData(0, 10, 10)]    // 0 = all
    [InlineData(1, 1, 1)]      // a single-file folder
    public void ApplyImageLimit_KeepsFirstNInOrder(int limit, int fileCount, int expectedCount)
    {
        var files = Enumerable.Range(0, fileCount).Select(i => $"C:\\photos\\img-{i:000}.jpg").ToArray();

        var result = BenchmarkWindow.ApplyImageLimit(files, limit);

        Assert.Equal(expectedCount, result.Length);
        Assert.Equal(files.Take(expectedCount), result);
    }
}
