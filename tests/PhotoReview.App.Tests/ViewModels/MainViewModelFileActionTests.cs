using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed class MainViewModelFileActionTests : IDisposable
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
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin;
    private readonly FakeDialogService _dialogService;
    private AppSettings _settings;

    public MainViewModelFileActionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_MainVM_ActionTests_" + Guid.NewGuid().ToString("N"));
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
            Actions =
            [
                new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted" },
                new ReviewAction { Name = "CopyToBackup", Operation = FileOperationType.Copy, Destination = "Backup" },
                new ReviewAction { Name = "ConfirmRecycle", Operation = FileOperationType.Recycle, Confirm = true }
            ]
        };
        _settingsStore.Save(_settings);

        _previewContext = new PreviewStateContext
        {
            IsOriginalLoadingMode = () => _settings.LoadingMode == LoadingMode.Original,
            TargetDecodeWidth = () => 1920,
            CurrentBackend = () => DecoderBackend.Wpf
        };

        _previewService = new PreviewImageService(
            _metrics,
            () => _previewContext.IsOriginalLoadingMode(),
            () => _previewContext.TargetDecodeWidth(),
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

    private static string CreateImageFile(string folder, string name)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, ValidPngBytes);
        return filePath;
    }

    private (MainViewModel ViewModel, FileActionService FileActions, UndoService Undo) CreateViewModel(IFileSystem? fs = null)
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
            fileActionService: fileActions,
            undoService: undo,
            dialogService: _dialogService,
            preloadController: _preloadController,
            naturalComparer: ManagedNaturalComparer.Instance);

        return (vm, fileActions, undo);
    }

    [Fact]
    public async Task RunActionAsync_Move_PresentsNextBeforeMoveCompletes_PreservingInv3()
    {
        var folder = Path.Combine(_tempDir, "inv3_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        var img2 = CreateImageFile(folder, "2.jpg");

        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptingFs = new BlockingMoveFileSystem(_fileSystem, moveGate.Task);

        var (vm, _, _) = CreateViewModel(interceptingFs);
        await vm.OpenFolderAsync(folder);

        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(0, vm.CurrentIndex);

        // Action index 0: Move
        var actionTask = vm.RunActionAsync(0);

        // Chờ sink nhận thông báo trình diễn ảnh thứ 2 TRƯỚC KHI move xong
        await _sink.WaitForPresentationCountAsync(2, TimeSpan.FromSeconds(5));

        Assert.Equal(1, vm.TotalFiles);
        Assert.Equal(img2, vm.Catalog.Current?.Path);

        // Tháo chốt cho phép Move hoàn thành
        moveGate.SetResult();
        await actionTask;

        var destination = Path.Combine(folder, "Sorted", "1.jpg");
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(img1));
    }

    [Fact]
    public async Task RunActionAsync_ConcurrentAction_IsRejectedByGate_PreservingInv4()
    {
        var folder = Path.Combine(_tempDir, "inv4_album");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptingFs = new BlockingMoveFileSystem(_fileSystem, moveGate.Task);

        var (vm, fileActions, _) = CreateViewModel(interceptingFs);
        await vm.OpenFolderAsync(folder);

        var firstAction = vm.RunActionAsync(0);
        Assert.True(fileActions.IsBusy);

        // Thao tác thứ hai đồng thời bị từ chối
        var secondAction = vm.RunActionAsync(0);
        await secondAction;

        moveGate.SetResult();
        await firstAction;

        Assert.False(fileActions.IsBusy);
    }

    [Fact]
    public async Task RunActionAsync_MoveFailure_RestoresImageToCatalog_PreservingInv5()
    {
        var folder = Path.Combine(_tempDir, "inv5_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var failingFs = new FailingMoveFileSystem(_fileSystem);
        var (vm, _, _) = CreateViewModel(failingFs);
        await vm.OpenFolderAsync(folder);

        Assert.Equal(2, vm.TotalFiles);

        await vm.RunActionAsync(0);

        // Nguồn phải được restore lại vào catalog tại đúng vị trí
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(img1, vm.Catalog.Paths[0]);
        Assert.Contains("Không thực hiện được", vm.StatusText);
    }

    [Fact]
    public async Task RunActionAsync_WithCompareSelectedPath_ActsOnSelectedPath()
    {
        var folder = Path.Combine(_tempDir, "compare_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        var img2 = CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        // Giả lập người dùng đang chọn ảnh 2 ở chế độ Compare
        vm.Compare.Select(img2);

        await vm.RunActionAsync(0); // Move sang "Sorted"

        var destination = Path.Combine(folder, "Sorted", "2.jpg");
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(img2));

        // Ảnh 1 vẫn còn trong catalog
        Assert.True(File.Exists(img1));
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact]
    public async Task RunActionAsync_WhenConfirmDeclined_DoesNotExecute()
    {
        var folder = Path.Combine(_tempDir, "confirm_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        // Action 2 là ConfirmRecycle với Confirm = true
        _dialogService.ConfirmationResponse = false; // Người dùng chọn "No"

        await vm.RunActionAsync(2);

        Assert.True(File.Exists(img1));
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact]
    public async Task RunActionAsync_WhenFolderSwitchedDuringIo_IgnoresCompletion()
    {
        var folder1 = Path.Combine(_tempDir, "album_stale1");
        var folder2 = Path.Combine(_tempDir, "album_stale2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        CreateImageFile(folder1, "1.jpg");
        CreateImageFile(folder2, "x.jpg");

        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptingFs = new BlockingMoveFileSystem(_fileSystem, moveGate.Task);

        var (vm, _, undo) = CreateViewModel(interceptingFs);
        await vm.OpenFolderAsync(folder1);

        var actionTask = vm.RunActionAsync(0);

        // Người dùng đổi thư mục giữa chừng khi Move đang chạy
        await vm.OpenFolderAsync(folder2);

        moveGate.SetResult();
        await actionTask;

        // Undo không được ghi nhận cho folder cũ
        Assert.Equal(0, undo.MoveHistoryCount);
    }

    [Fact]
    public async Task RecycleAsync_ExecutesRecycleAndAdvances()
    {
        var folder = Path.Combine(_tempDir, "recycle_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        var img2 = CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RecycleAsync();

        Assert.Contains(img1, _recycleBin.RecycledPaths);
        Assert.Equal(1, vm.TotalFiles);
        Assert.Equal(img2, vm.Catalog.Current?.Path);
    }

    [Fact]
    public async Task UndoAsync_RestoresMovedFile_InsertsSortedAndPresents()
    {
        var folder = Path.Combine(_tempDir, "undo_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RunActionAsync(0); // Move img1 to Sorted/1.jpg
        Assert.Equal(1, vm.TotalFiles);

        await vm.UndoAsync(); // Ctrl+Z

        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(img1, vm.Catalog.Paths[0]);
        Assert.True(File.Exists(img1));
    }

    [Fact]
    public async Task UndoLastAsync_WhenLastWasRecycle_RestoresAndReloadsFolder()
    {
        var folder = Path.Combine(_tempDir, "undolast_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RecycleAsync();
        Assert.Equal(0, vm.TotalFiles);

        await vm.UndoLastAsync();

        Assert.True(File.Exists(img1));
        Assert.Equal(1, vm.TotalFiles);
    }

    // --- Test Doubles ---

    private sealed class ForwardingFolderSink(Func<IFolderLoadSink> targetProvider) : IFolderLoadSink
    {
        public void ResetCaches() => targetProvider().ResetCaches();
        public void OnCatalogReady(string folder, int count) => targetProvider().OnCatalogReady(folder, count);
        public Task PresentAsync(int index, long presentationGeneration) => targetProvider().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder) => targetProvider().OnEmpty(folder);
        public void OnOrderApplied(int count, int currentIndex) => targetProvider().OnOrderApplied(count, currentIndex);
        public void OnFailed(string folder, Exception exception) => targetProvider().OnFailed(folder, exception);
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> RecycledPaths { get; } = [];

        public void SendToRecycleBin(string path)
        {
            RecycledPaths.Add(path);
            if (File.Exists(path)) File.Delete(path);
        }

        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc)
        {
            File.WriteAllBytes(path, ValidPngBytes);
            return true;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmationResponse { get; set; } = true;
        public bool ShowConfirmation(string title, string message) => ConfirmationResponse;
        public void ShowMessage(string title, string message) { }
    }

    private class DelegatingFileSystem(IFileSystem inner) : IFileSystem
    {
        public virtual bool FileExists(string path) => inner.FileExists(path);
        public virtual bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public virtual FileStat? GetFileStat(string path) => inner.GetFileStat(path);
        public virtual void Move(string source, string destination) => inner.Move(source, destination);
        public virtual void Copy(string source, string destination) => inner.Copy(source, destination);
        public virtual void Delete(string path) => inner.Delete(path);
        public virtual Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public virtual Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public virtual void WriteAllTextAtomic(string path, string text) => inner.WriteAllTextAtomic(path, text);
        public virtual string ReadAllText(string path) => inner.ReadAllText(path);
        public virtual IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public virtual IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public virtual IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public virtual void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    private sealed class BlockingMoveFileSystem(IFileSystem inner, Task blockTask) : DelegatingFileSystem(inner)
    {
        public override void Move(string source, string destination)
        {
            blockTask.GetAwaiter().GetResult();
            base.Move(source, destination);
        }
    }

    private sealed class FailingMoveFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override void Move(string source, string destination) => throw new IOException("Simulated disk error.");
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public List<string> Statuses { get; } = [];
        public List<string> PresentedPaths { get; } = [];

        public void SetCurrentImage(object? image) => Images.Add(image);
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path)
        {
            lock (PresentedPaths) PresentedPaths.Add(path);
        }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }

        public async Task WaitForPresentationCountAsync(int count, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < timeout)
            {
                lock (PresentedPaths)
                {
                    if (PresentedPaths.Count >= count) return;
                }
                await Task.Delay(20);
            }
        }
    }

    private sealed class TestPreloadController : IPreloadController
    {
        public int CancelCount { get; private set; }
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() => CancelCount++;
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
    }
}
