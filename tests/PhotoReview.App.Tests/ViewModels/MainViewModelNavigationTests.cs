using System.Collections.Generic;
using System.IO;
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
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed partial class MainViewModelNavigationTests : IDisposable
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
    private readonly TestDialogService _dialog = new();

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
            maxRamBytes: 16 * 1024 * 1024);

        _hashService = new FileHashService();
        var appPaths = new AppPaths(_tempDir);
        _sessionStore = new SessionStore(appPaths, _fileSystem);
        _settingsStore = new SettingsStore(appPaths, _fileSystem, new NullLog());
        // These tests exercise Explorer-order integration, which only Name (not the app's now-default
        // ImageSortMode.Default) queries.
        _settingsStore.Current.ImageSortMode = ImageSortMode.Name;
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

    /// <param name="sharedSessionWriter">Production wiring: the presenter, the folder coordinator and the view model all
    /// write the session through ONE debounced <see cref="SessionWriter"/> (otherwise the presenter saves directly).</param>
    private SessionWriter? _lastSessionWriter;

    private (MainViewModel ViewModel, ImagePresenter Presenter, FolderLoadCoordinator Coordinator) CreateViewModel(bool sharedSessionWriter = false)
    {
        MainViewModel? vm = null;
        var sessionWriter = new SessionWriter(_sessionStore, new NullLog());
        _lastSessionWriter = sessionWriter;

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
            getSession: () => vm?.Session,
            sessionWriter: sharedSessionWriter ? sessionWriter : null);

        var coordinator = new FolderLoadCoordinator(
            _catalog,
            _clock,
            _explorerOrder,
            _fileSystem,
            _sessionStore,
            _settingsStore,
            new ForwardingFolderLoadSink(() => vm!),
            sharedSessionWriter ? sessionWriter : null);

        var appPaths = new PhotoReview.Core.AppPaths(_tempDir);
        var journal = new OperationJournal(appPaths, _fileSystem, new SystemClock());
        var fileActions = new FileActionService(journal, _fileSystem, new SystemClock(), new TestRecycleBin());
        var undo = new UndoService(journal, _fileSystem, new TestRecycleBin(), fileActions);
        var dialog = _dialog;

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
            sessionWriter,
            preloadController: _preloadController);

        // Match production's WpfPresentationSink wiring (MainViewModelCompositionRoot.cs:71-73): image/status
        // assignment feeds back into the view model exactly as it does in the real app. Without this, FolderTitle
        // (and any other state that only NotifyNavigationStateChanged refreshes) never updates in these tests,
        // because it is a stored field written by UpdateFolderTitle(), not something computed live off the catalog.
        _sink.OnSetCurrentImage = (_, isFileChange) => vm!.NotifyCurrentImageChanged(isFileChange);
        _sink.OnSetStatusText = _ => vm!.NotifyPresentationChanged();

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
        public List<IReadOnlyList<SkippedEntry>> SkippedFilesShown { get; } = [];
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) => SkippedFilesShown.Add(entries);
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
        // Default TitleBarFields = FolderName only (not the full path).
        Assert.Contains(Path.GetFileName(folder), vm.FolderTitle);
    }

    [Fact(DisplayName = "TitleBarFields controls the title bar's content (in order) and follows navigation")]
    public async Task TitleBarFields_ControlsTitleContent_AndFollowsNavigation()
    {
        var folder = Path.Combine(_tempDir, "title_fields");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        CreateImageFile(folder, "b.jpg");
        var (vm, _, _) = CreateViewModel();
        vm.Settings = new AppSettings { LoadingMode = LoadingMode.Preview, TitleBarFields = TitleBarFields.IndexCount | TitleBarFields.FileName };

        await vm.OpenFolderAsync(folder);

        Assert.Contains("1/2", vm.FolderTitle, StringComparison.Ordinal);
        Assert.Contains("a.jpg", vm.FolderTitle, StringComparison.Ordinal);
        Assert.DoesNotContain(folder, vm.FolderTitle, StringComparison.Ordinal); // FolderPath/FolderName are both off

        await vm.NextAsync();

        Assert.Contains("2/2", vm.FolderTitle, StringComparison.Ordinal);
        Assert.Contains("b.jpg", vm.FolderTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("1/2", vm.FolderTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("a.jpg", vm.FolderTitle, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The title's LOG marker follows LoggingEnabled regardless of the selected TitleBarFields")]
    public async Task TitleBarFields_KeepsTheLoggingMarker()
    {
        var folder = Path.Combine(_tempDir, "title_logging");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        var (vm, _, _) = CreateViewModel();
        vm.Settings = new AppSettings { LoadingMode = LoadingMode.Preview, LoggingEnabled = true, TitleBarFields = TitleBarFields.FileName };

        await vm.OpenFolderAsync(folder);

        Assert.Contains("a.jpg", vm.FolderTitle, StringComparison.Ordinal);
        Assert.Contains("LOG", vm.FolderTitle, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "With no fields selected the title falls back to the folder's own name instead of going bare")]
    public async Task TitleBarFields_NoneSelected_FallsBackToFolderName()
    {
        var folder = Path.Combine(_tempDir, "title_none");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        var (vm, _, _) = CreateViewModel();
        vm.Settings = new AppSettings { LoadingMode = LoadingMode.Preview, TitleBarFields = TitleBarFields.None };

        await vm.OpenFolderAsync(folder);

        Assert.Contains(Path.GetFileName(folder), vm.FolderTitle, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "InstanceLabel prefixes both the empty-folder and folder-loaded title; unset leaves today's title byte-for-byte unchanged")]
    public async Task InstanceLabel_PrefixesTitle_UnsetLeavesTitleUnchanged()
    {
        var folder = Path.Combine(_tempDir, "instance_label");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        var (vm, _, _) = CreateViewModel();

        // Unset (default, the normal user launch): today's exact title, before and after a folder loads.
        var emptyTitleBefore = vm.FolderTitle;
        Assert.Equal(Tr.AppTitle, emptyTitleBefore);
        await vm.OpenFolderAsync(folder);
        var loadedTitleUnset = vm.FolderTitle;
        Assert.DoesNotContain("[", loadedTitleUnset, StringComparison.Ordinal);

        // No folder open: the label still applies to the plain Tr.AppTitle text.
        var (vmEmpty, _, _) = CreateViewModel();
        vmEmpty.InstanceLabel = "LABEL";
        Assert.Equal($"[LABEL] {Tr.AppTitle}", vmEmpty.FolderTitle);

        // Folder loaded: same prefix, same folder content as the unset case.
        vm.InstanceLabel = "LABEL";
        Assert.StartsWith("[LABEL] ", vm.FolderTitle, StringComparison.Ordinal);
        Assert.Equal($"[LABEL] {loadedTitleUnset}", vm.FolderTitle);

        // Clearing it restores exactly today's behaviour again.
        vm.InstanceLabel = null;
        Assert.Equal(loadedTitleUnset, vm.FolderTitle);
    }

    [Fact]
    public async Task OpenPathAsync_InvalidInput_SetsWarningStatus()
    {
        var (vm, _, _) = CreateViewModel();

        await vm.OpenPathAsync(Path.Combine(_tempDir, "non_existent_file.xyz"));

        Assert.Equal("Không tìm thấy ảnh được hỗ trợ.", vm.StatusText);
    }

    /// <summary>
    /// R05 (full code review 2026-09-27): <c>NotifyCurrentImageChanged</c> calls <c>NotifyNavigationStateChanged</c>
    /// (MainViewModel.cs ~144-148), and each navigation method (Next/Previous/First/Last/Skip) used to call it
    /// AGAIN once <c>PresentAsync</c> returned. Measured with production's real feedback wiring
    /// (<see cref="TestPresentationSink.OnSetCurrentImage"/>/<see cref="TestPresentationSink.OnSetStatusText"/>,
    /// wired by <see cref="CreateViewModel"/> to match <c>WpfPresentationSink</c> in
    /// <c>MainViewModelCompositionRoot.cs:71-73</c>) and <see cref="LoadingMode.Original"/> to avoid the
    /// thumbnail/preview decode race being nondeterministic in a unit test: one completed <c>NextAsync</c> drove
    /// <c>NotifyNavigationStateChanged</c> 4 times before the fix (the initial "Loading" status, the image
    /// assignment, the final "with dimensions" status, and the redundant explicit call) and 3 after (the explicit
    /// call removed) -- every one of the 3 remaining calls carries state that did not exist yet at the previous
    /// call, so none of them can be merged away without losing a distinct legitimate update. This also protects
    /// PR-D's crossfade signal (<see cref="MainViewModel.ImageChanging"/>, <c>ImageTransitionDecision</c>), which
    /// must keep firing before <c>CurrentImage</c>'s own PropertyChanged -- removing the redundant call does not
    /// touch that path at all.
    /// </summary>
    [Fact(DisplayName = "NextAsync raises the navigation-state notification exactly 3 times per completed navigation (R05: no redundant extra call)")]
    public async Task NextAsync_RaisesNavigationStateChanged_ExactlyThreeTimes()
    {
        var folder = Path.Combine(_tempDir, "album_notify_count");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.jpg");
        CreateImageFile(folder, "b.jpg");
        var (vm, _, _) = CreateViewModel();
        // Original mode: no thumbnail-vs-preview decode race, so the internal notification count is
        // deterministic (a Preview-mode race can non-deterministically add one more "thumbnail presented" call).
        _settings.LoadingMode = LoadingMode.Original;
        await vm.OpenFolderAsync(folder);

        var notificationCount = 0;
        // CurrentIndex is one of the properties NotifyNavigationStateChanged always raises, so counting its
        // PropertyChanged firings counts NotifyNavigationStateChanged invocations one-for-one.
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentIndex)) notificationCount++; };

        await vm.NextAsync();

        Assert.Equal(3, notificationCount);
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

        await vm.NavigateSiblingFolderAsync(1);
        Assert.Equal("Đã ở thư mục cuối cùng cùng cấp.", vm.StatusText);

        await vm.NavigateSiblingFolderAsync(-1);
        Assert.Equal("Đã ở thư mục đầu tiên cùng cấp.", vm.StatusText);
    }

    [Fact]
    public void ViewerControls_DelegateCorrectlyToViewerState()
    {
        var (vm, _, _) = CreateViewModel();

        // Q-R41: the keyboard zoom step is now AppSettings.KeyboardZoomStepPercent (default 10 %), applied to
        // ViewerState.ZoomStep by the MainViewModel constructor -- no longer the old hardcoded 0.25 (25 %).
        vm.ZoomIn();
        Assert.Equal(1.0 + AppSettings.DefaultKeyboardZoomStepPercent / 100.0, vm.Viewer.Zoom);

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

        await vm.LastAsync();

        Assert.Equal(2, vm.CurrentIndex);
        Assert.False(vm.CanNavigateNext);
        Assert.True(_clock.CurrentInteraction > interaction);
        await vm.FirstAsync();
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
    public void ShowSkippedFiles_ShowsTheSkippedEntriesThroughTheDialogService()
    {
        var (vm, _, _) = CreateViewModel();
        var entry = new SkippedEntry(Path.Combine(_tempDir, "x.jpg"), "locked");
        ((IFolderLoadSink)vm).OnFilesSkipped(_tempDir, [entry]);

        vm.ShowSkippedFiles();

        var shown = Assert.Single(_dialog.SkippedFilesShown);
        Assert.Equal(entry, Assert.Single(shown));
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
        vm.Settings.ShowFolderInfo = true; // off by default (Q-R20): opt in for this folder-info test

        await vm.OpenFolderAsync(Path.Combine(parent, "2024-05-02"));
        await vm.InfoOverlay.PendingSiblings.WithTimeout(TimeSpan.FromSeconds(10), "sibling info");

        Assert.True(vm.InfoOverlay.IsFolderInfoVisible);
        Assert.Equal(
            string.Join("     ", Tr.MainFolderInfoPrevious("PageUp", "2024-05-01"), Tr.MainFolderInfoCurrent("2024-05-02"), Tr.MainFolderInfoNext("PageDown", "2024-05-03")),
            vm.InfoOverlay.FolderInfoText);

        // The display agrees with the key: PageDown opens the folder shown on the right (the empty one is skipped).
        await vm.NavigateSiblingFolderAsync(1);
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
        public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session) => _getSink().OnCatalogReady(folder, count, session);
        public Task PresentAsync(int index, long presentationGeneration) => _getSink().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session) => _getSink().OnEmpty(folder, session);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => _getSink().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => _getSink().OnFailed(folder, exception);
        public void OnFilesSkipped(string folder, IReadOnlyList<PhotoReview.Core.Abstractions.SkippedEntry> skipped) => _getSink().OnFilesSkipped(folder, skipped);
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => _getSink().OnUnreadableRemovedAsync(removedPaths, currentRemoved);
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public List<string> Statuses { get; } = [];

        /// <summary>Wired by tests that need the same feedback loop as production's <c>WpfPresentationSink</c>
        /// (image/status assignment calling back into the view model), e.g. the R05 notification-count test.
        /// Left null (no-op) everywhere else so existing tests are unaffected.</summary>
        public Action<object?, bool>? OnSetCurrentImage { get; set; }
        public Action<string>? OnSetStatusText { get; set; }

        public void SetCurrentImage(object? image, bool isFileChange = false)
        {
            Images.Add(image);
            OnSetCurrentImage?.Invoke(image, isFileChange);
        }

        public void SetStatusText(string status)
        {
            Statuses.Add(status);
            OnSetStatusText?.Invoke(status);
        }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class TestPreloadController : IPreloadController
    {
        private readonly List<string> _calls = [];

        /// <summary>AR16: "cancel", "evict:&lt;normalized path&gt;" and "around:&lt;index&gt;", in call order.</summary>
        public IReadOnlyList<string> Calls { get { lock (_calls) return _calls.ToArray(); } }

        private void Record(string call) { lock (_calls) _calls.Add(call); }

        public Task PreloadAroundAsync(int center)
        {
            Record("around:" + center.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        }
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() => Record("cancel");
        public void RemovePreloadedKeysForPath(string normalizedPath) => Record("evict:" + normalizedPath);
        public void ClearPreloadedKeys() { }
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    // Q-R18: the view model consults the instance ownership before every folder open.

    [Fact]
    public async Task OpenFolderAsync_OwnershipForwards_KeepsCurrentFolderAndShowsStatus()
    {
        var a = Path.Combine(_tempDir, "a");
        var b = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        CreateImageFile(a, "a1.jpg");
        CreateImageFile(b, "b1.jpg");
        CreateImageFile(b, "b2.jpg");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(a);
        var ownership = new RecordingOwnership { Decision = PhotoReview.Core.Instance.FolderOpenDecision.ForwardedToOtherInstance };
        vm.FolderOwnership = ownership;

        await vm.OpenFolderAsync(b);

        Assert.Equal(1, vm.TotalFiles);
        // Default TitleBarFields = FolderName only (not the full path); the forwarded open to "b" left "a" current.
        Assert.Contains(Path.GetFileName(a), vm.FolderTitle);
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusFolderOpenedInOtherWindow("b"), vm.StatusText);
        Assert.Empty(ownership.AfterOpens);
    }

    [Fact]
    public async Task OpenFolderAsync_OwnershipRefuses_ShowsNoResponseStatus()
    {
        var b = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(b);
        CreateImageFile(b, "b1.jpg");
        var (vm, _, _) = CreateViewModel();
        vm.FolderOwnership = new RecordingOwnership { Decision = PhotoReview.Core.Instance.FolderOpenDecision.OwnedByOtherInstance };

        await vm.OpenFolderAsync(b);

        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusFolderOpenInOtherWindowNoResponse("b"), vm.StatusText);
    }

    [Fact]
    public async Task OpenFolderAsync_OwnershipProceeds_ReportsShownFolderAndFinishedOpen()
    {
        var a = Path.Combine(_tempDir, "a");
        var missing = Path.Combine(_tempDir, "missing");
        Directory.CreateDirectory(a);
        CreateImageFile(a, "a1.jpg");
        var (vm, _, _) = CreateViewModel();
        var ownership = new RecordingOwnership();
        vm.FolderOwnership = ownership;

        await vm.OpenFolderAsync(a);
        await vm.OpenFolderAsync(missing); // fails: the window keeps showing A

        Assert.Equal([a, missing], ownership.BeforeOpens);
        Assert.Equal([a], ownership.Shown);
        Assert.Equal([(a, (string?)a), (missing, (string?)a)], ownership.AfterOpens);
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact]
    public async Task OpenFolderAsync_WindowClosedWhileOwnershipPending_DropsTheOpenAndReportsAfterOpen()
    {
        var a = Path.Combine(_tempDir, "a");
        Directory.CreateDirectory(a);
        CreateImageFile(a, "a1.jpg");
        var (vm, _, _) = CreateViewModel();
        var ownership = new GatedOwnership();
        vm.FolderOwnership = ownership;

        var open = vm.OpenFolderAsync(a);
        vm.CloseSession(); // disposes the load coordinator while BeforeOpenAsync is still pending
        ownership.Gate.SetResult(PhotoReview.Core.Instance.FolderOpenDecision.Proceed);
        await open; // must not throw ObjectDisposedException

        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal([(a, (string?)null)], ownership.AfterOpens);
    }

    [Fact]
    public void OnFailed_UsesTheLocalizedSentenceOfTheException()
    {
        var (vm, _, _) = CreateViewModel();
        var error = PhotoReview.Core.Localization.UserFacingError.Localized(
            new IOException("raw english message"), () => "localized sentence");

        ((PhotoReview.App.Coordinators.IFolderLoadSink)vm).OnFailed(_tempDir, error);

        Assert.Contains("localized sentence", vm.StatusText);
        Assert.DoesNotContain("raw english message", vm.StatusText);
    }

    private sealed class GatedOwnership : PhotoReview.Core.Instance.IFolderOwnership
    {
        public TaskCompletionSource<PhotoReview.Core.Instance.FolderOpenDecision> Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Folder, string? Shown)> AfterOpens { get; } = [];

        public Task<PhotoReview.Core.Instance.FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default) => Gate.Task;

        public void OnFolderShown(string folder) { }

        public void AfterOpen(string folder, string? shownFolder) => AfterOpens.Add((folder, shownFolder));
    }

    private sealed class RecordingOwnership : PhotoReview.Core.Instance.IFolderOwnership
    {
        public PhotoReview.Core.Instance.FolderOpenDecision Decision { get; init; } = PhotoReview.Core.Instance.FolderOpenDecision.Proceed;
        public List<string> BeforeOpens { get; } = [];
        public List<string> Shown { get; } = [];
        public List<(string Folder, string? Shown)> AfterOpens { get; } = [];

        public Task<PhotoReview.Core.Instance.FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default)
        {
            BeforeOpens.Add(folder);
            return Task.FromResult(Decision);
        }

        public void OnFolderShown(string folder) => Shown.Add(folder);

        public void AfterOpen(string folder, string? shownFolder) => AfterOpens.Add((folder, shownFolder));
    }
}
