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

        public int? OpenThreadId { get; set; }

        public readonly HashSet<int> TouchThreads = [];

        public int? DisposeThreadId { get; private set; }

        public Exception? FailWith { get; set; }

        public void Touch()
        {
            lock (TouchThreads)
            {
                TouchThreads.Add(Environment.CurrentManagedThreadId);
            }

            if (FailWith is { } failure)
            {
                throw failure;
            }

            Interlocked.Increment(ref _touches);
        }

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private static void Pause(int ms) => SpinWait.SpinUntil(() => false, ms);

    private static long After(int ms) => Stopwatch.GetTimestamp() + (ms * Stopwatch.Frequency / 1000);

    // Production runs the worker at BelowNormal so it never takes CPU from the UI. Under a saturated CI runner (other test
    // classes spin on every core) a BelowNormal worker can go minutes without a quantum, so these tests run it at Normal:
    // what is under test is the keep-alive logic, not the OS scheduler's treatment of a low-priority thread.
    private const ThreadPriority TestPriority = ThreadPriority.Normal;

    private static GpuKeepAlive Create(FakeWork work, int intervalMs = 5, Func<long>? timestamp = null)
    {
        return new GpuKeepAlive(intervalMs, () =>
        {
            work.OpenThreadId = Environment.CurrentManagedThreadId;
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
        Assert.Null(work.OpenThreadId);
        Assert.Equal(0, keepAlive.Ticks);
        Assert.False(keepAlive.IsDisabled);
    }

    [Fact]
    public void Activation_TouchesRepeatedlyOnABackgroundThread_ThenStopsAtTheDeadline()
    {
        var work = new FakeWork();
        using var keepAlive = Create(work);

        keepAlive.Activate(After(150));

        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 5, Bound), "việc giữ ấm không chạy");
        Assert.True(keepAlive.HasThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, work.OpenThreadId);
        Assert.Equal(work.OpenThreadId, Assert.Single(work.TouchThreads)); // mở và chạm trên cùng một luồng nền
        Pause(450); // quá hạn chót (150 ms) rất xa
        var stopped = work.Touches;
        Pause(200);
        Assert.Equal(stopped, work.Touches); // hết khoảng hiệu lực: không chạm nữa
        Assert.Equal(stopped, keepAlive.Ticks);
        Assert.False(work.IsDisposed); // thiết bị giữ lại cho lần tương tác sau (không mở lại mỗi lần)
    }

    [Fact]
    public void ReactivationAfterTheDeadline_ResumesOnTheSameDevice()
    {
        var work = new FakeWork();
        var opens = 0;
        using var keepAlive = new GpuKeepAlive(5, () =>
        {
            Interlocked.Increment(ref opens);
            return work;
        }, priority: TestPriority);

        keepAlive.Activate(After(60));
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound));
        Pause(250);
        var before = work.Touches;

        keepAlive.Activate(After(200));

        Assert.True(SpinWait.SpinUntil(() => work.Touches >= before + 3, Bound), "không chạy lại sau khi kích hoạt lần hai");
        Assert.Equal(1, opens);
    }

    [Fact]
    public void ActivationNeverShortensAnEarlierDeadline()
    {
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 5);

        keepAlive.Activate(After(1500));
        keepAlive.Activate(After(1)); // hạn chót cũ hơn không được rút ngắn
        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound));
        Pause(300);
        var a = work.Touches;
        Pause(100);

        Assert.True(work.Touches > a, "khoảng giữ ấm bị rút ngắn bởi hạn chót cũ hơn");
    }

    [Fact]
    public void IntervalIsRespected_NotABusyLoop()
    {
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 50);

        keepAlive.Activate(After(400));
        Pause(450);

        // 400 ms / 50 ms = ~8 lần; vòng không ngủ sẽ ra hàng chục nghìn.
        Assert.InRange(work.Touches, 3, 14);
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
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: -5);

        keepAlive.Activate(After(300));
        Pause(450);

        // 300 ms / 1 ms = tối đa ~300; không kẹp thì hàng chục nghìn.
        Assert.InRange(work.Touches, 2, 500);
    }

    [Fact]
    public void HugeInterval_IsClampedToOneSecond_SoTheDeviceStillGetsTouched()
    {
        var work = new FakeWork();
        using var keepAlive = Create(work, intervalMs: 60_000);

        keepAlive.Activate(After(2500));
        Pause(2900);

        // Kẹp về 1000 ms: chạm ở ~0, ~1 s, ~2 s. Không kẹp thì chạm đúng một lần rồi ngủ 60 s.
        Assert.True(work.Touches >= 2, $"chỉ {work.Touches} lần chạm trong 2,9 s");
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
        Assert.Equal(work.OpenThreadId, work.DisposeThreadId); // thiết bị chết nơi nó sinh ra
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
        Assert.Null(work.OpenThreadId);
    }

    [Fact]
    public void Constructor_RejectsNullFactory() =>
        Assert.Throws<ArgumentNullException>(() => new GpuKeepAlive(20, null!));
}
