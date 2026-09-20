using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Platform.Windows;

namespace PhotoReview.App;

internal static class MainWindowHelpers
{
    internal readonly record struct ZoomViewportOffsets(double Horizontal, double Vertical);
    internal readonly record struct ZoomImagePoint(double X, double Y);

    internal static ZoomImagePoint CalculateUniformImagePoint(
        double elementWidth,
        double elementHeight,
        double sourceWidth,
        double sourceHeight,
        double pointerX,
        double pointerY)
    {
        if (elementWidth <= 0 || elementHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return new(0, 0);

        var scale = Math.Min(elementWidth / sourceWidth, elementHeight / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var left = (elementWidth - renderedWidth) / 2;
        var top = (elementHeight - renderedHeight) / 2;
        return new(
            Math.Clamp((pointerX - left) / scale, 0, sourceWidth),
            Math.Clamp((pointerY - top) / scale, 0, sourceHeight));
    }

    internal static ZoomViewportOffsets CalculateOffsetsFromAnchorDelta(
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double anchorBeforeX,
        double anchorBeforeY,
        double anchorAfterX,
        double anchorAfterY,
        double newExtentWidth,
        double newExtentHeight,
        double viewportWidth,
        double viewportHeight) => new(
            ClampOffset(currentHorizontalOffset + anchorAfterX - anchorBeforeX, newExtentWidth, viewportWidth),
            ClampOffset(currentVerticalOffset + anchorAfterY - anchorBeforeY, newExtentHeight, viewportHeight));

    internal static ZoomViewportOffsets CalculatePanOffsets(
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double deltaX,
        double deltaY,
        double extentWidth,
        double extentHeight,
        double viewportWidth,
        double viewportHeight) => new(
            ClampOffset(currentHorizontalOffset - deltaX, extentWidth, viewportWidth),
            ClampOffset(currentVerticalOffset - deltaY, extentHeight, viewportHeight));

    internal static ZoomViewportOffsets CalculateZoomViewportOffsets(
        double oldZoom,
        double newZoom,
        double mouseX,
        double mouseY,
        double oldHorizontalOffset,
        double oldVerticalOffset,
        double newExtentWidth,
        double newExtentHeight,
        double viewportWidth,
        double viewportHeight)
    {
        if (!double.IsFinite(oldZoom) || oldZoom <= 0 || !double.IsFinite(newZoom) || newZoom <= 0)
            return new(ClampOffset(oldHorizontalOffset, newExtentWidth, viewportWidth), ClampOffset(oldVerticalOffset, newExtentHeight, viewportHeight));

        var ratio = newZoom / oldZoom;
        var horizontal = (oldHorizontalOffset + Math.Max(0, mouseX)) * ratio - Math.Max(0, mouseX);
        var vertical = (oldVerticalOffset + Math.Max(0, mouseY)) * ratio - Math.Max(0, mouseY);
        return new(ClampOffset(horizontal, newExtentWidth, viewportWidth), ClampOffset(vertical, newExtentHeight, viewportHeight));
    }

    private static double ClampOffset(double value, double extent, double viewport)
    {
        var maximum = Math.Max(0, extent - viewport);
        return Math.Clamp(double.IsFinite(value) ? value : 0, 0, maximum);
    }

    public static MainWindowTestHooks ApplyTestEnvironment(MainWindowTestHooks hooks)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PhotoReview.Core.AppPaths.DataRootEnvironmentVariable)))
        {
            var root = Path.Combine(Path.GetTempPath(), "PhotoReview-Test-MainWindow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable(PhotoReview.Core.AppPaths.DataRootEnvironmentVariable, root);
        }
        return hooks;
    }

    public static MainViewModel CreateTestViewModel(
        MainWindowTestHooks? hooks,
        SettingsStore? settingsStore,
        Func<(double Width, double Height)> getViewportSize,
        out SettingsStore resolvedSettingsStore,
        out AppSettings resolvedSettings,
        out ShortcutRouter resolvedShortcutRouter,
        out IExplorerOrderProvider resolvedExplorerOrder)
    {
        hooks = ApplyTestEnvironment(hooks ?? new MainWindowTestHooks());
        var fs = new HookedFileSystem(hooks.MoveOverride);
        var appPaths = PhotoReview.Core.AppPaths.FromEnvironment();
        resolvedSettingsStore = settingsStore ?? new SettingsStore(appPaths, fs, new NullLog());
        resolvedSettings = resolvedSettingsStore.Load();
        resolvedShortcutRouter = new ShortcutRouter(resolvedSettings);

        var catalog = new ReviewCatalog();
        var clock = new GenerationClock();
        var sessionStore = new SessionStore(appPaths, fs);
        var journal = new OperationJournal(appPaths, fs, new SystemClock());
        var recycleBin = hooks.RecycleBin ?? WindowsRecycleBin.Instance;
        var fileActions = new FileActionService(journal, fs, new SystemClock(), recycleBin, hooks.MoveOverride);
        var undo = new UndoService(journal, fs, recycleBin, fileActions, hooks.MoveOverride);
        var metrics = new ReviewMetrics();
        var hashService = new FileHashService();
        var compare = new CompareViewModel();
        var viewer = new ViewerState();
        resolvedExplorerOrder = hooks.Explorer ?? new ExplorerOrderService();

        MainViewModel? vm = null;
        var currentSettings = resolvedSettings;
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var sink = new WpfPresentationSink(
            onSetCurrentImage: _ => vm?.NotifyPresentationChanged(),
            onSetStatusText: _ => vm?.NotifyPresentationChanged(),
            onApplyInitialViewMode: () => { var (w, h) = getViewportSize(); vm?.Viewer.ApplyInitialViewMode(currentSettings.InitialViewMode, w, h); },
            onPresented: hooks.OnPresented,
            dispatcher: dispatcher);

        var previewService = new PreviewImageService(metrics, () => currentSettings.LoadingMode == LoadingMode.Original, () => 1920, 64 * 1024 * 1024, disableDiskCacheOverride: true);
        var thumbs = new ThumbnailCache(persistNewThumbnails: false);
        var dummyPreload = new DummyPreloadController();

        var presenter = new ImagePresenter(catalog, clock, previewService, thumbs, dummyPreload, compare, hashService, metrics, () => currentSettings, sessionStore, sink, fs, getSession: () => vm?.Session, onPresentedHook: null);
        var coordinator = new FolderLoadCoordinator(catalog, clock, resolvedExplorerOrder, fs, sessionStore, resolvedSettingsStore, new ForwardingFolderSink(() => vm!));

        vm = new MainViewModel(
            catalog, clock, coordinator, presenter, viewer, compare, resolvedSettingsStore, sessionStore,
            fs, fileActions, undo, new WpfDialogService(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()),
            hashService, previewService, thumbs, new SessionWriter(sessionStore, FileLog.Default),
            preloadController: dummyPreload, naturalComparer: WindowsNaturalComparer.Instance, metrics: metrics);

        return vm;
    }
}

internal sealed class ForwardingFolderSink(Func<IFolderLoadSink> target) : IFolderLoadSink
{
    public void ResetCaches() => target().ResetCaches();
    public void OnCatalogReady(string folder, int count) => target().OnCatalogReady(folder, count);
    public Task PresentAsync(int index, long presentationGeneration) => target().PresentAsync(index, presentationGeneration);
    public void OnEmpty(string folder) => target().OnEmpty(folder);
    public void OnOrderApplied(int count, int currentIndex) => target().OnOrderApplied(count, currentIndex);
    public void OnFailed(string folder, Exception exception) => target().OnFailed(folder, exception);
}

internal sealed class DummyPreloadController : IPreloadController
{
    public Task PreloadAroundAsync(int center) => Task.CompletedTask;
    public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
    public void Cancel() { }
    public void RemovePreloadedKeysForPath(string normalizedPath) { }
    public void ClearPreloadedKeys() { }
}

internal sealed class HookedFileSystem : IFileSystem
{
    private readonly PhysicalFileSystem _inner = new();
    private readonly Func<string, string, Task>? _moveOverride;

    public HookedFileSystem(Func<string, string, Task>? moveOverride) => _moveOverride = moveOverride;

    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public FileStat? GetFileStat(string path) => _inner.GetFileStat(path);
    public void Move(string source, string destination) => _inner.Move(source, destination);
    public void Copy(string source, string destination) => _inner.Copy(source, destination);
    public void Delete(string path) => _inner.Delete(path);
    public Stream OpenReadShared(string path, int bufferSize = 65536) => _inner.OpenReadShared(path, bufferSize);
    public Stream OpenAppendDurable(string path) => _inner.OpenAppendDurable(path);
    public void WriteAllTextAtomic(string path, string text) => _inner.WriteAllTextAtomic(path, text);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public IEnumerable<string> ReadLines(string path) => _inner.ReadLines(path);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => _inner.EnumerateFiles(directory, pattern);
    public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
}
