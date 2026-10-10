using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests.Hosting;

/// <summary>
/// WP-14 (C-05): trạng thái armed, tối đa một tick mỗi khung, dừng khi không subscriber - với đồng hồ giả (không thời gian
/// thật). Handle của timer là thật nhưng không ai chờ trên nó ở đây.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FrameClockTests
{
    private static readonly long Period = Stopwatch.Frequency / 60;

    [Fact]
    public void TimerClock_WithoutSubscriberOrRequest_IsNotArmedAndNeverTicks()
    {
        var time = new FakeTime(10_000);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);

        Assert.False(clock.IsArmed);
        Assert.Null(clock.ScheduledFrame);
        Assert.False(clock.OnSignaled());
        Assert.False(clock.OnDeadline());
        Assert.Equal(0, clock.TickCount);
        Assert.Equal(long.MaxValue, clock.TicksUntilDeadline);
    }

    [Fact]
    public void TimerClock_Subscribed_TicksAtMostOncePerFrame()
    {
        var time = new FakeTime(10_000);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);
        var ticks = new List<FrameTick>();
        clock.Frame += (_, e) => ticks.Add(e.Tick);

        Assert.True(clock.IsArmed);
        Assert.Equal(10_000, clock.ScheduledFrame); // khung đầu: ngay

        Assert.True(clock.OnSignaled());
        Assert.Equal(10_000 + Period, clock.ScheduledFrame);
        Assert.False(clock.OnSignaled());           // cùng khung: không tick lần hai
        time.Now += Period / 2;
        Assert.False(clock.OnSignaled());           // nửa khung: chưa
        time.Now = 10_000 + Period;
        Assert.True(clock.OnSignaled());
        Assert.False(clock.OnDeadline());           // timer không có hạn chót riêng

        Assert.Equal([10_000, 10_000 + Period], ticks.Select(t => t.Timestamp));
        Assert.Equal(2, clock.TickCount);
    }

    [Fact]
    public void TimerClock_SignalWithinEarlyTolerance_TicksWithTheScheduledFrameTime()
    {
        var time = new FakeTime(0);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period * 1_000_000);
        var ticks = new List<long>();
        clock.Frame += (_, e) => ticks.Add(e.Tick.Timestamp);
        Assert.True(clock.OnSignaled());
        var due = clock.ScheduledFrame!.Value;

        time.Now = due - TimerFrameClock.EarlyTolerance - 1;
        Assert.False(clock.OnSignaled());
        Assert.Equal(due, clock.ScheduledFrame);    // vẫn hẹn đúng khung đó
        time.Now = due - TimerFrameClock.EarlyTolerance;
        Assert.True(clock.OnSignaled());

        Assert.Equal([0, due], ticks);
    }

    [Fact]
    public void TimerClock_LateSignal_SkipsMissedFramesInsteadOfBursting()
    {
        var time = new FakeTime(0);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);
        var ticks = new List<long>();
        clock.Frame += (_, e) => ticks.Add(e.Tick.Timestamp);
        Assert.True(clock.OnSignaled());

        time.Now = (3 * Period) + (Period / 2);     // UI bận 3,5 khung
        Assert.True(clock.OnSignaled());
        Assert.False(clock.OnSignaled());

        Assert.Equal(time.Now, ticks[^1]);          // neo lại vào hiện tại
        Assert.Equal(time.Now + Period, clock.ScheduledFrame);
    }

    [Fact]
    public void TimerClock_Unsubscribed_DisarmsAndIgnoresAStaleSignal()
    {
        var time = new FakeTime(5);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);
        var count = 0;
        EventHandler<FrameTickEventArgs> handler = (_, _) => count++;
        clock.Frame += handler;
        Assert.True(clock.OnSignaled());

        clock.Frame -= handler;
        time.Now += Period;

        Assert.False(clock.IsArmed);
        Assert.Null(clock.ScheduledFrame);
        Assert.False(clock.OnSignaled());
        Assert.Equal(1, count);
    }

    [Fact]
    public void RequestFrame_WithoutSubscriber_TicksOnceThenDisarms()
    {
        var time = new FakeTime(1_000);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);

        clock.RequestFrame();
        clock.RequestFrame();                       // gộp
        Assert.True(clock.IsArmed);
        Assert.True(clock.OnSignaled());

        Assert.False(clock.IsArmed);
        time.Now += Period;
        Assert.False(clock.OnSignaled());
        Assert.Equal(1, clock.TickCount);
    }

    [Fact]
    public void RequestFrame_FromInsideAHandler_KeepsTheClockArmedForOneMoreFrame()
    {
        var time = new FakeTime(1_000);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);
        var count = 0;
        EventHandler<FrameTickEventArgs>? handler = null;
        handler = (_, _) =>
        {
            count++;
            clock.Frame -= handler;
            if (count == 1)
            {
                clock.RequestFrame();
            }
        };
        clock.Frame += handler;

        Assert.True(clock.OnSignaled());
        Assert.True(clock.IsArmed);
        time.Now += Period;
        Assert.True(clock.OnSignaled());
        Assert.False(clock.IsArmed);
        Assert.Equal(1, count);                     // khung thứ hai không còn subscriber
        Assert.Equal(2, clock.TickCount);
    }

    [Fact]
    public void Frame_ReportsRenderingTimeSinceTheClockStartedAndTheDisplayTiming()
    {
        var time = new FakeTime(1_000);
        var timing = new DisplayTiming(LastVBlank: 900, RefreshPeriod: Period);
        using var clock = new TimerFrameClock(new FixedDisplayClock(timing), 0, time.Read, Period);
        FrameTick? tick = null;
        clock.Frame += (_, e) => tick = e.Tick;

        Assert.True(clock.OnSignaled());

        Assert.NotNull(tick);
        Assert.Equal(timing, tick.Value.Timing);
        Assert.Equal((tick.Value.Timestamp - 1_000) * 1000.0 / Stopwatch.Frequency, tick.Value.RenderingTimeMs, 9);
    }

    [Fact]
    public void TimerClock_WithDisplayTimingAndNoFixedPeriod_AnchorsFramesToTheVBlankGrid()
    {
        var refresh = Stopwatch.Frequency / 100;    // 100 Hz
        var time = new FakeTime(50_000_000);
        var timing = new DisplayTiming(LastVBlank: 50_000_000 - (refresh / 3), RefreshPeriod: refresh);
        using var clock = new TimerFrameClock(new FixedDisplayClock(timing), 0, time.Read, fixedPeriod: null);
        clock.Frame += (_, _) => { };

        var first = clock.ScheduledFrame!.Value;
        Assert.Equal(timing.LastVBlank + refresh, first); // điểm lưới đầu >= bây giờ
        Assert.Equal(refresh, clock.Period);

        time.Now = first;
        Assert.True(clock.OnSignaled());
        Assert.Equal(first + refresh, clock.ScheduledFrame);
    }

    [Fact]
    public void FrameClock_UsedOffItsUiThread_Throws()
    {
        using var clock = new TimerFrameClock(null, 0, new FakeTime(1_000).Read, Period);
        Exception? error = null;
        Exception? request = null;

        // Thread riêng (không phải thread pool): bảo đảm khác thread đã tạo đồng hồ.
        var other = new Thread(() =>
        {
            error = Record.Exception(() => clock.Frame += (_, _) => { });
            request = Record.Exception(clock.RequestFrame);
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(30)));

        Assert.IsType<InvalidOperationException>(error);
        Assert.IsType<InvalidOperationException>(request);
        Assert.False(clock.IsArmed);
    }

    [Fact]
    public void FrameClock_Disposed_DisarmsAndDropsSubscribers()
    {
        var time = new FakeTime(1_000);
        var clock = new TimerFrameClock(null, 0, time.Read, Period);
        clock.Frame += (_, _) => { };

        clock.Dispose();
        clock.Dispose();

        Assert.False(clock.IsArmed);
        Assert.False(clock.OnSignaled());
    }

    [Fact]
    public void FrameHandlerThrows_PropagatesAndTheClockStaysConsistent()
    {
        var time = new FakeTime(1_000);
        using var clock = new TimerFrameClock(null, 0, time.Read, Period);
        clock.Frame += (_, _) => throw new InvalidDataException("render failed");

        Assert.Throws<InvalidDataException>(() => clock.OnSignaled());

        Assert.True(clock.IsArmed);
        Assert.Equal(1_000 + Period, clock.ScheduledFrame); // khung kế vẫn được hẹn
    }

    [Fact]
    public void SwapChainClock_ZeroHandle_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new SwapChainFrameClock(0));
    }

    [Fact]
    public void SwapChainClock_Signal_TicksAndAnEarlyRepeatSignalIsDeferredToTheNextPeriod()
    {
        var time = new FakeTime(100_000);
        using var clock = new SwapChainFrameClock(new nint(1), null, 0, time.Read, Period);
        var ticks = new List<long>();
        clock.Frame += (_, e) => ticks.Add(e.Tick.Timestamp);

        Assert.Equal(100_000 + (2 * Period), clock.DeadlineTimestamp); // dự phòng khi không ai Present
        Assert.True(clock.OnSignaled());
        time.Now += Period / 4;
        Assert.False(clock.OnSignaled());           // báo dồn trong cùng khung
        Assert.Equal(100_000 + Period, clock.DeadlineTimestamp);
        time.Now = 100_000 + Period - 1;
        Assert.False(clock.OnDeadline());
        time.Now = 100_000 + Period;
        Assert.True(clock.OnDeadline());

        Assert.Equal([100_000, 100_000 + Period], ticks);
    }

    [Fact]
    public void SwapChainClock_NoPresentAfterATick_FallsBackToTheTwoPeriodDeadline()
    {
        var time = new FakeTime(0);
        using var clock = new SwapChainFrameClock(new nint(1), null, 0, time.Read, Period);
        clock.Frame += (_, _) => { };
        time.Now = 10;
        Assert.True(clock.OnSignaled());

        Assert.Equal(10 + (2 * Period), clock.DeadlineTimestamp);
        time.Now = 10 + (2 * Period) - 1;
        Assert.False(clock.OnDeadline());
        time.Now = 10 + (2 * Period);
        Assert.True(clock.OnDeadline());
        Assert.Equal(2, clock.TickCount);
    }

    [Fact]
    public void SwapChainClock_NotArmed_HasNoDeadlineAndIgnoresSignals()
    {
        var time = new FakeTime(0);
        using var clock = new SwapChainFrameClock(new nint(1), null, 0, time.Read, Period);

        Assert.Equal(long.MaxValue, clock.DeadlineTimestamp);
        Assert.False(clock.OnSignaled());
        time.Now = long.MaxValue / 2;
        Assert.False(clock.OnDeadline());
    }

    [Fact]
    public void SwapChainClock_WithDisplayTiming_ReportsTheNextVBlankAsFrameTime()
    {
        var time = new FakeTime(10_050);
        var timing = new DisplayTiming(LastVBlank: 10_000, RefreshPeriod: 100);
        using var clock = new SwapChainFrameClock(new nint(1), new FixedDisplayClock(timing), 0, time.Read, fixedPeriod: null);
        long? frame = null;
        clock.Frame += (_, e) => frame = e.Tick.Timestamp;

        Assert.True(clock.OnSignaled());

        Assert.Equal(10_100, frame);
    }

    internal sealed class FakeTime(long start)
    {
        public long Now { get; set; } = start;

        public long Read() => Now;
    }

    private sealed class FixedDisplayClock(DisplayTiming timing) : IDisplayClock
    {
        public DisplayTiming? GetTiming(IntPtr window) => timing;
    }
}
