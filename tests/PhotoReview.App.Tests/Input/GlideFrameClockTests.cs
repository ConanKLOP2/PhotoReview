using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// <see cref="GlideFrameClock"/> at the refresh rates photos are reviewed on (60, 75 Hz) and gaming panels (144, 240 Hz).
/// Time base: 10 000 ticks per ms (a 10 MHz QPC), so a 75 Hz refresh is 133 333 ticks.
/// </summary>
public sealed class GlideFrameClockTests
{
    private const double TicksPerMs = 10_000;

    public static TheoryData<double> RefreshRates => new() { 60.0, 75.0, 144.0, 240.0 };

    private static long Period(double hz) => (long)Math.Round(1000 * TicksPerMs / hz);

    private static long At(double refreshes, double hz) => (long)Math.Round(refreshes * Period(hz));

    /// <summary>Callback time (in refreshes) plus the lead, rounded up: the refresh a frame is expected on.</summary>
    private static long ExpectedRefresh(double refreshes, double hz) =>
        (long)Math.Ceiling(refreshes + GlideFrameClock.PresentLeadMs * hz / 1000);

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Predict_EveryStepIsAWholeNumberOfRefreshes_AndTheTotalIsTheRefreshesPassed(double hz)
    {
        var timing = new DisplayTiming(0, Period(hz));
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, TicksPerMs);
        double[] callbacks = [0.2, 0.5, 0.9, 1.3, 1.4, 1.6, 4.3, 4.4, 5.2, 7.9, 8.1, 11.6];

        Assert.Equal(0, clock.Advance(1, At(callbacks[0], hz), timing)); // anchors
        var total = 0.0;
        var periodMs = Period(hz) / TicksPerMs;
        for (var i = 1; i < callbacks.Length; i++)
        {
            var step = clock.Advance(1 + i, At(callbacks[i], hz), timing); // RenderingTime is ignored
            var expected = (ExpectedRefresh(callbacks[i], hz) - ExpectedRefresh(callbacks[i - 1], hz)) * periodMs;
            Assert.Equal(expected, step, 6);
            total += step;
        }
        Assert.Equal((ExpectedRefresh(callbacks[^1], hz) - ExpectedRefresh(callbacks[0], hz)) * periodMs, total, 6);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Predict_ALateFrameJumpsToTheRefreshItIsSeenOn(double hz)
    {
        var timing = new DisplayTiming(0, Period(hz));
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, TicksPerMs);
        var periodMs = Period(hz) / TicksPerMs;

        clock.Advance(0, At(0.1, hz), timing);
        Assert.Equal(periodMs, clock.Advance(0, At(1.1, hz), timing), 6);      // on time: one refresh
        Assert.Equal(3 * periodMs, clock.Advance(0, At(4.1, hz), timing), 6);  // two refreshes missed: three at once
        Assert.Equal(0, clock.Advance(0, At(4.15, hz), timing));               // same refresh again: no move
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Predict_UsesTheLatestVBlankEstimate_NotTheOneItStartedWith(double hz)
    {
        var period = Period(hz);
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, TicksPerMs);

        clock.Advance(0, At(0.1, hz), new DisplayTiming(0, period));
        // The estimate moved on (a later LastVBlank on the same grid): still one refresh per refresh.
        Assert.Equal(period / TicksPerMs, clock.Advance(0, At(1.1, hz), new DisplayTiming(At(1, hz), period)), 6);
        Assert.Equal(period / TicksPerMs, clock.Advance(0, At(2.1, hz), new DisplayTiming(At(2, hz), period)), 6);
    }

    [Fact]
    public void Predict_WithoutTimingYet_StepsByRenderingTime_ThenLocksToTheRefreshGrid()
    {
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, TicksPerMs);

        Assert.Equal(0, clock.Advance(100, 0, null));
        Assert.Equal(7, clock.Advance(107, At(0.4, 60), null), 6);          // like Off
        Assert.Equal(9, clock.Advance(116, At(0.9, 60), new DisplayTiming(0, Period(60))), 6); // anchors here, like Off
        Assert.Equal(Period(60) / TicksPerMs, clock.Advance(117, At(1.9, 60), new DisplayTiming(0, Period(60))), 6);
    }

    [Fact]
    public void Predict_MonitorChangeFrom60To75Hz_ReanchorsOnTheNewGrid()
    {
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, TicksPerMs);
        var sixty = new DisplayTiming(0, Period(60));
        clock.Advance(0, At(0.1, 60), sixty);
        clock.Advance(16, At(1.1, 60), sixty);

        // The window is now on a 75 Hz monitor with another phase: one frame by RenderingTime, then 75 Hz refreshes.
        var start = At(2.1, 60);
        var seventyFive = new DisplayTiming(start - 30_000, Period(75));
        Assert.Equal(14, clock.Advance(30, start, seventyFive), 6);
        Assert.Equal(Period(75) / TicksPerMs, clock.Advance(31, start + Period(75), seventyFive), 6);
        Assert.Equal(2 * Period(75) / TicksPerMs, clock.Advance(32, start + 3 * Period(75), seventyFive), 6);
    }

    [Fact]
    public void Off_StepsByRenderingTime_AndIgnoresTheTiming()
    {
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Off, TicksPerMs);
        var timing = new DisplayTiming(0, Period(60));

        Assert.False(clock.NeedsDisplayTiming);
        Assert.Equal(0, clock.Advance(100, 0, timing));
        Assert.Equal(0, clock.Advance(100, At(3, 60), timing));   // repeated RenderingTime: not a new frame
        Assert.Equal(5, clock.Advance(105, At(9, 60), timing), 6);
    }

    [Fact]
    public void UndefinedMode_BehavesLikeOff()
    {
        var clock = new GlideFrameClock();
        clock.Start((KineticGlideSmoothing)42, TicksPerMs);

        Assert.False(clock.NeedsDisplayTiming);
        clock.Advance(10, 0, new DisplayTiming(0, Period(60)));
        Assert.Equal(4, clock.Advance(14, 0, new DisplayTiming(0, Period(60))), 6);
    }
}
