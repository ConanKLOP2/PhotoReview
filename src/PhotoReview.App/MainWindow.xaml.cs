using PhotoReview.Core.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    private readonly IExplorerOrderProvider? _explorerOrder;
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private double? _cachedDpiScale;
    private readonly ViewportSizeSource? _viewport;
    private bool _placementRestored;
    private long _viewportOperationVersion;
    private bool _isPanning;
    private bool _panMoved;
    private Point _panStartPoint;
    private Point _panLastPoint;

    // AR02d: read-only properties replacing the public mutable fields that used to be kept in
    // sync by WireViewModelEvents/SyncFiles (ST06/Q-ST3 exception, superseded by this change).

    public IReadOnlyList<string> Files => _viewModel.Catalog.Paths;
    public int CurrentIndex => _viewModel.CurrentIndex;
    public string? CompareSelectedPath => _viewModel.Compare.SelectedPath;
    public ReviewMetrics Metrics => _viewModel.Metrics;
    public bool IsFileActionInProgress => _viewModel.IsFileActionInProgress;

    public MainViewModel ViewModel => _viewModel;
    public AppSettings Settings => _settings;

    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore, ViewportSizeSource viewport, IExplorerOrderProvider? explorerOrder = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        ArgumentNullException.ThrowIfNull(viewport);
        _explorerOrder = explorerOrder;
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
        _settingsStore.Changed += (_, s) => { _settings = s; _viewModel.Settings = s; _shortcutRouter.Rebuild(s); };
        PhotoReviewPerf.StartupMark("mainWindowCtor");
        // I18N: a live language switch re-renders the texts the ViewModel builds in code (ADR 0006).
        Localizer.CurrentChanged += OnLanguageChanged;
        DataContext = _viewModel;
        InitializeComponent();
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ReturnFocusAfterButtonClick), handledEventsToo: true);
        PhotoReviewPerf.StartupMark("xamlLoaded");
        ContentRendered += (_, _) => PhotoReviewPerf.StartupMark("contentRendered");
        viewport.Get = GetViewportSize;
        _viewport = viewport;
        DpiChanged += MainWindow_DpiChanged;
        UpdateTargetDecodeBox();
        WireViewModelEvents();
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
        };
    }

    public Task LoadFolderAsync(string folder, string? initialPath = null) => _viewModel.OpenFolderAsync(folder, initialPath);

    /// <summary>
    /// Public test helper: Executes the unified undo operation with proper mutual exclusion guard.
    /// Used by integration tests via reflection. Routes to MainViewModel.UndoAsync() (gate lives in the ViewModel, OC14).
    /// </summary>
    public Task UndoLastActionAsync() => _viewModel.UndoAsync();
    public void ResetFitView() => _ = ApplyFitViewAsync();
    public void SetZoom(double level) => _viewModel.Viewer.SetZoom(level);
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

    private (double Width, double Height) GetViewportSize() =>
        (Math.Max(0, ImageScroll.ActualWidth - ImageScroll.BorderThickness.Left - ImageScroll.BorderThickness.Right),
         Math.Max(0, ImageScroll.ActualHeight - ImageScroll.BorderThickness.Top - ImageScroll.BorderThickness.Bottom));

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
        if (!_placementRestored) { _placementRestored = true; WindowPlacementService.Restore(this); }
        UpdateFitSize();
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
    private void Window_Closing(object? sender, CancelEventArgs e) => WindowPlacementService.Save(this);

    /// <summary>Harness use: never restore or save the user's real window-placement.json for this instance.</summary>
    public void SuppressWindowPlacement()
    {
        _placementRestored = true;
        Closing -= Window_Closing;
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) _viewModel.RefreshLocalizedText();
        else Dispatcher.BeginInvoke(_viewModel.RefreshLocalizedText);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        CancelPan();
        _viewModel.FlushSession();
        (_viewModel.PreloadController as IDisposable)?.Dispose();
        _explorerOrder?.Dispose();
    }

    private async void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var delta = e.Delta;
        await ZoomAroundAsync(e.GetPosition(ImageScroll), () => _viewModel.Viewer.WheelZoom(delta));
    }

    /// <summary>ZoomActualSize: 100 % (ADR 0008) keeping the image point at the viewport centre in place.</summary>
    private async Task ZoomActualSizeAsync()
    {
        if (!_viewModel.HasImages) return;
        CancelPan();
        var centre = new Point(ImageScroll.ViewportWidth / 2, ImageScroll.ViewportHeight / 2);
        await ZoomAroundAsync(centre, _viewModel.ZoomActualSize);
    }

    /// <summary>
    /// Applies <paramref name="applyZoom"/> and scrolls so the image point under <paramref name="anchor"/> (in
    /// ImageScroll coordinates) stays under it: the wheel's mouse position, or the viewport centre for ZoomActualSize.
    /// </summary>
    private async Task ZoomAroundAsync(Point anchor, Action applyZoom)
    {
        var elementPoint = ImageScroll.TranslatePoint(anchor, MainImage);
        var anchorBefore = MainImage.TranslatePoint(elementPoint, ImageScroll);
        // feat(zoom): the element is sized from original dims x zoom (no LayoutTransform), so the
        // anchor is carried across the zoom step as a fraction of the displayed image.
        MainWindowHelpers.ZoomImagePoint anchorFraction;
        if (_viewModel.Viewer.IsFit && MainImage.Source is { Width: > 0, Height: > 0 } source)
        {
            var sourcePoint = MainWindowHelpers.CalculateUniformImagePoint(
                MainImage.ActualWidth,
                MainImage.ActualHeight,
                source.Width,
                source.Height,
                elementPoint.X,
                elementPoint.Y);
            anchorFraction = MainWindowHelpers.NormalizeImagePoint(sourcePoint.X, sourcePoint.Y, source.Width, source.Height);
        }
        else
        {
            anchorFraction = MainWindowHelpers.NormalizeImagePoint(elementPoint.X, elementPoint.Y, MainImage.ActualWidth, MainImage.ActualHeight);
        }
        var version = ++_viewportOperationVersion;
        applyZoom();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        if (version != _viewportOperationVersion || !IsLoaded) return;
        ImageScroll.UpdateLayout();

        var pointInImage = new Point(anchorFraction.X * MainImage.ActualWidth, anchorFraction.Y * MainImage.ActualHeight);
        var anchorAfter = MainImage.TranslatePoint(pointInImage, ImageScroll);
        var offsets = MainWindowHelpers.CalculateOffsetsFromAnchorDelta(
            ImageScroll.HorizontalOffset,
            ImageScroll.VerticalOffset,
            anchorBefore.X,
            anchorBefore.Y,
            anchorAfter.X,
            anchorAfter.Y,
            ImageScroll.ExtentWidth,
            ImageScroll.ExtentHeight,
            ImageScroll.ViewportWidth,
            ImageScroll.ViewportHeight);
        ImageScroll.ScrollToHorizontalOffset(offsets.Horizontal);
        ImageScroll.ScrollToVerticalOffset(offsets.Vertical);
    }

    private void MainImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DF03: Double-click to Fit (before pan check since CanPan=false in Fit mode)
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            CancelPan();
            _ = ApplyFitViewAsync();
            e.Handled = true;
            return;
        }

        if (!CanPan()) return;

        _isPanning = true;
        _panMoved = false;
        _panStartPoint = e.GetPosition(ImageScroll);
        _panLastPoint = _panStartPoint;
        MainImage.Cursor = Cursors.SizeAll;
        MainImage.CaptureMouse();
        e.Handled = true;
    }

    private void MainImage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(ImageScroll);
        var deltaX = point.X - _panLastPoint.X;
        var deltaY = point.Y - _panLastPoint.Y;
        _panLastPoint = point;
        // OC15: once the drag threshold is crossed it stays crossed until the pan ends, so only
        // test it while still below; delta/scroll below always run.
        if (!_panMoved && MainWindowHelpers.IsBeyondDragThreshold(
                point.X - _panStartPoint.X,
                point.Y - _panStartPoint.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            _panMoved = true;
        }

        var offsets = MainWindowHelpers.CalculatePanOffsets(
            ImageScroll.HorizontalOffset,
            ImageScroll.VerticalOffset,
            deltaX,
            deltaY,
            ImageScroll.ExtentWidth,
            ImageScroll.ExtentHeight,
            ImageScroll.ViewportWidth,
            ImageScroll.ViewportHeight);
        ImageScroll.ScrollToHorizontalOffset(offsets.Horizontal);
        ImageScroll.ScrollToVerticalOffset(offsets.Vertical);
        if (_panMoved) e.Handled = true;
    }

    private void MainImage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var moved = _panMoved;
        CancelPan();
        if (moved) e.Handled = true;
    }

    private void MainImage_LostMouseCapture(object sender, MouseEventArgs e) => CancelPan();

    private bool CanPan() => !_viewModel.Viewer.IsFit &&
        (ImageScroll.ExtentWidth > ImageScroll.ViewportWidth + 0.5 || ImageScroll.ExtentHeight > ImageScroll.ViewportHeight + 0.5);

    private void CancelPan()
    {
        _isPanning = false;
        _panMoved = false;
        if (Mouse.Captured == MainImage) Mouse.Capture(null);
        MainImage.Cursor = Cursors.Arrow;
    }

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
        if (PhotoReviewPerf.Log.IsEnabled())
            PhotoReviewPerf.Log.KeyInput(0, pressedKey.ToString(), unchecked(Environment.TickCount - e.Timestamp));

        // Space/Enter belong to a focused button or compare pane (keyboard activation); the window-level tunnel must not steal them.
        // A mouse click leaves focus on the toolbar button, so ReturnFocusAfterButtonClick hands it back to the window;
        // otherwise Space/Enter would keep re-activating that button instead of Skip / Move to folder 2.
        if (pressedKey is Key.Space or Key.Enter && e.OriginalSource is DependencyObject source && OwnsActivationKeys(source)) return;

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
            case ReviewCommandType.ZoomActualSize: await ZoomActualSizeAsync(); break;
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

    private async Task ApplyFitViewAsync()
    {
        CancelPan();
        ++_viewportOperationVersion;
        var version = _viewportOperationVersion;
        var (width, height) = GetViewportSize();
        _viewModel.Viewer.ResetFit(width, height);

        // Converge layout: loop until viewport stabilizes or max iterations reached
        ViewportSnapshot? lastSnapshot = null;
        for (var pass = 0; pass < 3; pass++)
        {
            if (version != _viewportOperationVersion || !IsLoaded) return;
            ImageScroll.UpdateLayout();
            UpdateFitSize();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            // Check viewport convergence
            var currentSnapshot = CaptureViewportSnapshot();
            if (lastSnapshot != null && ViewportConvergence.IsStableViewport(lastSnapshot, currentSnapshot))
            {
                // Viewport stable, no need to continue
                break;
            }
            lastSnapshot = currentSnapshot;
        }

        if (version != _viewportOperationVersion || !IsLoaded) return;
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHome();
        ImageScroll.ScrollToHorizontalOffset(0);
        ImageScroll.ScrollToVerticalOffset(0);
    }

    private ViewportSnapshot CaptureViewportSnapshot()
    {
        var image = MainImage.Source;
        return new ViewportSnapshot(
            Zoom: _viewModel.Viewer.Zoom,
            Stretch: _viewModel.Viewer.Stretch,
            MaxImageWidth: _viewModel.Viewer.MaxImageWidth,
            MaxImageHeight: _viewModel.Viewer.MaxImageHeight,
            ActualImageWidth: MainImage.ActualWidth,
            ActualImageHeight: MainImage.ActualHeight,
            ExtentWidth: ImageScroll.ExtentWidth,
            ExtentHeight: ImageScroll.ExtentHeight,
            ViewportWidth: ImageScroll.ViewportWidth,
            ViewportHeight: ImageScroll.ViewportHeight,
            HorizontalOffset: ImageScroll.HorizontalOffset,
            VerticalOffset: ImageScroll.VerticalOffset,
            HorizontalScrollbarVisibility: ImageScroll.ComputedHorizontalScrollBarVisibility,
            VerticalScrollbarVisibility: ImageScroll.ComputedVerticalScrollBarVisibility);
    }
    private void Recovery_Click(object sender, RoutedEventArgs e) => _viewModel.ShowRecovery();
    private void SkippedFiles_Click(object sender, RoutedEventArgs e) => new SkippedFilesWindow(_viewModel.SkippedEntries) { Owner = this }.ShowDialog();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDiagnostics();
    private void Benchmark_Click(object sender, RoutedEventArgs e) => _viewModel.ShowBenchmark();
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await _viewModel.ClearCacheAsync();
    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(true);
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(false);
    private async void UndoLastAction_Click(object sender, RoutedEventArgs e) => await _viewModel.UndoAsync();

}
