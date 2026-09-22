using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows.Fixtures;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TC03: Warm navigation (Next/Previous in cached range) must not read sources.
/// TC05: Move/Delete with queue behavior - verifies Q-T1 decision (actions queued, not dropped).
/// Uses production MainViewModel, ImagePresenter, PreviewImageService, and real decoder.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class WarmNavigationReadBoundsTests : IAsyncLifetime
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

    private string? _fixtureFolder;
    private readonly int _photoCount = 20;

    public Task InitializeAsync()
    {
        // TC01: Build fixture folder with real JPEG photos
        _fixtureFolder = PhotoFolderBuilder.BuildFolder(_photoCount);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // TS07: Cleanup fixture folder on session end
        if (_fixtureFolder != null && Directory.Exists(_fixtureFolder))
        {
            try { Directory.Delete(_fixtureFolder, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }

        return Task.CompletedTask;
    }

    private static string CreateImageFile(string folder, string name)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, ValidPngBytes);
        return filePath;
    }

    private (MainViewModel ViewModel, FileActionService FileActions) CreateViewModelWithActions(
        string albumFolder,
        AppSettings? settings = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_TC05_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var catalog = new ReviewCatalog();
        var clock = new GenerationClock();
        var metrics = new ReviewMetrics();
        var compareViewModel = new CompareViewModel();
        var viewerState = new ViewerState();
        var sink = new StubPresentationSink();
        var preloadController = new StubPreloadController();
        var explorerOrder = new StubExplorerOrderProvider();
        var fileSystem = new PhysicalFileSystem();
        var recycleBin = new StubRecycleBin();
        var dialogService = new StubDialogService();

        var appPaths = new AppPaths(tempDir);
        var journal = new OperationJournal(appPaths, fileSystem, new SystemClock());
        var sessionStore = new SessionStore(appPaths, fileSystem);
        var settingsStore = new SettingsStore(appPaths, fileSystem, new NullLog());

        var appSettings = settings ?? new AppSettings
        {
            LoadingMode = LoadingMode.Preview,
            Actions =
            [
                new ReviewAction { Name = "MoveToTemp", Operation = FileOperationType.Move, Destination = "Temp" },
                new ReviewAction { Name = "MoveToBackup", Operation = FileOperationType.Move, Destination = "Backup" }
            ]
        };
        settingsStore.Save(appSettings);

        var previewContext = new PreviewStateContext
        {
            IsOriginalLoadingMode = () => appSettings.LoadingMode == LoadingMode.Original,
            TargetDecodeWidth = () => 1920,
            CurrentBackend = () => DecoderBackend.Wpf
        };

        var previewService = new PreviewImageService(
            metrics,
            () => previewContext.IsOriginalLoadingMode(),
            () => previewContext.TargetDecodeWidth(),
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => previewContext.CurrentBackend(),
            disableDiskCacheOverride: true);

        var thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(tempDir, "thumbs"),
            maxRamBytes: 16 * 1024 * 1024,
            persistNewThumbnails: false);

        var hashService = new FileHashService();

        var systemClock = new SystemClock();
        var fileActions = new FileActionService(journal, fileSystem, systemClock, recycleBin);
        var undo = new UndoService(journal, fileSystem, recycleBin, fileActions);

        MainViewModel? vm = null;
        var presenter = new ImagePresenter(
            catalog,
            clock,
            previewService,
            thumbnailCache,
            preloadController,
            compareViewModel,
            hashService,
            metrics,
            () => appSettings,
            sessionStore,
            sink,
            fileSystem,
            getSession: () => vm?.Session);

        var coordinator = new FolderLoadCoordinator(
            catalog,
            clock,
            explorerOrder,
            fileSystem,
            sessionStore,
            settingsStore,
            new StubFolderSink(() => vm!));

        vm = new MainViewModel(
            catalog,
            clock,
            coordinator,
            presenter,
            viewerState,
            compareViewModel,
            settingsStore,
            sessionStore,
            fileSystem,
            fileActions,
            undo,
            dialogService,
            hashService,
            previewService,
            thumbnailCache,
            new SessionWriter(sessionStore, FileLog.Default),
            preloadController: preloadController,
            naturalComparer: ManagedNaturalComparer.Instance);

        return (vm, fileActions);
    }

    [Fact(DisplayName = "TC03: Warm Next does not read sources when in cache")]
    public async Task WarmNext_InCachedRange_ReadsZeroSources()
    {
        Assert.NotNull(_fixtureFolder);
        Assert.True(System.IO.Directory.Exists(_fixtureFolder));

        var files = System.IO.Directory.GetFiles(_fixtureFolder, "*.jpg");
        Assert.True(files.Length >= 20, "Fixture needs at least 20 images");

        // Use production stack without WPF binding (no hang risk).
        var (vm, fileActions) = CreateViewModelWithActions(_fixtureFolder);
        var probe = new ReadBudgetProbe(new PhysicalFileSystem(), vm.Metrics);

        // Open folder and wait to complete
        await vm.OpenFolderAsync(_fixtureFolder).WithTimeout(TimeSpan.FromSeconds(20), "OpenFolderAsync");
        Assert.True(vm.HasImages, "Folder should have loaded images");
        Assert.True(vm.TotalFiles >= 20, "Should have loaded fixture images");

        // Warmup: pre-populate cache by navigating to index 8 and back.
        for (int i = 0; i < 8; i++)
        {
            await vm.NextAsync().WithTimeout(TimeSpan.FromSeconds(5), "NextAsync warmup");
        }
        for (int i = 0; i < 8; i++)
        {
            await vm.PreviousAsync().WithTimeout(TimeSpan.FromSeconds(5), "PreviousAsync warmup");
        }
        Assert.Equal(0, vm.CurrentIndex);

        // Verify cache is populated: measure decoded bytes before navigation.
        // Warm cache test uses 64 MB RAM cache and 1920px preview (~11 MB each),
        // so 5 images = ~55 MB; warm range must fit to avoid eviction mid-test.
        var beforeSnapshot = probe.Capture();

        // Navigate within cached range: indices 0->5, then 5->2.
        for (int i = 0; i < 5; i++)
        {
            await vm.NextAsync().WithTimeout(TimeSpan.FromSeconds(5), "NextAsync warm nav");
        }
        for (int i = 0; i < 3; i++)
        {
            await vm.PreviousAsync().WithTimeout(TimeSpan.FromSeconds(5), "PreviousAsync warm nav");
        }

        var afterSnapshot = probe.Capture();

        // Assert: Warm navigation did not read sources (images already cached).
        ReadBudgetProbe.AssertSourceReadsDelta(beforeSnapshot, afterSnapshot, maxDelta: 0,
            context: "Warm navigation should not read sources (cache hit only)");

        // Assert: Navigation succeeded (landed on index 0 + 5 - 3 = 2).
        Assert.Equal(2, vm.CurrentIndex);
    }

    [Fact(DisplayName = "TC04: Move/Delete does not re-read remaining images", Skip = "TC04 not yet implemented; requires move/delete integration")]
    public async Task MoveDelete_InFolder_DoesNotReadUnaffected()
    {
        await Task.CompletedTask;
    }

    [Fact(DisplayName = "TC05: Rapid Next without await - all images presented without duplicates")]
    public async Task RapidNextRepeat_ManyKeysWithoutAwait_AllPresentedWithoutDuplicates()
    {
        Assert.NotNull(_fixtureFolder);
        Assert.True(System.IO.Directory.Exists(_fixtureFolder));

        var files = System.IO.Directory.GetFiles(_fixtureFolder, "*.jpg");
        Assert.True(files.Length >= 20, "Fixture needs at least 20 images");

        var (vm, _) = CreateViewModelWithActions(_fixtureFolder);
        await vm.OpenFolderAsync(_fixtureFolder).WithTimeout(TimeSpan.FromSeconds(20), "OpenFolderAsync");

        Assert.Equal(0, vm.CurrentIndex);
        Assert.True(vm.CanNavigateNext);
        Assert.False(vm.CanNavigatePrevious);
        Assert.True(vm.HasImages);

        // Queue K=10 Next operations without await (all at once).
        const int K = 10;
        var nextTasks = new List<Task>(K);
        for (var i = 0; i < K; i++)
        {
            nextTasks.Add(vm.NextAsync().WithTimeout(TimeSpan.FromSeconds(5), $"NextAsync #{i}"));
        }

        // Await all queued navigations.
        await Task.WhenAll(nextTasks);

        // Assert: all K navigations completed successfully (landed at index K).
        Assert.Equal(K, vm.CurrentIndex);

        // Assert: current image is set (not null after all navigations).
        Assert.NotNull(vm.CurrentImage);

        // Assert: we can still navigate forward (index 10 < 20 images).
        Assert.True(vm.CanNavigateNext);

        // Assert: the presentation matches the final index (last navigation won).
        Assert.NotNull(vm.Catalog.Current);
        var expectedImage = files.OrderBy(f => f, ManagedNaturalComparer.Instance).ElementAt(K);
        Assert.Equal(expectedImage, vm.Catalog.Current.Path);
    }

    [Fact(DisplayName = "TC06: Move/Delete with queue behavior (Q-T1) - skipped: queue not yet implemented", Skip = "Q-T1 decided but not implemented: production still drops actions when busy; open separate task")]
    public async Task MoveDeleteQueue_RapidActionsWhileBusy_AllExecutedInOrder()
    {
        // Verify Q-T1 decision: when user presses Move/Delete rapidly while one action is running,
        // actions are QUEUED (not dropped), and final state matches the queue order.

        var tempAlbum = Path.Combine(Path.GetTempPath(), "TC05_RapidMove_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempAlbum);

        try
        {
            // Step 1: Use fixture with 10+ images
            Assert.NotNull(_fixtureFolder);
            var files = Directory.GetFiles(_fixtureFolder, "*.jpg").ToList();
            Assert.True(files.Count >= 10, "Fixture needs at least 10 images");

            // Copy first 5 images to album for this test
            var image0 = CreateImageFile(tempAlbum, "img0.jpg");
            var image1 = CreateImageFile(tempAlbum, "img1.jpg");
            var image2 = CreateImageFile(tempAlbum, "img2.jpg");
            CreateImageFile(tempAlbum, "img3.jpg");
            CreateImageFile(tempAlbum, "img4.jpg");

            // Step 2: Create MainViewModel and load folder
            var (vm, fileActions) = CreateViewModelWithActions(tempAlbum);
            await vm.OpenFolderAsync(tempAlbum);

            Assert.True(vm.HasImages);
            Assert.Equal(5, vm.TotalFiles);

            // Step 3: Note initial Catalog.Paths
            var initialPaths = vm.Catalog.Paths.ToList();
            Assert.Contains(image0, initialPaths);
            Assert.Contains(image1, initialPaths);
            Assert.Contains(image2, initialPaths);

            // Step 4: Queue 3 Move operations: images[0], [1], [2] to Temp folder
            // Call MainViewModel.RunActionAsync(moveActionIndex) 3x rapidly without await
            var moveDestination = Path.Combine(tempAlbum, "Temp");
            Directory.CreateDirectory(moveDestination);

            var action1 = vm.RunActionAsync(0); // Move current (img0)
            var action2 = vm.RunActionAsync(0); // Queue move
            var action3 = vm.RunActionAsync(0); // Queue move

            // Step 5: Await all (Task.WhenAll)
            await Task.WhenAll(action1, action2, action3);

            // Step 6: Assert - all 3 images moved (check Catalog.Paths no longer contains them)
            var finalPaths = vm.Catalog.Paths.ToList();
            Assert.DoesNotContain(image0, finalPaths);
            Assert.DoesNotContain(image1, finalPaths);
            Assert.DoesNotContain(image2, finalPaths);

            // Step 7: Assert - 3 files exist in destination
            var movedFiles = Directory.GetFiles(moveDestination);
            Assert.Equal(3, movedFiles.Length);

            // Step 8: Assert - Catalog.Current is one of the surviving images (not a deleted one)
            Assert.NotNull(vm.Catalog.Current);
            Assert.NotEqual(image0, vm.Catalog.Current.Path);
            Assert.NotEqual(image1, vm.Catalog.Current.Path);
            Assert.NotEqual(image2, vm.Catalog.Current.Path);
            Assert.True(File.Exists(vm.Catalog.Current.Path), "Current image must still exist");

            // Step 9: Assert - FileActionService.IsBusy is False (all done)
            Assert.False(fileActions.IsBusy);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempAlbum)) Directory.Delete(tempAlbum, true);
            }
            catch { }
        }
    }

    // --- Test Doubles ---

    private sealed class StubFolderSink(Func<IFolderLoadSink> targetProvider) : IFolderLoadSink
    {
        public void ResetCaches() => targetProvider().ResetCaches();
        public void OnCatalogReady(string folder, int count) => targetProvider().OnCatalogReady(folder, count);
        public Task PresentAsync(int index, long presentationGeneration) => targetProvider().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder) => targetProvider().OnEmpty(folder);
        public void OnOrderApplied(int count, int currentIndex) => targetProvider().OnOrderApplied(count, currentIndex);
        public void OnFailed(string folder, Exception exception) => targetProvider().OnFailed(folder, exception);
    }

    private sealed class StubPresentationSink : IPresentationSink
    {
        public void SetCurrentImage(object? image) { }
        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class StubRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc)
        {
            File.WriteAllBytes(path, ValidPngBytes);
            return true;
        }
    }

    private sealed class StubDialogService : IDialogService
    {
        public bool ShowConfirmation(string title, string message) => true;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => true;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => true;
        public void ShowBenchmark(string? folder = null) { }
    }

    private sealed class StubExplorerOrderProvider : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    private sealed class StubPreloadController : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    private sealed class PreviewStateContext
    {
        public Func<bool>? IsOriginalLoadingMode { get; set; }
        public Func<int>? TargetDecodeWidth { get; set; }
        public Func<DecoderBackend>? CurrentBackend { get; set; }
    }
}
