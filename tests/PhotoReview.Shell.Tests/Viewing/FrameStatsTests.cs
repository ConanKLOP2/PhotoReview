using System.Diagnostics;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>WP-21: thống kê khung (phân vị, vòng tròn, đếm khung bị chặn) - dữ liệu của báo cáo hiệu năng.</summary>
[Trait("Category", "HotPath")]
public sealed class FrameStatsTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    [Fact]
    public void Percentiles_AreNearestRankOverTheRecordedFrames()
    {
        var stats = new FrameStats();
        for (var i = 1; i <= 100; i++)
        {
            stats.Record(Ms(i), Ms(i / 2.0));
        }

        Assert.Equal(100, stats.Count);
        Assert.Equal(50, stats.FrameMs(50), 3);
        Assert.Equal(95, stats.FrameMs(95), 3);
        Assert.Equal(100, stats.FrameMs(100), 3);
        Assert.Equal(25, stats.PresentMs(50), 3);
        Assert.Equal(50, stats.MaxPresentMs(), 3);
    }

    [Fact]
    public void BlockedPresents_CountsFramesAtOrOverTheThreshold()
    {
        var stats = new FrameStats();

        stats.Record(Ms(3), Ms(FrameStats.BlockedThresholdMs - 0.5));
        stats.Record(Ms(30), Ms(FrameStats.BlockedThresholdMs));
        stats.Record(Ms(300), Ms(250));

        Assert.Equal(2, stats.BlockedPresents);
        Assert.Equal(3, stats.Total);
    }

    [Fact]
    public void Ring_KeepsOnlyTheLastCapacityFrames_ButCountsAll()
    {
        var stats = new FrameStats();
        for (var i = 0; i < FrameStats.Capacity + 10; i++)
        {
            stats.Record(Ms(i < 10 ? 1000 : 1), Ms(0.1));
        }

        Assert.Equal(FrameStats.Capacity, stats.Count);
        Assert.Equal(FrameStats.Capacity + 10, stats.Total);
        Assert.Equal(1, stats.FrameMs(100), 3);     // 10 khung 1000 ms đầu đã bị ghi đè
    }

    [Fact]
    public void Empty_ReportsZeros_AndResetClearsEverything()
    {
        var stats = new FrameStats();
        Assert.Equal(0, stats.FrameMs(95));
        Assert.Equal(0, stats.MaxPresentMs());

        stats.Record(Ms(5), Ms(5));
        stats.Reset();

        Assert.Equal((0, 0L, 0L), (stats.Count, stats.Total, stats.BlockedPresents));
    }

    [Theory]
    [InlineData(0.0, 0.0, false)]
    [InlineData(7.9, 7.9, false)]
    [InlineData(8.0, 0.0, true)]
    [InlineData(0.0, 8.0, true)]
    public void IsSlow_IsTrueWhenEitherEndDrawOrPresentReachesTheThreshold(double endDrawMs, double presentMs, bool expected)
    {
        Assert.Equal(expected, FrameStats.IsSlow(endDrawMs, presentMs));
    }

    [Fact]
    public void Slow_KeepsOnlyBlockedFramesAndIsBounded()
    {
        var stats = new FrameStats();

        stats.RecordSlow(5, 1, 1, 1, "fast");
        stats.RecordSlow(500, 1, 480, 1, "endDraw blocked");
        stats.RecordSlow(500, 1, 1, 480, "present blocked");
        for (var i = 0; i < 100; i++)
        {
            stats.RecordSlow(500, 1, 480, 1, "many");
        }

        Assert.Equal(64, stats.Slow.Count);
        Assert.Equal("endDraw blocked", stats.Slow[0].Note);
    }
}
