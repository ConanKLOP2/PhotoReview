using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>Phụ thuộc của <see cref="ShellOverlay"/> (không có mặc định ẩn: test tiêm bản giả từng cái).</summary>
internal sealed class ShellOverlayOptions
{
    public required ITextRenderer Text { get; init; }

    public required IFrameClock Clock { get; init; }

    /// <summary>Cài đặt hiện hành (đọc mỗi lần đánh giá, không cache).</summary>
    public required Func<AppSettings> Settings { get; init; }

    public required IOverlayCommands Commands { get; init; }

    /// <summary>Yêu cầu vẽ một khung (gộp).</summary>
    public required Action RequestRender { get; init; }

    /// <summary>Cửa sổ chính đang active (overlay thông tin luôn hiện khi không active).</summary>
    public required Func<bool> IsWindowActive { get; init; }

    public IPopupMenuHost? Menus { get; init; }

    public IOverlayTooltips? Tooltips { get; init; }

    /// <summary>Gốc vùng client trên màn hình, DIP của DPI cửa sổ (neo menu Tools). Mặc định (0,0).</summary>
    public Func<PointD>? ClientOriginOnScreenDip { get; init; }

    /// <summary>Báo lỗi một lệnh bất đồng bộ (tên lệnh, ngoại lệ).</summary>
    public Action<string, Exception>? OnCommandFailed { get; init; }

    /// <summary>Dựng/hiện menu Tools ném ngoại lệ: ghi shell-errors.log + dòng trạng thái "Menu error"; null thì dùng <see cref="OnCommandFailed"/>.</summary>
    public Action<Exception>? OnMenuFailed { get; init; }

    /// <summary>Đồng hồ Stopwatch cho hẹn giờ auto-hide (test).</summary>
    public Func<long>? Timestamp { get; init; }

    public long? TicksPerSecond { get; init; }

    /// <summary><see cref="Animator"/> ticksPerSecond (test).</summary>
    public long? AnimatorTicksPerSecond { get; init; }
}

/// <summary>
/// WP-23: bộ overlay HUD của shell - sở hữu <see cref="OverlayHost"/> với cây của <c>MainWindow.xaml</c> (ToolbarPanel ở z 100,
/// <c>InfoOverlayHost</c> chứa StatusPanel/FolderInfoPanel/ZoomIndicatorPanel/CapturePairBadge), <see cref="Animator"/>,
/// logic auto-hide (<see cref="ToolbarAutoHidePolicy"/>, <see cref="InfoOverlayAutoHidePolicy"/>: fade vào 150 ms / ra 200 ms,
/// hot zone 24 DIP, độ trễ theo cài đặt), nút toolbar (hover/nhấn/bàn phím/click), menu Tools, tooltip. Không biết
/// <c>MainViewModel</c> hay HWND: dữ liệu vào bằng <see cref="Update"/> (<see cref="OverlayModel"/>), chuột/phím bằng các
/// phương thức <c>Pointer*</c>/<see cref="KeyDown"/>, vẽ bằng <see cref="Render"/>. UI thread.
/// </summary>
internal sealed class ShellOverlay : IDisposable
{
    internal const double HotZoneMargin = 24;
    internal const int FadeInMs = 150;
    internal const int FadeOutMs = 200;

    private readonly ShellOverlayOptions _options;
    private readonly OverlayHost _host = new();
    private readonly Animator _animator;
    private readonly FrameDelayTimer _toolbarTimer;
    private readonly FrameDelayTimer _infoTimer;
    private readonly FadeTargetGate _toolbarGate = new();
    private readonly FadeTargetGate _infoGate = new();
    private readonly List<OverlayTooltip> _tooltipScratch = [];

    private OverlayModel _model = new();
    private ButtonElement? _hot;
    private ButtonElement? _pressed;
    private bool _pointerDown;
    private bool _toolbarMouseInsideHotZone = true; // "trong" cho tới khi chuột báo ngược lại (như bản WPF)
    private bool _toolbarWasKeptVisible = true;
    private bool _toolsOpen;
    private bool _infoIdleElapsed;
    private bool _infoWasKeptVisible = true;
    private SizeD _lastClient;
    private double _lastDpi = 1;
    private bool _disposed;

    public ShellOverlay(ShellOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        Toolbar = new ToolbarPanel { IsVisible = true };
        Status = new StatusPanel();
        FolderInfo = new FolderInfoPanel();
        ZoomIndicator = new ZoomIndicatorPanel();
        CapturePair = new CapturePairBadge();
        InfoLayer = new LayerElement("InfoOverlayHost");
        InfoLayer.Add(Status).Add(FolderInfo).Add(ZoomIndicator).Add(CapturePair);
        _host.Add(InfoLayer);
        _host.Add(Toolbar); // ToolbarPanel Panel.ZIndex=100: trên cùng

        _animator = options.AnimatorTicksPerSecond is { } tps
            ? new Animator(options.Clock, _host.Resolve, tps)
            : new Animator(options.Clock, _host.Resolve);
        _animator.Changed += OnAnimationChanged;
        _toolbarTimer = new FrameDelayTimer(options.Clock, OnToolbarHideTimer, options.Timestamp, options.TicksPerSecond);
        _infoTimer = new FrameDelayTimer(options.Clock, OnInfoIdleTimer, options.Timestamp, options.TicksPerSecond);

        Toolbar.OpenFolder.Clicked += (_, _) => RunGuarded(Tr.MainMenuOpenFolder.TrimEnd('…', '.', ' '), _options.Commands.OpenFolderAsync);
        Toolbar.Settings.Clicked += (_, _) => _options.Commands.ShowSettings();
        Toolbar.Fit.Clicked += (_, _) => RunGuarded(Tr.MainToolbarFit, _options.Commands.FitAsync);
        Toolbar.Tools.Clicked += (_, _) => ShowToolsMenu();
        Status.ViewList.Clicked += (_, _) => _options.Commands.ShowSkippedFiles();

        ApplyModel();
        ApplyToolbarVisibility();
        ApplyInfoOverlayVisibility();
    }

    public OverlayHost Host => _host;

    public ToolbarPanel Toolbar { get; }

    public StatusPanel Status { get; }

    public FolderInfoPanel FolderInfo { get; }

    public ZoomIndicatorPanel ZoomIndicator { get; }

    public CapturePairBadge CapturePair { get; }

    /// <summary><c>InfoOverlayHost</c>: lớp được fade/click-through bởi auto-hide.</summary>
    public LayerElement InfoLayer { get; }

    public Animator Animator => _animator;

    public OverlayModel Model => _model;

    /// <summary>Menu Tools đang mở (nút ⋮ ở trạng thái bật; ép toolbar hiện).</summary>
    public bool IsToolsMenuOpen => _toolsOpen;

    /// <summary>Toolbar giữ bàn phím (<c>IsKeyboardFocusWithin</c>).</summary>
    public bool HasKeyboardFocus => Toolbar.HasKeyboardFocus;

    // Dữ liệu ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Nhận trạng thái mới từ view model. Như các handler <c>PropertyChanged</c> của MainWindow: <c>HasImages</c> đổi -&gt;
    /// đánh giá lại toolbar + overlay thông tin; trạng thái/cảnh báo đổi -&gt; đánh giá lại overlay thông tin; Compare mở/đóng
    /// -&gt; tính là hoạt động (hiện lại và đếm giờ).
    /// </summary>
    public void Update(OverlayModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_disposed)
        {
            return;
        }

        OverlayModel old = _model;
        _model = model;
        ApplyModel();
        if (old.HasImages != model.HasImages)
        {
            ApplyToolbarVisibility();
        }

        if (old.CompareVisible != model.CompareVisible)
        {
            NoteInfoActivity();
        }
        else if (old.HasImages != model.HasImages || old.StatusText != model.StatusText
            || old.HasSkippedEntries != model.HasSkippedEntries || old.StatusNeedsAttention != model.StatusNeedsAttention)
        {
            ApplyInfoOverlayVisibility();
        }

        _options.RequestRender();
    }

    /// <summary>Cài đặt đổi (auto-hide bật/tắt, độ trễ, độ mờ...): như <c>_onSettingsChanged</c> của MainWindow.</summary>
    public void SettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        ApplyToolbarVisibility();
        NoteInfoActivity();
        _options.RequestRender();
    }

    /// <summary><c>Localizer.CurrentChanged</c>: đổi chữ của mọi phần tử địa phương hoá rồi vẽ lại.</summary>
    public void ApplyLocalization()
    {
        if (_disposed)
        {
            return;
        }

        Toolbar.ApplyLocalization();
        Status.ApplyLocalization();
        FolderInfo.ApplyLocalization();
        ZoomIndicator.ApplyLocalization();
        _options.RequestRender();
    }

    /// <summary>Hoạt động của người dùng không đi qua chuột (đổi ảnh, cuộn, phím): hiện overlay thông tin và đếm giờ lại.</summary>
    public void NoteActivity()
    {
        if (!_disposed)
        {
            NoteInfoActivity();
        }
    }

    /// <summary>Cửa sổ chính được kích hoạt (<c>Activated</c>): tính là hoạt động.</summary>
    public void WindowActivated() => NoteActivity();

    /// <summary>Cửa sổ chính mất kích hoạt: overlay thông tin phải hiện (hộp thoại/ứng dụng khác đang giữ focus).</summary>
    public void WindowDeactivated()
    {
        if (!_disposed)
        {
            ApplyInfoOverlayVisibility();
        }
    }

    // Vẽ ----------------------------------------------------------------------------------------------------------------

    /// <summary>Bố cục theo client (DIP) rồi vẽ overlay + đồng bộ vùng tooltip. Gọi sau lớp ảnh, trong cùng khung.</summary>
    public void Render(IDrawContext dc, SizeD clientDip, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (_disposed)
        {
            return;
        }

        Arrange(clientDip, dpiScale);
        _host.Render(dc);
        SyncTooltips();
    }

    /// <summary>Chỉ bố cục (test hộp bao, hit-test mà không cần bề mặt vẽ).</summary>
    public void Arrange(SizeD clientDip, double dpiScale)
    {
        _lastClient = clientDip;
        _lastDpi = dpiScale <= 0 ? 1 : dpiScale;
        _host.Arrange(new OverlayLayoutContext(clientDip, _lastDpi, _options.Text));
    }

    // Chuột -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chuột di chuyển (DIP, toạ độ client): cập nhật hover, hot zone của toolbar và hoạt động của overlay thông tin. Luôn trả
    /// false (không nuốt WM_MOUSEMOVE: ảnh vẫn nhận để pan/hover).
    /// </summary>
    public void PointerMoved(PointD point)
    {
        if (_disposed)
        {
            return;
        }

        OverlayElement? hit = _host.HitTest(point);
        ButtonElement? hot = hit as ButtonElement;
        if (!ReferenceEquals(hot, _hot))
        {
            if (_hot is not null)
            {
                _hot.IsHot = false;
            }

            _hot = hot;
            if (hot is not null)
            {
                hot.IsHot = true;
            }

            _options.RequestRender();
        }

        // Window_MouseMove của MainWindow: NoteInfoActivity trước, rồi hot zone của toolbar theo hộp toolbar.
        NoteInfoActivity();
        RectD bar = Toolbar.Bounds;
        _toolbarMouseInsideHotZone = ToolbarAutoHidePolicy.IsInsideHotZone(point.X, point.Y, bar.X, bar.Y, bar.Width, bar.Height, HotZoneMargin);
        ApplyToolbarVisibility();
    }

    /// <summary>Chuột rời cửa sổ: bỏ hover (hot zone giữ nguyên như WPF, không có MouseMove để cập nhật).</summary>
    public void PointerLeft()
    {
        if (_hot is not null)
        {
            _hot.IsHot = false;
            _hot = null;
            _options.RequestRender();
        }
    }

    /// <summary>
    /// Nhấn chuột trái: true = overlay nhận (nuốt, ảnh không thấy); false = xuyên xuống ảnh (và bàn phím rời toolbar như
    /// <c>ReturnFocusAfterButtonClick</c>/click ngoài).
    /// </summary>
    public bool PointerDown(PointD point)
    {
        if (_disposed)
        {
            return false;
        }

        OverlayElement? hit = _host.HitTest(point);
        if (hit is null)
        {
            ClearKeyboardFocus();
            return false;
        }

        ClearKeyboardFocus();
        _pointerDown = true;
        if (hit is ButtonElement { IsEnabled: true } button)
        {
            _pressed = button;
            button.IsPressed = true;
            _options.RequestRender();
        }

        return true;
    }

    /// <summary>
    /// Nhả chuột trái: nếu nhả trong chính nút đã nhấn thì click. True nếu cú nhấn thuộc về overlay (kể cả nhấn vào panel).
    /// </summary>
    public bool PointerUp(PointD point)
    {
        if (_disposed || !_pointerDown)
        {
            return false;
        }

        _pointerDown = false;
        if (_pressed is { } button)
        {
            _pressed = null;
            button.IsPressed = false;
            _options.RequestRender();
            if (ReferenceEquals(_host.HitTest(point), button))
            {
                button.Click();
            }
        }

        return true;
    }

    /// <summary>Huỷ cú nhấn đang giữ (mất capture): không click.</summary>
    public void PointerCancel()
    {
        _pointerDown = false;
        if (_pressed is not null)
        {
            _pressed.IsPressed = false;
            _pressed = null;
            _options.RequestRender();
        }
    }

    /// <summary>Cú nhấn chuột trái đang do overlay giữ (nút hoặc panel): cần capture chuột, và nhả cũng bị nuốt.</summary>
    public bool IsPointerDown => _pointerDown;

    /// <summary>Nút đang bị nhấn giữ (null khi cú nhấn rơi vào panel/không phải nút).</summary>
    public ButtonElement? PressedButton => _pressed;

    /// <summary>Điểm có thuộc phần tử overlay nhận chuột (nút/panel hiện, không click xuyên)?</summary>
    public bool HitTest(PointD point) => !_disposed && _host.HitTest(point) is not null;

    /// <summary>Bánh xe/chuột ở <paramref name="point"/>: true nếu một panel overlay nhận (WPF: không tới ScrollViewer).</summary>
    public bool Wheel(PointD point)
    {
        NoteActivity();
        return HitTest(point);
    }

    // Bàn phím ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Phím xuống. Tab/Shift+Tab đi qua bốn nút toolbar (rồi ra ngoài); Space/Enter kích hoạt nút đang giữ bàn phím (bỏ qua
    /// auto-repeat). Mọi phím là hoạt động của overlay thông tin. True = đã dùng.
    /// </summary>
    public bool KeyDown(int virtualKey, bool shift, bool isAutoRepeat)
    {
        if (_disposed)
        {
            return false;
        }

        NoteInfoActivity();
        if (virtualKey == OverlayNative.VkTab)
        {
            if (isAutoRepeat)
            {
                return Toolbar.HasKeyboardFocus;
            }

            Toolbar.MoveFocus(!shift);
            ApplyToolbarVisibility();
            _options.RequestRender();
            return true;
        }

        if ((virtualKey == OverlayNative.VkSpace || virtualKey == OverlayNative.VkReturn) && Toolbar.FocusedButton is { } focused)
        {
            if (!isAutoRepeat)
            {
                focused.Click();
            }

            return true;
        }

        return false;
    }

    // Auto-hide ---------------------------------------------------------------------------------------------------------

    private void ApplyToolbarVisibility()
    {
        AppSettings settings = _options.Settings();
        bool keepVisible = ToolbarAutoHidePolicy.IsShown(
            autoHideEnabled: settings.ToolbarAutoHide,
            hasFolderOpen: _model.HasImages,
            isToolsPopupOpen: _toolsOpen,
            isKeyboardFocusInsideToolbar: Toolbar.HasKeyboardFocus,
            isMouseInsideHotZone: _toolbarMouseInsideHotZone);

        if (keepVisible)
        {
            _toolbarTimer.Stop();
            SetToolbarOpacity(visible: true, settings);
        }
        else if (_toolbarWasKeptVisible)
        {
            // Vừa đủ điều kiện để ẩn: đếm ngược từ bây giờ (không đặt lại ở mỗi lần chuột di chuyển ngoài hot zone).
            _toolbarTimer.Start(TimeSpan.FromMilliseconds(Math.Max(1, ToolbarAutoHidePolicy.DelayMs(settings))));
        }

        _toolbarWasKeptVisible = keepVisible;
    }

    private void OnToolbarHideTimer() => SetToolbarOpacity(visible: false, _options.Settings());

    private void SetToolbarOpacity(bool visible, AppSettings settings) =>
        Fade(Toolbar, _toolbarGate, visible, (float)ToolbarAutoHidePolicy.TargetOpacity(settings.ToolbarOpacityPercent));

    /// <summary>Hoạt động: hiện overlay thông tin và đếm giờ lại (<c>NoteInfoActivity</c>).</summary>
    private void NoteInfoActivity()
    {
        _infoIdleElapsed = false;
        _infoTimer.Stop();
        ApplyInfoOverlayVisibility();
        if (_infoWasKeptVisible)
        {
            return; // không gì có thể ẩn lúc này (tắt tính năng, thông điệp, compare, cửa sổ không active): không đếm
        }

        _infoTimer.Start(TimeSpan.FromMilliseconds(Math.Max(1, InfoOverlayAutoHidePolicy.DelayMs(_options.Settings()))));
    }

    private void OnInfoIdleTimer()
    {
        _infoIdleElapsed = true;
        ApplyInfoOverlayVisibility();
    }

    private void ApplyInfoOverlayVisibility()
    {
        AppSettings settings = _options.Settings();
        bool active = _options.IsWindowActive();
        bool keepVisible = InfoOverlayAutoHidePolicy.MustStayVisible(
            settings.InfoOverlayAutoHide, _model.HasImages, _model.StatusNeedsAttention, _model.CompareVisible, active);
        bool wasKept = _infoWasKeptVisible;
        _infoWasKeptVisible = keepVisible;
        if (wasKept && !keepVisible)
        {
            // Thông điệp/hộp thoại vừa xong: tính độ trễ từ bây giờ, không từ lần hoạt động cuối.
            NoteInfoActivity();
            return;
        }

        InfoOverlayAutoHidePolicy.Outcome outcome = InfoOverlayAutoHidePolicy.Evaluate(
            settings.InfoOverlayAutoHide, _model.HasImages, _model.StatusNeedsAttention, _model.CompareVisible, active, _infoIdleElapsed);
        Fade(InfoLayer, _infoGate, outcome.Opacity > 0, 1f);
    }

    /// <summary>
    /// Fade dùng chung (<c>MainWindow.FadeTo</c>): đích không đổi thì không làm gì; ẩn = click xuyên ngay từ đầu lượt fade.
    /// </summary>
    private void Fade(OverlayElement element, FadeTargetGate gate, bool visible, float visibleOpacity)
    {
        float target = visible ? visibleOpacity : 0f;
        if (!gate.TryChange(target))
        {
            return;
        }

        _animator.FadeTo(element.Id, target, TimeSpan.FromMilliseconds(visible ? FadeInMs : FadeOutMs), Easing.Linear);
        element.IsHitTestVisible = visible;
        _options.RequestRender();
    }

    private void OnAnimationChanged(object? sender, EventArgs e) => _options.RequestRender();

    // Menu Tools --------------------------------------------------------------------------------------------------------

    private void ShowToolsMenu()
    {
        if (_toolsOpen || _options.Menus is not { } menus)
        {
            return;
        }

        RectD bar = Toolbar.Tools.Bounds;
        PointD origin = _options.ClientOriginOnScreenDip?.Invoke() ?? default;
        var anchor = new PointD(origin.X + bar.X, origin.Y + bar.Y + bar.Height);
        _toolsOpen = true;
        Toolbar.Tools.IsChecked = true;
        ApplyToolbarVisibility();
        string? chosen;
        try
        {
            chosen = menus.Show(ToolsMenu.Build(_model.HasImages), anchor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            chosen = null;
            if (_options.OnMenuFailed is { } onMenuFailed)
            {
                onMenuFailed(ex);
            }
            else
            {
                _options.OnCommandFailed?.Invoke("Tools menu", ex);
            }
        }
        finally
        {
            _toolsOpen = false;
            Toolbar.Tools.IsChecked = false;
            ApplyToolbarVisibility();
            _options.RequestRender();
        }

        if (chosen is null || _disposed)
        {
            return;
        }

        try
        {
            if (ToolsMenu.Execute(chosen, _options.Commands, _model.HasImages) is { } run)
            {
                ObserveFailure(chosen, run);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _options.OnCommandFailed?.Invoke(chosen, ex);
        }
    }

    // Phụ trợ -----------------------------------------------------------------------------------------------------------

    private void ClearKeyboardFocus()
    {
        if (Toolbar.HasKeyboardFocus)
        {
            Toolbar.ClearFocus();
            ApplyToolbarVisibility();
            _options.RequestRender();
        }
    }

    private void ApplyModel()
    {
        Status.Apply(_model);
        FolderInfo.Apply(_model);
        ZoomIndicator.Apply(_model);
        CapturePair.Apply(_model);
    }

    private void SyncTooltips()
    {
        if (_options.Tooltips is not { } tooltips)
        {
            return;
        }

        _tooltipScratch.Clear();
        if (Toolbar.IsVisible && Toolbar.IsHitTestVisible && Toolbar.Opacity > 0f)
        {
            foreach (ButtonElement button in Toolbar.Buttons)
            {
                if (button.ToolTip.Length > 0)
                {
                    _tooltipScratch.Add(new OverlayTooltip(button.Id, button.Bounds, button.ToolTip));
                }
            }
        }

        if (InfoLayer.IsHitTestVisible && InfoLayer.Opacity > 0f)
        {
            if (Status.IsVisible && Status.Exif.IsVisible && _model.ExifText.Length > 0)
            {
                _tooltipScratch.Add(new OverlayTooltip(StatusPanel.ExifTextId, Status.Exif.Bounds, _model.ExifText));
            }

            if (CapturePair.IsVisible && CapturePair.ToolTip.Length > 0)
            {
                _tooltipScratch.Add(new OverlayTooltip(CapturePairBadge.PanelId, CapturePair.Bounds, CapturePair.ToolTip));
            }
        }

        tooltips.Sync(_tooltipScratch, _lastDpi);
    }

    private void RunGuarded(string actionName, Func<Task> action)
    {
        Task task;
        try
        {
            task = action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _options.OnCommandFailed?.Invoke(actionName, ex);
            return;
        }

        ObserveFailure(actionName, task);
    }

    private void ObserveFailure(string actionName, Task task)
    {
        _ = task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception?.GetBaseException() is { } ex && ex is not OperationCanceledException)
                {
                    _options.OnCommandFailed?.Invoke(actionName, ex);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animator.Changed -= OnAnimationChanged;
        _toolbarTimer.Dispose();
        _infoTimer.Dispose();
        _animator.Dispose();
        _options.Tooltips?.Dispose();
        _host.Dispose();
    }
}
