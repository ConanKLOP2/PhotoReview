using System.Diagnostics;
using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: hẹn giờ một lần theo <see cref="IFrameClock"/> (thay <c>DispatcherTimer</c> của auto-hide trong <c>MainWindow</c>).
/// Chỉ đăng ký nghe đồng hồ khung khi đang đếm (hết giờ/<see cref="Stop"/> thì ngừng, vòng khung được nghỉ). Hạn chót tính
/// bằng Stopwatch ticks tại lúc <see cref="Start"/>; chạm hạn ở khung đầu tiên có <c>Timestamp</c> &gt;= hạn chót
/// (độ phân giải = một khung). Test tiêm <c>timestamp</c> và <c>ticksPerSecond</c>. UI thread.
/// </summary>
internal sealed class FrameDelayTimer : IDisposable
{
    private readonly IFrameClock _clock;
    private readonly Action _elapsed;
    private readonly Func<long> _timestamp;
    private readonly long _ticksPerSecond;
    private long _deadline;
    private bool _running;
    private bool _disposed;

    public FrameDelayTimer(IFrameClock clock, Action elapsed, Func<long>? timestamp = null, long? ticksPerSecond = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elapsed);
        if (ticksPerSecond is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        }

        _clock = clock;
        _elapsed = elapsed;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _ticksPerSecond = ticksPerSecond ?? Stopwatch.Frequency;
    }

    public bool IsRunning => _running;

    /// <summary>Đếm lại từ bây giờ (đang chạy thì đặt lại hạn chót).</summary>
    public void Start(TimeSpan delay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long ticks = (long)Math.Ceiling(Math.Max(0, delay.TotalSeconds) * _ticksPerSecond);
        _deadline = _timestamp() + ticks;
        if (!_running)
        {
            _running = true;
            _clock.Frame += OnFrame;
        }

        _clock.RequestFrame();
    }

    public void Stop()
    {
        if (_running)
        {
            _running = false;
            _clock.Frame -= OnFrame;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void OnFrame(object? sender, FrameTickEventArgs e)
    {
        if (!_running || e.Tick.Timestamp < _deadline)
        {
            return;
        }

        Stop();
        _elapsed();
    }
}
