using System;
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
using PhotoReview.Core.Caching;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

[Trait("Category", "HotPath")]
public sealed class ImagePresenterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly ReviewMetrics _metrics;
    private readonly CompareViewModel _compareViewModel;
    private readonly TestPresentationSink _sink;
    private readonly TestPreloadController _preloadController;
    private readonly PreviewStateContext _previewContext;
    private readonly PreviewImageService _previewService;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly FileHashService _hashService;
    private readonly SessionStore _sessionStore;
    private AppSettings _settings;

    public ImagePresenterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_PresenterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _catalog = new ReviewCatalog();
        _clock = new GenerationClock();
        _metrics = new ReviewMetrics();
        _compareViewModel = new CompareViewModel();
        _sink = new TestPresentationSink();
        _preloadController = new TestPreloadController();
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
        _sessionStore = new SessionStore(new AppPaths(_tempDir), new PhysicalFileSystem());
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

    private static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
        0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    private string CreateFakeImageFile(string name, int? dummySize = null)
    {
        var filePath = Path.Combine(_tempDir, name);
        File.WriteAllBytes(filePath, ValidPngBytes);
        return filePath;
    }

    private ImagePresenter CreatePresenter(SessionState? session = null, Action<string>? onPresented = null, SessionWriter? sessionWriter = null)
    {
        return new ImagePresenter(
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
            fileSystem: null,
            getSession: () => session,
            onPresentedHook: onPresented,
            sessionWriter: sessionWriter);
    }

    [Fact]
    public async Task PresentAsync_WhenFileMissing_RemovesFromCatalogAndAdvances()
    {
        var f1 = Path.Combine(_tempDir, "missing1.jpg");
        var f2 = CreateFakeImageFile("exists2.jpg", 2048);
        var f3 = CreateFakeImageFile("exists3.jpg", 4096);
        _catalog.Reset([f1, f2, f3]);

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        Assert.Equal(2, _catalog.Count);
        Assert.DoesNotContain(f1, _catalog.Paths);
        Assert.Equal(f2, _catalog.Paths[0]);
        Assert.Equal(0, _catalog.CurrentIndex);
    }

    [Fact]
    public async Task PresentAsync_WhenAllFilesMissing_EmitsNoImagesRemaining()
    {
        var f1 = Path.Combine(_tempDir, "missing1.jpg");
        _catalog.Reset([f1]);

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        Assert.Equal(0, _catalog.Count);
        Assert.Equal("Không còn ảnh trong thư mục", presenter.StatusText);
        Assert.Null(presenter.CurrentImage);
    }

    [Fact]
    public async Task PresentAsync_WhenComparePairExists_ActivatesCompareViewModel()
    {
        var f1 = CreateFakeImageFile("photo.jpg", 1000);
        var f2 = CreateFakeImageFile("photo (1).jpg", 2000);
        _catalog.Reset([f1, f2]);

        _settings.CompareSizeEnabled = true;
        _settings.CompareHashEnabled = false;

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        Assert.True(presenter.IsCompareVisible);
        Assert.True(_compareViewModel.IsVisible);
        Assert.Equal(f1, _compareViewModel.LeftPath);
        Assert.Equal(f2, _compareViewModel.RightPath);
        Assert.Contains("So sánh", presenter.StatusText);
    }

    [Fact]
    public async Task PresentAsync_WhenCompareIsSuppressed_ClearsCompareAndKeepsMainImage()
    {
        var f1 = CreateFakeImageFile("photo.jpg");
        var f2 = CreateFakeImageFile("photo (1).jpg");
        _catalog.Reset([f1, f2]);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);
        Assert.True(_compareViewModel.IsVisible);

        await presenter.PresentAsync(0, allowCompare: false);

        Assert.False(_compareViewModel.IsVisible);
        Assert.Null(_compareViewModel.LeftPath);
        Assert.NotNull(presenter.CurrentImage);
    }

    [Fact]
    public async Task ClearPresentation_ClearsCurrentImageAndCompareState()
    {
        var f1 = CreateFakeImageFile("photo.jpg");
        var f2 = CreateFakeImageFile("photo (1).jpg");
        _catalog.Reset([f1, f2]);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);
        presenter.ClearPresentation();

        Assert.Null(presenter.CurrentImage);
        Assert.False(_compareViewModel.IsVisible);
        Assert.Null(_compareViewModel.LeftImage);
        Assert.Null(_compareViewModel.RightImage);
        Assert.Null(_sink.CurrentImage);
    }

    [Fact]
    public async Task PresentAsync_WhenSingleImage_DisplaysFormattedStatusWithDimensions()
    {
        var f1 = CreateFakeImageFile("single.jpg", 1024);
        _catalog.Reset([f1]);

        var presentedPath = string.Empty;
        var presenter = CreatePresenter(onPresented: p => presentedPath = p);

        await presenter.PresentAsync(0);

        Assert.False(presenter.IsCompareVisible);
        Assert.Equal(f1, presentedPath);
        Assert.Contains("1/1", presenter.StatusText);
        Assert.Contains("single.jpg", presenter.StatusText);
        Assert.True(_sink.InitialViewModeAppliedCount >= 1);
    }

    [Fact]
    public async Task PresentAsync_WhenSessionActive_UpdatesAndSavesSessionCurrentPath()
    {
        var f1 = CreateFakeImageFile("item1.jpg", 1024);
        _catalog.Reset([f1]);

        var session = new SessionState { Folder = _tempDir, CurrentPath = "" };
        var presenter = CreatePresenter(session: session);

        await presenter.PresentAsync(0);

        Assert.Equal(f1, session.CurrentPath);
        var reloaded = _sessionStore.Load(_tempDir);
        Assert.NotNull(reloaded);
        Assert.Equal(f1, reloaded.CurrentPath);
    }

    [Fact(DisplayName = "With a SessionWriter the session is debounced, not saved per presented image, and Flush persists it")]
    public async Task PresentAsync_WithSessionWriter_DefersSessionWriteUntilFlush()
    {
        var f1 = CreateFakeImageFile("item1.jpg", 1024);
        var f2 = CreateFakeImageFile("item2.jpg", 1024);
        _catalog.Reset([f1, f2]);
        var session = new SessionState { Folder = _tempDir, CurrentPath = "" };
        // TC09: AUDIT - Replace with barrier (TaskCompletionSource) or IUiScheduler clock fake
        using var writer = new SessionWriter(_sessionStore, delay: (_, token) => Task.Delay(Timeout.Infinite, token));
        var presenter = CreatePresenter(session: session, sessionWriter: writer);

        await presenter.PresentAsync(0);
        await presenter.PresentAsync(1);

        Assert.Equal("", _sessionStore.Load(_tempDir).CurrentPath ?? "");
        writer.Flush();
        Assert.Equal(f2, _sessionStore.Load(_tempDir).CurrentPath);
    }

    [Fact]
    public async Task PresentAsync_PreloadController_PreloadAroundTriggered()
    {
        var f1 = CreateFakeImageFile("img1.jpg", 1024);
        var f2 = CreateFakeImageFile("img2.jpg", 1024);
        _catalog.Reset([f1, f2]);

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        Assert.Contains(0, _preloadController.PreloadAroundCalls);
    }

    [Fact]
    public async Task PresentAsync_WhenRamHit_DirectlyPresentsCachedImageAndConsumesPreloadedKey()
    {
        var f1 = CreateFakeImageFile("ram1.jpg");
        _catalog.Reset([f1]);

        // Pre-warm preview in PreviewImageService
        var initialPreview = await _previewService.GetPreviewAsync(f1);
        var key = _previewService.GetCurrentCacheKey(f1);
        _preloadController.WarmedKeys.Add(key);

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        // Preload hit recorded
        Assert.Equal(1, _metrics.Snapshot().PreloadHits);
        Assert.Contains(f1, _sink.PresentedPaths);
        Assert.NotNull(presenter.CurrentImage);
    }

    [Fact]
    public async Task PresentAsync_TokenSuperseded_AbortsUiUpdates_PreservingInv1()
    {
        var f1 = CreateFakeImageFile("slow1.jpg");
        var f2 = CreateFakeImageFile("fast2.jpg");
        _catalog.Reset([f1, f2]);

        var presenter = CreatePresenter();

        // Simulate fast navigation: user starts navigating to 0, but immediate next navigation triggers
        var task1 = presenter.PresentAsync(0);
        // Interleaved immediate navigation:
        _clock.NextNavigation();

        await task1;

        // Current navigation generation has moved ahead, so task1 cannot claim current image
        Assert.NotEqual(1, _clock.CurrentNavigation);
    }

    // perf(open) task 1: ImagePresenter races the thumbnail against the preview instead of
    // awaiting the thumbnail first. These two tests use fake/controllable services (a gated
    // decoder for the preview, a directly-controllable reader for the thumbnail) instead of real
    // file decodes, so the race outcome is deterministic rather than timing-dependent.

    [Fact(DisplayName = "When the preview finishes before the thumbnail, PresentAsync shows the preview directly and never shows a thumbnail")]
    public async Task PresentAsync_WhenPreviewFasterThanThumbnail_ShowsPreviewDirectlyAndSkipsThumbnail()
    {
        var f1 = CreateFakeImageFile("preview-wins.jpg");
        _catalog.Reset([f1]);

        var previewImage = new FakeDecodedImage { PixelWidth = 1920 };
        // A fast, un-gated decoder: GetPreviewAsync resolves almost immediately.
        var previewService = CreatePreviewService(new ImmediateDecoder(previewImage));

        // A thumbnail reader whose task never completes during this test: it cannot possibly
        // win the race against the preview, so if the preview is ever shown it's because the
        // race logic picked it, not because the thumbnail was slow "by luck".
        var neverCompletes = new TaskCompletionSource<IDecodedImage?>();
        using var thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "never-thumbs"),
            persistNewThumbnails: false,
            embeddedThumbnailReader: (_, _) => neverCompletes.Task);

        var presenter = CreatePresenterWithServices(previewService, thumbnailCache);

        // The thumbnail task never completes (see neverCompletes above), so this can only
        // finish -- inside the timeout -- if PresentAsync truly never awaits it before the
        // preview; under the old "always await the thumbnail first" behavior this would hang.
        await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(previewImage.PlatformImage, presenter.CurrentImage);
    }

    [Fact(DisplayName = "When the thumbnail finishes before the preview, it is shown first and then replaced once the preview completes")]
    public async Task PresentAsync_WhenThumbnailFasterThanPreview_ShowsThumbnailThenReplacesWithPreview()
    {
        var f1 = CreateFakeImageFile("thumbnail-wins.jpg");
        _catalog.Reset([f1]);

        var thumbnailImage = new FakeDecodedImage { PixelWidth = 160 };
        var previewImage = new FakeDecodedImage { PixelWidth = 1920 };

        // Gated: GetPreviewAsync's decode blocks on a worker thread until the test releases it,
        // so the thumbnail (which resolves immediately below) is guaranteed to win the race.
        using var gatedDecoder = new GatedDecoder(previewImage);
        var previewService = CreatePreviewService(gatedDecoder);

        using var thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "fast-thumbs"),
            persistNewThumbnails: false,
            embeddedThumbnailReader: (_, _) => Task.FromResult<IDecodedImage?>(thumbnailImage));

        var thumbnailShown = new TaskCompletionSource<bool>();
        _sink.OnSetCurrentImage = img =>
        {
            if (ReferenceEquals(img, thumbnailImage.PlatformImage)) thumbnailShown.TrySetResult(true);
        };

        var presenter = CreatePresenterWithServices(previewService, thumbnailCache);

        var presentTask = presenter.PresentAsync(0);

        await thumbnailShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(thumbnailImage.PlatformImage, presenter.CurrentImage);

        // Now let the preview finish; PresentAsync must replace the thumbnail with it.
        gatedDecoder.Release();
        await presentTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(previewImage.PlatformImage, presenter.CurrentImage);
    }

    [Fact(DisplayName = "perf(preload): every navigation notifies preload before its own decode, and preload is still kicked after present")]
    public async Task PresentAsync_NotifiesPreloadOfEachNavigation()
    {
        var f1 = CreateFakeImageFile("nav1.jpg");
        var f2 = CreateFakeImageFile("nav2.jpg");
        _catalog.Reset([f1, f2]);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);
        await presenter.PresentAsync(1);

        Assert.Equal([0, 1], _preloadController.NotifyNavigationCalls);
        Assert.Equal([0, 1], _preloadController.PreloadAroundCalls);
    }

    [Fact(DisplayName = "perf(preload): a superseded navigation's not-yet-started viewer decode is dropped, not decoded, and reports no error")]
    public async Task PresentAsync_SupersededNavigation_DropsPendingViewerDecode()
    {
        var f1 = CreateFakeImageFile("superseded.jpg");
        var f2 = CreateFakeImageFile("current.jpg");
        _catalog.Reset([f1, f2]);
        var decoder = new RecordingDecoder();
        var previewService = CreatePreviewService(decoder);
        var presenter = CreatePresenterWithServices(previewService, _thumbnailCache);

        _preloadController.ViewerDecodeDelay = TimeSpan.FromSeconds(30); // burst: first nav waits before decoding
        var first = presenter.PresentAsync(0);
        _preloadController.ViewerDecodeDelay = TimeSpan.Zero;
        var second = presenter.PresentAsync(1);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([f2], decoder.Paths);
        Assert.Contains(f2, _sink.PresentedPaths);
        Assert.DoesNotContain(f1, _sink.PresentedPaths);
        Assert.DoesNotContain(_sink.Statuses, s => s.Contains(Path.GetFileName(f1), StringComparison.Ordinal));
        Assert.False(previewService.HasInflightPreview(previewService.GetCurrentCacheKey(f1)));
    }

    private PreviewImageService CreatePreviewService(IImageDecoder decoder) => new(
        _metrics,
        () => _previewContext.IsOriginalLoadingMode(),
        () => _previewContext.TargetDecodeBox(),
        capacityBytes: 64 * 1024 * 1024,
        decoder: decoder,
        currentBackend: () => _previewContext.CurrentBackend(),
        disableDiskCacheOverride: true);

    private ImagePresenter CreatePresenterWithServices(PreviewImageService previewService, ThumbnailCache thumbnailCache) => new(
        _catalog,
        _clock,
        previewService,
        thumbnailCache,
        _preloadController,
        _compareViewModel,
        _hashService,
        _metrics,
        () => _settings,
        _sessionStore,
        _sink,
        fileSystem: null);

    /// <summary>Decoder that returns a canned image immediately (used for the "preview wins" race test).</summary>
    private sealed class ImmediateDecoder(IDecodedImage image) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => image;
        public ImageInfo ReadInfo(string path) => new(image.PixelWidth, image.PixelHeight, image.Orientation);
    }

    /// <summary>Decoder that records every path it decodes.</summary>
    private sealed class RecordingDecoder : IImageDecoder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _paths = new();
        public IReadOnlyCollection<string> Paths => _paths;

        public IDecodedImage Decode(DecodeRequest request)
        {
            _paths.Enqueue(request.Path);
            return new FakeDecodedImage();
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
    }

    /// <summary>Decoder whose Decode() blocks until the test calls Release() (used for the "thumbnail wins" race test).</summary>
    private sealed class GatedDecoder(IDecodedImage image) : IImageDecoder, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(0, 1);

        public void Release() => _gate.Release();

        public IDecodedImage Decode(DecodeRequest request)
        {
            _gate.Wait();
            return image;
        }

        public ImageInfo ReadInfo(string path) => new(image.PixelWidth, image.PixelHeight, image.Orientation);

        public void Dispose() => _gate.Dispose();
    }

    /// <summary>Minimal fake <see cref="IDecodedImage"/> for tests that only care about identity/ordering, not real pixels.</summary>
    private sealed class FakeDecodedImage : IDecodedImage
    {
        public int PixelWidth { get; init; } = 1;
        public int PixelHeight { get; init; } = 1;
        public bool Downscaled { get; init; }
        public int Orientation { get; init; } = 1;
        public long EstimatedBytes { get; init; } = 1;
        public object PlatformImage { get; } = new();
    }

    private sealed class TestPresentationSink : IPresentationSink
    {
        public List<object?> Images { get; } = [];
        public object? CurrentImage { get; private set; }
        public List<string> Statuses { get; } = [];
        public int InitialViewModeAppliedCount { get; private set; }
        public List<string> PresentedPaths { get; } = [];
        public List<string> TracedKinds { get; } = [];
        public Action<object?>? OnSetCurrentImage { get; set; }

        public void SetCurrentImage(object? image)
        {
            CurrentImage = image;
            Images.Add(image);
            OnSetCurrentImage?.Invoke(image);
        }
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ApplyInitialViewMode() => InitialViewModeAppliedCount++;
        public void OnPresented(string path) => PresentedPaths.Add(path);
        public void TracePresented(long token, string kind, long assignedTimestamp) => TracedKinds.Add(kind);
    }

    private sealed class TestPreloadController : IPreloadController
    {
        public List<int> PreloadAroundCalls { get; } = [];
        public List<int> NotifyNavigationCalls { get; } = [];
        public HashSet<ImageCacheKey> WarmedKeys { get; } = [];
        public TimeSpan ViewerDecodeDelay { get; set; }

        public void NotifyNavigation(int index) => NotifyNavigationCalls.Add(index);

        public TimeSpan GetViewerDecodeDelay() => ViewerDecodeDelay;

        public Task PreloadAroundAsync(int center)
        {
            PreloadAroundCalls.Add(center);
            return Task.CompletedTask;
        }

        public bool TryConsumePreloadedKey(ImageCacheKey key)
        {
            return WarmedKeys.Remove(key);
        }

        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }
}
