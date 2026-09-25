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

[Trait("Category", "HotPath")]
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

    private static string CreateImageFile(string folder, string name)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, ValidPngBytes);
        return filePath;
    }

    private (MainViewModel ViewModel, FileActionService FileActions, UndoService Undo) CreateViewModel(IFileSystem? fs = null, SessionWriter? sessionWriter = null)
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
            sessionWriter ?? new SessionWriter(_sessionStore, FileLog.Default),
            preloadController: _preloadController,
            naturalComparer: ManagedNaturalComparer.Instance);

        return (vm, fileActions, undo);
    }

    [Fact(DisplayName = "R7-3: CloseSession writes pending state through the bounded shutdown path (writer disposed)")]
    public void CloseSession_WritesPendingState_AndDisposesWriter()
    {
        var folder = Path.Combine(_tempDir, "close_session");
        Directory.CreateDirectory(folder);
        var writer = new SessionWriter(_sessionStore, FileLog.Default, delay: (_, _) => new TaskCompletionSource().Task);
        var (vm, _, _) = CreateViewModel(sessionWriter: writer);

        writer.Update(new SessionState { Folder = folder, CurrentPath = Path.Combine(folder, "a.jpg") });
        vm.CloseSession();
        Assert.Equal(Path.Combine(folder, "a.jpg"), _sessionStore.Load(folder).CurrentPath);

        // Disposed (Q-R5 bounded path), not just flushed: a late update after close is not written.
        writer.Update(new SessionState { Folder = folder, CurrentPath = Path.Combine(folder, "b.jpg") });
        writer.Flush();
        Assert.Equal(Path.Combine(folder, "a.jpg"), _sessionStore.Load(folder).CurrentPath);
    }

    [Fact]
    public async Task CloseSession_DisposesFolderCoordinator_SoNoFurtherLoadStarts()
    {
        var folder = Path.Combine(_tempDir, "close_coordinator");
        Directory.CreateDirectory(folder);
        var (vm, _, _) = CreateViewModel();

        vm.CloseSession();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.OpenFolderAsync(folder));
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

    [Fact(DisplayName = "R7-4: a folder switch while the action's Confirm dialog is open cancels the action")]
    public async Task RunActionAsync_FolderSwitchDuringConfirm_DoesNotExecute()
    {
        var folder = Path.Combine(_tempDir, "confirm_switch");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _dialogService.OnConfirmation = () => _clock.NextFolder(); // a forwarded open ran in the nested loop

        await vm.RunActionAsync(2); // ConfirmRecycle

        Assert.Single(_dialogService.Confirmations);
        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.True(File.Exists(img1));
    }

    [Fact(DisplayName = "R7-4: an image that left the catalog while the Confirm dialog was open is not acted on")]
    public async Task RunActionAsync_EntryRemovedDuringConfirm_DoesNotExecute()
    {
        var folder = Path.Combine(_tempDir, "confirm_removed");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _dialogService.OnConfirmation = () => _catalog.Remove(img1);

        await vm.RunActionAsync(2); // ConfirmRecycle on 1.jpg

        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.True(File.Exists(img1));
    }

    [Fact]
    public async Task RunActionAsync_Move_KeepsNextImageCachedAndEvictsOnlyRemovedOne()
    {
        var folder = Path.Combine(_tempDir, "evict_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        var img2 = CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        // The next image is already warm (as after a preload) before the action key is pressed.
        await _previewService.GetPreviewAsync(img2);
        Assert.True(_previewService.TryGetCachedPreview(img2, out _));
        Assert.True(_previewService.TryGetCachedPreview(img1, out _));
        var sourceReadsBefore = _metrics.Snapshot().SourceReads;

        await vm.RunActionAsync(0); // Move img1 -> Sorted; img2 becomes current
        await _sink.WaitForPresentationCountAsync(2, TimeSpan.FromSeconds(5));

        Assert.False(_previewService.TryGetCachedPreview(img1, out _));
        Assert.True(_previewService.TryGetCachedPreview(img2, out _));
        // Presenting the next image must be served from RAM: no new source read (R2-F-02).
        Assert.Equal(sourceReadsBefore, _metrics.Snapshot().SourceReads);
    }

    [Fact]
    public async Task OpenFolder_CancelsRunningPreloadBeforePreloadingNewCatalog()
    {
        var folderA = Path.Combine(_tempDir, "preload_a");
        var folderB = Path.Combine(_tempDir, "preload_b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        CreateImageFile(folderA, "a1.jpg");
        CreateImageFile(folderA, "a2.jpg");
        CreateImageFile(folderB, "b1.jpg");
        CreateImageFile(folderB, "b2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folderA);
        _preloadController.Events.Clear();

        await vm.OpenFolderAsync(folderB);

        // A loop started for folder A holds A's entries; it must be stopped before B is preloaded (R2-F-01).
        var cancelAt = _preloadController.Events.IndexOf("cancel");
        var preloadAt = _preloadController.Events.FindIndex(e => e.StartsWith("preload:", StringComparison.Ordinal));
        Assert.True(cancelAt >= 0, "Opening another folder did not cancel the running preload.");
        Assert.True(preloadAt < 0 || cancelAt < preloadAt, "Preload for the new folder started before the old loop was cancelled.");
    }

    [Fact]
    public async Task ExplorerOrderApplied_CancelsPreloadBeforeRecentering()
    {
        var folder = Path.Combine(_tempDir, "order_album");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _preloadController.Events.Clear();

        ((IFolderLoadSink)vm).OnOrderApplied(2, 0, currentKept: true);

        Assert.Equal(["cancel", "preload:0"], _preloadController.Events);
    }

    [Fact]
    public async Task RunActionAsync_AfterSettingsSaved_UsesSavedProfiles()
    {
        var folder = Path.Combine(_tempDir, "saved_settings_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        // Simulates Settings > Save: the window saves a NEW AppSettings instance (R2-F-03).
        _settingsStore.Save(new AppSettings
        {
            LoadingMode = LoadingMode.Preview,
            Actions = [new ReviewAction { Name = "MoveElsewhere", Operation = FileOperationType.Move, Destination = "NewDest", Confirm = true }]
        });
        _dialogService.ConfirmationResponse = true;

        await vm.RunActionAsync(0);

        Assert.True(File.Exists(Path.Combine(folder, "NewDest", "1.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "Sorted", "1.jpg")));
        Assert.False(File.Exists(img1));
    }

    [Fact]
    public async Task RunActionAsync_AfterSettingsSavedWithConfirm_AsksForConfirmation()
    {
        var folder = Path.Combine(_tempDir, "saved_confirm_album");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        _settingsStore.Save(new AppSettings
        {
            LoadingMode = LoadingMode.Preview,
            Actions = [new ReviewAction { Name = "MoveElsewhere", Operation = FileOperationType.Move, Destination = "NewDest", Confirm = true }]
        });
        _dialogService.ConfirmationResponse = false;

        await vm.RunActionAsync(0);

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

    [Fact(DisplayName = "R7-2: undoing a Move made in a previous folder opens that folder at the file, not the current catalog")]
    public async Task UndoAsync_MoveFromPreviousFolder_OpensThatFolderInsteadOfInsertingHere()
    {
        var folderA = Path.Combine(_tempDir, "undo_prev_a");
        var folderB = Path.Combine(_tempDir, "undo_prev_b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        var imgA = CreateImageFile(folderA, "1.jpg");
        CreateImageFile(folderA, "2.jpg");
        var imgB = CreateImageFile(folderB, "x.jpg");

        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folderA);
        await vm.RunActionAsync(0); // Move A\1.jpg to A\Sorted
        await vm.OpenFolderAsync(folderB);
        Assert.Equal([imgB], vm.Catalog.Paths);

        await vm.UndoAsync();

        Assert.True(File.Exists(imgA));
        Assert.Equal(folderA, vm.Session?.Folder);
        Assert.Equal(2, vm.TotalFiles);
        Assert.DoesNotContain(imgB, vm.Catalog.Paths);
        Assert.Equal(imgA, vm.Catalog.Current?.Path);
    }

    [Fact]
    public async Task OC14_UndoDuringFileAction_IsNoOp_AndGateReleasedAfter()
    {
        var folder = Path.Combine(_tempDir, "oc14_undo_during_action");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");
        CreateImageFile(folder, "3.jpg");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new SwitchableBlockingMoveFileSystem(_fileSystem);
        var (vm, _, undo) = CreateViewModel(fs);
        await vm.OpenFolderAsync(folder);

        await vm.RunActionAsync(0); // move 1.jpg, registers undo
        Assert.Equal(1, undo.MoveHistoryCount);

        fs.Block = gate.Task;
        var action = vm.RunActionAsync(0); // move 2.jpg, blocked
        Assert.True(vm.IsFileActionInProgress);

        await vm.UndoAsync(); // must be a no-op while the action holds the gate
        Assert.Equal(1, undo.MoveHistoryCount);

        gate.SetResult();
        await action;
        Assert.False(vm.IsFileActionInProgress);
        Assert.Equal(2, undo.MoveHistoryCount);
    }

    [Fact]
    public async Task OC14_FileActionDuringUndo_IsNoOp_AndConcurrentUndoDoesNotDoubleRestore()
    {
        var folder = Path.Combine(_tempDir, "oc14_action_during_undo");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");
        CreateImageFile(folder, "3.jpg");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new SwitchableBlockingMoveFileSystem(_fileSystem);
        var (vm, _, undo) = CreateViewModel(fs);
        await vm.OpenFolderAsync(folder);

        await vm.RunActionAsync(0);
        await vm.RunActionAsync(0);
        Assert.Equal(2, undo.MoveHistoryCount);
        var total = vm.TotalFiles;

        fs.Block = gate.Task;
        var firstUndo = vm.UndoAsync();
        Assert.True(vm.IsFileActionInProgress);

        await vm.RunActionAsync(0); // no-op: gate held by undo
        await vm.UndoAsync();       // second undo: no-op
        Assert.Equal(total, vm.TotalFiles);

        gate.SetResult();
        await firstUndo;

        Assert.False(vm.IsFileActionInProgress);
        Assert.Equal(total + 1, vm.TotalFiles);
        Assert.Equal(1, undo.MoveHistoryCount);
        Assert.Single(vm.Catalog.Paths, p => p == img1 || Path.GetFileName(p) == "2.jpg");
    }

    [Fact]
    public async Task OC14_FolderSwitchMidUndo_ReleasesGate_AndDoesNotTouchNewCatalog()
    {
        var folder1 = Path.Combine(_tempDir, "oc14_switch1");
        var folder2 = Path.Combine(_tempDir, "oc14_switch2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        CreateImageFile(folder1, "1.jpg");
        CreateImageFile(folder1, "2.jpg");
        CreateImageFile(folder2, "x.jpg");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new SwitchableBlockingMoveFileSystem(_fileSystem);
        var (vm, _, _) = CreateViewModel(fs);
        await vm.OpenFolderAsync(folder1);
        await vm.RunActionAsync(0);

        fs.Block = gate.Task;
        var undoTask = vm.UndoAsync();
        await vm.OpenFolderAsync(folder2);
        gate.SetResult();
        await undoTask;

        Assert.False(vm.IsFileActionInProgress);
        Assert.Single(vm.Catalog.Paths);
        Assert.Equal("x.jpg", Path.GetFileName(vm.Catalog.Paths[0]));
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

        await vm.UndoAsync();

        Assert.True(File.Exists(img1));
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact]
    public async Task UndoAsync_WithNoHistory_ReportsNothingToUndo_LeavesCatalog_AndReleasesGate()
    {
        var folder = Path.Combine(_tempDir, "undo_no_history");
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");

        var (vm, _, undo) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        var currentBefore = vm.Catalog.Current?.Path;

        await vm.UndoAsync();

        Assert.Equal(PhotoReview.Core.Localization.Tr.CoreUndoNothingToUndo, vm.StatusText);
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(currentBefore, vm.Catalog.Current?.Path);
        Assert.False(vm.IsFileActionInProgress);

        // The gate was released: the next file action runs and registers an undo entry.
        await vm.RunActionAsync(0);
        Assert.False(File.Exists(img1));
        Assert.Equal(1, undo.MoveHistoryCount);
    }

    // --- Q-R8: permanent delete on drives without a Recycle Bin ---

    private async Task<(MainViewModel Vm, UndoService Undo, string Img1)> OpenPermanentDeleteAlbumAsync(string name, bool noRecycleBin, bool allowSetting)
    {
        var folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        var img1 = CreateImageFile(folder, "1.jpg");
        CreateImageFile(folder, "2.jpg");
        _recycleBin.HasNoRecycleBin = noRecycleBin;
        _settings.AllowPermanentDeleteWithoutRecycleBin = allowSetting;
        var (vm, _, undo) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        return (vm, undo, img1);
    }

    [Fact]
    public async Task Recycle_NoRecycleBinDriveAndSettingOff_RefusesAndKeepsFile()
    {
        var (vm, _, img1) = await OpenPermanentDeleteAlbumAsync("qr8_off", noRecycleBin: true, allowSetting: false);

        await vm.RecycleAsync();

        Assert.True(File.Exists(img1));
        Assert.Empty(_recycleBin.PermanentlyDeleted);
        Assert.Empty(_dialogService.Confirmations);
        Assert.Equal(2, vm.TotalFiles); // INV-5: the source went back into the catalog
    }

    [Fact]
    public async Task Recycle_NoRecycleBinDriveSettingOnButUserDeclines_KeepsFile()
    {
        var (vm, _, img1) = await OpenPermanentDeleteAlbumAsync("qr8_decline", noRecycleBin: true, allowSetting: true);
        _dialogService.ConfirmationResponse = false;

        await vm.RecycleAsync();

        var confirmation = Assert.Single(_dialogService.Confirmations);
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogConfirmPermanentDeleteTitle, confirmation.Title);
        Assert.True(File.Exists(img1));
        Assert.Empty(_recycleBin.PermanentlyDeleted);
        Assert.Equal(2, vm.TotalFiles);
    }

    [Fact]
    public async Task Recycle_NoRecycleBinDriveSettingOnAndConfirmed_DeletesPermanentlyAndUndoSaysCannotRestore()
    {
        var (vm, undo, img1) = await OpenPermanentDeleteAlbumAsync("qr8_on", noRecycleBin: true, allowSetting: true);

        await vm.RecycleAsync();

        var confirmation = Assert.Single(_dialogService.Confirmations);
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogConfirmPermanentDeleteMessage("1.jpg"), confirmation.Message);
        Assert.False(File.Exists(img1));
        Assert.Equal(img1, Assert.Single(_recycleBin.PermanentlyDeleted));
        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.Equal(1, vm.TotalFiles);

        var undoResult = await undo.UndoLastAsync();
        Assert.False(undoResult.Succeeded);
        Assert.Equal(PhotoReview.Core.Localization.Tr.CoreUndoPermanentlyDeleted("1.jpg"), undoResult.ErrorMessage);
        Assert.Equal(0, _recycleBin.RestoreCalls);
        Assert.False(File.Exists(img1));
    }

    [Fact]
    public async Task RunAction_RecycleProfileWithConfirmOnNoRecycleBinDrive_AsksOnlyOnceAndExplicitly()
    {
        var (vm, _, img1) = await OpenPermanentDeleteAlbumAsync("qr8_profile", noRecycleBin: true, allowSetting: true);

        await vm.RunActionAsync(2); // "ConfirmRecycle": Recycle with Confirm = true

        var confirmation = Assert.Single(_dialogService.Confirmations);
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogConfirmPermanentDeleteTitle, confirmation.Title);
        Assert.Equal(img1, Assert.Single(_recycleBin.PermanentlyDeleted));
    }

    [Fact]
    public async Task Recycle_FixedDriveWithSettingOn_BehavesAsBefore()
    {
        var (vm, undo, img1) = await OpenPermanentDeleteAlbumAsync("qr8_fixed", noRecycleBin: false, allowSetting: true);

        await vm.RecycleAsync();

        Assert.Empty(_dialogService.Confirmations);
        Assert.Empty(_recycleBin.PermanentlyDeleted);
        Assert.Equal(img1, Assert.Single(_recycleBin.RecycledPaths));

        var undoResult = await undo.UndoLastAsync();
        Assert.True(undoResult.Succeeded);
        Assert.Equal(1, _recycleBin.RestoreCalls);
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
        /// <summary>Q-R8: simulates a removable/network drive (no Recycle Bin). Files are ordinary temp files; nothing real is affected.</summary>
        public bool HasNoRecycleBin { get; set; }
        public List<string> PermanentlyDeleted { get; } = [];
        public int RestoreCalls { get; private set; }

        public bool CanRecycle(string path) => !HasNoRecycleBin;

        public void SendToRecycleBin(string path)
        {
            if (HasNoRecycleBin) throw new IOException("no recycle bin");
            RecycledPaths.Add(path);
            if (File.Exists(path)) File.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            if (!HasNoRecycleBin) throw new InvalidOperationException("fixed drive must never be deleted permanently");
            PermanentlyDeleted.Add(path);
            if (File.Exists(path)) File.Delete(path);
        }

        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            File.WriteAllBytes(path, ValidPngBytes);
            return true;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmationResponse { get; set; } = true;
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public Action? OnConfirmation { get; set; }
        public bool ShowConfirmation(string title, string message)
        {
            Confirmations.Add((title, message));
            OnConfirmation?.Invoke();
            return ConfirmationResponse;
        }
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => true;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => true;
        public void ShowBenchmark(string? folder = null) { }
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
        public virtual Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public virtual void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
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

    private sealed class SwitchableBlockingMoveFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        private Task? _block;
        public Task? Block { set => Volatile.Write(ref _block, value); }

        public override void Move(string source, string destination)
        {
            Volatile.Read(ref _block)?.GetAwaiter().GetResult();
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

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public List<string> Statuses { get; } = [];
        public List<string> PresentedPaths { get; } = [];
        private TaskCompletionSource<bool>? _countBarrier;
        private int _targetCount;

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
        public int CancelCount { get; private set; }
        public List<string> Events { get; } = [];
        public Task PreloadAroundAsync(int center) { Events.Add("preload:" + center); return Task.CompletedTask; }
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { CancelCount++; Events.Add("cancel"); }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }
}
