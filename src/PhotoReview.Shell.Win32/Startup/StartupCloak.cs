using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// Khởi động không "khung trắng" (perf/startup-first-image, bản Win32 của <c>StartupWindowReveal</c>): cửa sổ được cloak (DWM)
/// ngay khi HWND hiện ra - trước <c>ShowWindow</c> - và chỉ được bỏ cloak sau khi khung ĐẦU TIÊN đã Present
/// (<see cref="NoteFirstPresent"/>). Cloak không làm chậm gì khác: cửa sổ vẫn được hiện, kích hoạt, layout và vẽ như thường.
/// <see cref="Reveal"/> idempotent, cũng chạy ở hạn chót an toàn (<see cref="SafetyTimeout"/>) nếu không bao giờ có khung;
/// <see cref="Stop"/> (cửa sổ đóng) huỷ hạn chót. DWM từ chối cloak thì không đổi gì (cửa sổ cứ hiện). Chỉ dùng trên UI thread
/// (hạn chót tự đăng lại lên dispatcher).
/// </summary>
internal sealed class StartupCloak : IDisposable
{
    /// <summary>Thời gian tối đa cửa sổ bị cloak khi không có khung nào.</summary>
    internal static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<nint, bool, bool> _setCloaked;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action? _onRevealed;
    private readonly TimeSpan _safetyTimeout;
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleTimeout;
    private IDisposable? _timer;
    private nint _handle;
    private bool _cloaked;
    private bool _revealed;

    /// <param name="setCloaked">Seam: (hwnd, cloaked) -> DWM chấp nhận. Production: <see cref="WindowsDwmCloak"/>.</param>
    /// <param name="scheduleTimeout">
    /// Seam: (độ trễ, việc) -> bộ hẹn giờ một lần; Dispose bộ hẹn = huỷ. Production: <see cref="System.Threading.Timer"/> (chạy việc
    /// trên thread pool). Test tiêm bộ hẹn bằng tay để KHÔNG phụ thuộc thread pool/đồng hồ thật (CI: thread pool bị chiếm làm timer
    /// 50 ms trễ &gt; 10 s).
    /// </param>
    public StartupCloak(IUiDispatcher dispatcher, Action? onRevealed = null, Func<nint, bool, bool>? setCloaked = null,
        TimeSpan? safetyTimeout = null, Func<TimeSpan, Action, IDisposable>? scheduleTimeout = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onRevealed = onRevealed;
        _setCloaked = setCloaked ?? ((hwnd, cloaked) => WindowsDwmCloak.TrySetCloaked(hwnd, cloaked));
        _safetyTimeout = safetyTimeout ?? SafetyTimeout;
        _scheduleTimeout = scheduleTimeout ?? ScheduleOnTimer;
    }

    /// <summary>Đang cloak và chưa bỏ.</summary>
    public bool IsHidden => _cloaked && !_revealed;

    public bool IsRevealed => _revealed;

    /// <summary>Cloak <paramref name="hwnd"/> trừ khi cổng này đã dùng (một cửa sổ chỉ cloak một lần).</summary>
    public void Hide(nint hwnd)
    {
        if (_revealed || _cloaked || hwnd == 0)
        {
            return;
        }

        _handle = hwnd;
        _cloaked = _setCloaked(hwnd, true);
        if (_cloaked)
        {
            _timer = _scheduleTimeout(_safetyTimeout, PostReveal);
        }
    }

    /// <summary>Khung đầu tiên (có ảnh hoặc canvas trống) đã Present: bỏ cloak.</summary>
    public void NoteFirstPresent() => Reveal();

    /// <summary>Bỏ cloak (một lần). Không làm gì nếu chưa từng cloak.</summary>
    public void Reveal()
    {
        Stop();
        if (_revealed)
        {
            return;
        }

        _revealed = true;
        if (!_cloaked)
        {
            return;
        }

        _setCloaked(_handle, false);
        _onRevealed?.Invoke();
    }

    /// <summary>Huỷ hạn chót an toàn (giữ trạng thái cloak: cửa sổ đang đóng).</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
    }

    public void Dispose() => Stop();

    private static Timer ScheduleOnTimer(TimeSpan delay, Action callback) =>
        new(static state => ((Action)state!)(), callback, delay, Timeout.InfiniteTimeSpan);

    private void PostReveal() => _dispatcher.Post(Reveal);
}
