using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
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
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// FEAT-RENAME PR2: <see cref="MainViewModel.RenameCurrentAsync"/> and its <see cref="IFileActionSink.OnFilesRenamed"/> in the
/// REAL view model + presenter + zoom-detail loader + file-action service, wired like production (the presentation sink feeds
/// <see cref="MainViewModel.NotifyCurrentImageChanged"/>). Real temp folders and session store; a fake Recycle Bin that is never
/// used; the Explorer order is held on a gate the test completes.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class MainViewModelRenameTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_MainVM_Rename_" + Guid.NewGuid().ToString("N"));
    private readonly ViewerState _viewer = new();
    private readonly ThumbnailCache _thumbnailCache;
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly PhysicalFileSystem _fileSystem = new();
    // ImageSortMode.Name: the Explorer order is queried (the default sort never asks Explorer, so INV-9 would have nothing to hold).
    private readonly AppSettings _settings = new() { LoadingMode = LoadingMode.Preview, Actions = ReviewAction.Defaults(), ImageSortMode = ImageSortMode.Name };
    private readonly RenameDecoder _decoder = new();
    private readonly RecordingPreload _preload = new();
    private readonly CountingSink _sink = new();
    private readonly HoldingOrderProvider _order = new();
    private readonly List<SessionWriter> _writers = [];
    private readonly List<PreviewImageService> _services = [];
    private readonly QuietDialog _dialog = new();

    public MainViewModelRenameTests()
    {
        Directory.CreateDirectory(_tempDir);
        var appPaths = new AppPaths(_tempDir);
        _sessionStore = new SessionStore(appPaths, _fileSystem);
        _settingsStore = new SettingsStore(appPaths, _fileSystem, new NullLog());
        _settingsStore.Save(_settings);
        _thumbnailCache = new ThumbnailCache(WpfBitmapSourceCodec.Instance,
            diskDirectory: Path.Combine(_tempDir, "thumbs"), maxRamBytes: 16 * 1024 * 1024);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        _thumbnailCache.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string MakeFolder(string name, params string[] files)
    {
        var folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(folder, file), [0xFF, 0xD8, 0xFF, 0xD9]);
        return folder;
    }

    private MainViewModel CreateViewModel(ReviewCatalog? catalog = null)
    {
        catalog ??= new ReviewCatalog();
        var metrics = new ReviewMetrics();
        var clock = new GenerationClock();
        var compare = new CompareViewModel();
        var hashService = new FileHashService();
        var previewService = new PreviewImageService(
            metrics, () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: _decoder, currentBackend: () => DecoderBackend.Wpf);
        _services.Add(previewService);
        var writer = new SessionWriter(_sessionStore, FileLog.Default);
        _writers.Add(writer);

        MainViewModel? vm = null;
        var presenter = new ImagePresenter(
            catalog, clock, previewService, _thumbnailCache, _preload, compare, hashService, metrics,
            () => _settings, _sessionStore, _sink, _fileSystem, getSession: () => vm?.Session);
        var coordinator = new FolderLoadCoordinator(
            catalog, clock, _order, _fileSystem, _sessionStore, _settingsStore, new ForwardingSink(() => vm!));
        var appPaths = new AppPaths(_tempDir);
        var journal = new OperationJournal(appPaths, _fileSystem, new SystemClock());
        var recycleBin = new UnusedBin();
        var fileActions = new FileActionService(journal, _fileSystem, new SystemClock(), recycleBin);
        var undo = new UndoService(journal, _fileSystem, recycleBin, fileActions);

        vm = new MainViewModel(
            catalog, clock, coordinator, presenter, _viewer, compare, _settingsStore, _sessionStore, _fileSystem,
            fileActions, undo, _dialog, hashService, previewService, _thumbnailCache,
            writer, preloadController: _preload, naturalComparer: ManagedNaturalComparer.Instance);

        var created = vm;
        _sink.OnSetCurrentImage = isFileChange => created.NotifyCurrentImageChanged(isFileChange);
        return vm;
    }

    private async Task<SessionState> LoadSettledSessionAsync(string folder)
    {
        var writer = _writers[0];
        await writer.FlushAsync();
        await writer.WhenIdleAsync();
        return _sessionStore.Load(folder);
    }

    /// <summary>Opens <paramref name="folder"/> at <paramref name="initialPath"/> and applies Fit-width, as the window does.</summary>
    private async Task<MainViewModel> OpenAsync(string folder, string? initialPath = null, ReviewCatalog? catalog = null)
    {
        var vm = CreateViewModel(catalog);
        await vm.OpenFolderAsync(folder, initialPath);
        return vm;
    }

    private void ApplyFitWidth()
    {
        _viewer.DpiScale = 1.0;
        _viewer.ApplyInitialViewMode(InitialViewMode.FitWidth, 1200, 800);
    }

    [Fact]
    public async Task RenameCurrentAsync_ShownPhoto_KeepsIndexZoomAndPresentsNothing()
    {
        var folder = MakeFolder("keep", "a.jpg", "b.jpg", "c.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var vm = await OpenAsync(folder, b);
        Assert.Equal(1, vm.Catalog.CurrentIndex);
        ApplyFitWidth();
        var zoom = _viewer.Zoom;
        var width = _viewer.SourcePixelWidth;
        var presented = _sink.Presented.Count;
        var images = _sink.ImageCount;
        var current = vm.Presenter.CurrentImage;

        var result = await vm.RenameCurrentAsync("renamed");

        var renamed = Path.Combine(folder, "renamed.jpg");
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal(renamed, result.NewPath);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(b));
        Assert.Equal([Path.Combine(folder, "a.jpg"), renamed, Path.Combine(folder, "c.jpg")], vm.Catalog.Paths);
        Assert.Equal(1, vm.Catalog.CurrentIndex);
        Assert.Equal(presented, _sink.Presented.Count); // PresentAsync was not run again
        Assert.Equal(images, _sink.ImageCount); // the bitmap was not touched
        Assert.Same(current, vm.Presenter.CurrentImage);
        Assert.Equal(renamed, vm.Presenter.CurrentPresentedPath);
        Assert.Equal("renamed.jpg", vm.Presenter.CurrentPhotoInfo?.FileName);
        Assert.Equal(zoom, _viewer.Zoom, 9);
        Assert.Equal(width, _viewer.SourcePixelWidth);
    }

    [Fact]
    public async Task RenameCurrentAsync_ThenNotifyAndZoomSwap_TheViewerStillSeesTheSameImage()
    {
        var folder = MakeFolder("same", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        ApplyFitWidth();
        Assert.Equal(1200.0 / 6000, _viewer.Zoom, 6);

        await vm.RenameCurrentAsync("z");
        vm.NotifyCurrentImageChanged(); // e.g. a zoom swap's SetCurrentImage: same file, so NOT a new image
        _viewer.SwapSourceSize(6016, 4016);

        // A same-image swap keeps the Fit axis: the width fit is re-applied to the new size. A "new image" forgets the axis
        // (see MainViewModelZoomSwapTests), which would leave the old zoom factor.
        Assert.Equal(1200.0 / 6016, _viewer.Zoom, 6);
    }

    [Fact]
    public async Task RenameCurrentAsync_ZoomedWithHeldOriginal_ZoomStaysAndTheOriginalIsNotDecodedAgain()
    {
        var folder = MakeFolder("zoomed", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        _viewer.SetZoom(2.0);
        var load = vm.Presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);
        _decoder.OriginalGate.Release();
        await load!;
        var held = vm.Presenter.ZoomDetail.HeldOriginal;
        Assert.NotNull(held);
        var decodes = _decoder.OriginalPaths.Count;
        var images = _sink.ImageCount;

        var result = await vm.RenameCurrentAsync("z");

        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal(2.0, _viewer.Zoom, 9);
        Assert.Same(held, vm.Presenter.ZoomDetail.HeldOriginal);
        Assert.True(vm.Presenter.ZoomDetail.IsShowingOriginal);
        Assert.Equal(decodes, _decoder.OriginalPaths.Count);
        Assert.Equal(images, _sink.ImageCount);
    }

    [Fact]
    public async Task RenameCurrentAsync_ZoomedAndTheOriginalNotYetDecoded_DecodesItViaTheNewName()
    {
        var folder = MakeFolder("zoomlate", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        _viewer.SetZoom(2.0);
        Assert.NotNull(vm.Presenter.ZoomDetail.PendingLoad); // decoding a.jpg, gated
        var renameTask = vm.RenameCurrentAsync("z");
        // The gated decode must be released for the rename to finish only if it blocks; it does not (it runs on its own thread).
        var result = await renameTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        var restarted = vm.Presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(restarted);

        _decoder.OriginalGate.Release(2);
        await restarted!.WaitAsync(TimeSpan.FromSeconds(10));

        var renamed = Path.Combine(folder, "z.jpg");
        Assert.Contains(renamed, _decoder.OriginalPaths);
        Assert.NotNull(vm.Presenter.ZoomDetail.HeldOriginal);
    }

    [Fact]
    public async Task RenameCurrentAsync_RekeysTheSessionAndPersistsIt()
    {
        var folder = MakeFolder("session", "a.jpg", "b.jpg", "c.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var vm = await OpenAsync(folder, b);
        Assert.Equal(b, vm.Session!.CurrentPath);

        await vm.RenameCurrentAsync("renamed");

        var renamed = Path.Combine(folder, "renamed.jpg");
        Assert.Equal(renamed, vm.Session!.CurrentPath);
        var persisted = await LoadSettledSessionAsync(folder);
        Assert.Equal(renamed, persisted.CurrentPath);
    }

    [Fact]
    public async Task RenameCurrentAsync_RekeysTheSkippedList()
    {
        var folder = MakeFolder("skipped", "a.jpg", "b.jpg", "c.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var c = Path.Combine(folder, "c.jpg");
        var vm = await OpenAsync(folder, b);
        vm.Session!.Skipped.Add(b);
        vm.Session.Skipped.Add(c);

        await vm.RenameCurrentAsync("renamed");

        var renamed = Path.Combine(folder, "renamed.jpg");
        Assert.Equal([renamed, c], vm.Session.Skipped);
        var persisted = await LoadSettledSessionAsync(folder);
        Assert.Equal([renamed, c], persisted.Skipped);
    }

    [Fact]
    public async Task RenameCurrentAsync_ANewViewModelOverTheSameFolder_StartsAtTheRenamedPhotoNotTheFirst()
    {
        var folder = MakeFolder("restart", "a.jpg", "b.jpg", "c.jpg");
        var c = Path.Combine(folder, "c.jpg");
        var vm = await OpenAsync(folder, c);
        await vm.RenameCurrentAsync("zz");
        await LoadSettledSessionAsync(folder);

        var again = await OpenAsync(folder, catalog: new ReviewCatalog());

        var renamed = Path.Combine(folder, "zz.jpg");
        Assert.Equal(renamed, again.Catalog.Current?.Path);
        Assert.Equal(renamed, again.Session?.CurrentPath);
        Assert.NotEqual(0, again.Catalog.CurrentIndex);
    }

    [Fact]
    public async Task RenameCurrentAsync_WhileAnExplorerOrderIsPending_WaitsForItThenRenames()
    {
        var folder = MakeFolder("pending", "a.jpg", "b.jpg", "c.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var vm = CreateViewModel();
        var snapshot = _order.Hold(folder);
        var open = vm.OpenFolderAsync(folder, b);
        await _sink.WaitForPresentedAsync(1);
        Assert.False(open.IsCompleted);

        var rename = vm.RenameCurrentAsync("renamed");

        Assert.False(rename.IsCompleted);
        Assert.True(File.Exists(b)); // nothing renamed while INV-9 holds the action back
        Assert.False(File.Exists(Path.Combine(folder, "renamed.jpg")));
        snapshot.SetResult(HoldingOrderProvider.Unavailable(folder));
        await open.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await rename.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.True(File.Exists(Path.Combine(folder, "renamed.jpg")));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public async Task RenameCurrentAsync_QueuedWhileAnotherFolderGetsOpened_IsDroppedAndRenamesNothing()
    {
        var folderA = MakeFolder("dropA", "a.jpg", "b.jpg");
        var folderB = MakeFolder("dropB", "x.jpg", "y.jpg");
        var a = Path.Combine(folderA, "a.jpg");
        var vm = CreateViewModel();
        var snapshot = _order.Hold(folderA);
        var openA = vm.OpenFolderAsync(folderA, a);
        await _sink.WaitForPresentedAsync(1);

        var rename = vm.RenameCurrentAsync("renamed"); // queued behind the pending order of folder A
        Assert.False(rename.IsCompleted);
        await vm.OpenFolderAsync(folderB); // another folder is opened meanwhile
        snapshot.TrySetResult(HoldingOrderProvider.Unavailable(folderA));
        await openA.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await rename.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Status != FileRenameStatus.Renamed, "renamed to " + result.NewPath);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(folderA, "renamed.jpg")));
        Assert.False(File.Exists(Path.Combine(folderB, "renamed.jpg")));
        Assert.True(File.Exists(Path.Combine(folderB, "x.jpg")));
    }

    [Fact]
    public async Task RenameCurrentAsync_CompareVisible_ClearsCompareAndPresentsTheRenamedEntryAgain()
    {
        var folder = MakeFolder("compare", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        vm.Compare.IsVisible = true;
        var presented = _sink.Presented.Count;

        var result = await vm.RenameCurrentAsync("z");

        var renamed = Path.Combine(folder, "z.jpg");
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.False(vm.Compare.IsVisible);
        Assert.Equal(presented + 1, _sink.Presented.Count);
        Assert.Equal(renamed, _sink.Presented[^1]);
        Assert.Equal(renamed, vm.Presenter.CurrentPresentedPath);
    }

    [Fact]
    public async Task RenameCurrentAsync_EvictsThePreviewCacheOfTheOldName()
    {
        var folder = MakeFolder("cache", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        var service = vm.PreviewService!;
        Assert.True(service.CacheCount >= 1); // the shown preview is cached under a.jpg

        await vm.RenameCurrentAsync("z");

        Assert.Equal(0, service.CacheCount); // nothing is left under the old name (the kept image is not re-cached either)
    }

    [Fact]
    public async Task RenameCurrentAsync_CancelsPreloadThenWarmsAroundTheCurrentIndex()
    {
        var folder = MakeFolder("preload", "a.jpg", "b.jpg", "c.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var vm = await OpenAsync(folder, b);
        _preload.Events.Clear();

        await vm.RenameCurrentAsync("z");

        Assert.Equal(["cancel", "preload:1"], _preload.Events);
    }

    [Fact]
    public async Task RenameCurrentAsync_InvalidName_ReportsInvalidAndChangesNothing()
    {
        var folder = MakeFolder("invalid", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        var presented = _sink.Presented.Count;

        var result = await vm.RenameCurrentAsync("a?b");

        Assert.Equal(FileRenameStatus.Invalid, result.Status);
        Assert.Equal(RenameNameProblem.InvalidCharacter, result.Problem);
        Assert.True(File.Exists(a));
        Assert.Equal(presented, _sink.Presented.Count);
        Assert.Equal(a, vm.Presenter.CurrentPresentedPath);
    }

    [Fact]
    public async Task RenameCurrentAsync_NoFolderOpen_IsNothingToRename()
    {
        var vm = CreateViewModel();

        var result = await vm.RenameCurrentAsync("x");

        Assert.Equal(FileRenameStatus.NothingToRename, result.Status);
    }

    [Fact]
    public async Task UndoAsync_AfterRename_PutsTheOldNameBackWithoutPresentingAndRekeysTheSession()
    {
        var folder = MakeFolder("undo", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        await vm.RenameCurrentAsync("z");
        var presented = _sink.Presented.Count;

        await vm.UndoAsync();

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(folder, "z.jpg")));
        Assert.Equal(a, vm.Presenter.CurrentPresentedPath);
        Assert.Equal(a, vm.Session!.CurrentPath);
        Assert.Equal(presented, _sink.Presented.Count);
        Assert.Equal("a.jpg", vm.Presenter.CurrentPhotoInfo?.FileName);
    }

    // ---- FEAT-RENAME PR3: the name prompt (PromptRenameCurrentAsync) ---------------------------------------------

    [Fact]
    public async Task PromptRenameCurrentAsync_AsksWithTheStemAndTheFixedExtension_ThenRenamesAndUndoPutsItBack()
    {
        var folder = MakeFolder("prompt", "a.jpg", "b.JPG", "c.jpg");
        var b = Path.Combine(folder, "b.JPG");
        var vm = await OpenAsync(folder, b);
        _dialog.OnPromptRename = request =>
        {
            Assert.Equal("b", request.InitialName);
            Assert.Equal(".JPG", request.Extension); // the extension is fixed, in the case it has on disk
            return "holiday 01";
        };

        var result = await vm.PromptRenameCurrentAsync();

        var renamed = Path.Combine(folder, "holiday 01.JPG");
        Assert.NotNull(result);
        Assert.Equal(FileRenameStatus.Renamed, result!.Status);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(b));
        Assert.Equal(renamed, vm.Catalog.Current?.Path);
        Assert.Equal(1, vm.Catalog.CurrentIndex);

        await vm.UndoAsync();

        Assert.True(File.Exists(b));
        Assert.False(File.Exists(renamed));
        Assert.Equal(b, vm.Catalog.Current?.Path);
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_Cancelled_ChangesNothingAndIsSilent()
    {
        var folder = MakeFolder("promptcancel", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        _dialog.OnPromptRename = _ => null;
        var presented = _sink.Presented.Count;

        var result = await vm.PromptRenameCurrentAsync();

        Assert.Null(result);
        Assert.Equal(1, _dialog.PromptCount);
        Assert.True(File.Exists(a));
        Assert.Equal(presented, _sink.Presented.Count);
        Assert.Equal([a, Path.Combine(folder, "b.jpg")], vm.Catalog.Paths);
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_TheSameNameTyped_IsNoChange_NothingTouched()
    {
        var folder = MakeFolder("promptsame", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        _dialog.OnPromptRename = _ => "a";

        var result = await vm.PromptRenameCurrentAsync();

        Assert.Equal(FileRenameStatus.NoChange, result!.Status);
        Assert.True(File.Exists(a));
        Assert.Equal([a, Path.Combine(folder, "b.jpg")], vm.Catalog.Paths);
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_ValidateFollowsThePolicy_AndTypingNeverTouchesTheDisk()
    {
        var folder = MakeFolder("promptvalidate", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        File.WriteAllBytes(Path.Combine(folder, "late.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]); // on disk, not in the listing
        var seen = new List<(string Typed, bool Commit, string? Error)>();
        _dialog.OnPromptRename = request =>
        {
            foreach (var (typed, commit) in new[]
            {
                ("", false), ("a?", false), ("a.", false), ("CON", false), (new string('x', 300), false), ("b", false),
                ("a", false), ("A", false), ("late", false), ("late", true), ("fresh", true),
            })
            {
                seen.Add((typed, commit, request.Validate(typed, commit)));
            }

            return null;
        };

        await vm.PromptRenameCurrentAsync();

        string? Error(string typed, bool commit) => seen.Single(s => s.Typed == typed && s.Commit == commit).Error;
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorEmpty, Error("", false));
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorInvalidCharacter, Error("a?", false));
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorTrailingDot, Error("a.", false));
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorReservedName, Error("CON", false));
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorTooLong, Error(new string('x', 300), false));
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorExists("b.jpg"), Error("b", false)); // another listed photo
        Assert.Null(Error("a", false)); // the file itself
        Assert.Null(Error("A", false)); // a change of letter case only is the file itself, not a clash
        Assert.Null(Error("late", false)); // typing does not ask the disk ...
        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorExists("late.jpg"), Error("late", true)); // ... the confirming call does
        Assert.Null(Error("fresh", true));
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_NoFolderOpen_NeverShowsTheBox()
    {
        var vm = CreateViewModel();
        _dialog.OnPromptRename = _ => "x";

        var result = await vm.PromptRenameCurrentAsync();

        Assert.Null(result);
        Assert.Equal(0, _dialog.PromptCount);
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_TheViewerMovedOnWhileTheBoxWasOpen_RenamesNothing()
    {
        var folder = MakeFolder("promptmoved", "a.jpg", "b.jpg", "c.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        _dialog.OnPromptRename = _ =>
        {
            vm.NextAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); // e.g. a refresh moved the selection
            return "moved";
        };

        var result = await vm.PromptRenameCurrentAsync();

        Assert.Null(result);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(folder, "moved.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "b.jpg")));
    }

    [Fact]
    public async Task PromptRenameCurrentAsync_ACaptureGroup_IsValidatedWithTheGroupsPlan_NotTheJpegAlone()
    {
        var folder = MakeFolder("promptgroup", "a.jpg", "b.jpg");
        var a = Path.Combine(folder, "a.jpg");
        var vm = await OpenAsync(folder, a);
        var group = new CaptureGroup(a, Path.Combine(folder, "a.cr2"), Path.Combine(folder, "unrelated.xmp")); // sidecar not named after the photo
        vm.Catalog.Reset([new CatalogEntry(a) { CaptureGroup = group }, new CatalogEntry(Path.Combine(folder, "b.jpg"))], RawPairMode.Separate);
        vm.Catalog.SetCurrent(0);
        string? error = null;
        _dialog.OnPromptRename = request =>
        {
            error = request.Validate("fresh", false);
            return null;
        };

        await vm.PromptRenameCurrentAsync();

        Assert.Equal(PhotoReview.Core.Localization.Tr.DialogRenameErrorSidecarNotMappable, error);
    }

    // ---- test doubles -------------------------------------------------------------------------------------------

    private sealed class RenameDecoder : IImageDecoder
    {
        private readonly List<string> _originals = [];
        private readonly object _gate = new();
        public SemaphoreSlim OriginalGate { get; } = new(0);

        public IReadOnlyList<string> OriginalPaths
        {
            get { lock (_gate) return [.. _originals]; }
        }

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (request.Box.IsUnbounded)
            {
                lock (_gate) _originals.Add(request.Path);
                OriginalGate.Wait();
                return new SizedImage(6000, 4000, 6000, 4000, downscaled: false);
            }
            return new SizedImage(600, 400, 6000, 4000, downscaled: true);
        }

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class SizedImage(int width, int height, int originalWidth, int originalHeight, bool downscaled) : IDecodedImage
    {
        public int PixelWidth => width;
        public int PixelHeight => height;
        public bool Downscaled => downscaled;
        public int Orientation => 1;
        public long EstimatedBytes => (long)width * height * 4;
        public object PlatformImage { get; } = new object();
        public int OriginalWidth => originalWidth;
        public int OriginalHeight => originalHeight;
    }

    private sealed class CountingSink : IPresentationSink
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _waiter;
        private int _waitFor;
        private int _images;

        public Action<bool>? OnSetCurrentImage { get; set; }
        public List<string> Presented { get; } = [];
        public int ImageCount => Volatile.Read(ref _images);

        public void SetCurrentImage(object? image, bool isFileChange = false)
        {
            Interlocked.Increment(ref _images);
            OnSetCurrentImage?.Invoke(isFileChange);
        }

        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }

        public void OnPresented(string path)
        {
            lock (_gate)
            {
                Presented.Add(path);
                if (_waiter is not null && Presented.Count >= _waitFor) _waiter.TrySetResult();
            }
        }

        public void TracePresented(long token, string kind, long assignedTimestamp) { }

        public Task WaitForPresentedAsync(int count)
        {
            lock (_gate)
            {
                if (Presented.Count >= count) return Task.CompletedTask;
                _waitFor = count;
                _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class RecordingPreload : IPreloadController
    {
        public List<string> Events { get; } = [];
        public Task PreloadAroundAsync(int center)
        {
            Events.Add("preload:" + center);
            return Task.CompletedTask;
        }

        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() => Events.Add("cancel");
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    /// <summary>Answers at once, except the folder a test <see cref="Hold"/>s: that snapshot completes when the test says so.</summary>
    private sealed class HoldingOrderProvider : IExplorerOrderProvider
    {
        private readonly Dictionary<string, TaskCompletionSource<ExplorerViewSnapshot>> _held = new(StringComparer.OrdinalIgnoreCase);

        // Keyed by the folder's own name: the coordinator may hand over a normalized spelling of the same folder.
        private static string Key(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

        public TaskCompletionSource<ExplorerViewSnapshot> Hold(string folder)
        {
            var source = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held[Key(folder)] = source;
            return source;
        }

        public static ExplorerViewSnapshot Unavailable(string folder) =>
            new(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16, CancellationToken cancellationToken = default) =>
            _held.TryGetValue(Key(folder), out var source) ? source.Task : Task.FromResult(Unavailable(folder));

        public void Dispose() { }
    }

    private sealed class ForwardingSink(Func<IFolderLoadSink> targetProvider) : IFolderLoadSink
    {
        public void ResetCaches() => targetProvider().ResetCaches();
        public void OnCatalogReady(string folder, int count, SessionState session) => targetProvider().OnCatalogReady(folder, count, session);
        public Task PresentAsync(int index, long presentationGeneration) => targetProvider().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder, SessionState session) => targetProvider().OnEmpty(folder, session);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => targetProvider().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => targetProvider().OnFailed(folder, exception);
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => targetProvider().OnUnreadableRemovedAsync(removedPaths, currentRemoved);
    }

    private sealed class UnusedBin : IRecycleBin
    {
        public bool CanRecycle(string path) => true;
        public bool FitsInRecycleBin(string path, long fileSize) => true;
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public void DeletePermanently(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class QuietDialog : IDialogService
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
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }

        public Func<RenamePromptRequest, string?>? OnPromptRename { get; set; }

        public int PromptCount { get; private set; }

        public string? PromptRename(RenamePromptRequest request)
        {
            PromptCount++;
            return OnPromptRename?.Invoke(request);
        }
    }
}
