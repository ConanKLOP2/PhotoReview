using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

[Trait("Category", "HotPath")]
public sealed class StartupCloakTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void Post(Action action, UiPriority priority) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Action action, UiPriority priority) => InvokeAsync(action);

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Recorder
    {
        public List<(nint Hwnd, bool Cloaked)> Calls { get; } = [];

        public bool Accept { get; set; } = true;

        public bool Set(nint hwnd, bool cloaked)
        {
            lock (Calls)
            {
                Calls.Add((hwnd, cloaked));
            }

            return Accept;
        }
    }

    [Fact]
    public void Hide_CloaksTheWindowOnceAndKeepsItHiddenUntilTheFirstPresent()
    {
        var recorder = new Recorder();
        using var cloak = new StartupCloak(new InlineDispatcher(), setCloaked: recorder.Set);

        cloak.Hide(42);
        cloak.Hide(42);

        Assert.True(cloak.IsHidden);
        Assert.Equal([((nint)42, true)], recorder.Calls);
    }

    [Fact]
    public void NoteFirstPresent_UncloaksOnceAndReportsTheReveal()
    {
        var recorder = new Recorder();
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set);
        cloak.Hide(42);

        cloak.NoteFirstPresent();
        cloak.NoteFirstPresent();

        Assert.False(cloak.IsHidden);
        Assert.True(cloak.IsRevealed);
        Assert.Equal(1, revealed);
        Assert.Equal([((nint)42, true), ((nint)42, false)], recorder.Calls);
    }

    [Fact]
    public void Hide_WhenDwmRefuses_LeavesTheWindowVisibleAndNeverReports()
    {
        var recorder = new Recorder { Accept = false };
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set);

        cloak.Hide(42);
        cloak.NoteFirstPresent();

        Assert.False(cloak.IsHidden);
        Assert.Equal(0, revealed);
        Assert.Single(recorder.Calls); // không có lời gọi uncloak thừa
    }

    [Fact]
    public void Hide_WithNoHandleOrAfterReveal_DoesNothing()
    {
        var recorder = new Recorder();
        using var cloak = new StartupCloak(new InlineDispatcher(), setCloaked: recorder.Set);

        cloak.Hide(0);
        cloak.Reveal();
        cloak.Hide(42);

        Assert.Empty(recorder.Calls);
        Assert.False(cloak.IsHidden);
    }

    /// <summary>
    /// Bộ hẹn giờ tiêm được: không đồng hồ, không thread pool. Test tự "đến hạn" bằng <see cref="Fire"/>; Dispose = huỷ như
    /// <see cref="System.Threading.Timer"/> (việc đã huỷ không bao giờ chạy).
    /// </summary>
    private sealed class ManualScheduler
    {
        private Action? _callback;

        public List<TimeSpan> Delays { get; } = [];

        public bool Disposed { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            Delays.Add(delay);
            _callback = callback;
            return new Handle(this);
        }

        /// <summary>Đến hạn: chạy việc đã hẹn nếu chưa bị huỷ (false = đã huỷ hoặc chưa hẹn).</summary>
        public bool Fire()
        {
            if (Disposed || _callback is null)
            {
                return false;
            }

            _callback();
            return true;
        }

        private sealed class Handle(ManualScheduler owner) : IDisposable
        {
            public void Dispose() => owner.Disposed = true;
        }
    }

    // Trước đây test này dùng Timer thật 50 ms + chờ tối đa 10 s: CI (4 vCPU, nhiều test chặn thread pool) từng làm callback của
    // timer trễ quá 10 s -> đỏ ngẫu nhiên. Bộ hẹn tiêm được làm test tất định mà vẫn bắt lỗi "hạn chót không được hẹn / không bỏ cloak".
    [Fact]
    public void SafetyTimeout_RevealsAWindowThatNeverGetsAFrame()
    {
        var recorder = new Recorder();
        var scheduler = new ManualScheduler();
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set,
            safetyTimeout: TimeSpan.FromMilliseconds(50), scheduleTimeout: scheduler.Schedule);

        cloak.Hide(7);

        Assert.Equal([TimeSpan.FromMilliseconds(50)], scheduler.Delays);
        Assert.True(cloak.IsHidden); // chưa đến hạn thì vẫn cloak
        Assert.True(scheduler.Fire());

        Assert.Equal(1, revealed);
        Assert.False(cloak.IsHidden);
        Assert.True(cloak.IsRevealed);
        Assert.Equal([((nint)7, true), ((nint)7, false)], recorder.Calls);
    }

    [Fact]
    public void SafetyTimeout_AfterTheFirstPresent_DoesNotRevealASecondTime()
    {
        var recorder = new Recorder();
        var scheduler = new ManualScheduler();
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set,
            scheduleTimeout: scheduler.Schedule);
        cloak.Hide(7);

        cloak.NoteFirstPresent();

        Assert.True(scheduler.Disposed); // khung đầu đã tới: hạn chót bị huỷ
        Assert.False(scheduler.Fire());
        Assert.Equal(1, revealed);
        Assert.Equal([((nint)7, true), ((nint)7, false)], recorder.Calls);
    }

    [Fact]
    public void Stop_CancelsTheSafetyTimeoutSoAClosedWindowIsNeverTouched()
    {
        var recorder = new Recorder();
        var scheduler = new ManualScheduler();
        var revealed = 0;
        var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set,
            scheduleTimeout: scheduler.Schedule);
        cloak.Hide(7);

        cloak.Stop();

        Assert.True(scheduler.Disposed);
        Assert.False(scheduler.Fire(), "hạn chót đã bị huỷ không được bỏ cloak");
        Assert.Equal(0, revealed);
        Assert.True(cloak.IsHidden); // vẫn cloak: cửa sổ đang đóng, hạn chót không được đụng vào nó
        Assert.Single(recorder.Calls);
    }

    /// <summary>
    /// Đường production (System.Threading.Timer thật): chỉ chạy ở -Slow vì cần thread pool rảnh; ở CI ngưỡng chờ không đủ chắc
    /// (xem ghi chú ở trên). Phần logic hạn chót đã được phủ tất định bằng bộ hẹn tiêm được.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void SafetyTimeout_WithTheRealTimer_RevealsAWindowThatNeverGetsAFrame()
    {
        var recorder = new Recorder();
        using var revealed = new ManualResetEventSlim();
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: revealed.Set, setCloaked: recorder.Set,
            safetyTimeout: TimeSpan.FromMilliseconds(50));

        cloak.Hide(7);

        Assert.True(revealed.Wait(TimeSpan.FromSeconds(30)), "hạn chót an toàn không bỏ cloak");
        Assert.False(cloak.IsHidden);
    }

    [Fact]
    public void SafetyTimeoutDefault_IsThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), StartupCloak.SafetyTimeout);
    }
}
