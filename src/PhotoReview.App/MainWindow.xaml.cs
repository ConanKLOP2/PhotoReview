using System.Diagnostics.CodeAnalysis;
using PhotoReview.Core.Localization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
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
    private readonly EventHandler<AppSettings> _onSettingsChanged;
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
        // AR02b finding (see AR02 plan (removed 2026-10-01, see git history) AR02d step 1, applied a step early
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
        _onSettingsChanged = (_, s) => { _settings = s; _viewModel.Settings = s; _shortcutRouter.Rebuild(s); ApplyToolbarVisibility(); NoteInfoActivity(); };
        _settingsStore.Changed += _onSettingsChanged; // RV-A16: removed in Window_Closed (the store outlives the window)
        PhotoReviewPerf.StartupMark("mainWindowCtor");
        // I18N: a live language switch re-renders the texts the ViewModel builds in code (ADR 0006).
        Localizer.CurrentChanged += OnLanguageChanged;
        DataContext = _viewModel;
        InitializeComponent();
        // Items exist before the first open: a MenuItem with no children shows no submenu arrow and never opens.
        BuildZoomMenu();
        DarkTitleBarChrome.Apply(this);
        _surface = new WpfImageSurface(ImageScroll, MainImage, _viewModel.Viewer, () => IsLoaded, UpdateFitSize, displayClock);
        _pointer = new PointerInputController(_surface, _viewModel.Viewer, () => _settings, _viewportVersion,
            new PointerCommands(() => _viewModel.HasImages, _viewModel.NextAsync, _viewModel.PreviousAsync, _viewModel.ZoomActualSize, ApplyFitViewAsync));
        _fit = new FitViewController(_surface, _viewModel.Viewer, _viewportVersion, _pointer.CancelPan);
        // PR-B: the sink (built in MainViewModelCompositionRoot, before this window exists) gets its initial-view
        // hook overridden now that the pointer controller (which owns the surface) is available, so Fit width/Fit
        // height get their scroll placement (see PointerInputController.ApplyInitialViewAsync).
        if (_viewModel.Presenter.Sink is WpfPresentationSink initialViewSink)
        {
            initialViewSink.ApplyInitialViewModeOverride = () =>
                _pointer.ApplyInitialViewAsync(_settings.InitialViewMode, _settings.ClickZoomPercent).FireAndLog("Initial view failed");
        }
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ReturnFocusAfterButtonClick), handledEventsToo: true);
        PhotoReviewPerf.StartupMark("xamlLoaded");
        ContentRendered += (_, _) => { PhotoReviewPerf.StartupMark("contentRendered"); _startupReveal.Reveal(); };
        // perf/startup-first-image: before the HWND is shown, put it where it was closed and keep it cloaked until its
        // first frame is rendered (no white surface, no 1200x800 window that then jumps to the saved placement).
        SourceInitialized += (_, _) => PrepareFirstShow();
        viewport.Get = GetViewportSize;
        _viewport = viewport;
        DpiChanged += MainWindow_DpiChanged;
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WindowMessageHook);
        UpdateTargetDecodeBox();
        WireViewModelEvents();
        InitToolbarAutoHide();
        InitInfoOverlayAutoHide();
        InitImageTransition();
    }

    public void InitializeWithInitialPath(string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return;
        PhotoReviewPerf.StartupMark("openPathBegin");
        if (!IsLoaded && File.Exists(initialPath)) _prewarmPath = initialPath; // its decode starts when the window is shown (StartInitialDecode)
        _viewModel.Presenter.DeferNextPreloadKick = true; // P-1: the launch image's frame first, then preloading
        _viewModel.OpenPathAsync(initialPath).FireAndLog("Open initial path failed");
    }

    /// <summary>Q-R10: opens a path forwarded from a second launch (UI thread).</summary>
    public Task OpenPathAsync(string path) => _viewModel.OpenPathAsync(path);

    private void WireViewModelEvents()
    {
        _viewModel.Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewerState.IsFullscreen)) ApplyFullscreenState(_viewModel.Viewer.IsFullscreen); };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentImage) && _viewModel.Viewer.IsFit)
                _ = Dispatcher.BeginInvoke(UpdateFitSize, System.Windows.Threading.DispatcherPriority.Render);
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

    public void ResetFitView() => ApplyFitViewAsync().FireAndLog("Reset fit view failed");
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

    // F11: the window never changes its OS state (see FullscreenWindowPlacer); only its style and one SetWindowPos change.
    private readonly FullscreenWindowPlacer _fullscreenPlacer = new();

    private void ApplyFullscreenState(bool isFullscreen)
    {
        if (isFullscreen) _fullscreenPlacer.Enter(this);
        else _fullscreenPlacer.Exit(this);
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
    internal const double PreviewQualityMultiplier = 1.15;
    internal const double DefaultWindowWidth = 1200; // MainWindow.xaml Width/Height (first start, no saved placement)
    internal const double DefaultWindowHeight = 800;

    private void UpdateTargetDecodeBox((double Width, double Height)? viewportSize = null)
    {
        if (_viewport is null) return;
        var (w, h) = viewportSize ?? GetViewportSize();
        var dpi = _cachedDpiScale ??= System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        // feat(zoom): the same device-pixel scale makes non-Fit zoom 100 % = 1 source px per device px.
        _viewModel.Viewer.DpiScale = dpi;
        _viewport.TargetDecodeBox = PhotoReview.Imaging.AdaptivePreviewPolicy.CalculateTargetDecodeBox(
            w > 1 ? w : FallbackViewportWidth, h > 1 ? h : FallbackViewportHeight, dpi, PreviewQualityMultiplier);
    }

    private readonly StartupWindowReveal _startupReveal = new(onRevealed: () => PhotoReviewPerf.StartupMark("windowRevealed"));
    private string? _prewarmPath;
    private bool _shownNative;

    /// <summary>Test seam: whether the startup cloak is still in effect.</summary>
    internal bool IsStartupCloaked => _startupReveal.IsHidden;

    private void PrepareFirstShow()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _startupReveal.Hide(handle);
        if (!_placementRestored && PlacementFile is { } placementFile)
        {
            _placementRestored = true; // one attempt, as the Loaded path below: an invalid file is not retried there
            if (WindowPlacementService.RestoreBeforeShow(this, placementFile)) PhotoReviewPerf.StartupMark("placementRestoredBeforeShow");
        }
    }

    private const int WmWindowPosChanged = 0x0047;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>WINDOWPOS.flags (offset: two handles + four ints) has SWP_SHOWWINDOW: the window is being shown now.</summary>
    private static bool WindowPosShowsWindow(IntPtr windowPos) =>
        windowPos != IntPtr.Zero && ((uint)System.Runtime.InteropServices.Marshal.ReadInt32(windowPos, (2 * IntPtr.Size) + (4 * sizeof(int))) & SwpShowWindow) != 0;

    /// <summary>
    /// perf/startup-first-image: the window is being shown in its final placement (restored before the show), so its
    /// client rect is the viewport the first image is decoded for. The decode box is set from it right away (the same
    /// value the first layout computes later) and the launch file's decode starts now, in parallel with the rest of
    /// Show() and the folder scan; the presenter then joins it.
    /// </summary>
    private void StartInitialDecode(IntPtr hwnd)
    {
        if (_prewarmPath is not { } path) return;
        _prewarmPath = null;
        if (!GetClientRect(hwnd, out var client) || client.Right <= 0 || client.Bottom <= 0) return;
        var dpi = _cachedDpiScale ??= System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        UpdateTargetDecodeBox((client.Right / dpi, client.Bottom / dpi));
        // P-1: App started the decode already, keyed by a predicted box; this joins it when the prediction was right.
        if (_viewport?.StartupPrediction is { } predicted)
            PhotoReviewPerf.StartupMark(predicted == _viewport.TargetDecodeBox ? "earlyDecodeBoxMatched" : "earlyDecodeBoxMismatch");
        if (_viewModel.PrewarmInitialImage(path) is not null) PhotoReviewPerf.StartupMark("initialDecodeStarted");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

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
    private void SetToolbarOpacity(bool visible) => FadeTo(ToolbarPanel, _toolbarFadeGate, visible, ToolbarAutoHidePolicy.TargetOpacity(_settings.ToolbarOpacityPercent));

    private readonly FadeTargetGate _toolbarFadeGate = new();
    private readonly FadeTargetGate _infoFadeGate = new();

    /// <summary>
    /// Shared fade for the toolbar and the info overlays; faded out = click-through so clicks/wheel/pan reach the image.
    /// Callers re-evaluate on every mouse move, so nothing is started while the target is unchanged.
    /// </summary>
    private static void FadeTo(UIElement element, FadeTargetGate gate, bool visible, double visibleOpacity = 1.0)
    {
        var target = visible ? visibleOpacity : 0.0;
        if (!gate.TryChange(target)) return;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(visible ? ToolbarFadeInMs : ToolbarFadeOutMs));
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
        FadeTo(InfoOverlayHost, _infoFadeGate, outcome.Opacity > 0);
    }

    // ---- feat/image-crossfade: optional fade when the CURRENT PHOTO CHANGES (navigation to another file).
    // Never fires for the progressive upgrades of the same image (ImagePresenter/MainViewModel guarantee
    // ImageChanging.IsFileChange is true only for the first bitmap of a navigation to a different file). Zero
    // cost when Settings.ImageTransition is None: the handler below returns before touching OutgoingImage,
    // which stays Collapsed (no layout/render, no animation clock). ----

    private readonly TranslateTransform _outgoingImageTransform = new();

    private void InitImageTransition()
    {
        OutgoingImage.RenderTransform = _outgoingImageTransform;
        _viewModel.ImageChanging += OnImageChanging;
    }

    private void OnImageChanging(object? sender, ImageChangingEventArgs e)
    {
        if (!e.IsFileChange) return;
        if (_settings.ImageTransition != ImageTransition.None) StartImageFade();
    }

    /// <summary>
    /// Called synchronously while <c>MainImage.Source</c> still holds the OUTGOING bitmap (the file being
    /// navigated away from) and <c>ImageScroll</c> still has its OLD size/scroll offsets -- the new image and
    /// layout are applied to MainImage/ImageScroll right after this returns (unchanged, seamless as today).
    /// Freezes the outgoing frame's geometry into OutgoingImage (a sibling OUTSIDE ImageScroll) and fades only
    /// that layer's opacity 1 -> 0; the incoming image underneath is already fully opaque, so there is no dark
    /// dip and only one animation runs at a time. If a new navigation starts before the fade ends, this method
    /// runs again and BeginAnimation's SnapshotAndReplace cancels the running clock instantly and restarts from
    /// whatever is on screen right now -- fades are never queued and the new image is never delayed by this.
    /// </summary>
    private void StartImageFade()
    {
        // First image after opening a folder (or after an error/empty state): nothing to fade from.
        if (MainImage.Source is not { } outgoingSource) return;
        var width = MainImage.ActualWidth;
        var height = MainImage.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // The element's top-left in ImageScroll coordinates already includes both the scroll offsets and the
        // letterbox centring at Fit (a zoomed image starts at -offset; a Fit image at (viewport - image) / 2).
        var origin = MainImage.TranslatePoint(new Point(0, 0), ImageScroll);
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)) return;

        OutgoingImage.Source = outgoingSource;
        OutgoingImage.Width = width;
        OutgoingImage.Height = height;
        _outgoingImageTransform.X = origin.X;
        _outgoingImageTransform.Y = origin.Y;
        OutgoingImage.Visibility = Visibility.Visible;
        OutgoingImage.Opacity = 1.0;

        var durationMs = Math.Clamp(_settings.ImageTransitionMs, AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs);
        var animation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += OnImageFadeCompleted;
        // SnapshotAndReplace: if a fade is already running, this instantly replaces its clock (no queueing);
        // the new one starts from the outgoing bitmap captured just above (whatever is on screen right now).
        OutgoingImage.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnImageFadeCompleted(object? sender, EventArgs e)
    {
        // Stop the clock and release the bitmap reference (cache eviction is unaffected either way, but
        // this avoids an extra live reference to a possibly-evicted preview sitting in the visual tree).
        OutgoingImage.BeginAnimation(OpacityProperty, null);
        OutgoingImage.Source = null;
        OutgoingImage.Visibility = Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateFitSize(); }
    private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateFitSize(); }
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
        var fullscreen = _viewModel.Viewer.IsFullscreen;
        WindowPlacementService.Save(this, placementFile, fullscreen ? _fullscreenPlacer.StateBefore : null, fullscreen ? _fullscreenPlacer.NormalBounds : null);
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
        if (_viewModel.DeferCloseForFileAction())
        {
            e.Cancel = true;
            if (!_closeWhenFileActionDone)
            {
                _closeWhenFileActionDone = true;
                CloseWhenFileActionDoneAsync().FireAndLog("Close after file action failed");
            }
        }
        base.OnClosing(e);
        if (!e.Cancel) _isClosingOrClosed = true;
    }

    private bool _isClosingOrClosed;

    /// <summary>True once the window is committed to closing (a deferred close does not count) or is closed: later requests to
    /// use it (a forwarded launch) must not touch it.</summary>
    internal bool IsClosingOrClosed => _isClosingOrClosed;

    protected override void OnClosed(EventArgs e)
    {
        _isClosingOrClosed = true;
        base.OnClosed(e);
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
        else _ = Dispatcher.BeginInvoke(_viewModel.RefreshLocalizedText);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        _settingsStore.Changed -= _onSettingsChanged;
        _startupReveal.Stop(); // a window closed before its first frame: neither its timer nor the render event may keep it alive
        try
        {
            _pointer.OnWindowClosed(); // stops a glide (unhooks the static render-frame event that would keep this window alive), ends a pan
            _viewModel.CloseSession();
        }
        finally
        {
            // The preload workers must stop even when closing the session threw.
            (_viewModel.PreloadController as IDisposable)?.Dispose();
        }
        // The IExplorerOrderProvider singleton is owned by the service provider (App.Dispose), not by this window (APP-01).
    }

    // ---- feat/mouse-zoom: wheel (zoom / navigate), click-to-zoom, drag-pan with kinetic glide ----
    // AR13a: the state machine lives in Input/PointerInputController.cs; these handlers only forward WPF input
    // and set e.Handled from what it returns.

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        // Q-TOUCHPAD-REFRESH: the device hint is read while the WM_MOUSEWHEEL is still being dispatched (WPF raises this synchronously).
        var input = new WheelInput(e.Delta, Horizontal: false, ctrl, e.Timestamp, WheelMessageSource.Current());
        var position = e.GetPosition(ImageScroll).ToPointD();
        await RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.OnWheelAsync(input, position));
    }

    /// <summary>
    /// Q-TOUCHPAD-REFRESH: WPF has no horizontal-wheel event, so WM_MOUSEHWHEEL (a sideways two-finger swipe) is taken from the
    /// window's message hook and fed to the same controller -- only over the image viewport and not while Compare covers it.
    /// </summary>
    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanged && !_shownNative && WindowPosShowsWindow(lParam))
        {
            _shownNative = true;
            _startupReveal.NoteShown();
            StartInitialDecode(hwnd);
        }
        if (msg != WheelMessageSource.WmMouseHWheel || !IsLoaded || _viewModel.Compare.IsVisible) return IntPtr.Zero;
        var position = ImageScroll.PointFromScreen(WheelMessageSource.ScreenPoint(lParam).ToWpfPoint()).ToPointD();
        if (position.X < 0 || position.Y < 0 || position.X >= ImageScroll.ViewportWidth || position.Y >= ImageScroll.ViewportHeight) return IntPtr.Zero;
        handled = true;
        NoteInfoActivity();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var input = new WheelInput(WheelMessageSource.Delta(wParam), Horizontal: true, ctrl, Environment.TickCount, WheelMessageSource.Current());
        RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.OnWheelAsync(input, position)).FireAndLog("Horizontal wheel failed");
        return IntPtr.Zero;
    }

    /// <summary>
    /// A4: an <c>async void</c> handler that lets an exception escape reaches the dispatcher's unhandled-exception handler
    /// (a crash). Runs <paramref name="action"/>, and on failure logs it and shows the same "action failed" status text the
    /// file-action pipeline uses; cancellation is not a failure.
    /// </summary>
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
            AppLog.Error($"{actionName} failed", ex);
            _viewModel.StatusText = PhotoReview.App.ViewModels.StatusFormatter.ActionFailed(actionName, PhotoReview.Core.Localization.UserFacingError.Describe(ex));
        }
    }

    private static string TrimEllipsis(string text) => text.TrimEnd('…', '.', ' ');

    /// <summary>Tunnels before the image's handler: any press stops a glide (remembered so that press is not a click).</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _pointer.OnWindowPreviewMouseDown();

    private void MainImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pointer.OnImagePress(e.ChangedButton.ToPointerButton(), e.ClickCount, e.GetPosition(ImageScroll).ToPointD(), e.Timestamp)) e.Handled = true;
    }

    /// <summary>
    /// Middle button over the image viewport (not its scroll bars): runs the command chosen by <see cref="AppSettings.MiddleClickAction"/> (nothing for
    /// <c>None</c>). Acts on the press; a repeated press of a multi-click is ignored so the action never runs twice. The
    /// left-button pan/click-zoom state machine is not involved.
    /// </summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ImageScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || IsInsideScrollBar(e.OriginalSource)) return;
        e.Handled = true;
        if (e.ClickCount != 1 || !_viewModel.HasImages) return;
        if (MiddleClickResolver.Resolve(_settings.MiddleClickAction) is { } command)
            await ExecuteReviewCommandAsync(command);
    }

    private static bool IsInsideScrollBar(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ScrollBar) return true;
        }
        return false;
    }

    private void MainImage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pointer.OnImageMove(e.LeftButton == MouseButtonState.Pressed, e.GetPosition(ImageScroll).ToPointD(), e.Timestamp)) e.Handled = true;
    }

    private void MainImage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pointer.OnImageRelease(e.GetPosition(ImageScroll).ToPointD(), e.Timestamp)) e.Handled = true;
    }

    private void MainImage_LostMouseCapture(object sender, MouseEventArgs e) => _pointer.OnLostCapture();

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        // async void: an exception escaping here would reach the dispatcher's unhandled-exception handler (a crash).
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await _viewModel.OpenPathAsync(files[0]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Drop open failed", ex);
            _viewModel.StatusText = PhotoReview.App.ViewModels.StatusFormatter.FolderOpenFailed(PhotoReview.Core.Localization.UserFacingError.Describe(ex));
        }
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        // feat/zoom-key-anchor: an arrow key that TryPanByArrow consumes owns the glide decision itself (it may
        // start/extend a kinetic impulse); every other key still stops a running glide unconditionally.
        var arrowConsumed = Keyboard.Modifiers == ModifierKeys.None && !_viewModel.Compare.IsVisible && _pointer.TryPanByArrow(pressedKey.ToKeyId(), e.IsRepeat);
        if (!arrowConsumed) _pointer.StopKinetic(); // feat/mouse-zoom: any other key press stops a glide
        if (PhotoReviewPerf.Log.IsEnabled())
            PhotoReviewPerf.Log.KeyInput(0, pressedKey.ToString(), unchecked(Environment.TickCount - e.Timestamp));
        if (arrowConsumed)
        {
            e.Handled = true;
            return;
        }

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

        var cmd = _shortcutRouter.TryResolve(e.Key.ToKeyId(), e.SystemKey.ToKeyId(), Keyboard.Modifiers.ToKeyModifiers(), _viewModel.Viewer.IsFullscreen, _viewModel.HasImages, hasComparePair: _viewModel.CurrentHasComparePair, isCompareVisible: _viewModel.Compare.IsVisible, hasCapturePair: _viewModel.CurrentHasCapturePair);
        if (cmd is null) return;
        e.Handled = true;
        if (e.IsRepeat && cmd.Value.Type.IgnoresAutoRepeat()) return; // R2-F-06: never repeat file actions

        await ExecuteReviewCommandAsync(cmd.Value);
    }


    /// <summary>
    /// Runs one resolved command. Shared by the keyboard path (<see cref="Window_KeyDown"/>) and the middle-click path
    /// (<see cref="MainImage_PreviewMouseDown"/> through <see cref="MiddleClickResolver"/>), so a command behaves the same
    /// whichever input started it.
    /// </summary>
    private async Task ExecuteReviewCommandAsync(ReviewCommand command)
    {
        switch (command.Type)
        {
            case ReviewCommandType.Fullscreen: _viewModel.ToggleFullscreen(); break;
            case ReviewCommandType.ExitFullscreen: _viewModel.ExitFullscreen(); break;
            case ReviewCommandType.Close: Close(); break;
            case ReviewCommandType.NextFolder: await RunGuardedAsync(Tr.MainMenuNextFolder, () => _viewModel.NavigateSiblingFolderAsync(1)); break;
            case ReviewCommandType.PreviousFolder: await RunGuardedAsync(Tr.MainMenuPreviousFolder, () => _viewModel.NavigateSiblingFolderAsync(-1)); break;
            case ReviewCommandType.FirstImage: await _viewModel.FirstAsync(); break;
            case ReviewCommandType.LastImage: await _viewModel.LastAsync(); break;
            case ReviewCommandType.ToggleInfoOverlay: _viewModel.ToggleInfoOverlay(); break;
            case ReviewCommandType.ZoomActualSize: await _pointer.ZoomActualSizeAsync(); break;
            case ReviewCommandType.Undo: await _viewModel.UndoAsync(); break;
            case ReviewCommandType.ToggleCompare: _viewModel.ToggleCompare(); break;
            case ReviewCommandType.ToggleCaptureMember: await _viewModel.ToggleCaptureGroupMemberAsync(); break;
            case ReviewCommandType.RunAction:
                var actionIndex = command.ActionIndex;
                var profiles = _settings.Actions;
                await RunGuardedAsync(actionIndex >= 0 && actionIndex < profiles.Count ? profiles[actionIndex].Name : string.Empty,
                    () => _viewModel.RunActionAsync(actionIndex));
                break;
            case ReviewCommandType.Recycle: await _viewModel.RecycleAsync(); break;
            case ReviewCommandType.Skip: await _viewModel.SkipAsync(); break;
            case ReviewCommandType.ToggleFit: ApplyFitViewAsync().FireAndLog("Toggle fit failed"); break;
            case ReviewCommandType.ZoomIn: await _pointer.ZoomInAsync(); break;
            case ReviewCommandType.ZoomOut: await _pointer.ZoomOutAsync(); break;
            case ReviewCommandType.Next: await _viewModel.NextAsync(); break;
            case ReviewCommandType.Previous: await _viewModel.PreviousAsync(); break;
            case ReviewCommandType.MoveToFolder: await RunGuardedAsync(Tr.ActionMoveToFolderName, () => _viewModel.MoveToFolderAsync(command.ForcePicker)); break;
            case ReviewCommandType.CopyToFolder: await RunGuardedAsync(Tr.ActionCopyToFolderName, () => _viewModel.CopyToFolderAsync(command.ForcePicker)); break;
            case ReviewCommandType.ClickZoom: await _pointer.ToggleClickZoomAsync(); break;
            case ReviewCommandType.FitWidth or ReviewCommandType.FitWidth2:
                await _pointer.FitWidthAsync(MainWindowHelpers.FitWidthAnchorFor(_settings, command.Type));
                break;
            case ReviewCommandType.FitHeight: await _pointer.FitHeightAsync(); break;
            case ReviewCommandType.ToggleKeepZoom: ToggleKeepZoomAcrossImages(); break;
            case ReviewCommandType.OpenFolder: await RunGuardedAsync(TrimEllipsis(Tr.MainMenuOpenFolder), () => _viewModel.PickAndOpenFolderAsync()); break;
            case ReviewCommandType.CustomZoom: await RunGuardedAsync(Tr.MainMenuZoom, OpenClickZoomCustomDialogAsync); break;
            case ReviewCommandType.Refresh: RefreshView(); break;
        }
    }

    /// <summary>
    /// Flips and persists <see cref="AppSettings.KeepZoomAcrossImages"/> (PR-B), same pattern as
    /// <see cref="MainViewModel.ToggleInfoOverlay"/>.
    /// </summary>
    private void ToggleKeepZoomAcrossImages()
    {
        var settings = _settings;
        settings.KeepZoomAcrossImages = !settings.KeepZoomAcrossImages;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Error("Could not save the keep-zoom setting", ex);
        }
    }

    private void ReturnFocusAfterButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) return;
        // Deferred: a dialog opened by the click may take focus first; only reclaim it while the button still holds it.
        _ = Dispatcher.BeginInvoke(() =>
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

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(TrimEllipsis(Tr.MainMenuOpenFolder), () => _viewModel.PickAndOpenFolderAsync());
    private void OpenInExternalEditor_Click(object sender, RoutedEventArgs e) => _viewModel.OpenInExternalEditor();
    private void CopyFileName_Click(object sender, RoutedEventArgs e) => _viewModel.CopyFileName();
    private void CopyFullPath_Click(object sender, RoutedEventArgs e) => _viewModel.CopyFullPathname();
    private void Settings_Click(object sender, RoutedEventArgs e) => _viewModel.ShowSettings();
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void FitImage_Click(object sender, RoutedEventArgs e) => await ApplyFitViewAsync();
    private Task ApplyFitViewAsync() => _fit.ApplyFitAsync(); // T89 convergence loop: Coordinators/FitViewController.cs

    private void Recovery_Click(object sender, RoutedEventArgs e) => _viewModel.ShowRecovery();
    private void SkippedFiles_Click(object sender, RoutedEventArgs e) => _viewModel.ShowSkippedFiles();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDiagnostics();
    private void Benchmark_Click(object sender, RoutedEventArgs e) => _viewModel.ShowBenchmark();
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await _viewModel.ClearCacheAsync();
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(true);
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(false);
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void UndoLastAction_Click(object sender, RoutedEventArgs e) => await _viewModel.UndoAsync();
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void Recycle_Click(object sender, RoutedEventArgs e) => await _viewModel.RecycleAsync();

    // ---- Context menu: "Zoom" submenu (Fit width/height, presets + Custom…, "also set" toggle, "set current as ----
    // ---- click level"). Built once; refreshed (text + IsChecked/IsEnabled) on every open so a live language ----
    // ---- switch and a setting changed elsewhere both show correctly (PR-C feat/context-menu-redesign). ----

    private static readonly int[] ZoomPresets = [50, 70, 100, 150, 200, 300, 400];
    private List<System.Windows.Controls.MenuItem>? _clickZoomPresetItems;
    private System.Windows.Controls.MenuItem? _clickZoomCustomItem;
    private System.Windows.Controls.MenuItem? _zoomFitWidthItem;
    private System.Windows.Controls.MenuItem? _zoomFitWidth2Item;
    private System.Windows.Controls.MenuItem? _zoomFitHeightItem;
    private System.Windows.Controls.MenuItem? _alsoSetClickLevelItem;
    private System.Windows.Controls.Separator? _zoomSeparatorPresets;  // Zoom submenu: before the preset group
    private System.Windows.Controls.Separator? _zoomSeparatorLevelOptions; // Zoom submenu: before "also set"/"set current"
    private System.Windows.Controls.MenuItem? _setCurrentZoomAsClickLevelItem;

    private void ZoomMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // Headers/gestures are re-read on every open so a language switch or a shortcut change shows without a restart.
        _zoomFitWidthItem!.Header = Tr.MainMenuZoomFitWidth;
        AutomationProperties.SetName(_zoomFitWidthItem, Tr.MainMenuZoomFitWidthAutomationName);
        _zoomFitWidthItem.InputGestureText = _settings.Shortcuts.FitWidth;
        _zoomFitWidth2Item!.Header = Tr.MainMenuZoomFitWidth2;
        AutomationProperties.SetName(_zoomFitWidth2Item, Tr.MainMenuZoomFitWidth2AutomationName);
        _zoomFitWidth2Item.InputGestureText = _settings.Shortcuts.FitWidth2;
        _zoomFitHeightItem!.Header = Tr.MainMenuZoomFitHeight;
        AutomationProperties.SetName(_zoomFitHeightItem, Tr.MainMenuZoomFitHeightAutomationName);
        _zoomFitHeightItem.InputGestureText = _settings.Shortcuts.FitHeight;

        _clickZoomCustomItem!.Header = Tr.MainMenuClickZoomLevelCustom;
        AutomationProperties.SetName(_clickZoomCustomItem, Tr.MainMenuClickZoomLevelCustomAutomationName);
        var current = _settings.ClickZoomPercent;
        foreach (var item in _clickZoomPresetItems!)
        {
            var percent = (int)item.Tag!;
            item.Header = Tr.MainMenuClickZoomLevelPreset(percent);
            AutomationProperties.SetName(item, Tr.MainMenuClickZoomLevelPresetAutomationName(percent));
            item.IsChecked = percent == current;
        }

        _alsoSetClickLevelItem!.Header = Tr.MainMenuZoomAlsoSetClickLevel;
        AutomationProperties.SetName(_alsoSetClickLevelItem, Tr.MainMenuZoomAlsoSetClickLevelAutomationName);
        _alsoSetClickLevelItem.IsChecked = _settings.SetZoomAlsoSetsClickLevel;

        _setCurrentZoomAsClickLevelItem!.Header = Tr.MainMenuZoomSetCurrentAsClickLevel;
        AutomationProperties.SetName(_setCurrentZoomAsClickLevelItem, Tr.MainMenuZoomSetCurrentAsClickLevelAutomationName);
        _setCurrentZoomAsClickLevelItem.IsEnabled = MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(_viewModel.Viewer.EffectiveZoom) is not null;
    }

    private void BuildZoomMenu()
    {
        var fitWidth = _zoomFitWidthItem = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuZoomFitWidth };
        AutomationProperties.SetName(fitWidth, Tr.MainMenuZoomFitWidthAutomationName);
        fitWidth.Click += ZoomFitWidth_Click;
        ZoomMenu.Items.Add(fitWidth);

        var fitWidth2 = _zoomFitWidth2Item = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuZoomFitWidth2 };
        AutomationProperties.SetName(fitWidth2, Tr.MainMenuZoomFitWidth2AutomationName);
        fitWidth2.Click += ZoomFitWidth2_Click;
        ZoomMenu.Items.Add(fitWidth2);

        var fitHeight = _zoomFitHeightItem = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuZoomFitHeight };
        AutomationProperties.SetName(fitHeight, Tr.MainMenuZoomFitHeightAutomationName);
        fitHeight.Click += ZoomFitHeight_Click;
        ZoomMenu.Items.Add(fitHeight);

        _zoomSeparatorPresets = new System.Windows.Controls.Separator();
        ZoomMenu.Items.Add(_zoomSeparatorPresets);

        _clickZoomPresetItems = [];
        foreach (var percent in ZoomPresets)
        {
            var item = new System.Windows.Controls.MenuItem { IsCheckable = true, Tag = percent };
            item.Header = Tr.MainMenuClickZoomLevelPreset(percent);
            item.Click += ClickZoomPreset_Click;
            _clickZoomPresetItems.Add(item);
            ZoomMenu.Items.Add(item);
        }
        var custom = _clickZoomCustomItem = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuClickZoomLevelCustom };
        AutomationProperties.SetName(custom, Tr.MainMenuClickZoomLevelCustomAutomationName);
        custom.Click += ClickZoomCustom_Click;
        ZoomMenu.Items.Add(custom);

        _zoomSeparatorLevelOptions = new System.Windows.Controls.Separator();
        ZoomMenu.Items.Add(_zoomSeparatorLevelOptions);

        var alsoSet = _alsoSetClickLevelItem = new System.Windows.Controls.MenuItem { IsCheckable = true, Header = Tr.MainMenuZoomAlsoSetClickLevel };
        AutomationProperties.SetName(alsoSet, Tr.MainMenuZoomAlsoSetClickLevelAutomationName);
        alsoSet.Click += AlsoSetClickLevel_Click;
        ZoomMenu.Items.Add(alsoSet);

        var setCurrent = _setCurrentZoomAsClickLevelItem = new System.Windows.Controls.MenuItem { Header = Tr.MainMenuZoomSetCurrentAsClickLevel };
        AutomationProperties.SetName(setCurrent, Tr.MainMenuZoomSetCurrentAsClickLevelAutomationName);
        setCurrent.Click += SetCurrentZoomAsClickLevel_Click;
        ZoomMenu.Items.Add(setCurrent);
    }

    private void ClickZoomFit_Click(object sender, RoutedEventArgs e) => ApplyFitViewAsync().FireAndLog("Fit view failed");

    /// <summary>Q-TOUCHPAD-REFRESH: context menu "Refresh", the same command as its shortcut.</summary>
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshView();

    /// <summary>
    /// Refresh: best-quality re-render of the current view (<see cref="MainViewModel.RefreshView"/>). Stops a glide first so the
    /// view is still; zoom and scroll offsets are never changed here.
    /// </summary>
    private void RefreshView()
    {
        _pointer.StopKinetic();
        _viewModel.RefreshView();
        MainImage.InvalidateVisual();
    }

    /// <summary>"Zoom to N%": zooms to the configured level (does not change it; the presets below do).</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ZoomToLevel_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.SetClickZoomLevelAsync(_settings.ClickZoomPercent));

    /// <summary>"Zoom" submenu: Fit width, anchored at the mouse when it is over the viewport (same rule as the shortcut).</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ZoomFitWidth_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.FitWidthAsync(MainWindowHelpers.FitWidthAnchorFor(_settings, ReviewCommandType.FitWidth)));

    /// <summary>"Zoom" submenu: the second Fit width, with its own anchor (<see cref="AppSettings.FitWidthAnchor2"/>).</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ZoomFitWidth2_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.FitWidthAsync(MainWindowHelpers.FitWidthAnchorFor(_settings, ReviewCommandType.FitWidth2)));

    /// <summary>"Zoom" submenu: Fit height (always centred).</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ZoomFitHeight_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuZoom, () => _pointer.FitHeightAsync());

    /// <summary>Folder group: NavigateSiblingFolderAsync(+1), the same command PageDown runs.</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void NextFolder_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuNextFolder, () => _viewModel.NavigateSiblingFolderAsync(1));

    /// <summary>Folder group: NavigateSiblingFolderAsync(-1), the same command PageUp runs.</summary>
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void PreviousFolder_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuPreviousFolder, () => _viewModel.NavigateSiblingFolderAsync(-1));

    /// <summary>
    /// Refreshes the top-level items on every open: the level in "Zoom to N%", each item's shortcut (as configured,
    /// shown right-aligned like an accelerator; empty when the shortcut is cleared), the zoom cluster's visibility
    /// (<see cref="AppSettings.ShowZoomMenuItems"/>) -- Fit to window/Zoom to N%/the Zoom submenu and the separator
    /// right above them are hidden together as one unit -- and the folder group's visibility
    /// (<see cref="AppSettings.ShowFolderMenuItems"/>, PR-C) -- Open folder/Next folder/Previous folder and the
    /// separator right above them are hidden together as one unit -- and the Move to Recycle Bin item's visibility
    /// (<see cref="AppSettings.ShowRecycleMenuItem"/>) -- a single item, hidden by default, no separator of its own.
    /// </summary>
    private void ImageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var percent = _settings.ClickZoomPercent;
        ZoomToLevelMenuItem.Header = Tr.MainMenuZoomToLevel(percent);
        AutomationProperties.SetName(ZoomToLevelMenuItem, Tr.MainMenuZoomToLevelAutomationName(percent));
        ZoomToLevelMenuItem.InputGestureText = _settings.Shortcuts.ClickZoom;
        FitMenuItem.InputGestureText = _settings.Shortcuts.ToggleFit;
        RefreshMenuItem.InputGestureText = _settings.Shortcuts.Refresh;

        RecycleMenuItem.InputGestureText = _settings.Shortcuts.SendToRecycleBin;
        NextFolderMenuItem.InputGestureText = _settings.Shortcuts.NextFolder;
        PreviousFolderMenuItem.InputGestureText = _settings.Shortcuts.PreviousFolder;
        ApplyContextMenuLayout();
    }

    /// <summary>
    /// Shows/hides every menu element from the pure rule in <see cref="ContextMenuItems.Compute"/>: the user's hidden list
    /// (<see cref="AppSettings.HiddenContextMenuItems"/>, or the legacy flags of an old config) plus the context (Copy items need an
    /// open photo). A group separator is shown only between two groups that both have a visible item, so no stray line is left.
    /// </summary>
    private void ApplyContextMenuLayout()
    {
        var hidden = ContextMenuItems.EffectiveHidden(_settings);
        var context = new ContextMenuContext(_viewModel.CanCopyCurrentFilePath);
        var top = ContextMenuItems.Compute(hidden, context);
        static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        UndoMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.Undo));
        RecycleMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.MoveToRecycleBin));
        FitMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.Fit));
        ZoomToLevelMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.ZoomToLevel));
        ZoomMenu.Visibility = Vis(top.IsVisible(ContextMenuItemId.ZoomSubmenu));
        RefreshMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.Refresh));
        OpenFolderMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.OpenFolder));
        NextFolderMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.NextFolder));
        PreviousFolderMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.PreviousFolder));
        OpenInExternalEditorMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.ExternalEditor));
        CopyFileNameMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.CopyFileName));
        CopyFullPathMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.CopyFullPath));
        SettingsMenuItem.Visibility = Vis(top.IsVisible(ContextMenuItemId.Settings));
        ZoomGroupSeparator.Visibility = Vis(top.HasSeparatorBefore(2));
        FolderGroupSeparator.Visibility = Vis(top.HasSeparatorBefore(3));
        ExternalEditorGroupSeparator.Visibility = Vis(top.HasSeparatorBefore(4));
        CopyGroupSeparator.Visibility = Vis(top.HasSeparatorBefore(5));
        SettingsGroupSeparator.Visibility = Vis(top.HasSeparatorBefore(6));

        var zoom = ContextMenuItems.Compute(hidden, context, ContextMenuItemId.ZoomSubmenu);
        _zoomFitWidthItem!.Visibility = Vis(zoom.IsVisible(ContextMenuItemId.ZoomFitWidth));
        _zoomFitWidth2Item!.Visibility = Vis(zoom.IsVisible(ContextMenuItemId.ZoomFitWidth2));
        _zoomFitHeightItem!.Visibility = Vis(zoom.IsVisible(ContextMenuItemId.ZoomFitHeight));
        _zoomSeparatorPresets!.Visibility = Vis(zoom.HasSeparatorBefore(2));
        var presets = Vis(zoom.IsVisible(ContextMenuItemId.ZoomPresets));
        foreach (var item in _clickZoomPresetItems!) item.Visibility = presets;
        _clickZoomCustomItem!.Visibility = presets;
        _zoomSeparatorLevelOptions!.Visibility = Vis(zoom.HasSeparatorBefore(3));
        var levelOptions = Vis(zoom.IsVisible(ContextMenuItemId.ZoomLevelOptions));
        _alsoSetClickLevelItem!.Visibility = levelOptions;
        _setCurrentZoomAsClickLevelItem!.Visibility = levelOptions;
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ClickZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int percent }) return;
        await RunGuardedAsync(Tr.MainMenuZoom, () => ApplyZoomMenuSelectionAsync(percent));
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void ClickZoomCustom_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(Tr.MainMenuZoom, OpenClickZoomCustomDialogAsync);

    /// <summary>Q-R43: shared by the "Custom…" menu item and the CustomZoom keyboard shortcut.</summary>
    private async Task OpenClickZoomCustomDialogAsync()
    {
        var dialog = new ClickZoomCustomDialog(_settings.ClickZoomPercent) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await ApplyZoomMenuSelectionAsync(dialog.Value);
        }
    }

    /// <summary>
    /// "Zoom" submenu preset/Custom selection (PR-C): always zooms to <paramref name="percent"/> now; additionally
    /// persists it as <see cref="AppSettings.ClickZoomPercent"/> when <see cref="AppSettings.SetZoomAlsoSetsClickLevel"/>
    /// is on (default, matches the behaviour before that setting existed). When off, this is a one-off zoom that
    /// leaves the saved click zoom level untouched.
    /// </summary>
    private async Task ApplyZoomMenuSelectionAsync(int percent)
    {
        if (_settings.SetZoomAlsoSetsClickLevel)
        {
            await ApplyClickZoomLevelAsync(percent);
        }
        else
        {
            await _pointer.SetClickZoomLevelAsync(percent);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The image still zooms for this session; only persisting the new level failed.
            AppLog.Error("Could not save the click zoom level setting", ex);
        }
        await _pointer.SetClickZoomLevelAsync(percent);
    }

    /// <summary>"Also set as click zoom level" toggle (PR-C): persists immediately, same pattern as <c>ToggleKeepZoomAcrossImages</c>.</summary>
    private void AlsoSetClickLevel_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settings;
        settings.SetZoomAlsoSetsClickLevel = _alsoSetClickLevelItem!.IsChecked;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Error("Could not save the 'also set as click zoom level' setting", ex);
        }
    }

    /// <summary>
    /// "Set current zoom as click level" (PR-C): captures the current effective zoom as the new <see cref="AppSettings.ClickZoomPercent"/>
    /// (round + clamp, <see cref="MainWindowHelpers.CalculateClickLevelFromEffectiveZoom"/>) and saves it -- the image is
    /// already at that zoom, so no re-zoom is needed. Disabled in Fit (see <see cref="ZoomMenu_SubmenuOpened"/>); the
    /// null check here is a defensive fallback, not the primary guard.
    /// </summary>
    private void SetCurrentZoomAsClickLevel_Click(object sender, RoutedEventArgs e)
    {
        var percent = MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(_viewModel.Viewer.EffectiveZoom);
        if (percent is null) return;
        var settings = _settings;
        settings.ClickZoomPercent = percent.Value;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Error("Could not save the click zoom level setting", ex);
        }
    }
}
