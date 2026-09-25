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
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

[Trait("Category", "HotPath")]
public sealed partial class ImagePresenterTests : IDisposable
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

    [Fact(DisplayName = "A run of missing files is skipped iteratively: one extra present, not one nested present per file (R2-F-07)")]
    public async Task PresentAsync_WhenManyConsecutiveFilesMissing_SkipsThemWithoutNestedPresents()
    {
        var paths = new List<string>();
        for (var i = 0; i < 200; i++) paths.Add(Path.Combine(_tempDir, $"gone{i}.jpg"));
        var survivor = CreateFakeImageFile("survivor.jpg");
        paths.Add(survivor);
        _catalog.Reset(paths);

        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);

        Assert.Equal([survivor], _catalog.Paths);
        Assert.Equal(0, _catalog.CurrentIndex);
        // The first present plus exactly one for the survivor; a recursive skip would notify once per missing file.
        Assert.Equal(2, _preloadController.NotifyNavigationCalls.Count);
    }

    [Fact(DisplayName = "R7-1: a photo rewritten after the folder scan is shown as it is now, not from the stale cache key")]
    public async Task PresentAsync_WhenFileRewrittenAfterScan_PresentsNewContentAndRefreshesCatalog()
    {
        var path = Path.Combine(_tempDir, "edited.png");
        File.WriteAllBytes(path, CreatePng(1, 1));
        var scanTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, scanTime);
        var scanned = new FileInfo(path);
        _catalog.Reset([new CatalogEntry(path).WithMetadata(scanned.Length, scanned.LastWriteTimeUtc)]);

        var presenter = CreatePresenter();
        await presenter.PresentAsync(0);
        Assert.Equal(1, presenter.CurrentOriginalWidth);

        // Edited in another app: different size and mtime, catalog still holds the scan's metadata.
        File.WriteAllBytes(path, CreatePng(3, 2));
        var editTime = scanTime.AddHours(1);
        File.SetLastWriteTimeUtc(path, editTime);
        var edited = new FileInfo(path);
        Assert.NotEqual(scanned.Length, edited.Length);

        await presenter.PresentAsync(0);

        Assert.Equal(3, presenter.CurrentOriginalWidth);
        Assert.Equal(2, presenter.CurrentOriginalHeight);
        var entry = _catalog.Find(path);
        Assert.NotNull(entry);
        Assert.Equal(edited.Length, entry!.Length);
        Assert.Equal(editTime, entry.LastWriteUtc);
        // The old version's RAM entry is gone: only the new key is cached for this path.
        Assert.Equal(1, _previewService.CacheCount);
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact(DisplayName = "A decode error with a localized sentence (source changed during decode) is shown in the UI language, not as English exception text")]
    public async Task PresentAsync_WhenSourceChangesDuringDecode_ShowsLocalizedImageError()
    {
        var path = CreateFakeImageFile("racing.png");
        var info = new FileInfo(path);
        _catalog.Reset([new CatalogEntry(path).WithMetadata(info.Length, info.LastWriteTimeUtc)]);
        var presenter = CreatePresenter();

        // Another program keeps rewriting the file: the key taken before the decode never matches afterwards.
        using (new FileToucher(path))
        {
            await presenter.PresentAsync(0);
        }

        Assert.Equal($"Lỗi ảnh: racing.png — Tệp ảnh đã thay đổi trong lúc giải mã: {path}", presenter.StatusText);
        Assert.DoesNotContain("changed during decode", presenter.StatusText, StringComparison.Ordinal);
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

    [Fact(DisplayName = "LastPresentStartedFromRam reports whether a present found its preview in RAM (the probe's language-independent loading check)")]
    public async Task PresentAsync_LastPresentStartedFromRam_TracksRamHit()
    {
        var f1 = CreateFakeImageFile("state1.jpg");
        var f2 = CreateFakeImageFile("state2.jpg");
        _catalog.Reset([f1, f2]);
        await _previewService.GetPreviewAsync(f2);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0); // cold: shows the loading status first
        Assert.False(presenter.LastPresentStartedFromRam);

        await presenter.PresentAsync(1); // warm: shown straight from RAM
        Assert.True(presenter.LastPresentStartedFromRam);

        _previewService.EvictCachedPath(f1, _ => { });
        await presenter.PresentAsync(0); // cold again: the previous present's true must not leak
        Assert.False(presenter.LastPresentStartedFromRam);
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

    [Fact(DisplayName = "A superseded navigation whose preview decode later fails leaves no unobserved task exception (R2-F-29)")]
    public async Task PresentAsync_SupersededNavigation_ObservesLaterPreviewFault()
    {
        var f1 = CreateFakeImageFile("fault-after-supersede.jpg");
        _catalog.Reset([f1]);

        using var decoder = new FaultingGatedDecoder("marker-r2-f29");
        var previewService = CreatePreviewService(decoder);
        // The thumbnail wins the race and, while it is being read, a newer navigation supersedes this one.
        using var thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "supersede-thumbs"),
            persistNewThumbnails: false,
            embeddedThumbnailReader: (_, _) =>
            {
                _clock.NextNavigation();
                return Task.FromResult<IDecodedImage?>(new FakeDecodedImage { PixelWidth = 160 });
            });
        var presenter = CreatePresenterWithServices(previewService, thumbnailCache);

        var unobserved = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(decoder.Marker, StringComparison.Ordinal)))
                unobserved.Enqueue(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(f1, _sink.PresentedPaths); // it really was superseded

            decoder.ReleaseAndThrow();
            await decoder.Threw.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(
                () => !previewService.HasInflightPreview(previewService.GetCurrentCacheKey(f1)), TimeSpan.FromSeconds(10)));

            // Unobserved-exception reporting happens when the faulted task is finalized.
            for (var i = 0; i < 20 && unobserved.IsEmpty; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>Decoder that blocks until released and then throws, signalling <see cref="Threw"/> just before it does.</summary>
    private sealed class FaultingGatedDecoder(string marker) : IImageDecoder, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(0, 1);
        private readonly TaskCompletionSource _threw = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Marker { get; } = marker;
        public Task Threw => _threw.Task;
        public void ReleaseAndThrow() => _gate.Release();

        public IDecodedImage Decode(DecodeRequest request)
        {
            _gate.Wait();
            _threw.TrySetResult();
            throw new InvalidOperationException(Marker);
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
        public void Dispose() => _gate.Dispose();
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
