using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

// WP-26 (NE-3 a): thanh cuộn tự vẽ của ImageScroll. Hình học/chuột/vẽ ở Viewing/ShellScrollBars; file này chỉ nối vào cửa sổ chính.
internal sealed partial class ShellMainWindow
{
    private ShellScrollBars? _scrollBars;

    /// <summary>Thanh cuộn của cửa sổ (test và gói khác đọc hình học/trạng thái kéo qua đây).</summary>
    internal ShellScrollBars ScrollBars => _scrollBars ?? throw new InvalidOperationException("Scroll bars are not registered.");

    /// <summary>
    /// Gọi từ <c>RegisterFeatures</c> TRƯỚC <c>RegisterInput</c>: handler chuột của thanh cuộn phải đứng trước handler của ảnh
    /// (<c>ShellInput</c>) vì hộp của ảnh đang zoom phủ cả vùng thanh cuộn.
    /// </summary>
    private void RegisterScrollBars()
    {
        // WP-22: nhấn trên thanh cuộn vẫn là "Window_PreviewMouseDown" (dừng đà kinetic) dù ảnh không nhận nó.
        _scrollBars = new ShellScrollBars(_window, _viewport, () => _viewModel.Compare.IsVisible,
            onPress: () => _input?.Pointer.OnWindowPreviewMouseDown());
        _window.Closed += OnWindowClosedForScrollBars;
    }

    private void OnWindowClosedForScrollBars(object? sender, EventArgs e)
    {
        _window.Closed -= OnWindowClosedForScrollBars;
        _scrollBars?.Dispose();
    }

    /// <summary>Sau ảnh/compare, trước overlay (thanh cuộn thuộc ImageScroll nên nằm dưới toolbar/status).</summary>
    private void DrawScrollBars(IDrawContext context) => _scrollBars?.Draw(context);
}
