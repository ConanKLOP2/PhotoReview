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
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
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
            () => _previewContext.TargetDecodeBox(), WpfBitmapSourceCodec.Instance,
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => _previewContext.CurrentBackend(),
            disableDiskCacheOverride: true);

        _thumbnailCache = new ThumbnailCache(WpfBitmapSourceCodec.Instance,
            diskDirectory: Path.Combine(_tempDir, "thumbs"),
            maxRamBytes: 16 * 1024 * 1024);

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

    internal (MainViewModel ViewModel, FileActionService FileActions) CreateViewModel(IFileSystem? fs = null, Action? resetCaches = null)
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
            resetCachesAction: resetCaches,
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
        Assert.Equal(StatusFormatter.BatchDone(1, 0), vm.StatusText); // set after the reload, which clears the status line
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

    [Fact(DisplayName = "Closing defers only while the file-action gate is held; the recycle phase is never cancelled by the close request")]
    public async Task DeferCloseForFileAction_DefersOnlyWhileGateHeld_AndKeepsTheBatch()
    {
        var folder = Path.Combine(_tempDir, "dup_close");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png", ValidPngBytes);
        var numbered = CreateImageFile(folder, "photo (1).png", ValidPngBytes);

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        _dialogService.BatchReviewResponse = true;
        var deferredDuringReview = false;
        _dialogService.OnBatchReview = () => deferredDuringReview = vm.DeferCloseForFileAction();

        Assert.False(vm.DeferCloseForFileAction()); // idle: the window may close at once
        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.True(deferredDuringReview);
        Assert.Equal([numbered], _recycleBin.RecycledPaths); // hashing is over, so the close request does not abort the batch
        Assert.False(vm.DeferCloseForFileAction());
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
        Assert.Equal(StatusFormatter.BatchDone(1, 0), vm.StatusText); // set after the reload, which clears the status line
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
    public async Task ShowSettings_WhenRawSupportTurnedOff_ReloadsTheFolderSoNoRawEntryStaysListed()
    {
        var folder = Path.Combine(_tempDir, "raw_toggle_folder");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png", ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "2.cr2"), new byte[64]);
        _settings.RawSupportEnabled = true;

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        await vm.FolderLoadTask;
        Assert.Equal(2, vm.TotalFiles);

        _dialogService.SettingsResponse = true;
        _dialogService.OnShowSettings = () =>
        {
            _settings.RawSupportEnabled = false;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();
        await vm.FolderLoadTask;

        Assert.Equal(1, vm.TotalFiles); // reloaded with RAW hidden; without the reload the RAW entry would stay listed
        Assert.True(_preloadController.CancelCalled);
    }

    [Fact]
    public async Task ShowSettings_WhenRawSupportUnchanged_DoesNotReloadTheFolder()
    {
        var folder = Path.Combine(_tempDir, "raw_same_folder");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png", ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "2.cr2"), new byte[64]);
        _settings.RawSupportEnabled = true;

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        await vm.FolderLoadTask;
        var loadBefore = vm.FolderLoadTask;

        _dialogService.SettingsResponse = true;
        _dialogService.OnShowSettings = () => _settingsStore.Save(_settings);

        vm.ShowSettings();

        Assert.Same(loadBefore, vm.FolderLoadTask);
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

    [Fact]
    public void Sink_OnEmptyWithSubfolders_SetsSubfoldersStatusText()
    {
        var folder = Path.Combine(_tempDir, "empty_with_subfolders");
        var (vm, _) = CreateViewModel();
        IFolderLoadSink sink = vm;

        sink.OnEmptyWithSubfolders(folder, new SessionState { Folder = folder }, subfolderCount: 3);

        Assert.Equal(StatusFormatter.NoSupportedImagesButSubfolders(3), vm.StatusText);
        Assert.NotEqual(StatusFormatter.NoSupportedImages(), vm.StatusText);
    }

    [Fact]
    public async Task CanOpenInExternalEditor_TrueOnlyWhenPathSetAndImageOpen()
    {
        var folder = Path.Combine(_tempDir, "editor_can_open2");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        var (vm, _) = CreateViewModel();

        Assert.False(vm.CanOpenInExternalEditor); // no editor path, no image

        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        Assert.False(vm.CanOpenInExternalEditor); // editor path set, but no image open yet

        await vm.OpenFolderAsync(folder);
        Assert.True(vm.HasImages);
        Assert.True(vm.CanOpenInExternalEditor); // both conditions now hold

        _settings.ExternalEditorPath = "   ";
        Assert.False(vm.CanOpenInExternalEditor); // whitespace-only path counts as not configured
    }

    [Fact]
    public async Task OpenInExternalEditor_LaunchesConfiguredEditorWithCurrentPhotoPath()
    {
        var folder = Path.Combine(_tempDir, "editor_launch");
        Directory.CreateDirectory(folder);
        var photo = CreateImageFile(folder, "photo.png");
        var (vm, _) = CreateViewModel();
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        await vm.OpenFolderAsync(folder);

        (string ExePath, string FilePath)? launched = null;
        vm.StartExternalEditor = (exePath, filePath) => launched = (exePath, filePath);

        vm.OpenInExternalEditor();

        Assert.NotNull(launched);
        Assert.Equal(@"C:\Tools\editor.exe", launched!.Value.ExePath);
        Assert.Equal(photo, launched.Value.FilePath);
        Assert.Empty(_dialogService.Errors);
    }

    [Fact]
    public async Task OpenInExternalEditor_FileNoLongerExists_ShowsErrorAndDoesNotLaunch()
    {
        var folder = Path.Combine(_tempDir, "editor_missing_file");
        Directory.CreateDirectory(folder);
        var photo = CreateImageFile(folder, "photo.png");
        var (vm, _) = CreateViewModel();
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        await vm.OpenFolderAsync(folder);
        File.Delete(photo); // now missing on disk, but still the catalog's current entry

        var launched = false;
        vm.StartExternalEditor = (_, _) => launched = true;

        vm.OpenInExternalEditor();

        Assert.False(launched);
        Assert.Single(_dialogService.Errors);
    }

    [Fact]
    public async Task OpenInExternalEditor_NoEditorConfigured_OpensSettingsOnExternalEditorField()
    {
        var folder = Path.Combine(_tempDir, "editor_not_configured");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        var (vm, _) = CreateViewModel();
        _settings.ExternalEditorPath = string.Empty;
        await vm.OpenFolderAsync(folder);

        var launched = false;
        vm.StartExternalEditor = (_, _) => launched = true;

        vm.OpenInExternalEditor();

        Assert.False(launched);
        Assert.Empty(_dialogService.Errors);
        Assert.Equal(SettingsTarget.ExternalEditor, Assert.Single(_dialogService.SettingsTargets));
    }

    [Fact]
    public async Task OpenInExternalEditor_EditorConfigured_DoesNotOpenSettings()
    {
        var folder = Path.Combine(_tempDir, "editor_configured_no_settings");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        var (vm, _) = CreateViewModel();
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        await vm.OpenFolderAsync(folder);
        vm.StartExternalEditor = (_, _) => { };

        vm.OpenInExternalEditor();

        Assert.Empty(_dialogService.SettingsTargets);
    }

    // --- Test Doubles ---

    private sealed class ForwardingFolderSink(Func<IFolderLoadSink> targetProvider) : IFolderLoadSink
    {
        public void ResetCaches() => targetProvider().ResetCaches();
        public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session) => targetProvider().OnCatalogReady(folder, count, session);
        public Task PresentAsync(int index, long presentationGeneration) => targetProvider().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session) => targetProvider().OnEmpty(folder, session);
        public void OnEmptyWithSubfolders(string folder, PhotoReview.Core.Session.SessionState session, int subfolderCount) => targetProvider().OnEmptyWithSubfolders(folder, session, subfolderCount);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => targetProvider().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => targetProvider().OnFailed(folder, exception);
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => targetProvider().OnUnreadableRemovedAsync(removedPaths, currentRemoved);
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> RecycledPaths { get; } = [];

        /// <summary>When set, SendToRecycleBin blocks until it completes (an action that is "in flight").</summary>
        public Task? SendGate { get; set; }

        /// <summary>Paths whose recycling fails with an I/O error (a locked file), to exercise partial batch failure.</summary>
        public HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SendToRecycleBin(string path)
        {
            SendGate?.GetAwaiter().GetResult();
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
        public string? LastPickInitialFolder { get; private set; }
        public string? LastBenchmarkFolder { get; private set; }

        public bool ShowConfirmation(string title, string message) => ConfirmationResponse;
        public void ShowMessage(string title, string message) { }
        public List<(string Title, string Message)> Errors { get; } = [];
        public void ShowError(string title, string message) => Errors.Add((title, message));
        public string? PickFolder(string? initialFolder = null) { LastPickInitialFolder = initialFolder; return PickedFolderResponse; }
        public bool ShowBatchReview(IReadOnlyList<string> paths)
        {
            BatchReviewCalled = true;
            BatchReviewCount++;
            OnBatchReview?.Invoke();
            return BatchReviewResponse;
        }
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public List<SettingsTarget> SettingsTargets { get; } = [];
        public bool ShowSettings() => ShowSettings(SettingsTarget.Default);
        public bool ShowSettings(SettingsTarget target)
        {
            SettingsTargets.Add(target);
            OnShowSettings?.Invoke();
            return SettingsResponse;
        }
        public void ShowBenchmark(string? folder = null) => LastBenchmarkFolder = folder;
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        /// <summary>When set, the snapshot completes only when this task does (a pending Explorer order).</summary>
        public Task? Gate { get; set; }

        public async Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder,
            TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null,
            int progressInterval = 16,
            CancellationToken cancellationToken = default) =>
            await WaitForGateAsync(folder);

        private async Task<ExplorerViewSnapshot> WaitForGateAsync(string folder)
        {
            if (Gate is { } gate) await gate;
            return new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow);
        }

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

        public void SetCurrentImage(object? image, bool isFileChange = false) => Images.Add(image);
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

            // APP-T03: the timeout must actually bound the wait (a CancellationTokenSource nobody observed never fired).
            var barrier = _countBarrier;
            try
            {
                await barrier.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
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
