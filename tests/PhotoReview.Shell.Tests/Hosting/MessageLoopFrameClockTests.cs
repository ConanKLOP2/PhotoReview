using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests.Hosting;

/// <summary>
/// WP-14 (C-05 + vòng lặp): đồng hồ khung chạy thật trong <see cref="MessageLoop"/> - tick trên UI thread, không quá một lần
/// mỗi chu kỳ, dừng khi hết subscriber (đo bằng một đồng hồ "nhân chứng" chạy song song, không chờ thời gian cố định), và
/// handle của đồng hồ không armed không bị vòng lặp chờ/tiêu.
/// </summary>
[Trait("Category", "UI")]
public sealed class MessageLoopFrameClockTests
{
    private static readonly long Period = Stopwatch.Frequency / 100; // 10 ms

    [Fact]
    public async Task TimerClock_InTheLoop_TicksOnTheUiThreadAtMostOncePerPeriod()
    {
        await using var ui = await UiThread.StartAsync();
        var ticks = new List<(long FrameTimestamp, long Observed, int Thread)>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = 0;

        await ui.InvokeAsync(() =>
        {
            var clock = new TimerFrameClock(null, 0, null, Period);
            ui.Loop.AddFrameClock(clock);
            started = Stopwatch.GetTimestamp();
            clock.Frame += (_, e) =>
            {
                ticks.Add((e.Tick.Timestamp, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
                if (ticks.Count == 20)
                {
                    enough.TrySetResult();
                }
            };
        });
        await enough.Task.WaitAsync(UiThread.Bound);

        var snapshot = await ui.InvokeAsync(() => ticks.ToArray());
        var elapsed = snapshot[^1].Observed - started;
        Assert.All(snapshot, t => Assert.Equal(ui.ManagedThreadId, t.Thread));
        for (var i = 1; i < snapshot.Length; i++)
        {
            Assert.True(snapshot[i].FrameTimestamp - snapshot[i - 1].FrameTimestamp >= Period - TimerFrameClock.EarlyTolerance,
                $"frames {i - 1} and {i} are {snapshot[i].FrameTimestamp - snapshot[i - 1].FrameTimestamp} ticks apart (period {Period})");
        }

        // Không phát dồn: số tick không vượt số chu kỳ đã trôi qua (+1 cho tick đầu ngay lập tức).
        Assert.True(snapshot.Length <= (elapsed / Period) + 2, $"{snapshot.Length} ticks in {elapsed} ticks of time (period {Period})");
    }

    [Fact]
    public async Task TimerClock_LastSubscriberRemoved_StopsTickingWhileAWitnessClockKeepsGoing()
    {
        await using var ui = await UiThread.StartAsync();
        TimerFrameClock? subject = null;
        TimerFrameClock? witness = null;
        var subjectTicksAfterUnsubscribe = -1L;
        var witnessTicks = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await ui.InvokeAsync(() =>
        {
            subject = new TimerFrameClock(null, 0, null, Period);
            witness = new TimerFrameClock(null, 0, null, Period);
            ui.Loop.AddFrameClock(subject);
            ui.Loop.AddFrameClock(witness);

            EventHandler<FrameTickEventArgs>? onSubject = null;
            onSubject = (_, _) =>
            {
                if (subject.TickCount >= 3)
                {
                    subject.Frame -= onSubject;
                    subjectTicksAfterUnsubscribe = subject.TickCount;
                    witness.Frame += (_, _) =>
                    {
                        if (++witnessTicks == 5)
                        {
                            done.TrySetResult(); // >= 4 chu kỳ đã trôi qua kể từ khi bỏ đăng ký
                        }
                    };
                }
            };
            subject.Frame += onSubject;
        });
        await done.Task.WaitAsync(UiThread.Bound);

        var (ticks, armed, scheduled) = await ui.InvokeAsync(() => (subject!.TickCount, subject.IsArmed, subject.ScheduledFrame));
        Assert.Equal(subjectTicksAfterUnsubscribe, ticks);
        Assert.False(armed);
        Assert.Null(scheduled);
    }

    [Fact]
    public async Task SwapChainClock_InTheLoop_TicksWhenItsHandleIsSignaled_AndADisarmedHandleIsNotConsumed()
    {
        await using var ui = await UiThread.StartAsync();
        using var frameLatency = new Semaphore(0, 16);
        var handle = frameLatency.SafeWaitHandle.DangerousGetHandle();
        // Chu kỳ dài: hạn chót dự phòng (2 chu kỳ = 20 s) không thể là nguồn của tick trong test này.
        var longPeriod = Stopwatch.Frequency * 10;
        SwapChainFrameClock? clock = null;
        var firstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FrameTickEventArgs> onFrame = (_, _) => firstTick.TrySetResult();

        await ui.InvokeAsync(() =>
        {
            clock = new SwapChainFrameClock(handle, null, 0, null, longPeriod);
            ui.Loop.AddFrameClock(clock);
            clock.Frame += onFrame;
        });
        frameLatency.Release();
        await firstTick.Task.WaitAsync(UiThread.Bound);
        Assert.False(frameLatency.WaitOne(0)); // vòng lặp đã tiêu tín hiệu

        await ui.InvokeAsync(() => clock!.Frame -= onFrame);
        frameLatency.Release();
        await WaitForWitnessTicksAsync(ui, 3);

        Assert.Equal(1, await ui.InvokeAsync(() => clock!.TickCount));
        Assert.True(frameLatency.WaitOne(0)); // đồng hồ không armed: vòng lặp không chờ/tiêu handle của nó
    }

    [Fact]
    public async Task RequestFrame_InTheLoop_ProducesExactlyOneTick()
    {
        await using var ui = await UiThread.StartAsync();
        TimerFrameClock? clock = null;

        await ui.InvokeAsync(() =>
        {
            clock = new TimerFrameClock(null, 0, null, Period);
            ui.Loop.AddFrameClock(clock);
            clock.RequestFrame();
            clock.RequestFrame();
        });
        await WaitForWitnessTicksAsync(ui, 3);

        Assert.Equal(1, await ui.InvokeAsync(() => clock!.TickCount));
        Assert.False(await ui.InvokeAsync(() => clock!.IsArmed));
    }

    [Fact]
    public async Task IdleLoop_BlocksInMsgWaitInsteadOfSpinning()
    {
        await using var ui = await UiThread.StartAsync();
        var before = await ui.InvokeAsync(() => (ui.Loop.PassCount, ui.Loop.WaitCount));

        await WaitForWitnessTicksAsync(ui, 10); // >= 9 chu kỳ 10 ms trôi qua

        var after = await ui.InvokeAsync(() => (ui.Loop.PassCount, ui.Loop.WaitCount));
        // Mỗi tick đánh thức vòng lặp vài lượt (tick + message của test); vòng lặp quay rỗng sẽ có hàng triệu lượt.
        Assert.InRange(after.PassCount - before.PassCount, 1, 100);
        Assert.True(after.WaitCount - before.WaitCount >= 9, $"loop blocked only {after.WaitCount - before.WaitCount} times");
    }

    /// <summary>Chờ (có giới hạn) cho tới khi một đồng hồ khác trên cùng vòng lặp tick đủ <paramref name="ticks"/> lần.</summary>
    private static async Task WaitForWitnessTicksAsync(UiThread ui, int ticks)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ui.InvokeAsync(() =>
        {
            var witness = new TimerFrameClock(null, 0, null, Period);
            ui.Loop.AddFrameClock(witness);
            var count = 0;
            EventHandler<FrameTickEventArgs>? handler = null;
            handler = (_, _) =>
            {
                if (++count == ticks)
                {
                    witness.Frame -= handler;
                    ui.Loop.RemoveFrameClock(witness);
                    witness.Dispose();
                    done.TrySetResult();
                }
            };
            witness.Frame += handler;
        });
        await done.Task.WaitAsync(UiThread.Bound);
    }
}
