using PhotoReview.Core.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
        _settingsStore.Changed += (_, s) => { _settings = s; _viewModel.Settings = s; _shortcutRouter.Rebuild(s); };
        PhotoReviewPerf.StartupMark("mainWindowCtor");
        // I18N: a live language switch re-renders the texts the ViewModel builds in code (ADR 0006).
        Localizer.CurrentChanged += OnLanguageChanged;
        DataContext = _viewModel;
        InitializeComponent();
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
        };
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

}
