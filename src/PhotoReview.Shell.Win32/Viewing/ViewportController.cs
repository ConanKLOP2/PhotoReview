using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// Những gì MainWindow.xaml bind vào <c>MainImage</c> (từ <c>ViewerState</c> và bitmap đang vẽ), tức phần input layout không đến
/// từ cửa sổ. <see cref="IsFit"/> = <c>ViewerState.IsFit</c> (cho nhánh <c>MainImage_SizeChanged</c> -> UpdateFitSize).
/// </summary>
internal readonly record struct ViewportContent(
    ViewerStretchMode Stretch,
    double ImageWidth, double ImageHeight,
    double MaxImageWidth, double MaxImageHeight,
    double BitmapWidth, double BitmapHeight,
    bool IsFit)
{
    /// <summary>Chưa có ảnh: như ViewerState mới (Uniform, chưa giới hạn) không bitmap.</summary>
    public static ViewportContent Empty { get; } = new(ViewerStretchMode.Uniform, double.NaN, double.NaN,
        double.PositiveInfinity, double.PositiveInfinity, 0, 0, IsFit: true);
}

/// <summary>Tham số nền tảng của <see cref="ViewportController"/>; giá trị mặc định = bản WPF ở 96 DPI.</summary>
internal sealed class ViewportControllerOptions
{
    /// <summary>DIP. Themes/DarkScrollBars.xaml: ScrollBar Width/Height = 10.</summary>
    public double ScrollBarThickness { get; init; } = 10;

    /// <summary>NE-3; Auto = như WPF.</summary>
    public ScrollBarPolicy ScrollBars { get; init; } = ScrollBarPolicy.Auto;

    /// <summary>SM_CXDRAG/SM_CYDRAG theo pixel của DPI cửa sổ (WP-22 nối GetSystemMetricsForDpi); mặc định Windows là 4.</summary>
    public Func<(int Horizontal, int Vertical)> DragMetricsPixels { get; init; } = static () => (4, 4);

    /// <summary>Vị trí con trỏ theo pixel màn hình (GetCursorPos), null khi không đọc được. WP-22 nối interop.</summary>
    public Func<PointD?> CursorScreenPixel { get; init; } = static () => null;

    /// <summary>Đồng hồ vblank (Platform.Windows), như WpfImageSurface.</summary>
    public IDisplayClock? DisplayClock { get; init; }

    /// <summary>QPC ticks; seam cho test.</summary>
    public Func<long> Timestamp { get; init; } = Stopwatch.GetTimestamp;
}

/// <summary>
/// WP-16: bản Win32 của cặp <c>ImageScroll</c> + <c>MainImage</c> (C-08 + C-07 phía Win32). Giữ <see cref="ViewportState"/>,
/// lấy input từ cửa sổ (<see cref="IShellWindow.ClientSizeDip"/>) và <see cref="ViewportContent"/>, chạy
/// <see cref="ViewportLayoutEngine"/> đồng bộ ở <see cref="UpdateLayout"/> hoặc trong một lượt layout xếp ở
/// <see cref="UiPriority.Render"/> (như LayoutManager của WPF), và cung cấp đúng các thành viên của <c>IImageSurface</c> +
/// <c>IFitSurface</c> (toạ độ <see cref="PointD"/> theo C-06) để <c>PointerInputController</c> và <c>FitViewController</c> chạy
/// trên nó không sửa. Hai interface đó (C-07) chưa khoá và còn ở App (WPF) tới WP-07/WP-09; khi chúng sang App.Shared
/// (hợp đồng v1.1) lớp này chỉ cần thêm danh sách interface + <c>Capture()</c> (xem NOWPF-WP16-VIEWPORT-ENGINE).
/// Chỉ dùng trên luồng UI. Chủ sở hữu gọi <see cref="InvalidateLayout"/> khi ViewerState/bitmap đổi (thay binding WPF).
/// </summary>
internal sealed class ViewportController : IDisposable
{
    private const int MaxLayoutIterations = 8;

    private readonly IShellWindow _window;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFrameClock _frameClock;
    private readonly Func<ViewportContent> _content;
    private readonly Action _updateFitSize;
    private readonly ViewportControllerOptions _options;
    private readonly ViewportState _state = new();
    private readonly Action _postedLayout;
    private readonly List<(EventHandler Handler, EventHandler<FrameTickEventArgs> Wrapper)> _frameHooks = [];
    private bool _layoutPosted;
    private bool _disposed;

    public ViewportController(IShellWindow window, IUiDispatcher dispatcher, IFrameClock frameClock, Func<ViewportContent> content,
        Action updateFitSize, ViewportControllerOptions? options = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _updateFitSize = updateFitSize ?? throw new ArgumentNullException(nameof(updateFitSize));
        _options = options ?? new ViewportControllerOptions();
        _postedLayout = RunPostedLayout;
        _window.ClientSizeChanged += OnClientSizeChanged;
        InvalidateLayout();
    }

    /// <summary>Sau mỗi lượt layout làm đổi layout hoặc offset (renderer vẽ lại; cửa sổ đã được <see cref="IShellWindow.Invalidate"/>).</summary>
    public event EventHandler? LayoutUpdated;

    /// <summary>Layout của lượt gần nhất (vẽ ảnh tại <c>ImageRect - offset</c>, thanh cuộn theo cờ nhìn thấy).</summary>
    public ViewportLayout Layout => _state.Layout;

    /// <summary>Đang có lượt layout xếp ở mức Render.</summary>
    public bool IsLayoutPosted => _layoutPosted;

    /// <summary>Thay binding WPF: input (ViewerState, bitmap) có thể đã đổi; xếp một lượt layout ở mức Render (gộp).</summary>
    public void InvalidateLayout()
    {
        if (_layoutPosted || _disposed) return;
        _layoutPosted = true;
        _dispatcher.Post(_postedLayout, UiPriority.Render);
    }

    private void RunPostedLayout()
    {
        _layoutPosted = false;
        if (!_disposed) RunLayout();
    }

    private ViewportInput CurrentInput(in ViewportContent content)
    {
        var client = _window.ClientSizeDip;
        return new ViewportInput(client.Width, client.Height, _options.ScrollBarThickness, _options.ScrollBars,
            content.Stretch, content.ImageWidth, content.ImageHeight, content.MaxImageWidth, content.MaxImageHeight,
            content.BitmapWidth, content.BitmapHeight);
    }

    /// <summary>
    /// Lượt layout tới khi sạch (như UpdateLayout của WPF): đọc lại input, tính, áp offset; kích thước ảnh đổi khi đang Fit
    /// -> UpdateFitSize (MainImage_SizeChanged) rồi tính lại.
    /// </summary>
    private void RunLayout()
    {
        var changed = false;
        for (var i = 0; i < MaxLayoutIterations; i++)
        {
            var content = _content();
            _state.SetInput(CurrentInput(content));
            var imageSize = (_state.ImageActualWidth, _state.ImageActualHeight);
            if (!_state.UpdateLayout()) break;
            changed = true;
            if (content.IsFit && imageSize != (_state.ImageActualWidth, _state.ImageActualHeight)) _updateFitSize();
        }
        if (!changed) return;
        _window.Invalidate();
        LayoutUpdated?.Invoke(this, EventArgs.Empty);
    }

    // ImageScroll_SizeChanged + Window_SizeChanged: UpdateFitSize, rồi layout.
    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        _updateFitSize();
        InvalidateLayout();
    }

    // ---- thành viên của IFitSurface (C-07) ----

    /// <summary>Kích thước vùng ảnh (ImageScroll trong viền = toàn client): khoảng Fit lấp đầy.</summary>
    public (double Width, double Height) ViewportSize
    {
        get
        {
            var client = _window.ClientSizeDip;
            return (ViewportLayoutMath.SanitizeLength(client.Width), ViewportLayoutMath.SanitizeLength(client.Height));
        }
    }

    public bool IsLoaded => _window.IsLoaded && !_disposed;

    /// <summary>ImageScroll.UpdateLayout(): tính lại đồng bộ (C-07: "Win32: tính lại ViewportLayout đồng bộ").</summary>
    public void UpdateLayout() => RunLayout();

    public void UpdateFitSize() => _updateFitSize();

    /// <summary>Dispatcher.InvokeAsync(noop, Render): xong sau lượt layout đã xếp trước đó ở cùng mức.</summary>
    public Task YieldToRenderAsync() => _dispatcher.InvokeAsync(static () => { }, UiPriority.Render);

    /// <summary>ScrollToHome rồi offset ngang/dọc = 0.</summary>
    public void ScrollHome()
    {
        _state.ScrollHome();
        InvalidateLayout();
    }

    // ---- thành viên của IImageSurface (C-07) ----

    public double HorizontalOffset => _state.HorizontalOffset;
    public double VerticalOffset => _state.VerticalOffset;
    public double ViewportWidth => _state.Layout.ViewportWidth;
    public double ViewportHeight => _state.Layout.ViewportHeight;
    public double ExtentWidth => _state.Layout.ExtentWidth;
    public double ExtentHeight => _state.Layout.ExtentHeight;

    /// <summary>C-07: GetSystemMetrics(SM_CXDRAG/SM_CYDRAG) / DpiScale (DIP, như SystemParameters.MinimumHorizontalDragDistance).</summary>
    public (double Horizontal, double Vertical) DragThreshold
    {
        get
        {
            var (horizontal, vertical) = _options.DragMetricsPixels();
            var dpi = NormalizedDpi(_window.DpiScale);
            return (horizontal / dpi, vertical / dpi);
        }
    }

    /// <summary>Kẹp như ScrollViewer; áp ở lượt layout kế tiếp (đã xếp ở mức Render).</summary>
    public void ScrollTo(double horizontal, double vertical)
    {
        _state.ScrollTo(horizontal, vertical);
        InvalidateLayout();
    }

    public PointD ImageOrigin => _state.ImageOrigin;

    public PointD ToImageElement(PointD surfacePoint) => _state.ToImageElement(surfacePoint);

    public double ImageActualWidth => _state.ImageActualWidth;
    public double ImageActualHeight => _state.ImageActualHeight;

    /// <summary>Kích thước bitmap đang vẽ (DIP), null khi không có bitmap.</summary>
    public (double Width, double Height)? SourceSize
    {
        get
        {
            var content = _content();
            return content.BitmapWidth > 0 && content.BitmapHeight > 0 ? (content.BitmapWidth, content.BitmapHeight) : null;
        }
    }

    public void CaptureMouse() => _window.CapturePointer();

    /// <summary>Chỉ nhả khi cửa sổ (tức vùng ảnh - phần tử duy nhất bắt chuột) đang giữ.</summary>
    public void ReleaseMouseCapture()
    {
        if (_window.HasPointerCapture) _window.ReleasePointer();
    }

    public void SetPanCursor(bool panning) => _window.SetCursor(panning ? ShellCursor.SizeAll : ShellCursor.Arrow);

    /// <summary>CompositionTarget.Rendering += handler: mỗi handler được gói một lần cho mỗi lần hook (hook hai lần = gọi hai lần, như WPF).</summary>
    public void HookRenderFrame(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EventHandler<FrameTickEventArgs> wrapper = (sender, e) => handler(sender, e);
        _frameHooks.Add((handler, wrapper));
        _frameClock.Frame += wrapper;
        _frameClock.RequestFrame();
    }

    /// <summary>CompositionTarget.Rendering -= handler: gỡ lần hook gần nhất của handler đó; handler chưa hook thì bỏ qua.</summary>
    public void UnhookRenderFrame(EventHandler handler)
    {
        for (var i = _frameHooks.Count - 1; i >= 0; i--)
        {
            if (_frameHooks[i].Handler != handler) continue;
            _frameClock.Frame -= _frameHooks[i].Wrapper;
            _frameHooks.RemoveAt(i);
            return;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance member of IImageSurface (C-07) once this class implements the interface (contract v1.1).")]
    public TimeSpan? RenderingTime(EventArgs e) =>
        e is FrameTickEventArgs frame ? TimeSpan.FromMilliseconds(frame.Tick.RenderingTimeMs) : null;

    public long Timestamp => _options.Timestamp();

    public DisplayTiming? DisplayTiming => _options.DisplayClock?.GetTiming(_window.Hwnd);

    /// <summary>Con trỏ trong toạ độ viewport, null khi ngoài [0, ViewportWidth] x [0, ViewportHeight] hoặc không đọc được.</summary>
    public PointD? PointerPosition
    {
        get
        {
            if (_options.CursorScreenPixel() is not { } screen) return null;
            var p = _window.ScreenToClientDip(screen);
            return p.X >= 0 && p.Y >= 0 && p.X <= ViewportWidth && p.Y <= ViewportHeight ? p : null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.ClientSizeChanged -= OnClientSizeChanged;
        foreach (var (_, wrapper) in _frameHooks) _frameClock.Frame -= wrapper;
        _frameHooks.Clear();
    }

    /// <summary>Cùng khoảng hợp lệ với ViewerState.NormalizeDpi (0,25..16; ngoài ra 1).</summary>
    internal static double NormalizedDpi(double dpiScale) => dpiScale is >= 0.25 and <= 16 ? dpiScale : 1.0;
}
