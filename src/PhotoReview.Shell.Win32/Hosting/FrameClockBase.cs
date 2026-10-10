using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// Phần chung của C-05 trên Win32: trạng thái "armed" (có subscriber của <see cref="Frame"/> hoặc <see cref="RequestFrame"/>
/// đang chờ) và việc phát một <see cref="FrameTick"/>. Nguồn nhịp (waitable object + hạn chót) do lớp con cấp;
/// <see cref="MessageLoop"/> chỉ chờ trên đồng hồ đang armed, nên không subscriber = không đánh thức, không tốn CPU.
/// Chỉ dùng trên UI thread (thread tạo nó). Ngoại lệ của handler <see cref="Frame"/> lan ra vòng lặp (như WPF Rendering).
/// </summary>
internal abstract class FrameClockBase : IFrameClock, IDisposable
{
    private readonly int _threadId;
    private readonly IDisplayClock? _displayClock;
    private readonly nint _window;
    private readonly Func<long> _now;
    private readonly long _epoch;
    private EventHandler<FrameTickEventArgs>? _frame;
    private bool _requested;
    private bool _armed;
    private bool _disposed;

    protected FrameClockBase(IDisplayClock? displayClock, nint window, Func<long>? timestamp)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _displayClock = displayClock;
        _window = window;
        _now = timestamp ?? Stopwatch.GetTimestamp;
        _epoch = _now();
    }

    public event EventHandler<FrameTickEventArgs>? Frame
    {
        add
        {
            VerifyAccess();
            _frame += value;
            UpdateArmed();
        }

        remove
        {
            VerifyAccess();
            _frame -= value;
            UpdateArmed();
        }
    }

    /// <summary>Có subscriber hoặc yêu cầu khung đang chờ: vòng lặp chờ trên nguồn nhịp của đồng hồ này.</summary>
    internal bool IsArmed => _armed;

    /// <summary>Số khung đã phát (chẩn đoán/test).</summary>
    internal long TickCount { get; private set; }

    /// <summary>Handle để <c>MsgWaitForMultipleObjectsEx</c> chờ khi armed; 0 = không có.</summary>
    internal abstract nint WaitHandle { get; }

    /// <summary>Stopwatch ticks mà vòng lặp phải gọi <see cref="OnDeadline"/> dù handle chưa báo; MaxValue = không có.</summary>
    internal virtual long DeadlineTimestamp => long.MaxValue;

    /// <summary>Stopwatch ticks còn lại tới <see cref="DeadlineTimestamp"/> theo đồng hồ của chính đồng hồ khung; MaxValue = không có.</summary>
    internal long TicksUntilDeadline
    {
        get
        {
            var deadline = DeadlineTimestamp;
            return deadline == long.MaxValue ? long.MaxValue : deadline - _now();
        }
    }

    protected long Now() => _now();

    /// <summary>Nhịp hiển thị hiện tại của màn hình chứa cửa sổ, nếu biết.</summary>
    protected DisplayTiming? CurrentTiming() => _displayClock?.GetTiming(_window);

    public void RequestFrame()
    {
        VerifyAccess();
        _requested = true;
        UpdateArmed();
    }

    /// <summary>Handle của đồng hồ đã báo (vòng lặp gọi). Phát khung nếu đúng lúc; true nếu đã phát.</summary>
    internal bool OnSignaled() => TryTick(signaled: true);

    /// <summary>Đã tới <see cref="DeadlineTimestamp"/> (vòng lặp gọi). True nếu đã phát.</summary>
    internal bool OnDeadline() => TryTick(signaled: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frame = null;
        _requested = false;
        UpdateArmed();
        DisposeCore();
    }

    /// <summary>Quyết định nhịp: true để phát với <paramref name="frameTimestamp"/> (Stopwatch ticks của khung).</summary>
    protected abstract bool ShouldTick(long now, bool signaled, out long frameTimestamp);

    /// <summary>Bắt đầu/dừng nguồn nhịp (bật/tắt timer).</summary>
    protected abstract void OnArmedChanged(bool armed, long now);

    /// <summary>Sau khi phát một khung mà vẫn armed: hẹn khung kế.</summary>
    protected virtual void OnTicked(long frameTimestamp, long now)
    {
    }

    protected virtual void DisposeCore()
    {
    }

    protected void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("A frame clock must be used on its UI thread.");
        }
    }

    private bool TryTick(bool signaled)
    {
        VerifyAccess();
        if (!_armed)
        {
            return false;
        }

        var now = _now();
        if (!ShouldTick(now, signaled, out var frameTimestamp))
        {
            return false;
        }

        _requested = false;
        TickCount++;
        var tick = new FrameTick(frameTimestamp, (frameTimestamp - _epoch) * 1000.0 / Stopwatch.Frequency, CurrentTiming());
        try
        {
            _frame?.Invoke(this, new FrameTickEventArgs(tick));
        }
        finally
        {
            UpdateArmed();
            if (_armed)
            {
                OnTicked(frameTimestamp, _now());
            }
        }

        return true;
    }

    private void UpdateArmed()
    {
        var armed = !_disposed && (_frame is not null || _requested);
        if (armed == _armed)
        {
            return;
        }

        _armed = armed;
        OnArmedChanged(armed, _now());
    }

    /// <summary>Điểm lưới vblank đầu tiên &gt;= <paramref name="at"/>.</summary>
    protected static long NextGridPoint(DisplayTiming timing, long at)
    {
        if (timing.RefreshPeriod <= 0 || at <= timing.LastVBlank)
        {
            return Math.Max(at, timing.LastVBlank);
        }

        var periods = (at - timing.LastVBlank + timing.RefreshPeriod - 1) / timing.RefreshPeriod;
        return timing.LastVBlank + (periods * timing.RefreshPeriod);
    }
}
