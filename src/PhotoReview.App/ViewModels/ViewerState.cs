using PhotoReview.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Chế độ hiển thị co giãn ảnh độc lập với WPF (tuân thủ quy tắc K-2).
/// </summary>
public enum ViewerStretchMode
{
    None = 0,
    Uniform = 1
}

/// <summary>
/// Quản lý trạng thái xem ảnh (mức zoom, chế độ co giãn, kích thước giới hạn viewport, toàn màn hình).
/// Kế thừa ObservableObject của CommunityToolkit.Mvvm.
/// </summary>
public sealed partial class ViewerState : ObservableObject
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;
    public const double ZoomStep = 0.25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    private double _zoom = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    private ViewerStretchMode _stretch = ViewerStretchMode.Uniform;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private double _maxImageWidth = double.PositiveInfinity;

    [ObservableProperty]
    private double _maxImageHeight = double.PositiveInfinity;

    /// <summary>
    /// Cho biết ảnh có đang hiển thị vừa vặn khung nhìn ở tỉ lệ gốc hay không.
    /// </summary>
    public bool IsFit => Stretch == ViewerStretchMode.Uniform && Math.Abs(Zoom - 1.0) < 0.001;

    public ViewerState()
    {
    }

    /// <summary>
    /// Đặt mức zoom cụ thể và chuyển chế độ hiển thị sang None (tỉ lệ tự do không kẹp theo viewport).
    /// </summary>
    public void SetZoom(double value)
    {
        Stretch = ViewerStretchMode.None;
        MaxImageWidth = double.PositiveInfinity;
        MaxImageHeight = double.PositiveInfinity;
        Zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    /// <summary>
    /// Tăng mức zoom thêm 0.25 (tối đa 4.0).
    /// </summary>
    public void ZoomIn() => SetZoom(Zoom + ZoomStep);

    /// <summary>
    /// Giảm mức zoom bớt 0.25 (tối thiểu 0.25).
    /// </summary>
    public void ZoomOut() => SetZoom(Zoom - ZoomStep);

    /// <summary>
    /// Điều chỉnh zoom bằng con lăn chuột theo delta.
    /// </summary>
    public void WheelZoom(int delta)
    {
        var next = Zoom + (delta > 0 ? ZoomStep : -ZoomStep);
        SetZoom(next);
    }

    /// <summary>
    /// Khôi phục chế độ Fit (vừa vặn khung nhìn).
    /// </summary>
    public void ResetFit(double viewportWidth = 0, double viewportHeight = 0)
    {
        Zoom = 1.0;
        Stretch = ViewerStretchMode.Uniform;
        UpdateViewport(viewportWidth, viewportHeight, force: true);
    }

    /// <summary>
    /// Cập nhật kích thước khung nhìn cho ảnh khi đang ở chế độ Fit.
    /// </summary>
    public void UpdateViewport(double viewportWidth, double viewportHeight, bool force = false)
    {
        if (!force && Stretch != ViewerStretchMode.Uniform) return;

        if (viewportWidth > 1 && viewportHeight > 1)
        {
            MaxImageWidth = viewportWidth;
            MaxImageHeight = viewportHeight;
        }
    }

    /// <summary>
    /// Áp dụng chế độ xem ban đầu theo cấu hình InitialViewMode.
    /// </summary>
    public void ApplyInitialViewMode(InitialViewMode mode, double viewportWidth, double viewportHeight)
    {
        if (mode == InitialViewMode.Fit)
        {
            ResetFit(viewportWidth, viewportHeight);
        }
        else
        {
            var zoom = mode switch
            {
                InitialViewMode.Percent200 => 2.0,
                InitialViewMode.Percent400 => 4.0,
                _ => 1.0
            };
            SetZoom(zoom);
        }
    }

    /// <summary>
    /// Bật/tắt chế độ toàn màn hình.
    /// </summary>
    public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    /// <summary>
    /// Thoát chế độ toàn màn hình.
    /// </summary>
    public void ExitFullscreen() => IsFullscreen = false;
}