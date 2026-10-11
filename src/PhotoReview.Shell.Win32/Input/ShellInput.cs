using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Localization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: những gì <see cref="ShellInput"/> cần từ cửa sổ chính ngoài viewport (bản Win32 của các lời gọi
/// <c>_viewModel.*</c> trong <c>MainWindow.Window_KeyDown</c>/<c>ExecuteReviewCommandAsync</c>). Test cung cấp bản giả.
/// </summary>
internal interface IShellInputHost
{
    bool HasImages { get; }

    bool IsFullscreen { get; }

    bool IsCompareVisible { get; }

    bool CurrentHasComparePair { get; }

    bool CurrentHasCapturePair { get; }

    Task NextAsync();

    Task PreviousAsync();

    /// <summary><c>ViewerState.ZoomToActualSize</c> (lệnh của <see cref="PointerCommands"/>).</summary>
    void ZoomActualSize();

    /// <summary>Esc khi đang băm trùng lặp: huỷ việc băm và nuốt phím (Q-R25).</summary>
    bool CancelDuplicateCheck();

    /// <summary>
    /// Lệnh không thuộc viewport (điều hướng thư mục, file action, fullscreen, Close, OpenFolder, CustomZoom, Refresh...), y như
    /// các nhánh còn lại của <c>MainWindow.ExecuteReviewCommandAsync</c>.
    /// </summary>
    Task ExecuteAsync(ReviewCommand command);

    /// <summary>Một lệnh thất bại: log và hiện "action failed" (như <c>MainWindow.RunGuardedAsync</c>).</summary>
    void ReportActionFailed(string actionName, Exception exception);
}

/// <summary>
/// WP-22 (NO-WPF-EXEC-PLAN, thẻ WP-22): nhập liệu của shell, bản Win32 của phần chuột/phím trong <c>MainWindow</c> WPF.
/// Sở hữu <see cref="PointerInputController"/>, <see cref="FitViewController"/> và <see cref="ShortcutRouter"/> THẬT (App.Shared,
/// không sửa) trên <see cref="ViewportImageSurface"/>, và đăng ba <see cref="IWindowMessageHandler"/> vào cửa sổ:
/// <see cref="KeyboardMessageHandler"/>, <see cref="PointerMessageHandler"/>, <see cref="WheelMessageHandler"/>. Các handler chỉ
/// dịch thông điệp (toạ độ pixel -> DIP, cờ MK_*, bit lParam) rồi gọi <see cref="HandleKeyDown"/>/<see cref="HandlePointerDown"/>/...;
/// quyết định (thứ tự phím, ai nhận chuột) nằm ở đây, giữ đúng thứ tự của <c>Window_KeyDown</c>:
/// pan phím mũi tên -> Esc đóng popup/huỷ băm trùng lặp -> Space/Enter của nút -> <see cref="ShortcutRouter"/> -> lệnh.
/// Chỉ dùng trên UI thread.
/// </summary>
internal sealed class ShellInput : IDisposable
{
    private readonly IShellWindow _window;
    private readonly ViewportController _viewport;
    private readonly IShellInputHost _host;
    private readonly ShellInputEnvironment _environment;
    private readonly Func<AppSettings> _settings;
    private readonly ILog? _log;
    private readonly ViewportOperationVersion _version = new();
    private readonly ShortcutRouter _router;
    private readonly EventHandler _onDeactivated;
    private bool _disposed;

    public ShellInput(IShellWindow window, ViewportController viewport, ViewerState viewer, Func<AppSettings> settings,
        IShellInputHost host, ShellInputEnvironment? environment = null, ILog? log = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        Viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _environment = environment ?? new ShellInputEnvironment();
        _log = log;

        Surface = new ViewportImageSurface(viewport, viewer);
        Pointer = new PointerInputController(Surface, viewer, settings, _version,
            new PointerCommands(() => host.HasImages, host.NextAsync, host.PreviousAsync, host.ZoomActualSize, ApplyFitAsync));
        Fit = new FitViewController(Surface, viewer, _version, Pointer.CancelPan);
        _router = new ShortcutRouter(settings());

        // MainWindow.WireViewModelEvents: mọi đổi zoom (wheel, phím, click, Fit) dừng đà; rời cửa sổ cũng vậy.
        Viewer.ZoomModeChanged += OnZoomModeChanged;
        _onDeactivated = (_, _) => Pointer.StopKinetic();
        _window.Deactivated += _onDeactivated;

        _window.AddMessageHandler(new KeyboardMessageHandler(this));
        _window.AddMessageHandler(new PointerMessageHandler(this));
        _window.AddMessageHandler(new WheelMessageHandler(this));
    }

    public ViewerState Viewer { get; }

    public ViewportImageSurface Surface { get; }

    public PointerInputController Pointer { get; }

    public FitViewController Fit { get; }

    public ClickCounter Clicks { get; } = new();

    /// <summary>
    /// Điểm cắm của WP-23 (overlay/toolbar): true khi điểm (DIP, toạ độ client) nằm trên một phần tử overlay nhận chuột
    /// (toolbar, popup Tools, panel). Điểm đó không bao giờ là nhấn/kéo/cuộn của ảnh. Mặc định: không có overlay.
    /// </summary>
    public Func<PointD, bool>? OverlayHitTest { get; set; }

    /// <summary>
    /// Điểm cắm của WP-23: nút/ô so sánh đang giữ focus bàn phím sở hữu Space/Enter (kích hoạt bằng phím), nên cửa sổ không được
    /// lấy chúng làm lệnh (Skip / Move to folder 2). Mặc định false (không phần tử focusable nào ngoài cửa sổ).
    /// </summary>
    public Func<bool>? ActivationKeyOwned { get; set; }

    /// <summary>Điểm cắm của WP-23: Esc khi popup Tools mở đóng popup, không đóng ứng dụng (R7-6). Trả true = đã xử lý và nuốt phím.</summary>
    public Func<bool>? EscapeInterceptor { get; set; }

    /// <summary>Vị trí con trỏ (DIP, client) của lần di chuyển gần nhất; null khi chưa có.</summary>
    public PointD? LastPointerPosition { get; private set; }

    /// <summary>Có hoạt động chuột (di chuyển, wheel ngang): <c>Window_MouseMove</c>/<c>NoteInfoActivity</c> của WPF (auto-hide của WP-23).</summary>
    public event EventHandler? PointerActivity;

    internal ShellInputEnvironment Environment => _environment;

    internal ShortcutRouter Router => _router;

    // ---- cài đặt / trạng thái ----

    /// <summary><c>_shortcutRouter.Rebuild(s)</c> khi cài đặt đổi (R2-F-27: không dựng lại mỗi lần bấm phím).</summary>
    public void RebuildShortcuts(AppSettings settings) => _router.Rebuild(settings);

    /// <summary><c>_pointer.OnCurrentIndexChanged</c>: điều hướng dừng đà (đổi bản đầy đủ của cùng ảnh thì không).</summary>
    public void NoteCurrentIndexChanged(int index) => Pointer.OnCurrentIndexChanged(index);

    /// <summary>Hook <c>ViewModel.Presenter</c> -> InitialViewMode (PR-B): Fit width/height có vị trí cuộn riêng.</summary>
    public Task ApplyInitialViewAsync()
    {
        var settings = _settings();
        return Pointer.ApplyInitialViewAsync(settings.InitialViewMode, settings.ClickZoomPercent);
    }

    public Task ApplyFitAsync() => Fit.ApplyFitAsync();

    private void OnZoomModeChanged(object? sender, EventArgs e) => Pointer.StopKinetic();

    // ---- vùng chuột ----

    private double PixelsToDip(int pixels) => pixels / _window.DpiScale;

    internal PointD ClientDip(int pixelX, int pixelY) => new(PixelsToDip(pixelX), PixelsToDip(pixelY));

    /// <summary>Toạ độ màn hình (pixel; lParam của wheel) -> client DIP.</summary>
    internal PointD ScreenToClientDip(PointD screenPixel) => _window.ScreenToClientDip(screenPixel);

    /// <summary>Điểm thuộc phần tử ảnh (<c>MainImage</c>: chỉ chữ nhật ảnh nhận PreviewMouseLeftButtonDown/Move/Up của WPF).</summary>
    internal bool IsOverImage(PointD point)
    {
        var origin = _viewport.ImageOrigin;
        return new RectD(origin.X, origin.Y, _viewport.ImageActualWidth, _viewport.ImageActualHeight).Contains(point);
    }

    /// <summary>Điểm trong vùng nhìn ImageScroll (không tính thanh cuộn đang chiếm chỗ).</summary>
    internal bool IsInsideViewport(PointD point) =>
        point.X >= 0 && point.Y >= 0 && point.X < _viewport.ViewportWidth && point.Y < _viewport.ViewportHeight;

    internal bool IsOverOverlay(PointD point) => OverlayHitTest?.Invoke(point) == true;

    // ---- phím ----

    /// <summary>
    /// <c>MainWindow.Window_KeyDown</c>. <paramref name="isSystemKey"/> = WM_SYSKEYDOWN (WPF: <c>Key.System</c> với <c>SystemKey</c>
    /// là phím thật). Trả true = đã xử lý (không rơi xuống DefWindowProc: không bíp, không mở menu hệ thống).
    /// </summary>
    internal bool HandleKeyDown(KeyId key, bool isSystemKey, bool isRepeat, int messageTime)
    {
        var modifiers = _environment.Modifiers();

        // feat/zoom-key-anchor: mũi tên mà TryPanByArrow nuốt tự quyết định đà; mọi phím khác dừng đà vô điều kiện.
        var arrowConsumed = modifiers == KeyModifiers.None && !_host.IsCompareVisible && Pointer.TryPanByArrow(key, isRepeat);
        if (!arrowConsumed)
        {
            Pointer.StopKinetic();
        }

        if (PhotoReviewPerf.Log.IsEnabled())
        {
            PhotoReviewPerf.Log.KeyInput(0, key.ToString(), unchecked(_environment.TickCount() - messageTime));
        }

        if (arrowConsumed)
        {
            return true;
        }

        // R7-6: Esc khi popup Tools mở đóng popup, không đóng cả ứng dụng.
        if (key == KeyId.Escape && EscapeInterceptor?.Invoke() == true)
        {
            return true;
        }

        // Q-R25: Esc khi đang băm trùng lặp: huỷ (trước khi thoát fullscreen/đóng cửa sổ), phím bị nuốt.
        if (key == KeyId.Escape && _host.CancelDuplicateCheck())
        {
            return true;
        }

        // Space/Enter thuộc nút/ô so sánh đang giữ focus (kích hoạt bằng phím): cửa sổ không lấy làm lệnh.
        if (key is KeyId.Space or KeyId.Enter && ActivationKeyOwned?.Invoke() == true)
        {
            return false;
        }

        var command = _router.TryResolve(isSystemKey ? KeyId.System : key, isSystemKey ? key : KeyId.None, modifiers,
            _host.IsFullscreen, _host.HasImages, hasComparePair: _host.CurrentHasComparePair,
            isCompareVisible: _host.IsCompareVisible, hasCapturePair: _host.CurrentHasCapturePair);
        if (command is null)
        {
            return false;
        }

        // R2-F-06: file action không bao giờ lặp khi giữ phím; phím vẫn bị nuốt.
        if (!(isRepeat && command.Value.Type.IgnoresAutoRepeat()))
        {
            ExecuteInBackground(command.Value);
        }

        return true;
    }

    private void ExecuteInBackground(ReviewCommand command) => _ = ExecuteGuardedAsync(command);

    private async Task ExecuteGuardedAsync(ReviewCommand command)
    {
        try
        {
            await ExecuteAsync(command);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Error($"Command {command.Type} failed", ex);
            _host.ReportActionFailed(command.Type.ToString(), ex);
        }
    }

    /// <summary>
    /// <c>MainWindow.ExecuteReviewCommandAsync</c>: phần viewport (zoom, Fit) chạy trên controller thật; phần còn lại do host.
    /// Dùng chung cho đường phím và đường nhấn chuột giữa.
    /// </summary>
    public async Task ExecuteAsync(ReviewCommand command)
    {
        switch (command.Type)
        {
            case ReviewCommandType.ZoomActualSize:
                await Pointer.ZoomActualSizeAsync();
                break;
            case ReviewCommandType.ToggleFit:
                ApplyFitAsync().FireAndLog("Toggle fit failed");
                break;
            case ReviewCommandType.ZoomIn:
                await Pointer.ZoomInAsync();
                break;
            case ReviewCommandType.ZoomOut:
                await Pointer.ZoomOutAsync();
                break;
            case ReviewCommandType.ClickZoom:
                await Pointer.ToggleClickZoomAsync();
                break;
            case ReviewCommandType.FitWidth or ReviewCommandType.FitWidth2:
                await Pointer.FitWidthAsync(MainWindowHelpers.FitWidthAnchorFor(_settings(), command.Type));
                break;
            case ReviewCommandType.FitHeight:
                await Pointer.FitHeightAsync();
                break;
            default:
                await _host.ExecuteAsync(command);
                break;
        }
    }

    // ---- chuột ----

    /// <summary>
    /// Nhấn nút trên cửa sổ. <c>Window_PreviewMouseDown</c> chạy trước cho MỌI nút; rồi nút trái trên chữ nhật ảnh vào
    /// <c>OnImagePress</c>, nút giữa trong vùng nhìn chạy lệnh <c>MiddleClickAction</c> (chỉ lần nhấn đầu của chuỗi).
    /// </summary>
    internal bool HandlePointerDown(PointerButton button, int pixelX, int pixelY)
    {
        var time = _environment.MessageTime();
        var (slopWidth, slopHeight) = _environment.DoubleClickSizePixels(InputNative.DpiOf(_window.DpiScale));
        var clickCount = Clicks.Register(button, pixelX, pixelY, time, _environment.DoubleClickTimeMs(), slopWidth, slopHeight);
        Pointer.OnWindowPreviewMouseDown();

        var point = ClientDip(pixelX, pixelY);
        if (IsOverOverlay(point) || _host.IsCompareVisible)
        {
            return false;
        }

        switch (button)
        {
            case PointerButton.Left when IsOverImage(point):
                return Pointer.OnImagePress(PointerButton.Left, clickCount, point, time);
            case PointerButton.Middle when IsInsideViewport(point):
                if (clickCount == 1 && _host.HasImages && MiddleClickResolver.Resolve(_settings().MiddleClickAction) is { } command)
                {
                    ExecuteInBackground(command);
                }

                return true; // ImageScroll_PreviewMouseDown: e.Handled = true cho mọi nhấn nút giữa trong vùng nhìn
            default:
                return false;
        }
    }

    /// <summary>Di chuyển: tới ảnh khi con trỏ trên ảnh hoặc ảnh đang giữ capture (như sự kiện chuột trực tiếp/capture của WPF).</summary>
    internal void HandlePointerMove(int pixelX, int pixelY, bool leftButtonPressed)
    {
        var point = ClientDip(pixelX, pixelY);
        LastPointerPosition = point;
        PointerActivity?.Invoke(this, EventArgs.Empty);
        if (IsOverOverlay(point) && !_window.HasPointerCapture)
        {
            return;
        }

        if (_window.HasPointerCapture || (IsOverImage(point) && !_host.IsCompareVisible))
        {
            Pointer.OnImageMove(leftButtonPressed, point, _environment.MessageTime());
        }
    }

    internal void HandleLeftButtonUp(int pixelX, int pixelY)
    {
        var point = ClientDip(pixelX, pixelY);
        if (_window.HasPointerCapture || (IsOverImage(point) && !IsOverOverlay(point) && !_host.IsCompareVisible))
        {
            Pointer.OnImageRelease(point, _environment.MessageTime());
        }
    }

    internal void HandleCaptureLost() => Pointer.OnLostCapture();

    // ---- wheel ----

    /// <summary>
    /// WM_MOUSEWHEEL: <c>ImageScroll_PreviewMouseWheel</c> (luôn nuốt). Điểm ở toạ độ client (DIP); trong overlay hoặc khi
    /// so sánh phủ viewport thì wheel không thuộc ảnh.
    /// </summary>
    internal bool HandleVerticalWheel(int delta, bool ctrl, PointD clientDip)
    {
        if (IsOverOverlay(clientDip) || _host.IsCompareVisible)
        {
            return false;
        }

        var input = new WheelInput(delta, Horizontal: false, ctrl, _environment.MessageTime(), _environment.WheelDevice());
        RunGuarded(Tr.MainMenuZoom, () => Pointer.OnWheelAsync(input, clientDip));
        return true;
    }

    /// <summary>
    /// WM_MOUSEHWHEEL (vuốt ngang touchpad, wheel nghiêng): chỉ trên vùng nhìn của ảnh và không khi so sánh phủ; dấu thời gian là
    /// <c>Environment.TickCount</c> (như hook của WPF, không có <c>e.Timestamp</c>).
    /// </summary>
    internal bool HandleHorizontalWheel(int delta, bool ctrl, PointD clientDip)
    {
        if (!_window.IsLoaded || _host.IsCompareVisible || !IsInsideViewport(clientDip))
        {
            return false;
        }

        PointerActivity?.Invoke(this, EventArgs.Empty);
        var input = new WheelInput(delta, Horizontal: true, ctrl, _environment.TickCount(), _environment.WheelDevice());
        RunGuarded(Tr.MainMenuZoom, () => Pointer.OnWheelAsync(input, clientDip));
        return true;
    }

    private void RunGuarded(string actionName, Func<Task> action) => _ = RunGuardedAsync(actionName, action);

    private async Task RunGuardedAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Error($"{actionName} failed", ex);
            _host.ReportActionFailed(actionName, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Viewer.ZoomModeChanged -= OnZoomModeChanged;
        _window.Deactivated -= _onDeactivated;
        Pointer.OnWindowClosed();
    }
}
