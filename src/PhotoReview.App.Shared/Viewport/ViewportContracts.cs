using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Viewport;

// C-08 (NO-WPF-EXEC-PLAN mục 5): engine viewport thuần thay ScrollViewer + Image. Thực thi ở WP-16; chuẩn đúng là
// golden G-VIEW (viewport-layout.v1.json, WP-10), sai số <= 0,5 DIP.

/// <summary>Auto = như WPF (thanh cuộn chiếm chỗ). Chính sách cho bản Win32 chọn theo NE-3.</summary>
public enum ScrollBarPolicy
{
    Auto = 0,
    Overlay = 1,
    Hidden = 2,
}

/// <param name="ClientWidth">DIP, vùng ImageScroll (cửa sổ trừ viền nếu có; overlay không trừ).</param>
/// <param name="ClientHeight">DIP.</param>
/// <param name="ScrollBarThickness">DIP (WPF: SystemParameters.VerticalScrollBarWidth theo DarkScrollBars.xaml).</param>
/// <param name="ScrollBars">Chính sách thanh cuộn.</param>
/// <param name="Stretch">Từ ViewerState.</param>
/// <param name="ImageWidth">ViewerState.ImageWidth (NaN trong Fit).</param>
/// <param name="ImageHeight">ViewerState.ImageHeight (NaN trong Fit).</param>
/// <param name="MaxImageWidth">ViewerState.MaxImageWidth.</param>
/// <param name="MaxImageHeight">ViewerState.MaxImageHeight.</param>
/// <param name="BitmapWidth">Kích thước tự nhiên của bitmap (DIP) cho Stretch=Uniform.</param>
/// <param name="BitmapHeight">Xem <paramref name="BitmapWidth"/>.</param>
public readonly record struct ViewportInput(
    double ClientWidth, double ClientHeight,
    double ScrollBarThickness,
    ScrollBarPolicy ScrollBars,
    ViewerStretchMode Stretch,
    double ImageWidth, double ImageHeight,
    double MaxImageWidth, double MaxImageHeight,
    double BitmapWidth, double BitmapHeight);

/// <param name="ViewportWidth">= ScrollViewer.ViewportWidth.</param>
/// <param name="ViewportHeight">= ScrollViewer.ViewportHeight.</param>
/// <param name="ExtentWidth">= ScrollViewer.ExtentWidth.</param>
/// <param name="ExtentHeight">= ScrollViewer.ExtentHeight.</param>
/// <param name="HorizontalBarVisible">Thanh cuộn ngang nhìn thấy.</param>
/// <param name="VerticalBarVisible">Thanh cuộn dọc nhìn thấy.</param>
/// <param name="ImageRect">Vị trí phần tử ảnh trong toạ độ extent (căn giữa khi nhỏ hơn viewport).</param>
/// <param name="MaxHorizontalOffset">Offset ngang tối đa.</param>
/// <param name="MaxVerticalOffset">Offset dọc tối đa.</param>
public readonly record struct ViewportLayout(
    double ViewportWidth, double ViewportHeight,
    double ExtentWidth, double ExtentHeight,
    bool HorizontalBarVisible, bool VerticalBarVisible,
    RectD ImageRect,
    double MaxHorizontalOffset, double MaxVerticalOffset);

public static class ViewportLayoutEngine
{
    /// <summary>
    /// Mô phỏng ScrollViewer(HorizontalScrollBarVisibility=Auto, VerticalScrollBarVisibility=Auto,
    /// HorizontalContentAlignment=Stretch, VerticalContentAlignment=Stretch) chứa Image(Stretch, MaxWidth/MaxHeight,
    /// Width/Height). Lặp điểm bất động cho thanh cuộn Auto (tối đa 3 vòng). Thuần, không cấp phát. WP-16 thực thi.
    /// </summary>
    public static ViewportLayout Compute(in ViewportInput input) => throw new NotImplementedException();

    /// <summary>Kẹp offset như ScrollViewer. WP-16 thực thi.</summary>
    public static (double Horizontal, double Vertical) ClampOffset(in ViewportLayout layout, double horizontal, double vertical) =>
        throw new NotImplementedException();
}
