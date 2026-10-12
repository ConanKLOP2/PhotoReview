using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Integration.Tests.ShellHost;

/// <summary>WP-29: thống kê của probe (percentile, loại ngoại lai, so sánh hồi quy, lịch xen kẽ). Có mutation check (xem NOWPF-WP29).</summary>
[Trait("Category", "Integration")]
public sealed class ProbeStatisticsTests
{
    [Fact]
    public void Percentile_InterpolatesBetweenRanks_OnUnsortedInput()
    {
        double[] values = [5, 1, 4, 2, 3];
        Assert.Equal(3.0, ProbeStatistics.Percentile(values, 50), 9);
        Assert.Equal(4.8, ProbeStatistics.Percentile(values, 95), 9);
        Assert.Equal(1.0, ProbeStatistics.Percentile(values, 0), 9);
        Assert.Equal(5.0, ProbeStatistics.Percentile(values, 100), 9);
        Assert.Equal(12.5, ProbeStatistics.Percentile([10, 20], 25), 9);
    }

    [Fact]
    public void Percentile_ClampsOutOfRangePercent_AndHandlesEmptyAndSingle()
    {
        double[] values = [1, 2, 3];
        Assert.Equal(1.0, ProbeStatistics.Percentile(values, -10), 9);
        Assert.Equal(3.0, ProbeStatistics.Percentile(values, 250), 9);
        Assert.True(double.IsNaN(ProbeStatistics.Percentile([], 50)));
        Assert.Equal(7.0, ProbeStatistics.Percentile([7], 95), 9);
    }

    [Fact]
    public void Median_OfEvenCount_IsTheMeanOfTheMiddleTwo() =>
        Assert.Equal(2.5, ProbeStatistics.Median([4, 1, 3, 2]), 9);

    [Fact]
    public void RemoveHighOutliers_DropsASpike_ButKeepsOrder()
    {
        var kept = ProbeStatistics.RemoveHighOutliers([10, 12, 100, 11, 10, 13]);
        Assert.Equal([10, 12, 11, 10, 13], kept);
    }

    [Fact]
    public void RemoveHighOutliers_NeedsFiveSamples()
    {
        double[] four = [10, 11, 12, 200];
        Assert.Equal(4, ProbeStatistics.RemoveHighOutliers(four).Count);
    }

    [Fact]
    public void RemoveHighOutliers_KeepsEverything_WhenMadIsZero()
    {
        double[] flat = [10, 10, 10, 10, 10, 500];
        Assert.Equal(6, ProbeStatistics.RemoveHighOutliers(flat).Count);
    }

    [Fact]
    public void RemoveHighOutliers_NeverDropsTheLowSide()
    {
        var kept = ProbeStatistics.RemoveHighOutliers([100, 101, 102, 103, 104, 1]);
        Assert.Contains(1.0, kept);
    }

    [Fact]
    public void RemoveHighOutliers_ValueExactlyAtTheLimit_IsKept()
    {
        // median 3, MAD 1, k 1 => limit = 3 + 1 * 1.4826 * 1
        var limit = 3 + (1.0 * ProbeStatistics.MadScale * 1.0);
        var kept = ProbeStatistics.RemoveHighOutliers([1, 2, 3, 4, limit], k: 1.0);
        Assert.Equal(5, kept.Count);
    }

    [Fact]
    public void RemoveHighOutliers_UsesTheNormalConsistencyScale()
    {
        // median 5.5, MAD 2.5: limit = 5.5 + 3 * 1.4826 * 2.5 = 16.6 keeps 15 (without the scale it would be 13).
        var kept = ProbeStatistics.RemoveHighOutliers([1, 2, 3, 4, 5, 6, 7, 8, 9, 15]);
        Assert.Contains(15.0, kept);
        Assert.Equal(10, kept.Count);
    }

    [Fact]
    public void Summarize_CountsDroppedAndIgnoresNonFinite()
    {
        var summary = ProbeStatistics.Summarize([10, 11, 12, 10, 11, 900, double.NaN, double.PositiveInfinity]);
        Assert.Equal(5, summary.Count);
        Assert.Equal(1, summary.Dropped);
        Assert.Equal(10.0, summary.Min, 9);
        Assert.Equal(12.0, summary.Max, 9);
        Assert.Equal(11.0, summary.Median, 9);
        Assert.Equal(10.8, summary.Mean, 9);
        Assert.Equal(11.8, summary.P95, 9);
    }

    [Fact]
    public void Summarize_WithoutOutlierRemoval_KeepsTheSpike()
    {
        var summary = ProbeStatistics.Summarize([10, 11, 12, 10, 11, 900], removeOutliers: false);
        Assert.Equal(6, summary.Count);
        Assert.Equal(0, summary.Dropped);
        Assert.Equal(900.0, summary.Max, 9);
    }

    [Fact]
    public void Summarize_Empty_IsNaN()
    {
        var summary = ProbeStatistics.Summarize([]);
        Assert.Equal(0, summary.Count);
        Assert.True(double.IsNaN(summary.Median));
    }

    [Fact]
    public void Compare_ExactlyAtTheThreshold_IsNotARegression()
    {
        var result = ProbeStatistics.Compare([100, 100, 100], [105, 105, 105], thresholdPercent: 5, minAbsoluteMs: 1);
        Assert.False(result.IsRegression);
        Assert.Equal(5.0, result.DeltaMs, 9);
        Assert.Equal(5.0, result.DeltaPercent, 9);
    }

    [Fact]
    public void Compare_AboveThreshold_IsARegression() =>
        Assert.True(ProbeStatistics.Compare([100, 100, 100], [105.5, 105.5, 105.5]).IsRegression);

    [Fact]
    public void Compare_UsesTheLargerOfPercentAndAbsoluteAllowance()
    {
        // baseline 10 => 5 % = 0.5 ms, the 1 ms floor applies: +0.8 ms is within noise, +1.2 ms is not.
        Assert.False(ProbeStatistics.Compare([10, 10, 10], [10.8, 10.8, 10.8]).IsRegression);
        Assert.True(ProbeStatistics.Compare([10, 10, 10], [11.2, 11.2, 11.2]).IsRegression);
    }

    [Fact]
    public void Compare_Improvement_IsNegativeDelta_AndNotARegression()
    {
        var result = ProbeStatistics.Compare([200, 200, 200], [100, 100, 100]);
        Assert.False(result.IsRegression);
        Assert.Equal(-100.0, result.DeltaMs, 9);
        Assert.Equal(-50.0, result.DeltaPercent, 9);
    }

    [Fact]
    public void Interleave_RunsEveryAppExactlyOncePerRound_AndMarksWarmups()
    {
        var slots = ProbeSchedule.Interleave(["a", "b", "c"], rounds: 4, warmupRounds: 1, seed: 7);
        Assert.Equal(15, slots.Count);
        for (var round = 0; round < 5; round++)
        {
            var inRound = slots.Where(s => s.Round == round).Select(s => s.App).Order().ToArray();
            Assert.Equal(["a", "b", "c"], inRound);
        }

        Assert.All(slots.Where(s => s.Round == 0), s => Assert.True(s.IsWarmup));
        Assert.All(slots.Where(s => s.Round > 0), s => Assert.False(s.IsWarmup));
        // rounds are contiguous: the schedule is round-major (no app runs twice before the others ran)
        Assert.Equal(slots.Select(s => s.Round).Order().ToArray(), slots.Select(s => s.Round).ToArray());
    }

    [Fact]
    public void Interleave_IsDeterministicForTheSameSeed_AndShuffles()
    {
        var first = ProbeSchedule.Interleave(["a", "b", "c"], 10, 0, seed: 3);
        var second = ProbeSchedule.Interleave(["a", "b", "c"], 10, 0, seed: 3);
        Assert.Equal(first, second);

        var orders = first.GroupBy(s => s.Round).Select(g => string.Concat(g.Select(s => s.App))).Distinct().Count();
        Assert.True(orders > 1, "the order never changes between rounds");

        var other = ProbeSchedule.Interleave(["a", "b", "c"], 10, 0, seed: 4);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(3, -1)]
    public void Interleave_RejectsBadCounts(int rounds, int warmup) =>
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => ProbeSchedule.Interleave(["a"], rounds, warmup, 1));
}
