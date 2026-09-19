using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
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
    private readonly IProgressiveExplorerOrderProvider? _explorerOrder;
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private double? _cachedDpiScale;
    private bool _placementRestored;
    private long _viewportOperationVersion;

#pragma warning disable CS0169, CS0414, IDE0044, IDE0051, IDE0052
    // Reflection compatibility fields for legacy test harnesses
    private readonly List<string> _files = [];
    private int _fileActionInProgress;
    private Stack<(string Source, string Destination)> _moveHistory = [];
    private object? _lastUndoAction;
    private int _index;
    private string? _compareSelectedPath;
    private ReviewMetrics? _metrics;
    private object? _preloadScheduler;
#pragma warning restore CS0169, CS0414, IDE0044, IDE0051, IDE0052

    public MainViewModel ViewModel => _viewModel;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore, IProgressiveExplorerOrderProvider? explorerOrder = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _explorerOrder = explorerOrder;
        _settings = _settingsStore.Load();
        _shortcutRouter = new ShortcutRouter(_settings);
        _settingsStore.Changed += (_, s) => { _settings = s; _shortcutRouter.Rebuild(s); };
        DataContext = _viewModel;
        InitializeComponent();
        WireViewModelEvents();
    }

    public MainWindow(string? initialPath = null) : this((MainWindowTestHooks?)null, initialPath, null) { }
    public MainWindow(string? initialPath, SettingsStore? settingsStore) : this((MainWindowTestHooks?)null, initialPath, settingsStore) { }
    internal MainWindow(string? initialPath, MainWindowTestHooks? hooks) : this(hooks, initialPath, null) { }
    internal MainWindow(MainWindowTestHooks? hooks, string? initialPath = null, SettingsStore? settingsStore = null)
    {
        _viewModel = MainWindowHelpers.CreateTestViewModel(hooks, settingsStore, GetViewportSize, out _settingsStore, out _settings, out _shortcutRouter, out _explorerOrder);
        DataContext = _viewModel;
        InitializeComponent();
        WireViewModelEvents();
        InitializeWithInitialPath(initialPath);
    }

    public void InitializeWithInitialPath(string? initialPath)
    {
        if (!string.IsNullOrWhiteSpace(initialPath)) _ = _viewModel.OpenPathAsync(initialPath);
    }

    private void WireViewModelEvents()
    {
        _metrics = _viewModel.Metrics;
        _preloadScheduler = _viewModel.PreloadController;
        _index = _viewModel.CurrentIndex;
        _compareSelectedPath = _viewModel.Compare.SelectedPath;
        _moveHistory = _viewModel.UndoService.MoveHistory;
        _lastUndoAction = _viewModel.UndoService.LastUndoAction;
        _viewModel.Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewerState.IsFullscreen)) ApplyFullscreenState(_viewModel.Viewer.IsFullscreen); };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentIndex)) _index = _viewModel.CurrentIndex;
            if (e.PropertyName == nameof(MainViewModel.CurrentImage) && _viewModel.Viewer.IsFit)
                Dispatcher.BeginInvoke(UpdateFitSize, System.Windows.Threading.DispatcherPriority.Render);
        };
        _viewModel.Compare.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CompareViewModel.SelectedPath)) _compareSelectedPath = _viewModel.Compare.SelectedPath; };
        _viewModel.CatalogChanged += SyncFiles;
        SyncFiles();
    }

#pragma warning disable IDE0051
    private Task LoadFolderAsync(string folder, string? initialPath = null) => _viewModel.OpenFolderAsync(folder, initialPath);
    private async Task UndoLastActionAsync()
    {
        if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
        try { await _viewModel.UndoLastAsync(); _lastUndoAction = _viewModel.UndoService.LastUndoAction; }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }
    private void ResetFitView() => _ = ApplyFitViewAsync();
    private void SetZoom(double level) => _viewModel.Viewer.SetZoom(level);
    private Task ShowImageAsync(int index) => _viewModel.Presenter.PresentAsync(index);
    private bool TryGetCachedPreview(string path, out object? preview)
    {
        if (_viewModel.PreviewService is not null && _viewModel.PreviewService.TryGetCachedPreview(path, out var decoded))
        {
            preview = decoded.PlatformImage;
            return true;
        }
        preview = null;
        return false;
    }
#pragma warning restore IDE0051

    private void SyncFiles()
    {
        _files.Clear();
        _files.AddRange(_viewModel.Catalog.Paths);
    }

    private void ApplyFullscreenState(bool isFullscreen)
    {
        ResizeMode = isFullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowStyle = isFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = isFullscreen ? WindowState.Maximized : WindowState.Normal;
    }

    private (double Width, double Height) GetViewportSize() =>
        (Math.Max(0, ImageScroll.ActualWidth - ImageScroll.BorderThickness.Left - ImageScroll.BorderThickness.Right),
         Math.Max(0, ImageScroll.ActualHeight - ImageScroll.BorderThickness.Top - ImageScroll.BorderThickness.Bottom));

    private void UpdateFitSize()
    {
        var (w, h) = GetViewportSize();
        _viewModel.Viewer.UpdateViewport(w, h);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_placementRestored) { _placementRestored = true; WindowPlacementService.Restore(this); }
        UpdateFitSize();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();
    private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();
    private void MainImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel.Viewer.IsFit) UpdateFitSize();
    }
    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e) => _cachedDpiScale = e.NewDpi.DpiScaleX;
    private void Window_Closing(object? sender, CancelEventArgs e) => WindowPlacementService.Save(this);
    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.FlushSession();
        (_viewModel.PreloadController as IDisposable)?.Dispose();
        _explorerOrder?.Dispose();
    }

    private async void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var mouse = e.GetPosition(ImageScroll);
        var pointInImage = ImageScroll.TranslatePoint(mouse, MainImage);
        if (_viewModel.Viewer.IsFit && MainImage.Source is { Width: > 0, Height: > 0 } source)
        {
            var sourcePoint = MainWindowHelpers.CalculateUniformImagePoint(
                MainImage.ActualWidth,
                MainImage.ActualHeight,
                source.Width,
                source.Height,
                pointInImage.X,
                pointInImage.Y);
            pointInImage = new Point(sourcePoint.X, sourcePoint.Y);
        }
        var anchorBefore = MainImage.TranslatePoint(pointInImage, ImageScroll);
        var version = ++_viewportOperationVersion;
        _viewModel.Viewer.WheelZoom(e.Delta);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        if (version != _viewportOperationVersion || !IsLoaded) return;
        ImageScroll.UpdateLayout();

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
        _viewModel.Settings = _settings;
        _shortcutRouter.Rebuild(_settings);
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (PhotoReviewPerf.Log.IsEnabled())
            PhotoReviewPerf.Log.KeyInput(0, pressedKey.ToString(), unchecked(Environment.TickCount - e.Timestamp));

        var cmd = _shortcutRouter.TryResolve(e.Key, e.SystemKey, Keyboard.Modifiers, _viewModel.Viewer.IsFullscreen, _viewModel.HasImages, hasComparePair: _viewModel.Compare.IsVisible, isCompareVisible: _viewModel.Compare.IsVisible);
        if (cmd is null) return;
        e.Handled = true;

        switch (cmd.Value.Type)
        {
            case ReviewCommandType.Fullscreen: _viewModel.ToggleFullscreen(); break;
            case ReviewCommandType.ExitFullscreen: _viewModel.ExitFullscreen(); break;
            case ReviewCommandType.Close: Close(); break;
            case ReviewCommandType.NextFolder: await _viewModel.NavigateSiblingFolderAsync(1); break;
            case ReviewCommandType.PreviousFolder: await _viewModel.NavigateSiblingFolderAsync(-1); break;
            case ReviewCommandType.FirstImage: await _viewModel.FirstImageAsync(); break;
            case ReviewCommandType.Undo: await _viewModel.UndoAsync(); break;
            case ReviewCommandType.ToggleCompare: _viewModel.ToggleCompare(); break;
            case ReviewCommandType.RunAction:
                if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
                try { await _viewModel.RunActionAsync(cmd.Value.ActionIndex); _lastUndoAction = _viewModel.UndoService.LastUndoAction; }
                finally { Volatile.Write(ref _fileActionInProgress, 0); }
                break;
            case ReviewCommandType.Recycle:
                if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
                try { await _viewModel.RecycleAsync(); _lastUndoAction = _viewModel.UndoService.LastUndoAction; }
                finally { Volatile.Write(ref _fileActionInProgress, 0); }
                break;
            case ReviewCommandType.Skip: await _viewModel.SkipAsync(); break;
            case ReviewCommandType.ToggleFit: _ = ApplyFitViewAsync(); break;
            case ReviewCommandType.ZoomIn: _viewModel.ZoomIn(); break;
            case ReviewCommandType.ZoomOut: _viewModel.ZoomOut(); break;
            case ReviewCommandType.Next: await _viewModel.NextAsync(); break;
            case ReviewCommandType.Previous: await _viewModel.PreviousAsync(); break;
        }
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
        ++_viewportOperationVersion;
        var version = _viewportOperationVersion;
        var (width, height) = GetViewportSize();
        _viewModel.Viewer.ResetFit(width, height);

        for (var pass = 0; pass < 3; pass++)
        {
            if (version != _viewportOperationVersion || !IsLoaded) return;
            ImageScroll.UpdateLayout();
            UpdateFitSize();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        if (version != _viewportOperationVersion || !IsLoaded) return;
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHome();
        ImageScroll.ScrollToHorizontalOffset(0);
        ImageScroll.ScrollToVerticalOffset(0);
    }
    private void Recovery_Click(object sender, RoutedEventArgs e) => _viewModel.ShowRecovery();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDiagnostics();
    private void Benchmark_Click(object sender, RoutedEventArgs e) => _viewModel.ShowBenchmark();
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await _viewModel.ClearCacheAsync();
    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(true);
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveDuplicatesAsync(false);
    private async void UndoLastAction_Click(object sender, RoutedEventArgs e) => await _viewModel.UndoLastAsync();

}
