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

    /// <summary>
    /// Q-R41: how far a single ZoomIn/ZoomOut/wheel-zoom step moves the zoom level (a fraction of original size,
    /// e.g. 0.25 = 25 %). Was a hardcoded <c>const</c> before Q-R41; now set from
    /// <see cref="AppSettings.KeyboardZoomStepPercent"/> by the composition root / Settings window (same pattern as
    /// <see cref="ScalingQuality"/>). Defaults to the previous hardcoded value so a caller that never sets it keeps
    /// the old behaviour (tests, tools).
    /// </summary>
    public double ZoomStep { get; set; } = 0.25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(DisplayZoomPercent))]
    private double _zoom = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFit))]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(DisplayZoomPercent))]
    private ViewerStretchMode _stretch = ViewerStretchMode.Uniform;

    /// <summary>Full-resolution width of the current image after EXIF orientation (0 = unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(DisplayZoomPercent))]
    private int _sourcePixelWidth;

    /// <summary>Full-resolution height of the current image after EXIF orientation (0 = unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(DisplayZoomPercent))]
    private int _sourcePixelHeight;

    /// <summary>Device pixels per device-independent pixel of the viewer's monitor (1.0 = 96 DPI).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(DisplayZoomPercent))]
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
            var fit = Math.Min(MaxImageWidth * dpi / SourcePixelWidth, MaxImageHeight * dpi / SourcePixelHeight);
            return double.IsFinite(fit) && fit > 0 ? fit : 0; // an absurd viewport/DPI product must read as "unknown", not Infinity
        }
    }

    /// <summary>
    /// Original-relative zoom at which the current image's width exactly fills <see cref="MaxImageWidth"/> at the
    /// current <see cref="DpiScale"/> (0 = unknown). Same formula shape as <see cref="FitZoom"/>, but width only.
    /// </summary>
    public double FitWidthZoom
    {
        get
        {
            if (SourcePixelWidth <= 0) return 0;
            if (!double.IsFinite(MaxImageWidth) || MaxImageWidth <= 1) return 0;
            var dpi = NormalizeDpi(DpiScale);
            var fit = MaxImageWidth * dpi / SourcePixelWidth;
            return double.IsFinite(fit) && fit > 0 ? fit : 0;
        }
    }

    /// <summary>
    /// Original-relative zoom at which the current image's height exactly fills <see cref="MaxImageHeight"/> at the
    /// current <see cref="DpiScale"/> (0 = unknown). Same formula shape as <see cref="FitZoom"/>, but height only.
    /// </summary>
    public double FitHeightZoom
    {
        get
        {
            if (SourcePixelHeight <= 0) return 0;
            if (!double.IsFinite(MaxImageHeight) || MaxImageHeight <= 1) return 0;
            var dpi = NormalizeDpi(DpiScale);
            var fit = MaxImageHeight * dpi / SourcePixelHeight;
            return double.IsFinite(fit) && fit > 0 ? fit : 0;
        }
    }

    /// <summary>Windows scales 100 %..500 %; anything outside a generous range (0, NaN, Infinity, denormals) is a broken reading.</summary>
    private const double MinPlausibleDpiScale = 0.25;
    private const double MaxPlausibleDpiScale = 16;

    private static double NormalizeDpi(double dpiScale) =>
        dpiScale is >= MinPlausibleDpiScale and <= MaxPlausibleDpiScale ? dpiScale : 1.0;

    // In Fit, Zoom is 1.0 by convention; the original-relative zoom actually on screen is FitZoom.
    private double StepBase => IsFit && FitZoom > 0 ? FitZoom : Zoom;

    /// <summary>
    /// Q-R45: current zoom, in whole percent, for the on-image zoom HUD (<see cref="AppSettings.ShowZoomIndicator"/>).
    /// In Fit this is <see cref="FitZoom"/> (what is actually on screen), falling back to 100 % while the Fit zoom
    /// is not yet known (no image, or the viewport has not been measured). Not marked <see cref="ObservableProperty"/>
    /// itself (it is a computed read of several fields already wired to notify it, see their attributes above).
    /// </summary>
    public int DisplayZoomPercent => (int)Math.Round((IsFit ? (FitZoom > 0 ? FitZoom : 1.0) : Zoom) * 100);

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
    /// ADR 0008 amendment (R4): the bitmap behind the SAME image was replaced by one whose pixel size differs from the
    /// one it replaces (a RAW's LibRaw decode is a few pixels larger/smaller than the camera-visible preview).
    /// Original size becomes the new bitmap's own size, so 100 % stays exactly 1 pixel of what is shown, and the
    /// user's zoom PERCENT is kept -- except a Fit-width/Fit-height zoom, which is recomputed for the new size so the
    /// fitted dimension still fills the viewport exactly (no sliver, no scrollbar). Outside Fit,
    /// <see cref="SourceSizeSwapping"/> is raised BEFORE the size changes so the view can anchor its scroll position.
    /// A new image is not a swap: use <see cref="SetSourceSize"/>.
    /// </summary>
    public void SwapSourceSize(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (width == SourcePixelWidth && height == SourcePixelHeight) return;
        if (SourcePixelWidth <= 0 || SourcePixelHeight <= 0 || width <= 0 || height <= 0 || IsFit)
        {
            SetSourceSize(width, height);
            return;
        }

        SourceSizeSwapping?.Invoke(this, EventArgs.Empty);
        SetSourceSize(width, height);
        var fitZoom = _fitAxis switch
        {
            FitAxis.Width => _fitAxisViewport * NormalizeDpi(DpiScale) / width,
            FitAxis.Height => _fitAxisViewport * NormalizeDpi(DpiScale) / height,
            _ => 0.0,
        };
        if (double.IsFinite(fitZoom) && fitZoom > 0) Zoom = Math.Clamp(fitZoom, MinZoom, MaxZoom);
    }

    /// <summary>
    /// Raised by <see cref="SwapSourceSize"/> while zoomed, before the element size changes: the layout still shows the
    /// old size, so a listener can capture the image point it wants to keep in place.
    /// </summary>
    public event EventHandler? SourceSizeSwapping;

    private enum FitAxis { None, Width, Height }

    // Set when the current zoom came from ZoomToFitWidth/ZoomToFitHeight (and not changed since): the viewport
    // dimension (DIP) it filled, so SwapSourceSize can keep that dimension filled.
    private FitAxis _fitAxis;
    private double _fitAxisViewport;

    /// <summary>
    /// Đặt mức zoom cụ thể và chuyển chế độ hiển thị sang None (tỉ lệ tự do không kẹp theo viewport).
    /// </summary>
    public void SetZoom(double value) => SetZoomCore(value, FitAxis.None, 0);

    private void SetZoomCore(double value, FitAxis axis, double axisViewport)
    {
        // NaN survives Math.Clamp and would poison Zoom, ImageWidth/Height and every later step.
        if (double.IsNaN(value)) return;
        Stretch = ViewerStretchMode.None;
        MaxImageWidth = double.PositiveInfinity;
        MaxImageHeight = double.PositiveInfinity;
        _fitAxis = axis; // before Zoom/ZoomModeChanged: a held original may swap in synchronously from the event
        _fitAxisViewport = axisViewport;
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

    /// <summary>Original-relative zoom of "actual size": one source pixel per device pixel (ADR 0008).</summary>
    public const double ActualSizeZoom = 1.0;

    /// <summary>
    /// Zooms to 100 % = one source pixel per device pixel (ADR 0008). Leaves Fit even when the Fit zoom happens to
    /// be 1.0, and raises <see cref="ZoomModeChanged"/> so the current image's original is decoded on demand.
    /// </summary>
    public void ZoomToActualSize() => SetZoom(ActualSizeZoom);

    /// <summary>Zooms so the image width exactly fills <see cref="MaxImageWidth"/> (<see cref="FitWidthZoom"/>); no-op when unknown.</summary>
    public void ZoomToFitWidth()
    {
        if (FitWidthZoom > 0) SetZoomCore(FitWidthZoom, FitAxis.Width, MaxImageWidth);
    }

    /// <summary>Zooms so the image height exactly fills <see cref="MaxImageHeight"/> (<see cref="FitHeightZoom"/>); no-op when unknown.</summary>
    public void ZoomToFitHeight()
    {
        if (FitHeightZoom > 0) SetZoomCore(FitHeightZoom, FitAxis.Height, MaxImageHeight);
    }

    /// <summary>
    /// Tăng mức zoom thêm <see cref="ZoomStep"/> (Q-R41: cấu hình được qua <see cref="AppSettings.KeyboardZoomStepPercent"/>).
    /// </summary>
    public void ZoomIn() => StepZoom(ZoomStep);

    /// <summary>
    /// Giảm mức zoom bớt <see cref="ZoomStep"/> (Q-R41: cấu hình được qua <see cref="AppSettings.KeyboardZoomStepPercent"/>).
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
        _fitAxis = FitAxis.None;
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

        if (viewportWidth > 1 && viewportHeight > 1 && double.IsFinite(viewportWidth) && double.IsFinite(viewportHeight))
        {
            MaxImageWidth = viewportWidth;
            MaxImageHeight = viewportHeight;
        }
    }

    /// <summary>
    /// Áp dụng chế độ xem ban đầu theo cấu hình InitialViewMode. Returns false (no-op, nothing changed) when
    /// <paramref name="keepZoomAcrossImages"/> is true (<see cref="AppSettings.KeepZoomAcrossImages"/>): Fit stays
    /// Fit and a zoom stays at the same zoom across an image change. <see cref="InitialViewMode.FitWidth"/>/
    /// <see cref="InitialViewMode.FitHeight"/> refresh the viewport limits first (<see cref="UpdateViewport"/> with
    /// <c>force: true</c>) so <see cref="MaxImageWidth"/>/<see cref="MaxImageHeight"/> are current before computing
    /// <see cref="FitWidthZoom"/>/<see cref="FitHeightZoom"/>; scroll placement (top-third/centre) is the caller's
    /// job (<c>PointerInputController</c> owns the surface).
    /// </summary>
    public bool ApplyInitialViewMode(InitialViewMode mode, double viewportWidth, double viewportHeight,
        int clickZoomPercent = AppSettings.DefaultClickZoomPercent, bool keepZoomAcrossImages = false)
    {
        if (keepZoomAcrossImages) return false;
        switch (mode)
        {
            case InitialViewMode.Fit:
                ResetFit(viewportWidth, viewportHeight);
                break;
            case InitialViewMode.FitWidth:
                UpdateViewport(viewportWidth, viewportHeight, force: true);
                ZoomToFitWidth();
                break;
            case InitialViewMode.FitHeight:
                UpdateViewport(viewportWidth, viewportHeight, force: true);
                ZoomToFitHeight();
                break;
            case InitialViewMode.ClickZoomLevel:
                SetZoom(clickZoomPercent / 100.0);
                break;
            case InitialViewMode.Percent200:
                SetZoom(2.0);
                break;
            case InitialViewMode.Percent400: // legacy: SettingsNormalizer migrates a saved value to Percent200; kept defensively
                SetZoom(2.0);
                break;
            default: // Percent100 (and any unrecognized value)
                SetZoom(1.0);
                break;
        }
        return true;
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