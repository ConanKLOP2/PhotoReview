using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// C-05 dự phòng (không có swap chain waitable: WARP/RDP, trước khi surface sẵn sàng, test): một waitable timer độ phân giải
/// cao (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, rơi về timer thường) hẹn đúng một lần mỗi khung. Chu kỳ = chu kỳ làm
/// tươi của màn hình (<see cref="IDisplayClock"/>) nếu biết, ngược lại 60 Hz; khung được neo vào lưới vblank khi biết.
/// Bị trễ thì bỏ khung lỡ (không phát dồn), nên không bao giờ quá một tick mỗi chu kỳ. Không armed = timer huỷ, không đánh thức.
/// </summary>
internal sealed unsafe class TimerFrameClock : FrameClockBase
{
    /// <summary>Tín hiệu sớm hơn hẹn tối đa chừng này vẫn tính là đúng khung (lệch nhỏ giữa QPC và đồng hồ timer).</summary>
    internal static readonly long EarlyTolerance = Stopwatch.Frequency / 500; // 2 ms

    private readonly nint _timer;
    private readonly long? _fixedPeriod;
    private long _period;
    private long _due;        // Stopwatch ticks của khung đã hẹn (khi _scheduled)
    private bool _scheduled;
    private long _lastFrame;  // Stopwatch ticks của khung vừa phát (khi _hasLastFrame)
    private bool _hasLastFrame;

    public TimerFrameClock(IDisplayClock? displayClock = null, nint window = 0)
        : this(displayClock, window, timestamp: null, fixedPeriod: null)
    {
    }

    /// <param name="timestamp">Seam đồng hồ Stopwatch cho test.</param>
    /// <param name="fixedPeriod">Chu kỳ cố định (Stopwatch ticks) cho test; null = theo màn hình / 60 Hz.</param>
    internal TimerFrameClock(IDisplayClock? displayClock, nint window, Func<long>? timestamp, long? fixedPeriod)
        : base(displayClock, window, timestamp)
    {
        if (fixedPeriod is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedPeriod));
        }

        _fixedPeriod = fixedPeriod;
        _period = fixedPeriod ?? (Stopwatch.Frequency / 60);
        _timer = PendingKernel32.CreateWaitableTimerEx(0, 0, PendingKernel32.CreateWaitableTimerHighResolution, PendingKernel32.TimerAllAccess);
        if (_timer == 0)
        {
            _timer = PendingKernel32.CreateWaitableTimerEx(0, 0, 0, PendingKernel32.TimerAllAccess);
        }

        if (_timer == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWaitableTimerEx failed");
        }
    }

    internal override nint WaitHandle => _timer;

    /// <summary>Stopwatch ticks của khung đang hẹn; null = không hẹn (test).</summary>
    internal long? ScheduledFrame => _scheduled ? _due : null;

    internal long Period => _period;

    protected override void OnArmedChanged(bool armed, long now)
    {
        if (armed)
        {
            Schedule(now);
            return;
        }

        _scheduled = false;
        PendingKernel32.CancelWaitableTimer(_timer);
    }

    protected override bool ShouldTick(long now, bool signaled, out long frameTimestamp)
    {
        frameTimestamp = _due;
        if (!signaled || !_scheduled)
        {
            return false; // timer không có hạn chót riêng; tín hiệu cũ sau khi huỷ hẹn thì bỏ
        }

        if (now < _due - EarlyTolerance)
        {
            ArmTimer(_due, now); // báo sớm (không nên xảy ra): hẹn lại đúng khung, không phát hai lần trong một khung
            return false;
        }

        // Trễ quá một chu kỳ (UI bận): neo lại vào hiện tại, khung kế cách đây >= một chu kỳ - không phát dồn khung lỡ.
        if (now - _due >= _period)
        {
            frameTimestamp = now;
        }

        _scheduled = false;
        return true;
    }

    protected override void OnTicked(long frameTimestamp, long now)
    {
        _lastFrame = frameTimestamp;
        _hasLastFrame = true;
        Schedule(now);
    }

    protected override void DisposeCore() => PendingKernel32.CloseHandle(_timer);

    private void Schedule(long now)
    {
        var timing = CurrentTiming();
        if (_fixedPeriod is null && timing is { RefreshPeriod: > 0 } known)
        {
            _period = known.RefreshPeriod;
        }

        long next;
        if (_fixedPeriod is null && timing is { RefreshPeriod: > 0 } grid)
        {
            var earliest = !_hasLastFrame ? now : Math.Max(now, _lastFrame + (_period / 2));
            next = NextGridPoint(grid, earliest);
        }
        else
        {
            next = !_hasLastFrame ? now : Math.Max(now, _lastFrame + _period);
        }

        _due = next;
        _scheduled = true;
        ArmTimer(next, now);
    }

    private void ArmTimer(long due, long now)
    {
        var relative100ns = Math.Max(1L, (due - now) * 10_000_000 / Stopwatch.Frequency);
        var dueTime = -relative100ns; // âm = tương đối
        if (!PendingKernel32.SetWaitableTimer(_timer, &dueTime, 0, 0, 0, resume: false))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed");
        }
    }
}
