using PhotoReview.Core.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DpiChangedEventArgs = System.Windows.DpiChangedEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace PhotoReview.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ShortcutRouter _shortcutRouter;
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private double? _cachedDpiScale;
    private readonly ViewportSizeSource? _viewport;
    private bool _placementRestored;
    private readonly ViewportOperationVersion _viewportVersion = new(); // shared by zoom-at-point and Fit
    private readonly WpfImageSurface _surface;
    private readonly PointerInputController _pointer; // feat/mouse-zoom (AR13a)
    private readonly FitViewController _fit; // T89 convergence loop (AR13b)

    // AR02d: read-only properties replacing the public mutable fields that used to be kept in
    // sync by WireViewModelEvents/SyncFiles (ST06/Q-ST3 exception, superseded by this change).

    public IReadOnlyList<string> Files => _viewModel.Catalog.Paths;
    public int CurrentIndex => _viewModel.CurrentIndex;
    public string? CompareSelectedPath => _viewModel.Compare.SelectedPath;
    public ReviewMetrics Metrics => _viewModel.Metrics;
    public bool IsFileActionInProgress => _viewModel.IsFileActionInProgress;

    public MainViewModel ViewModel => _viewModel;
    public AppSettings Settings => _settings;

    /// <summary>
    /// R7-11: window-placement.json from <see cref="IAppPaths"/>; null when placement is suppressed (test harness).
    /// </summary>
    internal string? PlacementFile { get; private set; }

    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore, ViewportSizeSource viewport, IAppPaths? appPaths = null, IDisplayClock? displayClock = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        ArgumentNullException.ThrowIfNull(viewport);
        PlacementFile = (appPaths ?? PhotoReview.Core.AppPaths.FromEnvironment()).WindowPlacementFile;
        // AR02b finding (see AR02-single-composition-root.md AR02d step 1, applied a step early
        // here because AR02b's migrated integration tests need it to observe real behaviour):
        // this used to call _settingsStore.Load() again, which re-reads/deserializes config.json
        // into a *new* AppSettings instance distinct from the one MainViewModelCompositionRoot
        // already captured into FileActionController via SettingsStore.Current (production calls
        // store.Load() once, in App.App_Startup, before resolving MainWindow). In production the
        // second Load() happened to reparse the same unchanged file into content-identical settings,
        // so the object-identity split was invisible; in tests -- where nothing else calls Load()
        // first -- MainWindow's own Load() produced a *third* object, so mutating the settings a
        // test held (window._settings.Actions = ...) never reached FileActionController, which
        // kept whatever AppSettings' parameterless constructor defaults to. Using Current (already
        // loaded by App_Startup in production, and the same shared default instance the rest of the
        // graph was built from otherwise) keeps one settings object for the whole window and is also
        // one fewer disk read at startup.
        _settings = _settingsStore.Current;
        _shortcutRouter = new ShortcutRouter(_settings);
        // R2-F-27: the router/VM are refreshed here (settings change), not on every key press.
        _viewModel.Settings = _settings;
        _settingsStore.Changed += (_, s) => { _settings = s; _viewModel.Settings = s; _shortcutRouter.Rebuild(s); ApplyToolbarVisibility(); NoteInfoActivity(); };
        PhotoReviewPerf.StartupMark("mainWindowCtor");
        // I18N: a live language switch re-renders the texts the ViewModel builds in code (ADR 0006).
        Localizer.CurrentChanged += OnLanguageChanged;
        DataContext = _viewModel;
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        _surface = new WpfImageSurface(ImageScroll, MainImage, _viewModel.Viewer, () => IsLoaded, UpdateFitSize, displayClock);
        _pointer = new PointerInputController(_surface, _viewModel.Viewer, () => _settings, _viewportVersion,
            new PointerCommands(() => _viewModel.HasImages, _viewModel.NextAsync, _viewModel.PreviousAsync, _viewModel.ZoomActualSize, ApplyFitViewAsync));
        _fit = new FitViewController(_surface, _viewModel.Viewer, _viewportVersion, _pointer.CancelPan);
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ReturnFocusAfterButtonClick), handledEventsToo: true);
        PhotoReviewPerf.StartupMark("xamlLoaded");
        ContentRendered += (_, _) => PhotoReviewPerf.StartupMark("contentRendered");
        viewport.Get = GetViewportSize;
        _viewport = viewport;
        DpiChanged += MainWindow_DpiChanged;
        UpdateTargetDecodeBox();
        WireViewModelEvents();
        InitToolbarAutoHide();
        InitInfoOverlayAutoHide();
    }

    public void InitializeWithInitialPath(string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return;
        PhotoReviewPerf.StartupMark("openPathBegin");
        _ = _viewModel.OpenPathAsync(initialPath);
    }

    /// <summary>Q-R10: opens a path forwarded from a second launch (UI thread).</summary>
    public Task OpenPathAsync(string path) => _viewModel.OpenPathAsync(path);

    private void WireViewModelEvents()
    {
        _viewModel.Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewerState.IsFullscreen)) ApplyFullscreenState(_viewModel.Viewer.IsFullscreen); };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentImage) && _viewModel.Viewer.IsFit)
                Dispatcher.BeginInvoke(UpdateFitSize, System.Windows.Threading.DispatcherPriority.Render);
            // feat/mouse-zoom: navigation stops a glide (a full-resolution swap of the same image does not).
            if (e.PropertyName == nameof(MainViewModel.CurrentIndex)) _pointer.OnCurrentIndexChanged(_viewModel.CurrentIndex);
            // feat/ui-dark-chrome-toolbar: no folder open forces the toolbar visible (ToolbarAutoHidePolicy).
            if (e.PropertyName == nameof(MainViewModel.HasImages)) ApplyToolbarVisibility();
            // Q-R34: navigation is activity (a new photo's info shows for the delay); HasImages/StatusText change what must stay visible.
            if (e.PropertyName is nameof(MainViewModel.CurrentIndex) or nameof(MainViewModel.CurrentImage)) NoteInfoActivity();
            else if (e.PropertyName is nameof(MainViewModel.HasImages) or nameof(MainViewModel.StatusText) or nameof(MainViewModel.HasSkippedEntries)) ApplyInfoOverlayVisibility();
        };
        _viewModel.Compare.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CompareViewModel.IsVisible)) NoteInfoActivity(); };
        // feat/mouse-zoom: any zoom change (wheel, keys, click, Fit) stops a glide; so does leaving the window.
        _viewModel.Viewer.ZoomModeChanged += (_, _) => _pointer.StopKinetic();
        Deactivated += (_, _) => _pointer.StopKinetic();
        PreviewMouseDown += Window_PreviewMouseDown;
    }

    public Task LoadFolderAsync(string folder, string? initialPath = null) => _viewModel.OpenFolderAsync(folder, initialPath);

    /// <summary>
    /// Public test helper: Executes the unified undo operation with proper mutual exclusion guard.
    /// Used by integration tests via reflection. Routes to MainViewModel.UndoAsync() (gate lives in the ViewModel, OC14).
    /// </summary>
    public Task UndoLastActionAsync() => _viewModel.UndoAsync();
    public void ResetFitView() => _ = ApplyFitViewAsync();
    public void SetZoom(double level) => _viewModel.Viewer.SetZoom(level);

    /// <summary>Test seam (kinetic-pan frame measurement): drives a drag/glide through the real controller in-process.</summary>
    internal PointerInputController PointerInput => _pointer;
    public Task ShowImageAsync(int index) => _viewModel.Presenter.PresentAsync(index);
    public bool TryGetCachedPreview(string path, out object? preview)
    {
        if (_viewModel.PreviewService is not null && _viewModel.PreviewService.TryGetCachedPreview(path, out var decoded))
        {
            preview = decoded.PlatformImage;
            return true;
        }
        preview = null;
        return false;
    }

    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    private void ApplyFullscreenState(bool isFullscreen)
    {
        ResizeMode = isFullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowStyle = isFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        if (isFullscreen) _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        WindowState = isFullscreen ? WindowState.Maximized : _stateBeforeFullscreen;
    }

    private (double Width, double Height) GetViewportSize() => _surface.ViewportSize;

    private void UpdateFitSize()
    {
        var (w, h) = GetViewportSize();
        _viewModel.Viewer.UpdateViewport(w, h);
        UpdateTargetDecodeBox();
    }

    // Before T46d this was read live by PreviewImageService; it is now pushed on the UI thread
    // (viewport/DPI change) so preload workers can read it without touching WPF layout.
    // perf(decode): the target is a width x height box, so a landscape is bounded by the viewport
    // height and a portrait is no longer decoded ~4.5x larger than it is shown. The box is
    // quantized (AdaptivePreviewPolicy.BoxQuantum) so small resizes keep the same cache keys.
    private const double FallbackViewportWidth = 2200;
    private const double FallbackViewportHeight = 1400;
    private const double PreviewQualityMultiplier = 1.15;

    private void UpdateTargetDecodeBox()
    {
        if (_viewport is null) return;
        var (w, h) = GetViewportSize();
        var dpi = _cachedDpiScale ??= System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        // feat(zoom): the same device-pixel scale makes non-Fit zoom 100 % = 1 source px per device px.
        _viewModel.Viewer.DpiScale = dpi;
        _viewport.TargetDecodeBox = PhotoReview.Imaging.AdaptivePreviewPolicy.CalculateTargetDecodeBox(
            w > 1 ? w : FallbackViewportWidth, h > 1 ? h : FallbackViewportHeight, dpi, PreviewQualityMultiplier);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PhotoReviewPerf.StartupMark("windowLoaded");
        if (!_placementRestored) { _placementRestored = true; if (PlacementFile is { } placementFile) WindowPlacementService.Restore(this, placementFile); }
        UpdateFitSize();
    }

    // ---- Toolbar auto-hide (feat/ui-dark-chrome-toolbar). Decision logic is in ToolbarAutoHidePolicy
    // (unit-tested, no WPF dependency); this region only drives the timer/animation/mouse tracking. ----

    private DispatcherTimer? _toolbarHideTimer;
    private bool _toolbarMouseInsideHotZone = true; // assume "inside" until the first mouse move says otherwise
    private bool _toolbarWasKeptVisible = true;
    private const double ToolbarHotZoneMargin = 24; // px, beyond ToolbarPanel's own bounds
    private const int ToolbarFadeInMs = 150;
    private const int ToolbarFadeOutMs = 200;

    private void InitToolbarAutoHide()
    {
        _toolbarHideTimer = new DispatcherTimer();
        _toolbarHideTimer.Tick += (_, _) => { _toolbarHideTimer!.Stop(); SetToolbarOpacity(visible: false); };
        ToolsButton.Checked += (_, _) => ApplyToolbarVisibility();
        ToolsButton.Unchecked += (_, _) => ApplyToolbarVisibility();
        ToolbarPanel.GotKeyboardFocus += (_, _) => ApplyToolbarVisibility();
        ToolbarPanel.LostKeyboardFocus += (_, _) => ApplyToolbarVisibility();
        ApplyToolbarVisibility();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        NoteInfoActivity();
        if (ToolbarPanel is null) return;
        var topLeft = ToolbarPanel.TranslatePoint(new Point(0, 0), this);
        var position = e.GetPosition(this);
        _toolbarMouseInsideHotZone = ToolbarAutoHidePolicy.IsInsideHotZone(
            position.X, position.Y, topLeft.X, topLeft.Y, ToolbarPanel.ActualWidth, ToolbarPanel.ActualHeight, ToolbarHotZoneMargin);
        ApplyToolbarVisibility();
    }

    /// <summary>
    /// Re-evaluates whether the toolbar should be shown or eligible to auto-hide. Called from mouse
    /// move, the Tools popup opening/closing, keyboard focus entering/leaving the toolbar, a folder
    /// opening/closing (HasImages) and a settings change (auto-hide on/off, delay).
    /// </summary>
    private void ApplyToolbarVisibility()
    {
        if (_toolbarHideTimer is null) return; // constructor still running
        var keepVisible = ToolbarAutoHidePolicy.IsShown(
            autoHideEnabled: _settings.ToolbarAutoHide,
            hasFolderOpen: _viewModel.HasImages,
            isToolsPopupOpen: ToolsButton.IsChecked == true,
            isKeyboardFocusInsideToolbar: ToolbarPanel.IsKeyboardFocusWithin,
            isMouseInsideHotZone: _toolbarMouseInsideHotZone);

        if (keepVisible)
        {
            _toolbarHideTimer.Stop();
            SetToolbarOpacity(visible: true);
        }
        else if (_toolbarWasKeptVisible)
        {
            // Just became eligible to hide: start counting down (does not restart on every later
            // mouse move outside the hot zone, so it hides at the configured delay after leaving).
            _toolbarHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolbarAutoHidePolicy.DelayMs(_settings)));
            _toolbarHideTimer.Start();
        }
        _toolbarWasKeptVisible = keepVisible;
    }

    // The visible toolbar sits at the user's opacity (never below the minimum); hidden is always 0.
    private void SetToolbarOpacity(bool visible) => FadeTo(ToolbarPanel, visible, ToolbarAutoHidePolicy.TargetOpacity(_settings.ToolbarOpacityPercent));

    /// <summary>Shared fade for the toolbar and the info overlays; faded out = click-through so clicks/wheel/pan reach the image.</summary>
    private static void FadeTo(UIElement element, bool visible, double visibleOpacity = 1.0)
    {
        var animation = new DoubleAnimation(visible ? visibleOpacity : 0.0, TimeSpan.FromMilliseconds(visible ? ToolbarFadeInMs : ToolbarFadeOutMs));
        element.BeginAnimation(OpacityProperty, animation);
        element.IsHitTestVisible = visible;
    }

    // ---- Q-R34: info-overlay auto-hide. Decision logic is InfoOverlayAutoHidePolicy (unit-tested); this region
    // only tracks idle time (mouse / wheel / key / navigation) and animates InfoOverlayHost. Uses its own
    // InfoOverlayAutoHideDelayMs (independent of the toolbar's). Window inactive (dialog, Settings, other app) keeps the overlays visible. ----

    private DispatcherTimer? _infoHideTimer;
    private bool _infoIdleElapsed;
    private bool _infoWasKeptVisible = true;

    private void InitInfoOverlayAutoHide()
    {
        _infoHideTimer = new DispatcherTimer();
        _infoHideTimer.Tick += (_, _) => { _infoHideTimer!.Stop(); _infoIdleElapsed = true; ApplyInfoOverlayVisibility(); };
        PreviewMouseWheel += (_, _) => NoteInfoActivity();
        PreviewKeyDown += (_, _) => NoteInfoActivity();
        Activated += (_, _) => NoteInfoActivity();
        Deactivated += (_, _) => ApplyInfoOverlayVisibility();
        ApplyInfoOverlayVisibility();
    }

    /// <summary>Activity (mouse move/wheel/click, key, navigation, settings change): show the overlays and restart the idle countdown.</summary>
    private void NoteInfoActivity()
    {
        if (_infoHideTimer is null) return; // constructor still running
        _infoIdleElapsed = false;
        _infoHideTimer.Stop();
        ApplyInfoOverlayVisibility();
        if (_infoWasKeptVisible) return; // nothing can hide right now (feature off, message, compare, inactive): no countdown
        _infoHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, InfoOverlayAutoHidePolicy.DelayMs(_settings)));
        _infoHideTimer.Start();
    }

    private void ApplyInfoOverlayVisibility()
    {
        if (_infoHideTimer is null) return;
        var keepVisible = InfoOverlayAutoHidePolicy.MustStayVisible(
            autoHideEnabled: _settings.InfoOverlayAutoHide, hasFolderOpen: _viewModel.HasImages,
            statusNeedsAttention: _viewModel.StatusNeedsAttention, isCompareOpen: _viewModel.Compare.IsVisible, isWindowActive: IsActive);
        var wasKept = _infoWasKeptVisible;
        _infoWasKeptVisible = keepVisible;
        // A message/loading/dialog just ended: count the idle delay from now, not from the last activity.
        if (wasKept && !keepVisible) { NoteInfoActivity(); return; }
        var outcome = InfoOverlayAutoHidePolicy.Evaluate(
            _settings.InfoOverlayAutoHide, _viewModel.HasImages, _viewModel.StatusNeedsAttention, _viewModel.Compare.IsVisible, IsActive, _infoIdleElapsed);
        FadeTo(InfoOverlayHost, outcome.Opacity > 0);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();
    private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();
    private void MainImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel.Viewer.IsFit) UpdateFitSize();
    }
    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        _cachedDpiScale = e.NewDpi.DpiScaleX;
        UpdateTargetDecodeBox();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || PlacementFile is not { } placementFile) return;
        // R7-10: fullscreen is a borderless Maximized; reopen in the state the window had before it.
        WindowPlacementService.Save(this, placementFile, _viewModel.Viewer.IsFullscreen ? _stateBeforeFullscreen : null);
    }

    /// <summary>Harness use: never restore or save the user's real window-placement.json for this instance.</summary>
    public void SuppressWindowPlacement()
    {
        _placementRestored = true;
        PlacementFile = null;
        Closing -= Window_Closing;
    }

    private bool _closeWhenFileActionDone;

    /// <summary>
    /// R7-7: closing while a file action or undo holds the gate would kill a cross-drive Move mid-copy (the process
    /// exits under it). Keep the window open and close it once the action releases the gate (as RecoveryWindow does
    /// for its retry, R2-A-01). Esc goes through Close() and lands here too.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsFileActionInProgress)
        {
            e.Cancel = true;
            if (!_closeWhenFileActionDone)
            {
                _closeWhenFileActionDone = true;
                _ = CloseWhenFileActionDoneAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task CloseWhenFileActionDoneAsync()
    {
        await _viewModel.WhenFileActionIdleAsync();
        _closeWhenFileActionDone = false;
        Close();
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) _viewModel.RefreshLocalizedText();
        else Dispatcher.BeginInvoke(_viewModel.RefreshLocalizedText);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        _pointer.OnWindowClosed(); // stops a glide (unhooks the static render-frame event that would keep this window alive), ends a pan
        _viewModel.CloseSession();
        (_viewModel.PreloadController as IDisposable)?.Dispose();
        // The IExplorerOrderProvider singleton is owned by the service provider (App.Dispose), not by this window (APP-01).
    }

    // ---- feat/mouse-zoom: wheel (zoom / navigate), click-to-zoom, drag-pan with kinetic glide ----
    // AR13a: the state machine lives in Input/PointerInputController.cs; these handlers only forward WPF input
    // and set e.Handled from what it returns.

    private async void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        await _pointer.OnWheelAsync(e.Delta, ctrl, e.GetPosition(ImageScroll));
    }

    /// <summary>Tunnels before the image's handler: any press stops a glide (remembered so that press is not a click).</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _pointer.OnWindowPreviewMouseDown();

    private void MainImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pointer.OnImagePress(e.ChangedButton, e.ClickCount, e.GetPosition(ImageScroll), e.Timestamp)) e.Handled = true;
    }

    private void MainImage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pointer.OnImageMove(e.LeftButton == MouseButtonState.Pressed, e.GetPosition(ImageScroll), e.Timestamp)) e.Handled = true;
    }

    private void MainImage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pointer.OnImageRelease(e.GetPosition(ImageScroll), e.Timestamp)) e.Handled = true;
    }

    private void MainImage_LostMouseCapture(object sender, MouseEventArgs e) => _pointer.OnLostCapture();

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await _viewModel.OpenPathAsync(files[0]);
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        _pointer.StopKinetic(); // feat/mouse-zoom: any key press stops a glide
        if (PhotoReviewPerf.Log.IsEnabled())
            PhotoReviewPerf.Log.KeyInput(0, pressedKey.ToString(), unchecked(Environment.TickCount - e.Timestamp));

        // R7-6: Esc with the tools popup open closes the popup, not the whole app.
        if (pressedKey == Key.Escape && ToolsButton.IsChecked == true)
        {
            ToolsButton.IsChecked = false;
            e.Handled = true;
            return;
        }

        // Q-R25: Esc while a duplicate check is hashing cancels it (before fullscreen exit / window close); the key is consumed.
        if (pressedKey == Key.Escape && _viewModel.CancelDuplicateCheck())
        {
            e.Handled = true;
            return;
        }

        // Space/Enter belong to a focused button or compare pane (keyboard activation); the window-level tunnel must not steal them.
        // A mouse click leaves focus on the toolbar button, so ReturnFocusAfterButtonClick hands it back to the window;
        // otherwise Space/Enter would keep re-activating that button instead of Skip / Move to folder 2.
        if (pressedKey is Key.Space or Key.Enter && e.OriginalSource is DependencyObject source && OwnsActivationKeys(source)) return;

        // Arrow keys on a zoomed image pan the view (Left/Right navigate again at Fit, or on a fresh press at the edge).
        if (Keyboard.Modifiers == ModifierKeys.None && !_viewModel.Compare.IsVisible && _pointer.TryPanByArrow(pressedKey, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        var cmd = _shortcutRouter.TryResolve(e.Key, e.SystemKey, Keyboard.Modifiers, _viewModel.Viewer.IsFullscreen, _viewModel.HasImages, hasComparePair: _viewModel.CurrentHasComparePair, isCompareVisible: _viewModel.Compare.IsVisible);
        if (cmd is null) return;
        e.Handled = true;
        if (e.IsRepeat && cmd.Value.Type.IgnoresAutoRepeat()) return; // R2-F-06: never repeat file actions

        switch (cmd.Value.Type)
        {
            case ReviewCommandType.Fullscreen: _viewModel.ToggleFullscreen(); break;
            case ReviewCommandType.ExitFullscreen: _viewModel.ExitFullscreen(); break;
            case ReviewCommandType.Close: Close(); break;
            case ReviewCommandType.NextFolder: await _viewModel.NavigateSiblingFolderAsync(1); break;
            case ReviewCommandType.PreviousFolder: await _viewModel.NavigateSiblingFolderAsync(-1); break;
            case ReviewCommandType.FirstImage: await _viewModel.FirstImageAsync(); break;
            case ReviewCommandType.LastImage: await _viewModel.LastImageAsync(); break;
            case ReviewCommandType.ToggleInfoOverlay: _viewModel.ToggleInfoOverlay(); break;
            case ReviewCommandType.ZoomActualSize: await _pointer.ZoomActualSizeAsync(); break;
            case ReviewCommandType.Undo: await _viewModel.UndoAsync(); break;
            case ReviewCommandType.ToggleCompare: _viewModel.ToggleCompare(); break;
            case ReviewCommandType.RunAction:
                await _viewModel.RunActionAsync(cmd.Value.ActionIndex);
                break;
            case ReviewCommandType.Recycle: await _viewModel.RecycleAsync(); break;
            case ReviewCommandType.Skip: await _viewModel.SkipAsync(); break;
            case ReviewCommandType.ToggleFit: _ = ApplyFitViewAsync(); break;
            case ReviewCommandType.ZoomIn: _viewModel.ZoomIn(); break;
            case ReviewCommandType.ZoomOut: _viewModel.ZoomOut(); break;
            case ReviewCommandType.Next: await _viewModel.NextAsync(); break;
            case ReviewCommandType.Previous: await _viewModel.PreviousAsync(); break;
            case ReviewCommandType.MoveToFolder: await _viewModel.MoveToFolderAsync(cmd.Value.ForcePicker); break;
            case ReviewCommandType.CopyToFolder: await _viewModel.CopyToFolderAsync(cmd.Value.ForcePicker); break;
            case ReviewCommandType.ClickZoom: await _pointer.ToggleClickZoomAsync(); break;
        }
    }

    private void ReturnFocusAfterButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) return;
        // Deferred: a dialog opened by the click may take focus first; only reclaim it while the button still holds it.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsActive && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool OwnsActivationKeys(DependencyObject source)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, this); node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase || ReferenceEquals(node, CompareLeftBorder) || ReferenceEquals(node, CompareRightBorder)) return true;
        }
        return false;
    }

    private void CompareLeft_Click(object sender, MouseButtonEventArgs e) { _viewModel.Compare.SelectLeft(); e.Handled = true; }
    private void CompareRight_Click(object sender, MouseButtonEventArgs e) { _viewModel.Compare.SelectRight(); e.Handled = true; }
    private void CompareLeft_KeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { _viewModel.Compare.SelectLeft(); e.Handled = true; } }
    private void CompareRight_KeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { _viewModel.Compare.SelectRight(); e.Handled = true; } }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await _viewModel.PickAndOpenFolderAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => _viewModel.ShowSettings();
    private async void FitImage_Click(object sender, RoutedEventArgs e) => await ApplyFitViewAsync();
    private Task ApplyFitViewAsync() => _fit.ApplyFitAsync(); // T89 convergence loop: Coordinators/FitViewController.cs

    private void Recovery_Click(object sender, RoutedEventArgs e) => _viewModel.ShowRecovery();
    private void SkippedFiles_Click(object sender, RoutedEventArgs e) => _viewModel.ShowSkippedFiles();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDiagnostics();
    private void Benchmark_Click(object sender, RoutedEventArgs e) => _viewModel.ShowBenchmark();
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await _viewModel.ClearCacheAsync();
    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(true);
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(false);
    private async void UndoLastAction_Click(object sender, RoutedEventArgs e) => await _viewModel.UndoAsync();

    // ---- Context menu: "Click zoom level" submenu (presets + Custom…). Built once; refreshed (text + IsChecked) ----
    // ---- on every open so a live language switch and a setting changed elsewhere both show correctly. ----

    private static readonly int[] ClickZoomPresets = [30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 200, 300, 400];
    private List<System.Windows.Controls.MenuItem>? _clickZoomPresetItems;

    private void ClickZoomMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (_clickZoomPresetItems is null) BuildClickZoomMenu();
        var current = _settings.ClickZoomPercent;
        foreach (var item in _clickZoomPresetItems!)
        {
            var percent = (int)item.Tag!;
            item.Header = Tr.MainMenuClickZoomLevelPreset(percent);
            AutomationProperties.SetName(item, Tr.MainMenuClickZoomLevelPresetAutomationName(percent));
            item.IsChecked = percent == current;
        }
    }

    private void BuildClickZoomMenu()
    {
        _clickZoomPresetItems = [];
        var fit = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuClickZoomLevelFit };
        AutomationProperties.SetName(fit, Tr.MainMenuClickZoomLevelFitAutomationName);
        fit.Click += ClickZoomFit_Click;
        ClickZoomMenu.Items.Add(fit);
        ClickZoomMenu.Items.Add(new System.Windows.Controls.Separator());
        foreach (var percent in ClickZoomPresets)
        {
            var item = new System.Windows.Controls.MenuItem { IsCheckable = true, Tag = percent };
            item.Click += ClickZoomPreset_Click;
            _clickZoomPresetItems.Add(item);
            ClickZoomMenu.Items.Add(item);
        }
        ClickZoomMenu.Items.Add(new System.Windows.Controls.Separator());
        var custom = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuClickZoomLevelCustom };
        AutomationProperties.SetName(custom, Tr.MainMenuClickZoomLevelCustomAutomationName);
        custom.Click += ClickZoomCustom_Click;
        ClickZoomMenu.Items.Add(custom);
    }

    private void ClickZoomFit_Click(object sender, RoutedEventArgs e) => _ = ApplyFitViewAsync();

    private async void ClickZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int percent }) return;
        await ApplyClickZoomLevelAsync(percent);
    }

    private async void ClickZoomCustom_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ClickZoomCustomDialog(_settings.ClickZoomPercent) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await ApplyClickZoomLevelAsync(dialog.Value);
        }
    }

    /// <summary>Persists the new click zoom level (same pattern as <c>MainViewModel.ToggleInfoOverlay</c>) and applies it now.</summary>
    private async Task ApplyClickZoomLevelAsync(int percent)
    {
        var settings = _settings;
        settings.ClickZoomPercent = percent;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The image still zooms for this session; only persisting the new level failed.
            AppLog.Error("Could not save the click zoom level setting", ex);
        }
        await _pointer.SetClickZoomLevelAsync(percent);
    }
}
