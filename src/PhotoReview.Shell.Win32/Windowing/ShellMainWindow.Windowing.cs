using PhotoReview.App.ViewModels;
using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Win32;

/// <summary>Các seam của cửa sổ hoá (WP-25) mà test thay: null = Win32 thật.</summary>
/// <param name="Native">Truy vấn/di chuyển cửa sổ và monitor của F11 (test: giả lập hai màn hình).</param>
/// <param name="MonitorLayout">Bố cục monitor của <see cref="FullscreenController"/> lúc thoát F11.</param>
/// <param name="Dwm">DwmSetWindowAttribute của thanh tiêu đề tối.</param>
internal sealed record ShellWindowingSeams(IFullscreenNative? Native = null, IMonitorLayout? MonitorLayout = null, IDwmWindowAttributes? Dwm = null);

// WP-25: F11 áp lên cửa sổ thật, đổi cấu hình màn hình/DPI lúc đang F11, thanh tiêu đề tối, trạng thái lưu khi đóng.
// Tiêu đề + nhãn instance: MainViewModel.FolderTitle đã gộp "[nhãn] " (ApplyInstanceLabel) và ShellMainWindow đặt nó bằng
// _window.SetTitle; thu nhỏ/khôi phục + kích hoạt khi nhận forward: IForwardedOpenTarget (ShellMainWindow.cs); đóng hoãn khi file
// action giữ cổng: OnClosing (ShellMainWindow.cs). Phần còn lại của gói ở file này.
internal sealed partial class ShellMainWindow
{
    private ShellFullscreen? _fullscreen;
    private DisplayChangeHandler? _displayChangeHandler;
    private bool _windowingDisposed;

    /// <summary>Trạng thái F11 của cửa sổ (chẩn đoán/test).</summary>
    internal ShellFullscreen Fullscreen => _fullscreen!;

    /// <summary>R7-10: trạng thái cửa sổ có trước F11 khi đang F11 (để lưu placement lúc đóng), null khi không F11.</summary>
    private WindowShowState? FullscreenRestoreState => _fullscreen?.RestoreState;

    private (int Left, int Top, int Right, int Bottom)? FullscreenRestoreBounds => _fullscreen?.RestoreBounds;

    private void RegisterWindowing()
    {
        var seams = _options.Windowing ?? new ShellWindowingSeams();
        TitleBar.Apply(_window.Hwnd, seams.Dwm);
        _fullscreen = new ShellFullscreen(
            seams.MonitorLayout is { } layout ? new FullscreenController(layout) : new FullscreenController(),
            seams.Native ?? Win32FullscreenNative.Instance);
        _displayChangeHandler = new DisplayChangeHandler(this);
        _window.AddMessageHandler(_displayChangeHandler);
        _viewModel.Viewer.PropertyChanged += OnWindowingViewerChanged;
        _window.DpiChanged += OnWindowingDpiChanged;
    }

    private void DisposeWindowing()
    {
        if (_windowingDisposed)
        {
            return;
        }

        _windowingDisposed = true;
        _viewModel.Viewer.PropertyChanged -= OnWindowingViewerChanged;
        _window.DpiChanged -= OnWindowingDpiChanged;
        _fullscreen?.Reset();
    }

    /// <summary>ViewerState.IsFullscreen (F11/Esc đặt nó, WP-22) -> cửa sổ thật.</summary>
    private void OnWindowingViewerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(ViewerState.IsFullscreen) || _window.IsDestroyed)
        {
            return;
        }

        ApplyFullscreen(_viewModel.Viewer.IsFullscreen);
    }

    /// <summary>Áp trạng thái fullscreen lên HWND. True khi cửa sổ thật sự đổi.</summary>
    internal bool ApplyFullscreen(bool fullscreen)
    {
        if (_fullscreen is null || !_fullscreen.Apply(_window.Hwnd, fullscreen))
        {
            return false;
        }

        _viewport.InvalidateLayout();
        _window.Invalidate();
        return true;
    }

    /// <summary>
    /// WM_DPICHANGED: <see cref="ShellWindow"/> đã áp rect gợi ý. Đang F11 thì cửa sổ phải phủ lại đúng monitor mới (rect gợi ý
    /// là rect cửa sổ thường). Chạy TRƯỚC <c>OnDpiChanged</c> của lớp lõi (đăng ký sớm hơn): hộp decode/tỉ lệ đọc client cuối.
    /// </summary>
    private void OnWindowingDpiChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _fullscreen?.Refit(_window.Hwnd);
        }
    }

    /// <summary>WM_DISPLAYCHANGE (rút/cắm monitor, đổi độ phân giải): xếp việc ép lại F11 SAU khi HĐH dời xong cửa sổ.</summary>
    private void OnDisplayChanged()
    {
        if (_disposed || _fullscreen is not { IsActive: true })
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!_disposed && !_window.IsDestroyed && _fullscreen!.Refit(_window.Hwnd))
            {
                _viewport.InvalidateLayout();
                _window.Invalidate();
            }
        }, UiPriority.Render);
    }

    private sealed class DisplayChangeHandler(ShellMainWindow owner) : IWindowMessageHandler
    {
        public bool TryHandle(in WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg == WindowMessages.WmDisplayChange)
            {
                owner.OnDisplayChanged();
            }

            return false; // chỉ quan sát: xử lý mặc định vẫn chạy
        }
    }
}
