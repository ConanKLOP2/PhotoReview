using System.Diagnostics;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// C-05 chính: nhịp từ waitable object của swap chain (<c>IDXGISwapChain2::GetFrameLatencyWaitableObject</c>, lấy qua
/// <c>IRenderSurface.FrameLatencyWaitHandle</c> của WP-15; handle do swap chain sở hữu, lớp này KHÔNG đóng). Object báo khi
/// swap chain nhận được khung mới, tức "một lần mỗi khung sắp được trình bày" khi handler của <see cref="FrameClockBase.Frame"/>
/// Present mỗi tick.
/// <list type="bullet">
/// <item>Báo dồn (độ trễ khung &gt; 1, lần đầu) trong nửa chu kỳ sau tick trước: không phát ngay mà hoãn tới hết chu kỳ
/// (<see cref="DeadlineTimestamp"/>), nên không quá một tick mỗi khung và không mất nhịp.</item>
/// <item>Handler không Present (không có gì vẽ) thì object không báo nữa: hạn chót dự phòng 2 chu kỳ giữ đồng hồ chạy.</item>
/// </list>
/// </summary>
internal sealed class SwapChainFrameClock : FrameClockBase
{
    private readonly nint _waitHandle;
    private readonly long? _fixedPeriod;
    private long _period;
    private long _lastTick;   // Stopwatch ticks lúc phát gần nhất; 0 = chưa
    private long _armedAt;
    private bool _deferred;

    public SwapChainFrameClock(nint frameLatencyWaitHandle, IDisplayClock? displayClock = null, nint window = 0)
        : this(frameLatencyWaitHandle, displayClock, window, timestamp: null, fixedPeriod: null)
    {
    }

    internal SwapChainFrameClock(nint frameLatencyWaitHandle, IDisplayClock? displayClock, nint window, Func<long>? timestamp, long? fixedPeriod)
        : base(displayClock, window, timestamp)
    {
        if (frameLatencyWaitHandle == 0)
        {
            throw new ArgumentException("A swap chain frame latency waitable handle is required.", nameof(frameLatencyWaitHandle));
        }

        if (fixedPeriod is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedPeriod));
        }

        _waitHandle = frameLatencyWaitHandle;
        _fixedPeriod = fixedPeriod;
        _period = fixedPeriod ?? (Stopwatch.Frequency / 60);
    }

    internal override nint WaitHandle => _waitHandle;

    internal override long DeadlineTimestamp
    {
        get
        {
            if (!IsArmed)
            {
                return long.MaxValue;
            }

            return _deferred ? _lastTick + _period : Math.Max(_lastTick, _armedAt) + (2 * _period);
        }
    }

    protected override void OnArmedChanged(bool armed, long now)
    {
        if (armed)
        {
            _armedAt = now;
            _deferred = false;
        }
    }

    protected override bool ShouldTick(long now, bool signaled, out long frameTimestamp)
    {
        frameTimestamp = now;
        if (signaled)
        {
            if (_lastTick != 0 && now - _lastTick < _period / 2)
            {
                _deferred = true;
                return false;
            }
        }
        else if (now < DeadlineTimestamp)
        {
            return false;
        }

        var timing = CurrentTiming();
        if (_fixedPeriod is null && timing is { RefreshPeriod: > 0 } known)
        {
            _period = known.RefreshPeriod;
            frameTimestamp = NextGridPoint(known, now);
        }

        _deferred = false;
        _lastTick = now;
        return true;
    }
}
