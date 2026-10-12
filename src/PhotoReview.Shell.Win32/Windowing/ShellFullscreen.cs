using System.Runtime.InteropServices;
using PhotoReview.App.Windowing;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Windowing;

/// <summary>
/// Seam của <see cref="ShellFullscreen"/> trên HWND (production = user32; test dùng giả lập màn hình/cửa sổ, không phụ thuộc
/// số monitor hay cỡ màn hình của runner). Mọi toạ độ là pixel vật lý.
/// </summary>
internal interface IFullscreenNative
{
    /// <summary>Trạng thái hiện tại của cửa sổ: thu nhỏ -> Minimized, phóng to -> Maximized, còn lại Normal.</summary>
    WindowShowState CurrentState(nint hwnd);

    /// <summary>Toàn bộ monitor (rcMonitor, gồm cả thanh tác vụ) gần cửa sổ nhất; false khi không biết.</summary>
    bool TryGetMonitorRect(nint hwnd, out ScreenRect monitor);

    bool TryGetWindowRect(nint hwnd, out ScreenRect window);

    /// <summary>Một <c>SetWindowPos</c> đặt cửa sổ lên <paramref name="rect"/> (không đổi z-order, không lấy focus).</summary>
    void MoveTo(nint hwnd, ScreenRect rect);

    /// <summary>Đường dự phòng khi không có thông tin monitor: để HĐH tự phóng to / trả về trạng thái cũ.</summary>
    void Show(nint hwnd, WindowShowState state);
}

internal sealed unsafe partial class Win32FullscreenNative : IFullscreenNative
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    public static readonly Win32FullscreenNative Instance = new();

    public WindowShowState CurrentState(nint hwnd)
    {
        if (User32.IsIconic(hwnd))
        {
            return WindowShowState.Minimized;
        }

        return IsZoomed(hwnd) ? WindowShowState.Maximized : WindowShowState.Normal;
    }

    public bool TryGetMonitorRect(nint hwnd, out ScreenRect monitor)
    {
        var handle = User32.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (handle != 0 && User32.GetMonitorInfo(handle, &info))
        {
            monitor = new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            return true;
        }

        monitor = default;
        return false;
    }

    public bool TryGetWindowRect(nint hwnd, out ScreenRect window)
    {
        Rect rect = default;
        if (User32.GetWindowRect(hwnd, &rect))
        {
            window = new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }

        window = default;
        return false;
    }

    public void MoveTo(nint hwnd, ScreenRect rect) =>
        User32.SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate);

    public void Show(nint hwnd, WindowShowState state) =>
        User32.ShowWindow(hwnd, state == WindowShowState.Maximized ? WindowMessages.SwShowMaximized : WindowMessages.SwShowNormal);

    [LibraryImport("user32.dll", EntryPoint = "IsZoomed")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);
}

/// <summary>
/// F11 trên HWND của shell (WP-25, thẻ: <c>IFullscreenController</c> của WP-08 trên cửa sổ shell). Phần Win32 (style, MỘT
/// SetWindowPos lên cả monitor, khôi phục đúng rect, rơi về work area còn lại khi monitor đã rút) ở
/// <see cref="FullscreenController"/>; lớp này lo vòng đời: idempotent (hai lần bật không ghi đè trạng thái trước F11), đường
/// dự phòng khi không có thông tin monitor, và "ép lại" cửa sổ lên đúng monitor khi cấu hình màn hình/DPI đổi lúc đang F11.
/// Chỉ dùng trên UI thread.
/// </summary>
internal sealed class ShellFullscreen
{
    private readonly FullscreenController _controller;
    private readonly IFullscreenNative _native;
    private bool _active;

    public ShellFullscreen(FullscreenController controller, IFullscreenNative native)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public bool IsActive => _active;

    /// <summary>Trạng thái cửa sổ có TRƯỚC F11 (R7-10: đóng khi đang F11 lưu trạng thái này); null khi không ở fullscreen.</summary>
    public WindowShowState? RestoreState => _active ? _controller.StateBefore : null;

    /// <summary>Rect Normal (pixel vật lý) lúc vào F11: F11 đặt cửa sổ lên cả monitor nên GetWindowPlacement không còn cho rect này.</summary>
    public (int Left, int Top, int Right, int Bottom)? RestoreBounds => _active ? _controller.NormalBounds : null;

    /// <returns>True khi trạng thái thật sự đổi (bật khi đang tắt, tắt khi đang bật).</returns>
    public bool Apply(nint hwnd, bool fullscreen)
    {
        if (hwnd == 0 || fullscreen == _active)
        {
            return false;
        }

        if (fullscreen)
        {
            _controller.Enter(hwnd, _native.CurrentState(hwnd));
            _active = true;
            if (_controller.EnterNeedsMaximizedState)
            {
                _native.Show(hwnd, WindowShowState.Maximized); // không có thông tin monitor: để HĐH phóng to
            }
        }
        else
        {
            _controller.Exit(hwnd);
            _active = false;
            if (_controller.ExitNeedsStateRestore)
            {
                _native.Show(hwnd, _controller.StateBefore);
            }
        }

        return true;
    }

    /// <summary>
    /// Đang F11 mà cửa sổ không còn phủ đúng monitor của nó (monitor bị rút nên HĐH dời cửa sổ, đổi độ phân giải, WM_DPICHANGED
    /// áp rect gợi ý): đặt lại lên toàn bộ monitor gần nhất. Không F11: không làm gì.
    /// </summary>
    /// <returns>True khi cửa sổ đã được dời.</returns>
    public bool Refit(nint hwnd)
    {
        if (!_active || hwnd == 0 || _controller.EnterNeedsMaximizedState)
        {
            return false; // đường dự phòng phóng to: HĐH tự giữ cửa sổ phủ monitor
        }

        if (!_native.TryGetMonitorRect(hwnd, out var monitor) || !_native.TryGetWindowRect(hwnd, out var window) || window == monitor)
        {
            return false;
        }

        _native.MoveTo(hwnd, monitor);
        return true;
    }

    /// <summary>Cửa sổ bị huỷ khi đang F11: bỏ trạng thái (không còn gì để khôi phục).</summary>
    public void Reset() => _active = false;
}
