using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Diagnostics;

public sealed class BenchmarkStatisticsTests
{
    [Fact]
    public void PercentileEmptyCollectionReturnsZero()
    {
        var result = BenchmarkStatistics.Percentile([], 0.5);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void PercentileOutOfRangeThrowsArgumentOutOfRangeException(double percentile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.Percentile([1.0, 2.0], percentile));
    }

    [Fact]
    public void PercentileSingleValueReturnsThatValue()
    {
        var result = BenchmarkStatistics.Percentile([42.5], 0.95);
        Assert.Equal(42.5, result);
    }

    [Fact]
    public void PercentileTwoValuesInterpolatesCorrectly()
    {
        var p50 = BenchmarkStatistics.Percentile([20.0, 10.0], 0.5);
        Assert.Equal(15.0, p50);

        var p0 = BenchmarkStatistics.Percentile([10.0, 20.0], 0.0);
        Assert.Equal(10.0, p0);

        var p100 = BenchmarkStatistics.Percentile([10.0, 20.0], 1.0);
        Assert.Equal(20.0, p100);
    }

    [Fact]
    public void PercentileKnownDistributionCalculatesP50AndP95()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var p50 = BenchmarkStatistics.Percentile(values, 0.5);
        Assert.Equal(50.5, p50);

        var p95 = BenchmarkStatistics.Percentile(values, 0.95);
        Assert.Equal(95.05, p95, 2);
    }
}
