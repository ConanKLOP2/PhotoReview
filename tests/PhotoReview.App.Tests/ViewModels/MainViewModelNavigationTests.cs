using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed class MainViewModelNavigationTests : IDisposable
{
    private static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
        0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    private readonly string _tempDir;
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly ReviewMetrics _metrics;
    private readonly CompareViewModel _compareViewModel;
    private readonly ViewerState _viewerState;
    private readonly TestPresentationSink _sink;
    private readonly TestPreloadController _preloadController;
    private readonly PreviewStateContext _previewContext;
    private readonly PreviewImageService _previewService;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly FileHashService _hashService;
    private readonly SessionStore _sessionStore;
    private readonly SettingsStore _settingsStore;
    private readonly FakeExplorerOrderProvider _explorerOrder;
    private readonly PhysicalFileSystem _fileSystem;
    private AppSettings _settings;

    public MainViewModelNavigationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_MainVMTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _catalog = new ReviewCatalog();
        _clock = new GenerationClock();
        _metrics = new ReviewMetrics();
        _compareViewModel = new CompareViewModel();
        _viewerState = new ViewerState();
        _sink = new TestPresentationSink();
        _preloadController = new TestPreloadController();
        _explorerOrder = new FakeExplorerOrderProvider();
        _fileSystem = new PhysicalFileSystem();
        _settings = new AppSettings { LoadingMode = LoadingMode.Preview };

        _previewContext = new PreviewStateContext
        {
            IsOriginalLoadingMode = () => _settings.LoadingMode == LoadingMode.Original,
            TargetDecodeBox = () => new PhotoReview.Imaging.DecodeBox(1920, 0),
            CurrentBackend = () => DecoderBackend.Wpf
        };

        _previewService = new PreviewImageService(
            _metrics,
            () => _previewContext.IsOriginalLoadingMode(),
            () => _previewContext.TargetDecodeBox(),
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => _previewContext.CurrentBackend(),
            disableDiskCacheOverride: true);

        _thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "thumbs"),
            maxRamBytes: 16 * 1024 * 1024,
            persistNewThumbnails: false);

        _hashService = new FileHashService();
        var appPaths = new AppPaths(_tempDir);
        _sessionStore = new SessionStore(appPaths, _fileSystem);
        _settingsStore = new SettingsStore(appPaths, _fileSystem, new NullLog());
    }

    public void Dispose()
    {
        _thumbnailCache.Dispose();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private static string CreateImageFile(string folder, string name)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, ValidPngBytes);
        return filePath;
    }

    private (MainViewModel ViewModel, ImagePresenter Presenter, FolderLoadCoordinator Coordinator) CreateViewModel()
    {
        MainViewModel? vm = null;

        var presenter = new ImagePresenter(
            _catalog,
            _clock,
            _previewService,
            _thumbnailCache,
            _preloadController,
            _compareViewModel,
            _hashService,
            _metrics,
            () => _settings,
            _sessionStore,
            _sink,
            _fileSystem,
            getSession: () => vm?.Session);

        var coordinator = new FolderLoadCoordinator(
            _catalog,
            _clock,
            _explorerOrder,
            _fileSystem,
            _sessionStore,
            _settingsStore,
            new ForwardingFolderLoadSink(() => vm!));

        var appPaths = new PhotoReview.Core.AppPaths(_tempDir);
        var journal = new OperationJournal(appPaths, _fileSystem, new SystemClock());
        var fileActions = new FileActionService(journal, _fileSystem, new SystemClock(), new TestRecycleBin());
        var undo = new UndoService(journal, _fileSystem, new TestRecycleBin(), fileActions);
        var dialog = new TestDialogService();

        vm = new MainViewModel(
            _catalog,
            _clock,
            coordinator,
            presenter,
            _viewerState,
            _compareViewModel,
            _settingsStore,
            _sessionStore,
            _fileSystem,
            fileActions,
            undo,
            dialog,
            _hashService,
            _previewService,
            _thumbnailCache,
            new SessionWriter(_sessionStore, new NullLog()),
            preloadController: _preloadController);

        return (vm, presenter, coordinator);
    }

    private sealed class TestRecycleBin : IRecycleBin
    {
        // CA1822: Recycle and IsAccessible implement IRecycleBin (an instance interface
        // contract), so they cannot be marked static regardless of body content.
#pragma warning disable CA1822
        public void Recycle(string path) { }
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string path, long length, DateTime lastWriteUtc) => false;
        public bool IsAccessible => true;
#pragma warning restore CA1822
    }

    private sealed class TestDialogService : IDialogService
    {
        public bool ShowConfirmation(string title, string message) => false;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
    }

    [Fact]
    public async Task OpenFolderAsync_LoadsFolderAndUpdatesCatalogAndTitle()
    {
        var folder = Path.Combine(_tempDir, "album1");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "img1.jpg");
        CreateImageFile(folder, "img2.jpg");

        var (vm, _, _) = CreateViewModel();

        await vm.OpenFolderAsync(folder);

        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.True(vm.HasImages);
        Assert.True(vm.CanNavigateNext);
        Assert.False(vm.CanNavigatePrevious);
        Assert.Contains(folder, vm.FolderTitle);
    }

    [Fact]
    public async Task OpenPathAsync_InvalidInput_SetsWarningStatus()
    {
        var (vm, _, _) = CreateViewModel();

        await vm.OpenPathAsync(Path.Combine(_tempDir, "non_existent_file.xyz"));

        Assert.Equal("Không tìm thấy ảnh được hỗ trợ.", vm.StatusText);
    }

    [Fact]
    public async Task NextAsync_And_PreviousAsync_AdvancesAndUpdatesInteraction()
    {
        var folder = Path.Combine(_tempDir, "album_nav");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        CreateImageFile(folder, "b.jpg");
        CreateImageFile(folder, "c.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        var initialInteraction = _clock.CurrentInteraction;

        // Next
        await vm.NextAsync();
        Assert.Equal(1, vm.CurrentIndex);
        Assert.True(_clock.CurrentInteraction > initialInteraction);

        // Next again
        await vm.NextAsync();
        Assert.Equal(2, vm.CurrentIndex);
        Assert.False(vm.CanNavigateNext);
        Assert.True(vm.CanNavigatePrevious);

        // Previous
        await vm.PreviousAsync();
        Assert.Equal(1, vm.CurrentIndex);
        Assert.True(vm.CanNavigateNext);
        Assert.True(vm.CanNavigatePrevious);

        // First
        await vm.FirstAsync();
        Assert.Equal(0, vm.CurrentIndex);
        Assert.False(vm.CanNavigatePrevious);
    }

    [Fact]
    public async Task SkipAsync_AddsToSessionSkipped_AndAdvances()
    {
        var folder = Path.Combine(_tempDir, "album_skip");
        Directory.CreateDirectory(folder);
        var f1 = CreateImageFile(folder, "skip1.jpg");
        var f2 = CreateImageFile(folder, "skip2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.SkipAsync();

        Assert.NotNull(vm.Session);
        Assert.Contains(f1, vm.Session.Skipped);
        Assert.Equal(1, vm.CurrentIndex);

        var saved = _sessionStore.Load(folder);
        Assert.NotNull(saved);
        Assert.Contains(f1, saved.Skipped);
    }

    [Fact(DisplayName = "Skipping the same image twice records it once in the session (R2-F-10)")]
    public async Task SkipAsync_SameImageTwice_IsRecordedOnce()
    {
        var folder = Path.Combine(_tempDir, "album_skip_twice");
        Directory.CreateDirectory(folder);
        var f1 = CreateImageFile(folder, "skip1.jpg");
        CreateImageFile(folder, "skip2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.SkipAsync();
        await vm.FirstAsync();
        await vm.SkipAsync();

        Assert.NotNull(vm.Session);
        Assert.Single(vm.Session.Skipped, f1);
        Assert.Single(_sessionStore.Load(folder).Skipped);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_Boundary_DisplaysStatus()
    {
        var parentDir = Path.Combine(_tempDir, "siblings");
        var singleFolder = Path.Combine(parentDir, "only_one");
        Directory.CreateDirectory(singleFolder);
        CreateImageFile(singleFolder, "pic.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(singleFolder);

        await vm.NextFolderAsync();
        Assert.Equal("Đã ở thư mục cuối cùng cùng cấp.", vm.StatusText);

        await vm.PreviousFolderAsync();
        Assert.Equal("Đã ở thư mục đầu tiên cùng cấp.", vm.StatusText);
    }

    [Fact]
    public void ViewerControls_DelegateCorrectlyToViewerState()
    {
        var (vm, _, _) = CreateViewModel();

        vm.ZoomIn();
        Assert.Equal(1.25, vm.Viewer.Zoom);

        vm.ZoomOut();
        Assert.Equal(1.0, vm.Viewer.Zoom);

        vm.ToggleFit();
        Assert.True(vm.Viewer.IsFit);

        vm.ToggleFullscreen();
        Assert.True(vm.Viewer.IsFullscreen);

        vm.ExitFullscreen();
        Assert.False(vm.Viewer.IsFullscreen);
    }

    [Fact]
    public async Task LastAsync_GoesToLastImage_AndBackWithFirst()
    {
        var folder = Path.Combine(_tempDir, "album_last");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        CreateImageFile(folder, "b.jpg");
        CreateImageFile(folder, "c.jpg");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        var interaction = _clock.CurrentInteraction;

        await vm.LastImageAsync();

        Assert.Equal(2, vm.CurrentIndex);
        Assert.False(vm.CanNavigateNext);
        Assert.True(_clock.CurrentInteraction > interaction);
        await vm.FirstImageAsync();
        Assert.Equal(0, vm.CurrentIndex);
    }

    [Fact]
    public async Task LastAsync_WithoutImages_IsNoOp()
    {
        var (vm, _, _) = CreateViewModel();
        var interaction = _clock.CurrentInteraction;

        await vm.LastAsync();

        Assert.Equal(-1, vm.CurrentIndex);
        Assert.Equal(interaction, _clock.CurrentInteraction);
    }

    [Fact]
    public async Task ZoomActualSize_WithImage_Sets100PercentOfSourcePixels_WithoutImage_IsNoOp()
    {
        var (vm, _, _) = CreateViewModel();

        vm.ZoomActualSize();
        Assert.True(vm.Viewer.IsFit);

        var folder = Path.Combine(_tempDir, "album_zoom100");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        await vm.OpenFolderAsync(folder);
        vm.ZoomIn();

        vm.ZoomActualSize();

        Assert.False(vm.Viewer.IsFit);
        Assert.Equal(ViewerState.ActualSizeZoom, vm.Viewer.Zoom);
        Assert.Equal(1.0, vm.Viewer.EffectiveZoom);
    }

    [Fact]
    public void ToggleInfoOverlay_FlipsVisibility_AndPersistsToConfig()
    {
        var (vm, _, _) = CreateViewModel();
        Assert.True(vm.InfoOverlay.IsFileInfoVisible);
        Assert.True(vm.IsStatusPanelVisible);

        vm.ToggleInfoOverlay();

        Assert.False(vm.InfoOverlay.IsFileInfoVisible);
        Assert.False(vm.IsStatusPanelVisible);
        var reloaded = new SettingsStore(new AppPaths(_tempDir), _fileSystem, new NullLog()).Load();
        Assert.False(reloaded.ShowInfoOverlay);

        vm.ToggleInfoOverlay();

        Assert.True(vm.InfoOverlay.IsFileInfoVisible);
        Assert.True(new SettingsStore(new AppPaths(_tempDir), _fileSystem, new NullLog()).Load().ShowInfoOverlay);
    }

    [Fact]
    public void StatusPanel_StaysVisibleForSkippedFilesWarning_WhenInfoHidden()
    {
        var (vm, _, _) = CreateViewModel();
        vm.Settings = new AppSettings { ShowFileInfo = false };
        Assert.False(vm.IsStatusPanelVisible);

        ((IFolderLoadSink)vm).OnFilesSkipped(_tempDir, [new SkippedEntry(Path.Combine(_tempDir, "x.jpg"), "locked")]);

        Assert.False(vm.InfoOverlay.IsFileInfoVisible);
        Assert.True(vm.IsStatusPanelVisible);
    }

    [Fact]
    public async Task OpenFolderAsync_ShowsSiblingImageFolders_ThatPageUpPageDownOpen()
    {
        var parent = Path.Combine(_tempDir, "sibling_info");
        foreach (var name in new[] { "2024-05-01", "2024-05-02", "2024-05-02b-empty", "2024-05-03" })
            Directory.CreateDirectory(Path.Combine(parent, name));
        CreateImageFile(Path.Combine(parent, "2024-05-01"), "a.jpg");
        CreateImageFile(Path.Combine(parent, "2024-05-02"), "b.jpg");
        CreateImageFile(Path.Combine(parent, "2024-05-03"), "c.jpg");
        var (vm, _, _) = CreateViewModel();

        await vm.OpenFolderAsync(Path.Combine(parent, "2024-05-02"));
        await vm.InfoOverlay.PendingSiblings.WithTimeout(TimeSpan.FromSeconds(10), "sibling info");

        Assert.True(vm.InfoOverlay.IsFolderInfoVisible);
        Assert.Equal(
            string.Join("     ", Tr.MainFolderInfoPrevious("PageUp", "2024-05-01"), Tr.MainFolderInfoCurrent("2024-05-02"), Tr.MainFolderInfoNext("PageDown", "2024-05-03")),
            vm.InfoOverlay.FolderInfoText);

        // The display agrees with the key: PageDown opens the folder shown on the right (the empty one is skipped).
        await vm.NextFolderAsync();
        await vm.InfoOverlay.PendingSiblings.WithTimeout(TimeSpan.FromSeconds(10), "sibling info after switch");
        Assert.Contains("2024-05-03", vm.FolderTitle, StringComparison.Ordinal);
        Assert.Equal(
            string.Join("     ", Tr.MainFolderInfoPrevious("PageUp", "2024-05-02"), Tr.MainFolderInfoCurrent("2024-05-03")),
            vm.InfoOverlay.FolderInfoText);
    }
    private sealed class ForwardingFolderLoadSink : IFolderLoadSink
    {
        private readonly Func<IFolderLoadSink> _getSink;
        public ForwardingFolderLoadSink(Func<IFolderLoadSink> getSink) => _getSink = getSink;

        public void ResetCaches() => _getSink().ResetCaches();
        public void OnCatalogReady(string folder, int count) => _getSink().OnCatalogReady(folder, count);
        public Task PresentAsync(int index, long presentationGeneration) => _getSink().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder) => _getSink().OnEmpty(folder);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => _getSink().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => _getSink().OnFailed(folder, exception);
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public List<string> Statuses { get; } = [];
        public void SetCurrentImage(object? image) => Images.Add(image);
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class TestPreloadController : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }
}
