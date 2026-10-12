using System.Diagnostics;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP21-STALL: luồng giữ GPU ấm. Nguyên nhân gốc (perf/2026-10-12-wp21-stall.md): GPU Intel rỗi &gt; ~25 ms thì lần nộp việc đầu bị chặn
/// ~0,47 s; nộp việc tí hon cách &lt;= 25 ms từ luồng riêng thì UI không bao giờ gặp. Ở đây kiểm LOGIC (luồng, khoảng, tự tắt, dọn dẹp)
/// bằng việc giả; thiết bị thật ở <see cref="DeviceKeepAliveNativeTests"/>.
/// </summary>
public sealed class GpuKeepAliveTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class FakeWork : IGpuKeepAliveWork
    {
        private int _touches;
        private int _disposed;

        public int Touches => Volatile.Read(ref _touches);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public Thread? OpenThread { get; set; }

        public readonly HashSet<Thread> TouchThreads = [];

        public Thread? DisposeThread { get; private set; }

        public Exception? FailWith { get; set; }

        public void Touch()
        {
            lock (TouchThreads)
            {
                TouchThreads.Add(Thread.CurrentThread);
            }

            if (FailWith is { } failure)
            {
                throw failure;
            }

            Interlocked.Increment(ref _touches);
        }

        public void Dispose()
        {
            DisposeThread = Thread.CurrentThread;
            Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private static void Pause(int ms) => SpinWait.SpinUntil(() => false, ms);

    private static long After(int ms) => Stopwatch.GetTimestamp() + (ms * Stopwatch.Frequency / 1000);

    // Fake clock (see AfterTheDeadline_...): the keep-alive window is opened and closed by the test, never by wall time, so a
    // starved worker (12 busy threads) cannot "miss" a real-time window. Threads are compared as objects, never by ManagedThreadId
    // (ids of dead threads are recycled).
    private sealed class FakeClock(long start)
    {
        private long _now = start;

        public long Now => Volatile.Read(ref _now);

        public Func<long> Timestamp => () => Volatile.Read(ref _now);

        public void Set(long value) => Volatile.Write(ref _now, value);
    }

    /// <summary>The worker went back to sleep on its event after a window closed (observed state, not elapsed time).</summary>
    private static bool Idle(GpuKeepAlive keepAlive, long idleWaits) =>
        SpinWait.SpinUntil(() => keepAlive.IdleWaits >= idleWaits && keepAlive.IsBlocked, Bound);

    // Production runs the worker at BelowNormal so it never takes CPU from the UI. Under a saturated CI runner (other test
    // classes spin on every core) a BelowNormal worker can go minutes without a quantum, so these tests run it at Normal:
    // what is under test is the keep-alive logic, not the OS scheduler's treatment of a low-priority thread.
    private const ThreadPriority TestPriority = ThreadPriority.Normal;

    private static GpuKeepAlive Create(FakeWork work, int intervalMs = 5, Func<long>? timestamp = null)
    {
        return new GpuKeepAlive(intervalMs, () =>
        {
            work.OpenThread = Thread.CurrentThread;
            return work;
        }, timestamp, TestPriority);
    }

    [Fact]
    public void NothingRunsUntilTheFirstActivation()
    {
        var work = new FakeWork();
        using var keepAlive = Create(work);

        Pause(50);

        Assert.False(keepAlive.HasThread);
        Assert.Null(work.OpenThread);
        Assert.Equal(0, keepAlive.Ticks);
        Assert.False(keepAlive.IsDisabled);
    }

    [Fact]
    public void Activation_TouchesRepeatedlyOnABackgroundThread_ThenStopsAtTheDeadline()
    {
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        using var keepAlive = Create(work, timestamp: clock.Timestamp);

        keepAlive.Activate(1_100);

        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 5, Bound), "việc giữ ấm không chạy");
        Assert.True(keepAlive.HasThread);
        Assert.NotSame(Thread.CurrentThread, work.OpenThread);
        Assert.Same(work.OpenThread, Assert.Single(work.TouchThreads)); // mở và chạm trên cùng một luồng nền
        clock.Set(2_000); // quá hạn chót
        Assert.True(Idle(keepAlive, 2), "hết khoảng hiệu lực mà luồng giữ ấm không ngủ");
        var stopped = work.Touches;
        // Negative check only (cannot fail spuriously): a worker that kept touching would move within this window.
        Assert.False(SpinWait.SpinUntil(() => work.Touches != stopped, 50), "vẫn chạm sau hạn chót");
        Assert.Equal(stopped, keepAlive.Ticks);
        Assert.False(work.IsDisposed); // thiết bị giữ lại cho lần tương tác sau (không mở lại mỗi lần)
    }

    [Fact]
    public void ReactivationAfterTheDeadline_ResumesOnTheSameDevice()
    {
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        var opens = 0;
        using var keepAlive = new GpuKeepAlive(5, () =>
        {
            Interlocked.Increment(ref opens);
            return work;
        }, clock.Timestamp, TestPriority);

        keepAlive.Activate(1_100);
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound));
        clock.Set(2_000); // the first window is over
        Assert.True(Idle(keepAlive, 2), "luồng giữ ấm không ngủ sau hạn chót");
        var before = work.Touches;

        keepAlive.Activate(2_100);

        Assert.True(SpinWait.SpinUntil(() => work.Touches >= before + 3, Bound), "không chạy lại sau khi kích hoạt lần hai");
        Assert.Equal(1, opens);
    }

    [Fact]
    public void ActivationNeverShortensAnEarlierDeadline()
    {
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 5, timestamp: clock.Timestamp);

        keepAlive.Activate(1_500);
        keepAlive.Activate(1); // hạn chót cũ hơn không được rút ngắn (đã quá hạn theo đồng hồ giả)
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound));
        var a = work.Touches;

        Assert.True(SpinWait.SpinUntil(() => work.Touches > a + 2, Bound), "khoảng giữ ấm bị rút ngắn bởi hạn chót cũ hơn");
    }

    [Fact]
    public void IntervalIsRespected_NotABusyLoop()
    {
        // The clock never reaches the deadline. A loaded machine can only make the worker SLOWER, so an upper bound on the touch
        // count per elapsed wall time cannot fail spuriously; a worker that does not sleep its interval blows through it at once.
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 50, timestamp: clock.Timestamp);
        var watch = Stopwatch.StartNew();

        keepAlive.Activate(long.MaxValue);
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 6, Bound), "việc giữ ấm không chạy");
        var touches = work.Touches; // read before the elapsed time: a later reading only loosens the bound
        var elapsedMs = watch.ElapsedMilliseconds;

        // One touch at once, then one per 50 ms (+1 for timer rounding); a busy loop has tens of thousands by now.
        Assert.True(touches <= (elapsedMs / 50) + 2, $"{touches} touches in {elapsedMs} ms at a 50 ms interval");
    }

    [Fact]
    public void AfterTheDeadline_TheThreadSleepsInsteadOfSpinning()
    {
        // Deterministic: an injected clock replaces wall time and the sleeping state is observed directly (the worker is
        // blocked on its event) instead of being inferred from CPU time, which a loaded CI runner makes meaningless.
        var now = 1_000L;
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 5, timestamp: () => Volatile.Read(ref now));

        keepAlive.Activate(1_100); // window still open: the worker touches repeatedly
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound), "việc giữ ấm không chạy trong khoảng hiệu lực");

        Volatile.Write(ref now, 2_000); // the deadline has passed
        Assert.True(SpinWait.SpinUntil(() => keepAlive.IdleWaits >= 2 && keepAlive.IsBlocked, Bound),
            "hết hạn mà luồng giữ ấm không ngủ trên event (vòng bận?)");
        var touches = work.Touches;
        Assert.Equal(2, keepAlive.IdleWaits); // một lần chờ đầu + một lần chờ sau hạn chót, không hơn
        // Negative check only (cannot fail spuriously): a worker that polls on a timer would wake again within this window.
        Assert.False(SpinWait.SpinUntil(() => keepAlive.IdleWaits != 2, 50), "luồng giữ ấm tự thức dậy khi chưa được kích hoạt");

        keepAlive.Activate(1_500); // a deadline already in the past: exactly one wake-up, no touch, back to sleep
        Assert.True(SpinWait.SpinUntil(() => keepAlive.IdleWaits >= 3 && keepAlive.IsBlocked, Bound),
            "luồng giữ ấm không quay lại ngủ sau lần kích hoạt đã quá hạn");
        Assert.Equal(3, keepAlive.IdleWaits);
        Assert.Equal(touches, work.Touches);
        Assert.Equal(touches, keepAlive.Ticks);
    }

    [Fact]
    public void NonPositiveInterval_IsClampedToOneMillisecond_NotABusyLoop()
    {
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: -5, timestamp: clock.Timestamp);
        var watch = Stopwatch.StartNew();

        keepAlive.Activate(long.MaxValue);
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 40, Bound), "việc giữ ấm không chạy");
        var touches = work.Touches;
        var elapsedMs = watch.ElapsedMilliseconds;

        // Clamped to 1 ms: at most one touch per millisecond (+slack). Not clamped, 40 touches take microseconds.
        Assert.True(touches <= elapsedMs + 5, $"{touches} touches in {elapsedMs} ms: the interval is not clamped to 1 ms");
    }

    [Fact]
    public void HugeInterval_IsClampedToOneSecond_SoTheDeviceStillGetsTouched()
    {
        var clock = new FakeClock(1_000);
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 60_000, timestamp: clock.Timestamp);

        keepAlive.Activate(long.MaxValue);

        // Clamped to 1000 ms the second touch comes after ~1 s; unclamped it would come after 60 s, far beyond the bound.
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 2, Bound), $"chỉ {work.Touches} lần chạm: khoảng chạm không bị kẹp về 1 s");
    }

    [Fact]
    public void NoGpu_DisablesItselfWithoutThrowing()
    {
        using var keepAlive = new GpuKeepAlive(5, () => null, priority: TestPriority);

        keepAlive.Activate(After(500));

        Assert.True(SpinWait.SpinUntil(() => keepAlive.IsDisabled, Bound));
        Assert.Equal(0, keepAlive.Ticks);
        keepAlive.Activate(After(500)); // sau khi tắt: no-op
        Assert.Equal(0, keepAlive.Ticks);
    }

    [Fact]
    public void FactoryThatThrows_DisablesItselfWithoutThrowing()
    {
        using var keepAlive = new GpuKeepAlive(5, () => throw new InvalidOperationException("no device"), priority: TestPriority);

        keepAlive.Activate(After(500));

        Assert.True(SpinWait.SpinUntil(() => keepAlive.IsDisabled, Bound));
    }

    [Fact]
    public void TouchFailure_DisablesAndDisposesTheDevice_OnTheBackgroundThread()
    {
        var work = new FakeWork { FailWith = new InvalidOperationException("device removed") };
        using var keepAlive = Create(work);

        keepAlive.Activate(After(5000));

        Assert.True(SpinWait.SpinUntil(() => keepAlive.IsDisabled && work.IsDisposed, Bound));
        Assert.Equal(0, work.Touches);
        Assert.Same(work.OpenThread, work.DisposeThread); // thiết bị chết nơi nó sinh ra
        Assert.Equal(0, keepAlive.Ticks);
    }

    [Fact]
    public void Dispose_StopsTheThread_DisposesTheDevice_AndLaterActivationsAreNoOps()
    {
        var work = new FakeWork();
        var keepAlive = Create(work);
        keepAlive.Activate(After(60_000));
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 2, Bound));

        keepAlive.Dispose();

        Assert.True(work.IsDisposed, "Dispose phải chờ luồng giữ ấm trả thiết bị");
        var touches = work.Touches;
        keepAlive.Activate(After(60_000));
        Pause(100);
        Assert.Equal(touches, work.Touches);
        Assert.True(keepAlive.IsDisabled);
        keepAlive.Dispose(); // idempotent
    }

    [Fact]
    public void Dispose_BeforeAnyActivation_DoesNothing()
    {
        var work = new FakeWork();
        var keepAlive = Create(work);

        keepAlive.Dispose();
        keepAlive.Activate(After(1000));

        Assert.False(keepAlive.HasThread);
        Assert.Null(work.OpenThread);
    }

    [Fact]
    public void Constructor_RejectsNullFactory() =>
        Assert.Throws<ArgumentNullException>(() => new GpuKeepAlive(20, null!));
}
