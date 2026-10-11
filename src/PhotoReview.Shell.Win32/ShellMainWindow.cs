using System.ComponentModel;
using PhotoReview.App;
using PhotoReview.App.Input;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

/// <summary>Tuỳ chọn của <see cref="ShellMainWindow"/> (mặc định = chạy thật; test đổi từng cái).</summary>
/// <param name="PreferWarp">Dùng WARP thay vì GPU (RDP/không GPU/test; <c>PHOTOREVIEW_SHELL_WARP=1</c>).</param>
/// <param name="PlacementFile">Tệp window-placement.json để lưu khi đóng; null = không lưu (smoke, test).</param>
/// <param name="Activate">Lấy focus khi hiện (smoke/test = false).</param>
/// <param name="SettingsStore">WP-22: kho cài đặt (phím tắt dựng lại khi Changed, lưu KeepZoom/ClickZoom); null = chỉ đọc cài đặt của view model.</param>
/// <param name="ZoomPrompt">WP-22: hộp thoại zoom tuỳ chọn của phím CustomZoom; null = lệnh này không làm gì.</param>
/// <param name="InputEnvironment">WP-22: seam đọc hệ thống của nhập liệu (test); null = Win32 thật.</param>
internal sealed record ShellMainWindowOptions(bool PreferWarp = false, string? PlacementFile = null, bool Activate = true,
    SettingsStore? SettingsStore = null, IZoomPromptService? ZoomPrompt = null, ShellInputEnvironment? InputEnvironment = null);

/// <summary>
/// Cửa sổ chính của shell Win32 (NO-WPF-EXEC-PLAN C-15/C-16, WP-20 giai đoạn A): sở hữu <see cref="ShellWindow"/> + bề mặt Direct2D +
/// <see cref="GpuImageCache"/> + <see cref="ViewportController"/> và nối chúng với <see cref="MainViewModel"/> (thay phần binding
/// XAML của <c>MainWindow</c> WPF). Phần tối thiểu: cửa sổ chạy thật, vẽ ảnh hiện tại ở chế độ view do <c>ViewerState</c> chọn
/// (Fit / zoom / offset từ <c>ViewportController</c>), đóng sạch. Chuột/phím (WP-22), overlay/toolbar (WP-23), menu (WP-24),
/// trình bày/crossfade (WP-21), F11/DPI đa màn hình (WP-25) thêm bằng file <c>ShellMainWindow.&lt;Feature&gt;.cs</c> partial.
/// Chỉ dùng trên UI thread tạo nó.
/// </summary>
internal sealed partial class ShellMainWindow : IForwardedOpenTarget, IDisposable
{
    /// <summary>Kích thước mặc định (DIP) khi chưa có placement: <c>MainWindow.xaml</c> Width=1200 Height=800.</summary>
    internal const double DefaultWidthDip = 1200;

    internal const double DefaultHeightDip = 800;

    /// <summary>Hệ số chất lượng decode (cùng <c>MainWindow.PreviewQualityMultiplier</c>): ảnh nhìn ở DPI máy, dư 15 %.</summary>
    internal const double PreviewQualityMultiplier = 1.15;

    private const double FallbackViewportWidth = 2200;
    private const double FallbackViewportHeight = 1400;

    /// <summary><c>Dark.Canvas</c> (#101010) của <c>MainWindow.xaml</c>.</summary>
    private static readonly ColorF CanvasColor = new(0x10 / 255f, 0x10 / 255f, 0x10 / 255f, 1f);

    private readonly ShellWindow _window;
    private readonly Win32UiDispatcher _dispatcher;
    private readonly MainViewModel _viewModel;
    private readonly ViewportSizeSource _sizeSource;
    private readonly ShellPresentationHub _hub;
    private readonly StartupCloak _cloak;
    private readonly IWindowPlacementStore _placementStore;
    private readonly IWindowPlacementNative _placementNative;
    private readonly ShellMainWindowOptions _options;
    private readonly ILog _log;
    private readonly Action<string> _mark;
    private readonly ViewportController _viewport;
    private readonly EventHandler<FrameTickEventArgs> _onRender;
    private readonly PropertyChangedEventHandler _onViewModelChanged;
    private readonly PropertyChangedEventHandler _onViewerChanged;
    private readonly Action<object?, bool> _onImageChanged;
    private D2DRenderSurface? _surface;
    private GpuImageCache? _cache;
    private PixelBuffer? _image;
    private bool _firstPresentDone;
    private bool _closeWhenFileActionDone;
    private bool _disposed;

    public ShellMainWindow(ShellWindow window, Win32UiDispatcher dispatcher, IFrameClock frameClock, MainViewModel viewModel,
        ViewportSizeSource sizeSource, ShellPresentationHub hub, StartupCloak cloak, IWindowPlacementStore placementStore,
        IWindowPlacementNative placementNative, IDisplayClock? displayClock, ILog log, Action<string>? mark = null,
        ShellMainWindowOptions? options = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _sizeSource = sizeSource ?? throw new ArgumentNullException(nameof(sizeSource));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _cloak = cloak ?? throw new ArgumentNullException(nameof(cloak));
        _placementStore = placementStore ?? throw new ArgumentNullException(nameof(placementStore));
        _placementNative = placementNative ?? throw new ArgumentNullException(nameof(placementNative));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mark = mark ?? PhotoReviewPerf.StartupMark;
        _options = options ?? new ShellMainWindowOptions();
        ArgumentNullException.ThrowIfNull(frameClock);

        _viewport = new ViewportController(_window, _dispatcher, frameClock, ContentFromViewer, UpdateFitSize,
            CreateViewportOptions(_window, displayClock));
        _sizeSource.Get = () => _viewport.ViewportSize;
        _viewModel.Viewer.DpiScale = _window.DpiScale;
        RegisterFeatures();

        _onRender = (_, _) => RenderFrame();
        _onViewModelChanged = OnViewModelPropertyChanged;
        _onViewerChanged = (_, _) => OnViewerChanged();
        _onImageChanged = OnImageChanged;
        _window.Render += _onRender;
        _window.ClientSizeChanged += OnClientSizeChanged;
        _window.DpiChanged += OnDpiChanged;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
        _viewModel.PropertyChanged += _onViewModelChanged;
        _viewModel.Viewer.PropertyChanged += _onViewerChanged;
        _hub.ImageChanged += _onImageChanged;
        UpdateTargetDecodeBox();
        _window.SetTitle(_viewModel.FolderTitle);
        InitializeViewing(frameClock); // WP-21
    }

    /// <summary>
    /// Điểm cắm của các gói tính năng (WP-21..26): mỗi gói thêm đúng MỘT dòng ở CUỐI hàm này (lời gọi <c>RegisterXxx()</c> trong
    /// <c>ShellMainWindow.&lt;Feature&gt;.cs</c>); xung đột khi merge thì giữ cả các dòng. Chạy trong constructor, SAU khi viewport
    /// và view model đã có, TRƯỚC các đăng ký sự kiện cửa sổ của lớp lõi (nên <c>Closed</c> của tính năng chạy trước <c>OnClosed</c>).
    /// </summary>
    private void RegisterFeatures()
    {
        RegisterInput(); // WP-22: chuột, phím, wheel, touchpad, kéo-thả
        RegisterContextMenu(); // WP-24: menu chuột phải, submenu Zoom
    }

    public ShellWindow Window => _window;

    public nint Hwnd => _window.Hwnd;

    public MainViewModel ViewModel => _viewModel;

    internal ViewportController Viewport => _viewport;

    /// <summary>Dispatcher của UI thread (gói tính năng và test đăng việc lên UI thread qua nó).</summary>
    internal Win32UiDispatcher Dispatcher => _dispatcher;

    /// <summary>True sau khung đầu tiên đã Present.</summary>
    public bool FirstPresentDone => _firstPresentDone;

    public bool IsClosingOrClosed { get; private set; }

    /// <summary>Bề mặt vẽ đã tạo (sau <see cref="AttachRenderer"/>); null trước đó/sau dispose.</summary>
    internal D2DRenderSurface? Surface => _surface;

    internal GpuImageCache? ImageCache => _cache;

    /// <summary>Tạo thiết bị D3D/D2D + swap chain cho cửa sổ và cache ảnh GPU; gọi một lần, sau khi cửa sổ có HWND.</summary>
    public void AttachRenderer(IRenderSurfaceFactoryHook? factory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface is not null)
        {
            throw new InvalidOperationException("The renderer is already attached.");
        }

        var options = new RenderSurfaceOptions(PreferWarp: _options.PreferWarp);
        var surface = factory is null
            ? D2DRenderSurface.CreateWindowSurface(_window.Hwnd, options)
            : factory.Create(_window.Hwnd, options);
        _mark(ShellPerfMarks.D3dDeviceReady);
        _mark(ShellPerfMarks.SwapChainReady);
        var (width, height) = _window.ClientSizePixels;
        if (width > 0 && height > 0 && (surface.PixelWidth != width || surface.PixelHeight != height))
        {
            surface.Resize(width, height, _window.DpiScale);
        }

        _surface = surface;
        _cache = new GpuImageCache(surface, _dispatcher);
        AttachViewing(); // WP-21
        _window.SetSurfaceReady(true);
    }

    /// <summary>
    /// Bắt đầu mở đường dẫn của dòng lệnh: đặt hộp decode theo vùng client THẬT (cửa sổ đã ở placement cuối), nối vào decode
    /// sớm đã chạy (cùng khoá) rồi mở. Trả Task của việc mở (lỗi vào log bởi người gọi).
    /// </summary>
    public Task OpenInitialAsync(string? initialPath, bool isFile)
    {
        var (clientWidth, clientHeight) = _window.ClientSizePixels;
        if (clientWidth > 0 && clientHeight > 0)
        {
            var dpi = _window.DpiScale;
            UpdateTargetDecodeBox((clientWidth / dpi, clientHeight / dpi));
        }

        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return Task.CompletedTask;
        }

        _mark("openPathBegin");
        if (isFile)
        {
            if (_sizeSource.StartupPrediction is { } predicted)
            {
                _mark(predicted == _sizeSource.TargetDecodeBox ? "earlyDecodeBoxMatched" : "earlyDecodeBoxMismatch");
            }

            if (_viewModel.PrewarmInitialImage(initialPath) is not null)
            {
                _mark("initialDecodeStarted");
            }
        }

        _viewModel.Presenter.DeferNextPreloadKick = true; // P-1: khung của ảnh đầu trước, rồi mới preload
        return _viewModel.OpenPathAsync(initialPath);
    }

    // IForwardedOpenTarget -------------------------------------------------------------------

    bool IForwardedOpenTarget.IsMinimized => _window.Hwnd != 0 && User32.IsIconic(_window.Hwnd);

    void IForwardedOpenTarget.Restore()
    {
        if (_window.Hwnd != 0)
        {
            User32.ShowWindow(_window.Hwnd, WindowMessages.SwRestore);
        }
    }

    void IForwardedOpenTarget.Activate()
    {
        if (_window.IsDestroyed)
        {
            throw new InvalidOperationException("The main window is already destroyed.");
        }

        User32.SetForegroundWindow(_window.Hwnd);
    }

    public Task OpenPathAsync(string path) => _viewModel.OpenPathAsync(path);

    // Layout / hộp decode --------------------------------------------------------------------

    /// <summary>Thay binding của XAML: những gì <c>MainImage</c> lấy từ <c>ViewerState</c> và bitmap đang vẽ.</summary>
    private ViewportContent ContentFromViewer()
    {
        var viewer = _viewModel.Viewer;
        var image = _image;
        var width = image is { IsDisposed: false } ? image.Width : 0;
        var height = image is { IsDisposed: false } ? image.Height : 0;
        return new ViewportContent(viewer.Stretch, viewer.ImageWidth, viewer.ImageHeight, viewer.MaxImageWidth, viewer.MaxImageHeight,
            width, height, viewer.IsFit);
    }

    /// <summary><c>MainWindow.UpdateFitSize</c>: viewport mới -> ViewerState (MaxImage cho Fit) và hộp decode.</summary>
    private void UpdateFitSize()
    {
        var (width, height) = _viewport.ViewportSize;
        _viewModel.Viewer.UpdateViewport(width, height);
        UpdateTargetDecodeBox();
    }

    /// <summary><c>MainWindow.UpdateTargetDecodeBox</c>: hộp (w x h, pixel thiết bị) mà preview được decode theo, đẩy lên UI thread.</summary>
    private void UpdateTargetDecodeBox((double Width, double Height)? viewportSize = null)
    {
        var (width, height) = viewportSize ?? _viewport.ViewportSize;
        var dpi = _window.DpiScale;
        _viewModel.Viewer.DpiScale = dpi;
        _sizeSource.TargetDecodeBox = PhotoReview.Imaging.AdaptivePreviewPolicy.CalculateTargetDecodeBox(
            width > 1 ? width : FallbackViewportWidth, height > 1 ? height : FallbackViewportHeight, dpi, PreviewQualityMultiplier);
    }

    private void OnViewerChanged()
    {
        if (_disposed)
        {
            return;
        }

        _viewport.InvalidateLayout();
        _window.Invalidate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.FolderTitle))
        {
            _window.SetTitle(_viewModel.FolderTitle);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentImage) && _viewModel.Viewer.IsFit)
        {
            _dispatcher.Post(UpdateFitSizeIfAlive, UiPriority.Render); // như BeginInvoke(UpdateFitSize, Render) của MainWindow
        }
    }

    private void UpdateFitSizeIfAlive()
    {
        if (!_disposed)
        {
            UpdateFitSize();
        }
    }

    private void OnImageChanged(object? image, bool isFileChange) => HandleImageChanged(image, isFileChange); // WP-21

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewport.InvalidateLayout();
        _window.Invalidate();
    }

    private void OnDpiChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        UpdateTargetDecodeBox();
        _viewport.InvalidateLayout();
        _window.Invalidate();
    }

    // Đóng ----------------------------------------------------------------------------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // R7-7: đóng giữa lúc file action giữ cổng sẽ giết Move liên ổ giữa chừng: giữ cửa sổ, đóng khi action xong.
        if (_viewModel.DeferCloseForFileAction())
        {
            e.Cancel = true;
            if (!_closeWhenFileActionDone)
            {
                _closeWhenFileActionDone = true;
                _ = CloseWhenFileActionDoneAsync();
            }

            return;
        }

        IsClosingOrClosed = true;
        SavePlacement();
    }

    private async Task CloseWhenFileActionDoneAsync()
    {
        try
        {
            await _viewModel.WhenFileActionIdleAsync();
            _closeWhenFileActionDone = false;
            if (!_disposed && !_window.IsDestroyed)
            {
                _window.Close();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Close after file action failed", ex);
        }
    }

    private void SavePlacement()
    {
        if (_options.PlacementFile is { } file)
        {
            ShellPlacement.Save(_window.Hwnd, file, _placementStore, _placementNative, fullscreenRestoreState: null, _log);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        IsClosingOrClosed = true;
        _cloak.Stop(); // đóng trước khung đầu: hạn chót an toàn không được giữ cửa sổ sống
        try
        {
            _viewModel.CloseSession();
        }
        finally
        {
            // Worker preload phải dừng kể cả khi đóng phiên ném.
            (_viewModel.PreloadController as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Giải phóng theo thứ tự ngược với khởi tạo: ngắt sự kiện (không còn gì chạm view model/cửa sổ) -> cache ảnh GPU (trả bitmap
    /// qua thiết bị) -> bề mặt/thiết bị D3D -> viewport -> cửa sổ native. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.ImageChanged -= _onImageChanged;
        _viewModel.PropertyChanged -= _onViewModelChanged;
        _viewModel.Viewer.PropertyChanged -= _onViewerChanged;
        _window.Render -= _onRender;
        _window.ClientSizeChanged -= OnClientSizeChanged;
        _window.DpiChanged -= OnDpiChanged;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _sizeSource.Get = static () => (0, 0);
        _cloak.Stop();
        _image = null;
        DisposeViewing(); // WP-21: trước cache (lớp ảnh giữ tham chiếu bitmap GPU)
        Interlocked.Exchange(ref _cache, null)?.Dispose();
        Interlocked.Exchange(ref _surface, null)?.Dispose();
        _viewport.Dispose();
        _window.Dispose();
    }
}

/// <summary>Seam tạo bề mặt vẽ (test dùng WARP offscreen/giả lỗi); production dùng <see cref="D2DRenderSurface.CreateWindowSurface"/>.</summary>
internal interface IRenderSurfaceFactoryHook
{
    D2DRenderSurface Create(nint hwnd, RenderSurfaceOptions options);
}
