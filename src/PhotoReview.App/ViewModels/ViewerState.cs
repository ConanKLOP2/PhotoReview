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
/// <remarks>
/// feat(zoom) (option A for #43): outside Fit, <see cref="Zoom"/> is relative to the ORIGINAL source
/// pixels, not to whatever bitmap happens to be displayed. At zoom Z the image element is
/// <see cref="SourcePixelWidth"/> x Z / <see cref="DpiScale"/> by <see cref="SourcePixelHeight"/> x Z /
/// <see cref="DpiScale"/> device-independent pixels, so 100 % = 1 source pixel per device pixel on any
/// monitor scale (the same device-pixel convention the preview decode box already uses). The element
/// size is therefore independent of the bitmap: the viewer shows the (small) preview scaled up at once
/// and later swaps in the full-resolution decode without any layout or scroll change.
/// Fit is unchanged (<see cref="ImageWidth"/>/<see cref="ImageHeight"/> are NaN = auto, bounded by
/// <see cref="MaxImageWidth"/>/<see cref="MaxImageHeight"/>).
/// </remarks>
public sealed partial class ViewerState : ObservableObject
{
    /// <summary>Absolute zoom range accepted by <see cref="SetZoom"/> (click-to-zoom allows 10 %..800 %).</summary>
    public const double MinZoom = 0.10;
    public const double MaxZoom = 8.0;

    /// <summary>
    /// Range reached by stepping (wheel / zoom in / zoom out): 0.25 steps between 25 % and 400 %, as before
    /// click-to-zoom widened <see cref="MinZoom"/>/<see cref="MaxZoom"/>. A step never moves the zoom in the
    /// opposite direction: zooming in above <see cref="MaxStepZoom"/> does nothing and zooming out from there
    /// lands on <see cref="MaxStepZoom"/> (so 800 % is one step from 400 %, not sixteen).
    /// </summary>
    public const double MinStepZoom = 0.25;
    public const double MaxStepZoom = 4.0;
    public const double ZoomStep = 0.25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    private double _zoom = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    private ViewerStretchMode _stretch = ViewerStretchMode.Uniform;

    /// <summary>Full-resolution width of the current image after EXIF orientation (0 = unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    private int _sourcePixelWidth;

    /// <summary>Full-resolution height of the current image after EXIF orientation (0 = unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    private int _sourcePixelHeight;

    /// <summary>Device pixels per device-independent pixel of the viewer's monitor (1.0 = 96 DPI).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    private double _dpiScale = 1.0;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private ScalingQuality _scalingQuality = ScalingQuality.HighQuality;

    [ObservableProperty]
    private double _maxImageWidth = double.PositiveInfinity;

    [ObservableProperty]
    private double _maxImageHeight = double.PositiveInfinity;

    /// <summary>
    /// Cho biết ảnh có đang hiển thị vừa vặn khung nhìn ở tỉ lệ gốc hay không.
    /// </summary>
    public bool IsFit => Stretch == ViewerStretchMode.Uniform && Math.Abs(Zoom - 1.0) < 0.001;

    /// <summary>
    /// Layout width (DIP) of the image element: NaN (auto) in Fit or while the source size is unknown,
    /// otherwise <see cref="CalculateDisplaySize"/> of the original size at <see cref="Zoom"/>.
    /// </summary>
    public double ImageWidth => DisplaySize.Width;

    /// <summary>Layout height (DIP) of the image element; see <see cref="ImageWidth"/>.</summary>
    public double ImageHeight => DisplaySize.Height;

    private (double Width, double Height) DisplaySize =>
        IsFit || SourcePixelWidth <= 0 || SourcePixelHeight <= 0
            ? (double.NaN, double.NaN)
            : CalculateDisplaySize(SourcePixelWidth, SourcePixelHeight, Zoom, DpiScale);

    /// <summary>
    /// On-screen size, in device-independent pixels, of an image whose original (post-orientation)
    /// size is <paramref name="originalWidth"/> x <paramref name="originalHeight"/> at original-relative
    /// <paramref name="zoom"/>: 1.0 = one source pixel per device pixel.
    /// </summary>
    public static (double Width, double Height) CalculateDisplaySize(int originalWidth, int originalHeight, double zoom, double dpiScale)
    {
        var scale = zoom / NormalizeDpi(dpiScale);
        return (originalWidth * scale, originalHeight * scale);
    }

    /// <summary>
    /// Original-relative zoom at which the current image fills the Fit viewport (0 = unknown). Used as
    /// the starting point when leaving Fit by a step (wheel / zoom in / zoom out), so the first step
    /// continues from what is on screen instead of jumping to 100 % +/- one step.
    /// </summary>
    public double FitZoom
    {
        get
        {
            if (SourcePixelWidth <= 0 || SourcePixelHeight <= 0) return 0;
            if (!double.IsFinite(MaxImageWidth) || !double.IsFinite(MaxImageHeight) || MaxImageWidth <= 1 || MaxImageHeight <= 1) return 0;
            var dpi = NormalizeDpi(DpiScale);
            return Math.Min(MaxImageWidth * dpi / SourcePixelWidth, MaxImageHeight * dpi / SourcePixelHeight);
        }
    }

    private static double NormalizeDpi(double dpiScale) => dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;

    // In Fit, Zoom is 1.0 by convention; the original-relative zoom actually on screen is FitZoom.
    private double StepBase => IsFit && FitZoom > 0 ? FitZoom : Zoom;

    public ViewerState()
    {
    }

    /// <summary>Sets the original (post-orientation) pixel size of the image currently displayed.</summary>
    public void SetSourceSize(int width, int height)
    {
        SourcePixelWidth = Math.Max(0, width);
        SourcePixelHeight = Math.Max(0, height);
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
        ZoomModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised once after <see cref="SetZoom"/> or <see cref="ResetFit"/> has applied ALL of its
    /// property changes, so a listener never acts on a transient mix (e.g. Stretch=None with the old
    /// Fit zoom of 1.0 while leaving Fit, or Zoom=1.0 with Stretch still None while entering it).
    /// </summary>
    public event EventHandler? ZoomModeChanged;

    /// <summary>Original-relative zoom currently applied, or null in Fit.</summary>
    public double? EffectiveZoom => IsFit ? null : Zoom;

    /// <summary>
    /// Tăng mức zoom thêm 0.25 (tối đa 4.0).
    /// </summary>
    public void ZoomIn() => StepZoom(ZoomStep);

    /// <summary>
    /// Giảm mức zoom bớt 0.25 (tối thiểu 0.25).
    /// </summary>
    public void ZoomOut() => StepZoom(-ZoomStep);

    /// <summary>
    /// A step never moves the zoom against its direction: a step down from Fit (or from a click zoom) that is
    /// already below <see cref="MinStepZoom"/> (huge originals) does nothing instead of enlarging, and a step up
    /// from above <see cref="MaxStepZoom"/> does nothing instead of shrinking.
    /// </summary>
    private void StepZoom(double step)
    {
        var next = CalculateStepZoom(StepBase, step);
        if (next is { } value) SetZoom(value);
    }

    /// <summary>Pure step rule behind <see cref="ZoomIn"/>/<see cref="ZoomOut"/>/<see cref="WheelZoom"/>; null = no change.</summary>
    internal static double? CalculateStepZoom(double current, double step)
    {
        if (step > 0)
        {
            if (current >= MaxStepZoom - 0.0005) return null;
            return Math.Min(current + step, MaxStepZoom);
        }
        if (current <= MinStepZoom + 0.0005) return null;
        if (current > MaxStepZoom) return MaxStepZoom;
        return Math.Max(current + step, MinStepZoom);
    }

    /// <summary>
    /// Điều chỉnh zoom bằng con lăn chuột theo delta.
    /// </summary>
    public void WheelZoom(int delta)
    {
        StepZoom(delta > 0 ? ZoomStep : -ZoomStep);
    }

    /// <summary>
    /// Khôi phục chế độ Fit (vừa vặn khung nhìn).
    /// </summary>
    public void ResetFit(double viewportWidth = 0, double viewportHeight = 0)
    {
        Zoom = 1.0;
        Stretch = ViewerStretchMode.Uniform;
        UpdateViewport(viewportWidth, viewportHeight, force: true);
        ZoomModeChanged?.Invoke(this, EventArgs.Empty);
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