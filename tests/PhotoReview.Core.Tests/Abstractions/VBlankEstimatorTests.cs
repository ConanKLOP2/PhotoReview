using PhotoReview.Core.Abstractions;
using Xunit;

namespace PhotoReview.Core.Tests.Abstractions;

/// <summary>
/// <see cref="VBlankEstimator"/> on synthetic vblank observations (10 000 ticks per ms): true vblanks on a grid,
/// observed late by a wake-up delay, some missed. The 60/75/144/240 Hz grids are simulated here; the real 240 Hz panel
/// and ~60 Hz monitor of the dev machine are exercised by the Manual harness.
/// </summary>
public sealed class VBlankEstimatorTests
{
    private const double TicksPerMs = 10_000;

    public static TheoryData<double> RefreshRates => new() { 60.0, 75.0, 144.0, 240.0 };

    private static double Period(double hz) => 1000 * TicksPerMs / hz;

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void ExactObservations_GiveThePeriodAndTheLatestVBlank(double hz)
    {
        var estimator = new VBlankEstimator();
        const long origin = 5_000_000;
        for (var i = 0; i < 20; i++) estimator.Add(origin + (long)Math.Round(i * Period(hz)));

        var timing = Assert.NotNull(estimator.Current);
        Assert.Equal(Period(hz), timing.RefreshPeriod, 1.0);
        Assert.Equal(origin + 19 * Period(hz), timing.LastVBlank, 1.0);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void LateWakeUpsAndMissedVBlanks_DoNotShiftTheGrid(double hz)
    {
        var estimator = new VBlankEstimator();
        const long origin = 1_000_000;
        var last = 0L;
        // Wake-up delays of 0.05-1.2 ms; vblanks 7, 8 and 21 are missed (the observer was busy).
        double[] delaysMs = [0.3, 0.05, 1.2, 0.6, 0.1, 0.9, 0.2, 0.7, 0.4, 1.0, 0.15, 0.5];
        for (var i = 0; i < 30; i++)
        {
            if (i is 7 or 8 or 21) continue;
            last = origin + (long)Math.Round(i * Period(hz));
            estimator.Add(last + (long)(delaysMs[i % delaysMs.Length] * TicksPerMs));
        }

        var timing = Assert.NotNull(estimator.Current);
        Assert.InRange(timing.RefreshPeriod, Period(hz) * 0.995, Period(hz) * 1.005);
        // The phase is within the smallest wake-up delay seen, not the average one.
        Assert.InRange(timing.LastVBlank - last, -0.1 * TicksPerMs, 0.2 * TicksPerMs);
    }

    [Fact]
    public void FewerThanTheMinimumSamples_GiveNoTiming()
    {
        var estimator = new VBlankEstimator();
        for (var i = 0; i < VBlankEstimator.MinSamples - 1; i++) estimator.Add(1000 + i * 166_667L);
        Assert.Null(estimator.Current);

        estimator.Add(1000 + (VBlankEstimator.MinSamples - 1) * 166_667L);
        Assert.NotNull(estimator.Current);

        estimator.Reset();
        Assert.Null(estimator.Current);
    }

    [Theory]
    [InlineData(60.0, 75.0)]
    [InlineData(75.0, 60.0)]
    [InlineData(144.0, 240.0)]
    public void ARefreshRateChange_IsFollowedWithinAFewRefreshes(double before, double after)
    {
        var estimator = new VBlankEstimator();
        var t = 0.0;
        for (var i = 0; i < VBlankEstimator.Capacity; i++)
        {
            t += Period(before);
            estimator.Add((long)t);
        }
        for (var i = 0; i < 8; i++)
        {
            t += Period(after);
            estimator.Add((long)t);
        }

        var timing = Assert.NotNull(estimator.Current);
        Assert.InRange(timing.RefreshPeriod, Period(after) * 0.99, Period(after) * 1.01);
        Assert.Equal(t, timing.LastVBlank, 10.0);
    }

    /// <summary>
    /// 240 -> 60 Hz looks like three missed vblanks out of four, so it is only followed once the old samples have left
    /// the window (a monitor switch resets the estimator at once; this is a same-monitor mode change).
    /// </summary>
    [Fact]
    public void AnIntegerRatioRateChange_IsFollowedWithinTheSampleWindow()
    {
        var estimator = new VBlankEstimator();
        var t = 0.0;
        for (var i = 0; i < VBlankEstimator.Capacity; i++) estimator.Add((long)(t += Period(240)));
        for (var i = 0; i < VBlankEstimator.Capacity; i++) estimator.Add((long)(t += Period(60)));

        Assert.InRange(Assert.NotNull(estimator.Current).RefreshPeriod, Period(60) * 0.99, Period(60) * 1.01);
    }

    [Fact]
    public void NonIncreasingTimestamps_AreIgnored()
    {
        var estimator = new VBlankEstimator();
        for (var i = 1; i <= 6; i++) estimator.Add(i * 100_000L);
        estimator.Add(300_000);

        Assert.Equal(6, estimator.Count);
        Assert.Equal(100_000, Assert.NotNull(estimator.Current).RefreshPeriod);
    }
}
