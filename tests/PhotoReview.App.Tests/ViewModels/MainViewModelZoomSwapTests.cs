using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// R4 (ADR 0008 amendment): the REAL <see cref="MainViewModel"/> + <see cref="ImagePresenter"/> + <see cref="ZoomDetailLoader"/>
/// wired the way production wires them (the presentation sink feeds <see cref="MainViewModel.NotifyCurrentImageChanged"/>).
/// A decoded original whose pixel size differs from the preview size must go through <see cref="ViewerState.SwapSourceSize"/>
/// (zoom kept, <see cref="ViewerState.SourceSizeSwapping"/> before the size change); a new image must go through
/// <see cref="ViewerState.SetSourceSize"/> and never raise the swapping event.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class MainViewModelZoomSwapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ReviewCatalog _catalog = new();
    private readonly ViewerState _viewer = new();
    private readonly ThumbnailCache _thumbnailCache;
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly AppSettings _settings = new() { LoadingMode = LoadingMode.Preview, Actions = ReviewAction.Defaults() };

    public MainViewModelZoomSwapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_MainVM_ZoomSwap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var appPaths = new AppPaths(_tempDir);
        var fileSystem = new PhysicalFileSystem();
        _sessionStore = new SessionStore(appPaths, fileSystem);
        _settingsStore = new SettingsStore(appPaths, fileSystem, new NullLog());
        _settingsStore.Save(_settings);
        _thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "thumbs"), maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);
    }

    public void Dispose()
    {
        _thumbnailCache.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // Preview 6720x4480 (camera-visible) vs LibRaw 6744x4502 (larger) and 1 px smaller; then the next image.
    [Theory]
    [InlineData(6744, 4502, 2.0)]
    [InlineData(6719, 4479, 1.0)]
    public async Task DecodedOriginalArriving_WhileZoomed_SwapsTheSourceSizeThroughTheViewModel_AndTheNextImageDoesNot(
        int decodedWidth, int decodedHeight, double zoom)
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6720, 4480, decodedWidth, decodedHeight);
        decoder.Sizes["b.jpg"] = (6000, 4000, 6000, 4000);
        var vm = CreateViewModel(decoder, "a.jpg", "b.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            Assert.Equal((6720, 4480), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));

            var swappingSeenWidths = new List<int>();
            _viewer.SourceSizeSwapping += (_, _) => swappingSeenWidths.Add(_viewer.SourcePixelWidth);
            _viewer.SetZoom(zoom); // leaves Fit: the ViewModel asks the presenter for the full-resolution decode
            var load = vm.Presenter.ZoomDetail.PendingLoad;
            Assert.NotNull(load);
            Assert.Empty(swappingSeenWidths);

            decoder.OriginalGate.Release();
            await load!;

            // (1) raised exactly once, while the layout still had the preview size
            Assert.Equal([6720], swappingSeenWidths);
            // (2) the decoded original own size is now the source size; the user zoom factor is untouched
            Assert.Equal((decodedWidth, decodedHeight), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
            Assert.Equal(zoom, _viewer.Zoom, 9);
            Assert.False(_viewer.IsFit);

            // (3) the next image is a new image: SetSourceSize path, no swapping event
            await vm.NextAsync();
            Assert.Equal((6000, 4000), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
            Assert.Single(swappingSeenWidths);
        }
        finally
        {
            decoder.OriginalGate.Release();
            decoder.NextGate.Release();
        }
    }

    [Fact]
    public async Task Navigating_AfterFitWidthWithKeepZoom_ForgetsThePreviousImagesFitAxis()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        decoder.Sizes["b.jpg"] = (6000, 4000, 6000, 4000); // same size as A: only the new-image flag can tell them apart
        var vm = CreateViewModel(decoder, "a.jpg", "b.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.DpiScale = 1.0;
            _viewer.ApplyInitialViewMode(InitialViewMode.FitWidth, 1200, 800);
            var fitWidthZoom = _viewer.Zoom;
            Assert.Equal(1200.0 / 6000, fitWidthZoom, 6);

            await vm.NextAsync(); // KeepZoomAcrossImages: the zoom is kept, no ApplyInitialViewMode
            _viewer.SwapSourceSize(6016, 4016); // B's original arrives

            Assert.Equal(fitWidthZoom, _viewer.Zoom, 9); // not refitted against A's viewport
        }
        finally
        {
            decoder.OriginalGate.Release();
            decoder.NextGate.Release();
        }
    }

    [Fact]
    public async Task PresentingAnotherImage_WhileCompareIsVisible_ForgetsThePreviousImagesFitAxis()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        decoder.Sizes["b.jpg"] = (6000, 4000, 6000, 4000); // same size as A: only a current-path change can tell them apart
        var vm = CreateViewModel(decoder, "a.jpg", "b.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.DpiScale = 1.0;
            _viewer.ApplyInitialViewMode(InitialViewMode.FitWidth, 1200, 800);
            var fitWidthZoom = _viewer.Zoom;
            vm.Compare.IsVisible = true; // no fade is played while Compare is shown, although B is a new image

            await vm.Presenter.PresentAsync(1, allowCompare: false);
            _viewer.SwapSourceSize(6016, 4016); // B's original arrives

            Assert.Equal(fitWidthZoom, _viewer.Zoom, 9); // not refitted against A's viewport
        }
        finally
        {
            decoder.OriginalGate.Release();
            decoder.NextGate.Release();
        }
    }

    private IImageDecoder? _rawFullDecoder;
    private readonly ScriptedDialogService _dialog = new();

    // ---- RawFullDecode setting changes apply to the open image (items 1-2) ---------------------

    private (MainViewModel Vm, RawEmbeddedDecoder Embedded, RawFullDecoderFake Full) CreateRawViewModel(LoadingMode mode, RawFullDecode setting)
    {
        _settings.LoadingMode = mode;
        _settings.RawFullDecode = setting;
        _settingsStore.Save(_settings);
        var embedded = new RawEmbeddedDecoder();
        var full = new RawFullDecoderFake();
        _rawFullDecoder = full;
        return (CreateViewModel(embedded, "raw.cr2"), embedded, full);
    }

    [Fact]
    public async Task ShowSettings_RawFullDecodeNeverToOnZoom_OriginalMode_ZoomStartsOneFullDecode()
    {
        var (vm, _, full) = CreateRawViewModel(LoadingMode.Original, RawFullDecode.Never);
        await vm.Presenter.PresentAsync(0);
        _viewer.SetZoom(1.0);
        Assert.Null(vm.Presenter.ZoomDetail.PendingLoad); // Never: nothing armed

        _dialog.OnShowSettings = () => { _settings.RawFullDecode = RawFullDecode.OnZoom; _settingsStore.Save(_settings); };
        vm.ShowSettings();
        await vm.SettingsRefreshTask;
        _viewer.ResetFit(1280, 720);
        _viewer.SetZoom(1.0);
        var load = vm.Presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);
        full.Gate.Release();
        await load!;

        Assert.Equal(1, full.Decodes);
        Assert.NotNull(vm.Presenter.ZoomDetail.HeldOriginal);
    }

    [Fact]
    public async Task ShowSettings_RawFullDecodeOnZoomToNever_DropsHeldOriginalAndNeverDecodesOrSwapsAgain()
    {
        var (vm, _, full) = CreateRawViewModel(LoadingMode.Preview, RawFullDecode.OnZoom);
        await vm.Presenter.PresentAsync(0);
        _viewer.SetZoom(1.0);
        var firstLoad = vm.Presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(firstLoad);
        full.Gate.Release();
        await firstLoad!;
        Assert.NotNull(vm.Presenter.ZoomDetail.HeldOriginal);
        Assert.Equal(1, full.Decodes);

        _dialog.OnShowSettings = () => { _settings.RawFullDecode = RawFullDecode.Never; _settingsStore.Save(_settings); };
        vm.ShowSettings();
        await vm.SettingsRefreshTask;
        var swaps = 0;
        _viewer.SourceSizeSwapping += (_, _) => swaps++;
        _viewer.SetZoom(1.0);
        _viewer.SetZoom(2.0);

        Assert.Null(vm.Presenter.ZoomDetail.HeldOriginal);
        Assert.False(vm.Presenter.ZoomDetail.IsShowingOriginal);
        Assert.Null(vm.Presenter.ZoomDetail.PendingLoad);
        Assert.Equal(1, full.Decodes);
        Assert.Equal(0, swaps);
    }

    [Fact]
    public async Task ShowSettings_RawFullDecodeOnZoomToNever_WhileTargetArmedButNotDecoded_DropsTheTarget()
    {
        var (vm, _, full) = CreateRawViewModel(LoadingMode.Preview, RawFullDecode.OnZoom);
        await vm.Presenter.PresentAsync(0); // target armed, still at Fit

        _dialog.OnShowSettings = () => { _settings.RawFullDecode = RawFullDecode.Never; _settingsStore.Save(_settings); };
        vm.ShowSettings();
        await vm.SettingsRefreshTask;
        _viewer.SetZoom(1.0);

        Assert.Null(vm.Presenter.ZoomDetail.PendingLoad);
        Assert.Equal(0, full.Decodes);
    }

    [Theory]
    [InlineData(LoadingMode.Preview)]
    [InlineData(LoadingMode.Original)]
    public async Task RawZoom_FullDecodeNever_NeverDecodesNorSwapsTheSourceSize(LoadingMode mode)
    {
        var (vm, embedded, full) = CreateRawViewModel(mode, RawFullDecode.Never);
        await vm.Presenter.PresentAsync(0);
        var decodesAfterPresent = embedded.Decodes;
        var swaps = 0;
        _viewer.SourceSizeSwapping += (_, _) => swaps++;
        var size = (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight);

        _viewer.SetZoom(1.0);

        Assert.Null(vm.Presenter.ZoomDetail.PendingLoad);
        Assert.Equal(0, full.Decodes);
        Assert.Equal(decodesAfterPresent, embedded.Decodes); // no wasted re-decode of the embedded JPEG
        Assert.Equal(0, swaps);
        Assert.Equal((6000, 4000), size);
        Assert.Equal(size, (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight)); // layout unchanged
    }

    [Fact]
    public async Task RawZoom_PreviewModeFullDecodeOnZoom_ArmsAndSwapsTheSourceSizeOnce()
    {
        var (vm, _, full) = CreateRawViewModel(LoadingMode.Preview, RawFullDecode.OnZoom);
        await vm.Presenter.PresentAsync(0);
        var swapWidths = new List<int>();
        _viewer.SourceSizeSwapping += (_, _) => swapWidths.Add(_viewer.SourcePixelWidth);

        _viewer.SetZoom(1.0);
        var load = vm.Presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);
        full.Gate.Release();
        await load!;

        Assert.Equal(1, full.Decodes);
        Assert.Equal([6000], swapWidths);
        Assert.Equal((6016, 4016), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
    }

    /// <summary>A RAW's ordinary decode: always the 2000 px embedded JPEG of a 6000x4000 sensor, whatever the box.</summary>
    private sealed class RawEmbeddedDecoder : IImageDecoder
    {
        private int _decodes;
        public int Decodes => Volatile.Read(ref _decodes);
        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _decodes);
            return new SizedImage(2000, 1333, 6000, 4000, downscaled: true);
        }
        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    /// <summary>The RAW full decoder: 6016x4016 (LibRaw sensor area, differs from the camera-visible size).</summary>
    private sealed class RawFullDecoderFake : IImageDecoder
    {
        private int _decodes;
        public int Decodes => Volatile.Read(ref _decodes);
        /// <summary>Holds every decode until released, so the test can observe the in-flight load (an instant decode could finish before PendingLoad is read).</summary>
        public SemaphoreSlim Gate { get; } = new(0);
        public IDecodedImage Decode(DecodeRequest request)
        {
            Gate.Wait();
            Interlocked.Increment(ref _decodes);
            return new SizedImage(6016, 4016, 6016, 4016, downscaled: false);
        }
        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class ScriptedDialogService : IDialogService
    {
        public Action? OnShowSettings { get; set; }
        public bool ShowConfirmation(string title, string message) => false;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() { OnShowSettings?.Invoke(); return OnShowSettings is not null; }
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private MainViewModel CreateViewModel(IImageDecoder decoder, params string[] names)
    {
        var paths = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
            paths.Add(path);
        }
        _catalog.Reset(paths);

        var metrics = new ReviewMetrics();
        var clock = new GenerationClock();
        var compare = new CompareViewModel();
        var hashService = new FileHashService();
        var fileSystem = new PhysicalFileSystem();
        var previewService = new PreviewImageService(
            metrics,
            () => _settings.LoadingMode == LoadingMode.Original,
            () => new DecodeBox(1920, 1080),
            capacityBytes: 512L * 1024 * 1024,
            disableDiskCacheOverride: true,
            decoder: decoder,
            currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: _rawFullDecoder,
            isRawFullDecodeEnabled: () => _settings.RawFullDecode == RawFullDecode.OnZoom);
        var preload = new NullPreload();
        var sink = new VmSink();

        MainViewModel? vm = null;
        var presenter = new ImagePresenter(
            _catalog, clock, previewService, _thumbnailCache, preload, compare, hashService, metrics,
            () => _settings, _sessionStore, sink, fileSystem, getSession: () => vm?.Session);
        var coordinator = new FolderLoadCoordinator(
            _catalog, clock, new NoExplorerOrder(), fileSystem, _sessionStore, _settingsStore, new NullFolderSink());
        var appPaths = new AppPaths(_tempDir);
        var journal = new OperationJournal(appPaths, fileSystem, new SystemClock());
        var recycleBin = new NullRecycleBin();
        var fileActions = new FileActionService(journal, fileSystem, new SystemClock(), recycleBin);
        var undo = new UndoService(journal, fileSystem, recycleBin, fileActions);

        vm = new MainViewModel(
            _catalog, clock, coordinator, presenter, _viewer, compare, _settingsStore, _sessionStore, fileSystem,
            fileActions, undo, _dialog, hashService, previewService, _thumbnailCache,
            new SessionWriter(_sessionStore, FileLog.Default), preloadController: preload);

        // Production wiring (MainViewModelCompositionRoot): the sink feeds the real MainViewModel.NotifyCurrentImageChanged.
        sink.OnSetCurrentImage = isFileChange => vm.NotifyCurrentImageChanged(isFileChange);
        return vm;
    }

    /// <summary>Preview = downscaled 600 px wide; unbounded box = the full decode (own size, gated per image).</summary>
    private sealed class SizedDecoder : IImageDecoder
    {
        public Dictionary<string, (int OriginalWidth, int OriginalHeight, int DecodedWidth, int DecodedHeight)> Sizes { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim OriginalGate { get; } = new(0);
        public SemaphoreSlim NextGate { get; } = new(0);

        public IDecodedImage Decode(DecodeRequest request)
        {
            var name = Path.GetFileName(request.Path);
            var (ow, oh, dw, dh) = Sizes[name];
            if (request.Box.IsUnbounded)
            {
                (name == "a.jpg" ? OriginalGate : NextGate).Wait();
                return new SizedImage(dw, dh, dw, dh, downscaled: false);
            }
            return new SizedImage(600, 600 * oh / ow, ow, oh, downscaled: true);
        }

        public ImageInfo ReadInfo(string path)
        {
            var (ow, oh, _, _) = Sizes[Path.GetFileName(path)];
            return new ImageInfo(ow, oh);
        }
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

    private sealed class VmSink : IPresentationSink
    {
        public Action<bool>? OnSetCurrentImage { get; set; }
        public void SetCurrentImage(object? image, bool isFileChange = false) => OnSetCurrentImage?.Invoke(isFileChange);
        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class NullPreload : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    private sealed class NoExplorerOrder : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null, int progressInterval = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    private sealed class NullFolderSink : IFolderLoadSink
    {
        public void ResetCaches() { }
        public void OnCatalogReady(string folder, int count, SessionState session) { }
        public Task PresentAsync(int index, long presentationGeneration) => Task.CompletedTask;
        public void OnEmpty(string folder, SessionState session) { }
        public void OnEmptyWithSubfolders(string folder, SessionState session, int subfolderCount) { }
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) { }
        public void OnFailed(string folder, Exception exception) { }
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => Task.CompletedTask;
    }

    private sealed class NullRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
