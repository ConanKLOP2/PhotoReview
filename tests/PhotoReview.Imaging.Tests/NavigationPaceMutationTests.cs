using System.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Mutation-testing follow-ups for <see cref="NavigationPace"/> (docs/MUTATION-TESTING.md): the EWMA arithmetic, the burst
/// boundaries (500 ms step limit, lead ratio of exactly 1, idle window edges) and the viewer start delay, on a fake clock.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class NavigationPaceMutationTests
{
    private sealed class FakeClock
    {
        private long _ticks = 1_000_000;
        public long Now() => _ticks;
        public void AdvanceMs(double milliseconds) => _ticks += (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    private static (NavigationPace Pace, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        return (new NavigationPace(clock.Now), clock);
    }

    /// <summary>Records index 0, then one single step per entry of <paramref name="gapsMs"/>, each after that many ms.</summary>
    private static (NavigationPace Pace, FakeClock Clock) Steps(params double[] gapsMs)
    {
        var (pace, clock) = Create();
        pace.Record(0);
        var index = 0;
        foreach (var gap in gapsMs)
        {
            clock.AdvanceMs(gap);
            pace.Record(++index);
        }
        return (pace, clock);
    }

    [Fact(DisplayName = "Steps exactly at the 500 ms limit still form a burst (the limit is inclusive); one tick over restarts the estimate")]
    public void Record_StepAtExactlyMaxInterval_IsStillABurst()
    {
        var (pace, _) = Steps(NavigationPace.MaxStepIntervalMs, NavigationPace.MaxStepIntervalMs);
        Assert.Equal(2, pace.GetLead(decodeMs: 1000)); // interval 500 -> 1000 / 500

        var (slow, _) = Steps(NavigationPace.MaxStepIntervalMs + 1, NavigationPace.MaxStepIntervalMs + 1);
        Assert.Equal(0, slow.GetLead(decodeMs: 1000));
    }

    [Fact(DisplayName = "One interval is not yet a burst; the first interval is taken as is, later ones are blended with weight 0.3")]
    public void Record_EwmaArithmetic()
    {
        var (one, _) = Steps(100);
        Assert.Equal(0, one.GetLead(decodeMs: 300)); // a single interval: not a burst yet

        var (steady, _) = Steps(100, 100);
        Assert.Equal(3, steady.GetLead(decodeMs: 300)); // interval exactly 100 -> ceil(3.0)

        // 100, then 200: 100 + 0.3 * (200 - 100) = 130 -> 300 / 130 = 2.3 -> 3 (a subtracting EWMA gives 70 -> 5, an un-seeded one 81 -> 4)
        var (blended, _) = Steps(100, 200);
        Assert.Equal(3, blended.GetLead(decodeMs: 300));
    }

    [Fact(DisplayName = "A decode exactly as long as the key interval needs no lead; slightly longer rounds up")]
    public void GetLead_DecodeEqualsInterval_IsZero()
    {
        var (pace, _) = Steps(100, 100);

        Assert.Equal(0, pace.GetLead(decodeMs: 100));
        Assert.Equal(0, pace.GetLead(decodeMs: 50));
        Assert.Equal(2, pace.GetLead(decodeMs: 101));
        Assert.Equal(0, pace.GetLead(decodeMs: 0));
        Assert.Equal(NavigationPace.MaxLead, pace.GetLead(decodeMs: 100_000));
    }

    [Fact(DisplayName = "The viewer start delay is 1.5 x the interval (capped at 100 ms) when the decode is longer than the interval, else zero")]
    public void GetViewerStartDelay_Boundaries()
    {
        var (pace, _) = Steps(40, 40);

        Assert.Equal(TimeSpan.FromMilliseconds(60), pace.GetViewerStartDelay(decodeMs: 100));
        Assert.Equal(TimeSpan.Zero, pace.GetViewerStartDelay(decodeMs: 40));   // exactly the interval
        Assert.Equal(TimeSpan.Zero, pace.GetViewerStartDelay(decodeMs: 30));
        Assert.Equal(TimeSpan.Zero, pace.GetViewerStartDelay(decodeMs: 0));

        var (slow, _) = Steps(100, 100);
        Assert.Equal(TimeSpan.FromMilliseconds(NavigationPace.MaxViewerStartDelayMs), slow.GetViewerStartDelay(decodeMs: 500));

        var (idle, _) = Create();
        Assert.Equal(TimeSpan.Zero, idle.GetViewerStartDelay(decodeMs: 500)); // no burst at all
    }

    [Fact(DisplayName = "A burst stays active until max(2 x interval, 150 ms) after the last key (inclusive), then ends")]
    public void ActiveWindow_IsTwiceTheInterval()
    {
        var (pace, clock) = Steps(100, 100); // window = max(200, 150) = 200 ms

        clock.AdvanceMs(50);
        Assert.Equal(3, pace.GetLead(decodeMs: 300));
        clock.AdvanceMs(130); // 180 ms since the last key: inside 2 x interval, past the 150 ms floor
        Assert.Equal(3, pace.GetLead(decodeMs: 300));
        clock.AdvanceMs(20);  // exactly 200 ms
        Assert.Equal(3, pace.GetLead(decodeMs: 300));
        clock.AdvanceMs(1);
        Assert.Equal(0, pace.GetLead(decodeMs: 300));
        Assert.Equal(TimeSpan.Zero, pace.GetViewerStartDelay(decodeMs: 300));
    }

    [Fact(DisplayName = "For a fast burst the idle window is the 150 ms floor, inclusive")]
    public void ActiveWindow_FloorForFastBursts()
    {
        var (pace, clock) = Steps(20, 20); // 2 x interval = 40 ms, so the 150 ms floor applies

        clock.AdvanceMs(150);
        Assert.Equal(3, pace.GetLead(decodeMs: 60));
        clock.AdvanceMs(1);
        Assert.Equal(0, pace.GetLead(decodeMs: 60));
    }

    [Fact(DisplayName = "A jump or a pause restarts the interval estimate")]
    public void Record_JumpOrPause_RestartsTheEstimate()
    {
        var (pace, clock) = Steps(100, 100);
        Assert.Equal(3, pace.GetLead(decodeMs: 300));

        clock.AdvanceMs(100);
        pace.Record(50); // a jump, not a step
        Assert.Equal(0, pace.GetLead(decodeMs: 300));

        clock.AdvanceMs(100);
        pace.Record(51);
        clock.AdvanceMs(100);
        pace.Record(52);
        Assert.Equal(3, pace.GetLead(decodeMs: 300)); // a fresh burst from the new position
    }
}
