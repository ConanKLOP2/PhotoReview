using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed partial class MainViewModelAdvancedTests : IDisposable
{
    private static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static readonly byte[] DifferentPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x72, 0xB6, 0x0D,
        0x24, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x60, 0x60, 0x60, 0x00,
        0x00, 0x00, 0x04, 0x00, 0x01, 0x03, 0x1B, 0x02,
        0x85, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    private readonly string _tempDir;
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly ReviewMetrics _metrics;
    private readonly CompareViewModel _compareViewModel;
    private readonly ViewerState _viewerState;
    private readonly TestPresentationSink _sink;
    private readonly TestPreloadController _preloadController;
    private readonly PreviewImageService _previewService;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly FileHashService _hashService;
    private readonly PreviewStateContext _previewContext;
    private readonly SessionStore _sessionStore;
    private readonly SettingsStore _settingsStore;
    private readonly FakeExplorerOrderProvider _explorerOrder;
    private readonly PhysicalFileSystem _fileSystem;
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin;
    private readonly FakeDialogService _dialogService;
    private readonly RecordingUiScheduler _uiScheduler = new();
    private AppSettings _settings;

    public MainViewModelAdvancedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_MainVM_AdvTests_" + Guid.NewGuid().ToString("N"));
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
        _recycleBin = new FakeRecycleBin();
        _dialogService = new FakeDialogService();

        var appPaths = new AppPaths(_tempDir);
        _journal = new OperationJournal(appPaths, _fileSystem, new SystemClock());
        _sessionStore = new SessionStore(appPaths, _fileSystem);
        _settingsStore = new SettingsStore(appPaths, _fileSystem, new NullLog());

        _settings = new AppSettings
        {
            LoadingMode = LoadingMode.Preview,
            Actions = ReviewAction.Defaults()
        };
        _settingsStore.Save(_settings);

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

    private static string CreateImageFile(string folder, string name, byte[]? bytes = null)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, bytes ?? ValidPngBytes);
        return filePath;
    }

    internal (MainViewModel ViewModel, FileActionService FileActions) CreateViewModel(IFileSystem? fs = null)
    {
        var activeFs = fs ?? _fileSystem;
        var clock = new SystemClock();
        var fileActions = new FileActionService(_journal, activeFs, clock, _recycleBin);
        var undo = new UndoService(_journal, activeFs, _recycleBin, fileActions);

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
            activeFs,
            getSession: () => vm?.Session);

        var coordinator = new FolderLoadCoordinator(
            _catalog,
            _clock,
            _explorerOrder,
            activeFs,
            _sessionStore,
            _settingsStore,
            new ForwardingFolderSink(() => vm!));

        vm = new MainViewModel(
            _catalog,
            _clock,
            coordinator,
            presenter,
            _viewerState,
            _compareViewModel,
            _settingsStore,
            _sessionStore,
            activeFs,
            fileActions,
            undo,
            _dialogService,
            _hashService,
            _previewService,
            _thumbnailCache,
            new SessionWriter(_sessionStore, FileLog.Default),
            preloadController: _preloadController,
            naturalComparer: ManagedNaturalComparer.Instance,
            uiScheduler: _uiScheduler);

        return (vm, fileActions);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_NumberedDuplicates_RecyclesNumberedAndReloads()
    {
        var folder = Path.Combine(_tempDir, "dup_numbered");
        Directory.CreateDirectory(folder);
        var original = CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        Assert.Equal(2, vm.TotalFiles);
        _dialogService.BatchReviewResponse = true;

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Contains(numbered, _recycleBin.RecycledPaths);
        Assert.DoesNotContain(original, _recycleBin.RecycledPaths);
        Assert.False(File.Exists(numbered));
        Assert.True(File.Exists(original));
        Assert.Equal(1, vm.TotalFiles);
        Assert.Contains("1/1", vm.StatusText);
        Assert.Equal(1, _uiScheduler.InvokeCount);
    }

    [Fact(DisplayName = "Duplicate cleanup holds the file-action gate: a second cleanup or a recycle started during the review is a no-op (R2-F-20)")]
    public async Task RemoveDuplicatesAsync_HoldsFileActionGate_WhileReviewIsOpen()
    {
        var folder = Path.Combine(_tempDir, "dup_gate");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        _dialogService.BatchReviewResponse = true;
        var gateHeldDuringReview = false;
        Task? secondCleanup = null;
        Task? recycleDuringReview = null;
        _dialogService.OnBatchReview = () =>
        {
            gateHeldDuringReview = vm.IsFileActionInProgress;
            secondCleanup = vm.RemoveDuplicatesAsync(removeNumbered: true);
            recycleDuringReview = vm.RecycleAsync();
        };

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.True(gateHeldDuringReview);
        Assert.True(secondCleanup!.IsCompletedSuccessfully); // rejected synchronously by the gate
        Assert.True(recycleDuringReview!.IsCompletedSuccessfully);
        Assert.Equal(1, _dialogService.BatchReviewCount);
        Assert.Equal([numbered], _recycleBin.RecycledPaths); // only the reviewed duplicate, the current image was not recycled
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "R7-4: a folder switch while the duplicate review is open cancels the cleanup")]
    public async Task RemoveDuplicatesAsync_FolderSwitchDuringReview_RecyclesNothing()
    {
        var folder = Path.Combine(_tempDir, "dup_switch");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _dialogService.BatchReviewResponse = true;
        _dialogService.OnBatchReview = () => _clock.NextFolder(); // a forwarded open ran in the nested loop

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.True(File.Exists(numbered));
    }

    [Fact(DisplayName = "R7-4: a duplicate that left the catalog during the review is not recycled")]
    public async Task RemoveDuplicatesAsync_EntryRemovedDuringReview_SkipsIt()
    {
        var folder = Path.Combine(_tempDir, "dup_removed");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _dialogService.BatchReviewResponse = true;
        _dialogService.OnBatchReview = () => _catalog.Remove(numbered);

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.True(File.Exists(numbered));
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_OriginalDuplicates_RecyclesOriginalAndReloads()
    {
        var folder = Path.Combine(_tempDir, "dup_orig");
        Directory.CreateDirectory(folder);
        var original = CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        Assert.Equal(2, vm.TotalFiles);
        _dialogService.BatchReviewResponse = true;

        await vm.RemoveDuplicatesAsync(removeNumbered: false);

        Assert.Contains(original, _recycleBin.RecycledPaths);
        Assert.DoesNotContain(numbered, _recycleBin.RecycledPaths);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(numbered));
        Assert.Equal(1, vm.TotalFiles);
        Assert.Contains("1/1", vm.StatusText);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_WhenNoDuplicates_SetsStatusAndDoesNotOpenDialog()
    {
        var folder = Path.Combine(_tempDir, "dup_none");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "img1.png", ValidPngBytes);
        CreateImageFile(folder, "img2.png", DifferentPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        _dialogService.BatchReviewCalled = false;
        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.False(_dialogService.BatchReviewCalled);
        Assert.Equal("Không có bản trùng lặp nào cùng hash phù hợp.", vm.StatusText);
        Assert.Equal(2, vm.TotalFiles);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_WhenBatchReviewDeclined_CancelsBatch()
    {
        var folder = Path.Combine(_tempDir, "dup_declined");
        Directory.CreateDirectory(folder);
        var orig = CreateImageFile(folder, "pic.png", ValidPngBytes);
        var dup = CreateImageFile(folder, "pic (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        _dialogService.BatchReviewResponse = false;
        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal("Đã hủy xử lý hàng loạt.", vm.StatusText);
        Assert.True(File.Exists(orig));
        Assert.True(File.Exists(dup));
        Assert.Empty(_recycleBin.RecycledPaths);
    }

    [Fact]
    public async Task ClearCacheAsync_WhenDeclined_DoesNotClear()
    {
        var (vm, _) = CreateViewModel();
        _dialogService.ConfirmationResponse = false;

        await vm.ClearCacheAsync();

        Assert.False(_preloadController.CancelCalled);
        Assert.False(_preloadController.ClearKeysCalled);
    }

    [Fact]
    public async Task ClearCacheAsync_WhenConfirmed_ClearsAllCaches()
    {
        var (vm, _) = CreateViewModel();
        _dialogService.ConfirmationResponse = true;

        await vm.ClearCacheAsync();

        Assert.True(_preloadController.CancelCalled);
        Assert.True(_preloadController.ClearKeysCalled);
        Assert.Equal("Đã xóa cache ảnh xem trước.", vm.StatusText);
    }

    [Fact]
    public async Task ShowSettings_WhenLoadingModeChanged_CancelsPreloadAndPresentsAgain()
    {
        var folder = Path.Combine(_tempDir, "settings_folder");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        _preloadController.CancelCalled = false;
        var initialPresCount = _sink.PresentationCount;

        _dialogService.SettingsResponse = true;
        _dialogService.OnShowSettings = () =>
        {
            _settings.LoadingMode = LoadingMode.Original;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.True(_preloadController.CancelCalled);
        await _sink.WaitForPresentationCountAsync(initialPresCount + 1, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task PickAndOpenFolderAsync_WhenFolderSelected_OpensFolder()
    {
        var folder = Path.Combine(_tempDir, "picked_folder");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        _dialogService.PickedFolderResponse = folder;

        await vm.PickAndOpenFolderAsync();

        Assert.Equal(1, vm.TotalFiles);
        Assert.Equal(0, vm.CurrentIndex);
    }

    [Fact]
    public void Sink_OnCatalogReadyAndOnEmpty_AdoptTheSessionPassedInWithoutReloading()
    {
        var folder = Path.Combine(_tempDir, "session_adopt");
        var (vm, _) = CreateViewModel();
        IFolderLoadSink sink = vm;

        // The store holds a different session for this folder: a sink that re-read it would return that one.
        Directory.CreateDirectory(folder);
        _sessionStore.Save(new SessionState { Folder = folder, CurrentPath = "from-disk" });

        var passed = new SessionState { Folder = folder, CurrentPath = "from-coordinator" };
        sink.OnCatalogReady(folder, 3, passed);
        Assert.Same(passed, vm.Session);

        var passedEmpty = new SessionState { Folder = folder, CurrentPath = "from-coordinator-empty" };
        sink.OnEmpty(folder, passedEmpty);
        Assert.Same(passedEmpty, vm.Session);
    }

    [Fact]
    public void FolderText_ExplorerOrder_IsTrackedByStateFlagNotByText()
    {
        var folder = Path.Combine(_tempDir, "explorer_flag");
        var (vm, _) = CreateViewModel();
        IFolderLoadSink sink = vm;

        sink.OnCatalogReady(folder, 3, new PhotoReview.Core.Session.SessionState { Folder = folder });
        Assert.False(vm.IsExplorerOrderApplied);
        Assert.Equal($"{folder}  (3 ảnh)", vm.FolderText);

        sink.OnOrderApplied(3, 0, currentKept: false);
        Assert.True(vm.IsExplorerOrderApplied);
        Assert.Equal($"{folder}  (3 ảnh) · Explorer", vm.FolderText);

        // Applying again does not stack the suffix.
        sink.OnOrderApplied(3, 0, currentKept: false);
        Assert.Equal($"{folder}  (3 ảnh) · Explorer", vm.FolderText);

        // A new catalog starts in natural order again.
        sink.OnEmpty(folder, new SessionState { Folder = folder });
        Assert.False(vm.IsExplorerOrderApplied);
        Assert.Equal($"{folder}  (0 ảnh)", vm.FolderText);
    }

    // --- Test Doubles ---

    private sealed class ForwardingFolderSink(Func<IFolderLoadSink> targetProvider) : IFolderLoadSink
    {
        public void ResetCaches() => targetProvider().ResetCaches();
        public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session) => targetProvider().OnCatalogReady(folder, count, session);
        public Task PresentAsync(int index, long presentationGeneration) => targetProvider().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session) => targetProvider().OnEmpty(folder, session);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => targetProvider().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => targetProvider().OnFailed(folder, exception);
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> RecycledPaths { get; } = [];

        /// <summary>Paths whose recycling fails with an I/O error (a locked file), to exercise partial batch failure.</summary>
        public HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SendToRecycleBin(string path)
        {
            if (FailingPaths.Contains(path)) throw new IOException("locked by another process");
            RecycledPaths.Add(path);
            if (File.Exists(path)) File.Delete(path);
        }

        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc)
        {
            File.WriteAllBytes(path, ValidPngBytes);
            return true;
        }
    }

    private sealed class RecordingUiScheduler : IUiScheduler
    {
        public int InvokeCount { get; private set; }
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            InvokeCount++;
            action();
            return Task.CompletedTask;
        }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmationResponse { get; set; } = true;
        public bool BatchReviewResponse { get; set; } = true;
        public bool BatchReviewCalled { get; set; }
        public int BatchReviewCount { get; set; }
        public Action? OnBatchReview { get; set; }
        public bool SettingsResponse { get; set; } = true;
        public Action? OnShowSettings { get; set; }
        public string? PickedFolderResponse { get; set; }

        public bool ShowConfirmation(string title, string message) => ConfirmationResponse;
        public void ShowMessage(string title, string message) { }
        public List<(string Title, string Message)> Errors { get; } = [];
        public void ShowError(string title, string message) => Errors.Add((title, message));
        public string? PickFolder(string? initialFolder = null) => PickedFolderResponse;
        public bool ShowBatchReview(IReadOnlyList<string> paths)
        {
            BatchReviewCalled = true;
            BatchReviewCount++;
            OnBatchReview?.Invoke();
            return BatchReviewResponse;
        }
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings()
        {
            OnShowSettings?.Invoke();
            return SettingsResponse;
        }
        public void ShowBenchmark(string? folder = null) { }
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder,
            TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null,
            int progressInterval = 16,
            CancellationToken cancellationToken = default) =>
            TryGetSnapshotAsync(folder, timeout, cancellationToken);

        public void Dispose() { }
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public List<string> Statuses { get; } = [];
        public List<string> PresentedPaths { get; } = [];
        private TaskCompletionSource<bool>? _countBarrier;
        private int _targetCount;

        public int PresentationCount => PresentedPaths.Count;

        public void SetCurrentImage(object? image) => Images.Add(image);
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path)
        {
            lock (PresentedPaths)
            {
                PresentedPaths.Add(path);
                if (_countBarrier is not null && PresentedPaths.Count >= _targetCount)
                {
                    _countBarrier.TrySetResult(true);
                }
            }
        }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }

        public async Task WaitForPresentationCountAsync(int count, TimeSpan timeout)
        {
            lock (PresentedPaths)
            {
                if (PresentedPaths.Count >= count) return;
                _targetCount = count;
                _countBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _countBarrier.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (PresentedPaths)
                {
                    if (PresentedPaths.Count >= count) return;
                }
                throw;
            }
            finally
            {
                lock (PresentedPaths)
                {
                    _countBarrier = null;
                }
            }
        }
    }

    private sealed class TestPreloadController : IPreloadController
    {
        public bool CancelCalled { get; set; }
        public bool ClearKeysCalled { get; set; }

        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(PhotoReview.Imaging.ImageCacheKey key) => false;
        public void Cancel() => CancelCalled = true;
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() => ClearKeysCalled = true;
    }
}
