using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

// WP-23: overlay HUD (toolbar, trạng thái, thông tin thư mục, zoom, huy hiệu cặp ảnh) + auto-hide + tooltip của cửa sổ chính.
// Điểm chạm với phần chung: ShellStartup gọi AttachOverlay một lần sau AttachRenderer; Present() gọi DrawOverlay sau lớp ảnh
// trong CÙNG khung; Dispose gọi DisposeOverlay. Mọi logic nằm trong ShellOverlay (test không cần HWND).
internal sealed partial class ShellMainWindow
{
    private ShellOverlay? _overlay;
    private DWriteTextRenderer? _overlayText;
    private OverlayMessageHandler? _overlayHandler;
    private SettingsStore? _overlaySettings;
    private EventHandler<AppSettings>? _onOverlaySettingsChanged;
    private EventHandler? _onLanguageChanged;
    private PropertyChangedEventHandler? _onOverlaySourceChanged;
    private bool _overlayRefreshPosted;
    private OverlayFitSurface? _fitSurface;
    private FitViewController? _fitController;

    /// <summary>Bộ overlay (null trước <see cref="AttachOverlay"/> và sau dispose).</summary>
    internal ShellOverlay? Overlay => _overlay;

    /// <summary>Kho cài đặt mà overlay đang nghe (test đổi cài đặt qua nó).</summary>
    internal SettingsStore? OverlaySettingsStore => _overlaySettings;

    /// <summary>
    /// Lệnh Fit của nút toolbar. Gói input (WP-22) đặt bản dùng chung với <c>PointerInputController</c> (cùng
    /// <c>ViewportOperationVersion</c>); chưa đặt thì dùng <c>FitViewController</c> riêng trên <see cref="Viewport"/>.
    /// </summary>
    internal Func<Task>? FitHandler { get; set; }

    /// <summary>
    /// Dựng overlay: đọc <c>SettingsStore</c> (đổi cài đặt -&gt; đánh giá lại auto-hide), menu Tools (host menu đã đăng ký hoặc
    /// menu native của shell), tooltip native, chữ DirectWrite; nối <c>Localizer.CurrentChanged</c> và các <c>PropertyChanged</c>
    /// của view model. Gọi một lần, sau khi cửa sổ có HWND và trước khung đầu.
    /// </summary>
    internal void AttachOverlay(IServiceProvider services, IFrameClock frameClock)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameClock);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_overlay is not null)
        {
            throw new InvalidOperationException("The overlay is already attached.");
        }

        _overlayText = new DWriteTextRenderer();
        // Tooltip native tạo SAU khung đầu (Background): tốn ~3-4 ms nạp comctl32 v6, chỉ cần khi chuột dừng trên nút.
        var tooltips = new DeferredTooltips(() => new NativeTooltipHost(Hwnd), action => _dispatcher.Post(action, UiPriority.Background));
        var menus = services.GetService<IPopupMenuHost>() ?? new NativePopupMenuHost(() => Hwnd);
        _overlaySettings = services.GetService<SettingsStore>();

        var overlay = new ShellOverlay(new ShellOverlayOptions
        {
            Text = _overlayText,
            Clock = frameClock,
            Settings = () => _viewModel.Settings,
            Commands = new ViewModelOverlayCommands(_viewModel, FitAsync),
            RequestRender = _window.Invalidate,
            IsWindowActive = () => _window.IsActive,
            Menus = menus,
            Tooltips = tooltips,
            ClientOriginOnScreenDip = ClientOriginOnScreenDip,
            OnCommandFailed = OnOverlayCommandFailed,
        });
        _overlay = overlay;
        overlay.Update(OverlayModel.From(_viewModel));
        _overlayHandler = new OverlayMessageHandler(overlay, _window, tooltips);
        _window.InsertMessageHandlerFirst(_overlayHandler);

        _onOverlaySourceChanged = OnOverlaySourceChanged;
        _viewModel.PropertyChanged += _onOverlaySourceChanged;
        _viewModel.InfoOverlay.PropertyChanged += _onOverlaySourceChanged;
        _viewModel.Compare.PropertyChanged += _onOverlaySourceChanged;
        _window.Activated += OnOverlayWindowActivated;
        _window.Deactivated += OnOverlayWindowDeactivated;

        if (_overlaySettings is { } store)
        {
            _onOverlaySettingsChanged = (_, settings) =>
            {
                // Như _onSettingsChanged của MainWindow WPF: view model thấy cài đặt mới trước khi overlay đánh giá lại.
                _viewModel.Settings = settings;
                _overlay?.SettingsChanged();
                MarkOverlayModelDirty();
            };
            store.Changed += _onOverlaySettingsChanged;
        }

        _onLanguageChanged = (_, _) => _dispatcher.Post(OnLanguageChangedOnUi);
        Localizer.CurrentChanged += _onLanguageChanged;
    }

    /// <summary>Vẽ overlay sau lớp ảnh, trong cùng khung (gọi từ <c>Present</c> giữa BeginDraw và EndDraw).</summary>
    private void DrawOverlay(D2DDrawContext context)
    {
        _overlay?.Render(context, _window.ClientSizeDip, _window.DpiScale);
    }

    private void DisposeOverlay()
    {
        if (_onLanguageChanged is not null)
        {
            Localizer.CurrentChanged -= _onLanguageChanged;
            _onLanguageChanged = null;
        }

        if (_overlaySettings is not null && _onOverlaySettingsChanged is not null)
        {
            _overlaySettings.Changed -= _onOverlaySettingsChanged;
        }

        _onOverlaySettingsChanged = null;
        if (_onOverlaySourceChanged is not null)
        {
            _viewModel.PropertyChanged -= _onOverlaySourceChanged;
            _viewModel.InfoOverlay.PropertyChanged -= _onOverlaySourceChanged;
            _viewModel.Compare.PropertyChanged -= _onOverlaySourceChanged;
            _onOverlaySourceChanged = null;
        }

        _window.Activated -= OnOverlayWindowActivated;
        _window.Deactivated -= OnOverlayWindowDeactivated;
        Interlocked.Exchange(ref _overlay, null)?.Dispose();
        Interlocked.Exchange(ref _overlayText, null)?.Dispose();
    }

    private void OnOverlaySourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _overlay is null)
        {
            return;
        }

        // Điều hướng là hoạt động (ảnh mới hiện thông tin trong độ trễ); phần còn lại chỉ đổi dữ liệu.
        if (ReferenceEquals(sender, _viewModel) && e.PropertyName is nameof(MainViewModel.CurrentIndex) or nameof(MainViewModel.CurrentImage))
        {
            _overlay.NoteActivity();
        }

        MarkOverlayModelDirty();
    }

    /// <summary>Gộp mọi thay đổi trong một lượt dispatcher ở mức Render (đọc model một lần, trước khung kế).</summary>
    private void MarkOverlayModelDirty()
    {
        if (_overlayRefreshPosted || _disposed)
        {
            return;
        }

        _overlayRefreshPosted = true;
        _dispatcher.Post(RefreshOverlayModel, UiPriority.Render);
    }

    private void RefreshOverlayModel()
    {
        _overlayRefreshPosted = false;
        if (!_disposed && _overlay is { } overlay)
        {
            overlay.Update(OverlayModel.From(_viewModel));
        }
    }

    private void OnLanguageChangedOnUi()
    {
        if (_disposed || _overlay is null)
        {
            return;
        }

        _viewModel.RefreshLocalizedText();
        _overlay.ApplyLocalization();
        _overlay.Update(OverlayModel.From(_viewModel));
    }

    private void OnOverlayWindowActivated(object? sender, EventArgs e) => _overlay?.WindowActivated();

    private void OnOverlayWindowDeactivated(object? sender, EventArgs e) => _overlay?.WindowDeactivated();

    private PointD ClientOriginOnScreenDip()
    {
        (int x, int y) = OverlayNative.ClientOriginOnScreen(_window.Hwnd);
        double dpi = _window.DpiScale <= 0 ? 1 : _window.DpiScale;
        return new PointD(x / dpi, y / dpi);
    }

    private void OnOverlayCommandFailed(string actionName, Exception exception)
    {
        // Có thể gọi từ continuation ngoài UI thread: về UI thread rồi mới chạm view model.
        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _log.Error($"{actionName} failed", exception);
            _viewModel.StatusText = StatusFormatter.ActionFailed(actionName, UserFacingError.Describe(exception));
        });
    }

    private Task FitAsync()
    {
        if (FitHandler is { } handler)
        {
            return handler();
        }

        _fitSurface ??= new OverlayFitSurface(_viewport, _viewModel.Viewer);
        _fitController ??= new FitViewController(_fitSurface, _viewModel.Viewer, new ViewportOperationVersion(), static () => { });
        return _fitController.ApplyFitAsync();
    }

    /// <summary><c>ViewportController</c> + <c>ViewerState</c> dưới dạng <see cref="IFitSurface"/> cho nút Fit của toolbar.</summary>
    private sealed class OverlayFitSurface(ViewportController viewport, ViewerState viewer) : IFitSurface
    {
        public (double Width, double Height) ViewportSize => viewport.ViewportSize;

        public bool IsLoaded => viewport.IsLoaded;

        public void UpdateLayout() => viewport.UpdateLayout();

        public void UpdateFitSize() => viewport.UpdateFitSize();

        public Task YieldToRenderAsync() => viewport.YieldToRenderAsync();

        public ViewportSnapshot Capture() => new(
            viewer.Zoom,
            viewer.Stretch,
            viewer.MaxImageWidth,
            viewer.MaxImageHeight,
            viewport.ImageActualWidth,
            viewport.ImageActualHeight,
            viewport.ExtentWidth,
            viewport.ExtentHeight,
            viewport.ViewportWidth,
            viewport.ViewportHeight,
            viewport.HorizontalOffset,
            viewport.VerticalOffset,
            viewport.Layout.HorizontalBarVisible,
            viewport.Layout.VerticalBarVisible);

        public void ScrollHome() => viewport.ScrollHome();
    }
}

/// <summary>Lệnh toolbar/menu Tools trên <see cref="MainViewModel"/> (cùng lệnh với handler Click của <c>MainWindow</c> WPF).</summary>
internal sealed class ViewModelOverlayCommands(MainViewModel viewModel, Func<Task> fit) : IOverlayCommands
{
    public Task OpenFolderAsync() => viewModel.PickAndOpenFolderAsync();

    public void ShowSettings() => viewModel.ShowSettings();

    public Task FitAsync() => fit();

    public void ShowSkippedFiles() => viewModel.ShowSkippedFiles();

    public void ShowRecovery() => viewModel.ShowRecovery();

    public void ShowDiagnostics() => viewModel.ShowDiagnostics();

    public void ShowBenchmark() => viewModel.ShowBenchmark();

    public Task ClearCacheAsync() => viewModel.ClearCacheAsync();

    public Task RemoveDuplicatesAsync(bool numbered) => viewModel.RemoveDuplicatesAsync(numbered);
}
