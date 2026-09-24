using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
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

/// <summary>
/// feat(zoom) (option A for #43): non-Fit zoom is relative to original pixels and the current image's
/// full-resolution decode is fetched on demand, swapped in without a layout change, dropped on
/// navigation and never kept for more than the current image.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ZoomDetailTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ReviewCatalog _catalog = new();
    private readonly GenerationClock _clock = new();
    private readonly ReviewMetrics _metrics = new();
    private readonly CompareViewModel _compare = new();
    private readonly ViewerState _viewer = new();
    private readonly CurrentOnlySink _sink = new();
    private readonly ThumbnailCache _thumbnailCache;
    private readonly SessionStore _sessionStore;
    private readonly AppSettings _settings = new() { LoadingMode = LoadingMode.Preview };

    public ZoomDetailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_ZoomDetail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _thumbnailCache = new ThumbnailCache(
            diskDirectory: Path.Combine(_tempDir, "thumbs"),
            maxRamBytes: 1024 * 1024,
            persistNewThumbnails: false);
        _sessionStore = new SessionStore(new AppPaths(_tempDir), new PhysicalFileSystem());
    }

    public void Dispose()
    {
        _thumbnailCache.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // ---- zoom size math (ViewerState) ---------------------------------------------------------

    [Theory]
    // landscape 6000x4000
    [InlineData(6000, 4000, 1.0, 1.0, 6000, 4000)]
    [InlineData(6000, 4000, 2.0, 1.0, 12000, 8000)]
    [InlineData(6000, 4000, 1.0, 1.5, 4000, 2666.6666666667)]
    // portrait 4000x6000 (e.g. a 6000x4000 sensor image with EXIF orientation 6/8, already swapped)
    [InlineData(4000, 6000, 1.0, 1.0, 4000, 6000)]
    [InlineData(4000, 6000, 2.0, 1.25, 6400, 9600)]
    [InlineData(4000, 6000, 0.25, 1.0, 1000, 1500)]
    public void ImageSize_AtZoom_IsOriginalPixelsTimesZoomOverDpi(
        int originalWidth, int originalHeight, double zoom, double dpi, double expectedWidth, double expectedHeight)
    {
        var viewer = new ViewerState { DpiScale = dpi };
        viewer.SetSourceSize(originalWidth, originalHeight);

        viewer.SetZoom(zoom);

        Assert.Equal(expectedWidth, viewer.ImageWidth, 6);
        Assert.Equal(expectedHeight, viewer.ImageHeight, 6);
    }

    [Fact]
    public void ImageSize_InFitOrWithUnknownSource_IsAuto()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(6000, 4000);
        viewer.ResetFit(1280, 720);
        Assert.True(double.IsNaN(viewer.ImageWidth));
        Assert.True(double.IsNaN(viewer.ImageHeight));

        var unknown = new ViewerState();
        unknown.SetZoom(2.0);
        Assert.True(double.IsNaN(unknown.ImageWidth));
    }

    [Fact]
    public void ImageSize_DoesNotDependOnDisplayedBitmap_OnlyOnSourceSizeAndZoom()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(4000, 6000);
        viewer.SetZoom(2.0);
        var before = (viewer.ImageWidth, viewer.ImageHeight);

        // Swapping preview -> original re-reports the same source size: no change notification.
        var changed = new List<string?>();
        viewer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewer.SetSourceSize(4000, 6000);

        Assert.Equal(before, (viewer.ImageWidth, viewer.ImageHeight));
        Assert.DoesNotContain(nameof(ViewerState.ImageWidth), changed);
        Assert.DoesNotContain(nameof(ViewerState.ImageHeight), changed);
    }

    [Fact]
    public void ZoomStep_FromFit_StartsAtTheFitZoomInsteadOf100Percent()
    {
        var viewer = new ViewerState { DpiScale = 1.0 };
        viewer.SetSourceSize(6000, 4000);
        viewer.ResetFit(1500, 1000); // fit = 0.25 of the original

        Assert.Equal(0.25, viewer.FitZoom, 6);
        viewer.WheelZoom(120);
        Assert.Equal(0.5, viewer.Zoom, 6);

        viewer.ResetFit(1500, 1000);
        viewer.ZoomIn();
        Assert.Equal(0.5, viewer.Zoom, 6);
    }

    [Fact]
    public void ZoomModeChanged_RaisedOnceWithFinalState()
    {
        var viewer = new ViewerState();
        var seen = new List<double?>();
        viewer.ZoomModeChanged += (_, _) => seen.Add(viewer.EffectiveZoom);

        viewer.SetZoom(2.0);
        viewer.ResetFit(800, 600);
        viewer.ApplyInitialViewMode(InitialViewMode.Percent100, 800, 600);

        Assert.Equal([2.0, null, 1.0], seen);
    }

    [Theory]
    [InlineData(300, 200, 600, 400, 0.5, 0.5)]
    [InlineData(-60, 100, 600, 400, -0.1, 0.25)]
    [InlineData(10, 10, 0, 400, 0.5, 0.5)]
    public void WheelAnchor_IsCarriedAsAFractionOfTheImage(
        double x, double y, double width, double height, double expectedX, double expectedY)
    {
        var point = MainWindowHelpers.NormalizeImagePoint(x, y, width, height);

        Assert.Equal(expectedX, point.X, 6);
        Assert.Equal(expectedY, point.Y, 6);
    }

    // ---- on-demand original decode (ImagePresenter + ZoomDetailLoader) --------------------------

    [Fact]
    public async Task Fit_NeverDecodesTheOriginal()
    {
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg");

        await presenter.PresentAsync(0);

        Assert.Null(presenter.ZoomDetail.PendingLoad);
        Assert.Equal(0, decoder.OriginalDecodes);
        Assert.Null(presenter.ZoomDetail.HeldOriginal);
    }

    [Fact]
    public async Task LeavingFit_ShowsPreviewAtOriginalSize_ThenSwapsInOriginalWithoutLayoutChange()
    {
        var decoder = new SizedDecoder { OriginalGate = new SemaphoreSlim(0) };
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        var preview = _sink.Current;

        _viewer.SetZoom(1.0);

        // Preview still on screen, already laid out at the original's size.
        Assert.Same(preview, _sink.Current);
        Assert.Equal(6000, _viewer.ImageWidth, 6);
        Assert.Equal(4000, _viewer.ImageHeight, 6);
        var load = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);

        var layoutChanges = 0;
        _viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewerState.ImageWidth) or nameof(ViewerState.ImageHeight)) layoutChanges++;
        };
        decoder.OriginalGate.Release();
        await load!;

        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Same(presenter.ZoomDetail.HeldOriginal!.PlatformImage, _sink.Current);
        Assert.Equal(6000, presenter.ZoomDetail.HeldOriginal.PixelWidth);
        Assert.Equal(0, layoutChanges);
        Assert.Equal(6000, _viewer.ImageWidth, 6);
        Assert.Equal(4000, _viewer.ImageHeight, 6);
        Assert.Equal(1, decoder.OriginalDecodes);
    }

    [Fact]
    public async Task OriginalDecode_IsNotAddedToThePreviewRamCache()
    {
        var decoder = new SizedDecoder();
        var (presenter, service) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        var cachedBytes = service.CacheBytes;

        _viewer.SetZoom(2.0);
        await WhenOriginalShownAsync(presenter);

        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Equal(cachedBytes, service.CacheBytes);
    }

    [Fact]
    public async Task BackToFit_ShowsPreview_AndZoomingAgainReusesTheHeldOriginal()
    {
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        var preview = _sink.Current;
        _viewer.SetZoom(2.0);
        await WhenOriginalShownAsync(presenter);

        _viewer.ResetFit(1280, 720);
        Assert.Same(preview, _sink.Current);
        Assert.False(presenter.ZoomDetail.IsShowingOriginal);

        _viewer.SetZoom(4.0);
        Assert.Null(presenter.ZoomDetail.PendingLoad);
        Assert.Same(presenter.ZoomDetail.HeldOriginal!.PlatformImage, _sink.Current);
        Assert.Equal(1, decoder.OriginalDecodes);
    }

    [Fact]
    public async Task ZoomBelowPreviewResolution_DoesNotDecodeTheOriginal()
    {
        // Preview is 1600 px wide for a 6000 px original: 25 % (1500 px on screen) needs no more pixels.
        var decoder = new SizedDecoder { PreviewWidth = 1600 };
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);

        _viewer.SetZoom(0.25);

        Assert.Null(presenter.ZoomDetail.PendingLoad);
        Assert.Equal(0, decoder.OriginalDecodes);
    }

    [Fact]
    public async Task NavigatingAway_IgnoresTheInFlightOriginal_AndFetchesTheNextImagesOriginal()
    {
        var decoder = new SizedDecoder();
        using var gateA = new SemaphoreSlim(0);
        using var gateB = new SemaphoreSlim(0);
        decoder.GateByName["a.jpg"] = gateA;
        decoder.GateByName["b.jpg"] = gateB;
        var (presenter, _) = Create(decoder, "a.jpg", "b.jpg");
        _sink.OnApplyInitialViewMode = () => _viewer.ApplyInitialViewMode(InitialViewMode.Percent200, 1280, 720);
        await presenter.PresentAsync(0);
        var firstLoad = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(firstLoad);

        await presenter.PresentAsync(1);
        var secondPreview = _sink.Current;
        var secondLoad = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(secondLoad);
        Assert.NotSame(firstLoad, secondLoad);
        Assert.Equal(12000, _viewer.ImageWidth, 6); // b's preview at the same original-relative 200 %

        gateA.Release(); // a's original finishes after the navigation
        await firstLoad!;
        Assert.Same(secondPreview, _sink.Current);
        Assert.Null(presenter.ZoomDetail.HeldOriginal);

        gateB.Release();
        await secondLoad!;
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Same(presenter.ZoomDetail.HeldOriginal!.PlatformImage, _sink.Current);
        Assert.Equal(2, decoder.OriginalDecodes);
    }

    [Fact]
    public async Task Navigation_ReleasesTheOriginal_ForGarbageCollection()
    {
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg", "b.jpg");
        _sink.OnApplyInitialViewMode = () => _viewer.ApplyInitialViewMode(InitialViewMode.Fit, 1280, 720);
        await presenter.PresentAsync(0);
        var weakOriginal = await ZoomAndCaptureOriginalAsync(presenter);

        await presenter.PresentAsync(1); // b stays in Fit (initial view mode): nothing new decoded

        Assert.Null(presenter.ZoomDetail.PendingLoad);
        // Without a UI SynchronizationContext the test body runs as a continuation on the pool
        // thread that finished the load; hop off it so no finished frame there still roots the bitmap.
        for (var i = 0; i < 20 && weakOriginal.IsAlive; i++)
        {
            await Task.Delay(10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.Null(presenter.ZoomDetail.HeldOriginal);
        Assert.False(weakOriginal.IsAlive, "The previous image's full-resolution decode is still reachable after navigation.");
    }

    [Fact]
    public async Task OriginalLoadingMode_NeverDecodesAgain()
    {
        _settings.LoadingMode = LoadingMode.Original;
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        var shown = _sink.Current;
        Assert.Equal(1, decoder.OriginalDecodes); // the normal Original-mode present

        _viewer.SetZoom(2.0);

        Assert.Null(presenter.ZoomDetail.PendingLoad);
        Assert.Equal(1, decoder.OriginalDecodes);
        Assert.Same(shown, _sink.Current);
        Assert.Equal(12000, _viewer.ImageWidth, 6);
    }

    [Fact]
    public async Task ExifRotatedSource_UsesPostOrientationOriginalSize()
    {
        // 300x200 stored pixels with orientation 6 (rotate 90): displayed 200 wide, 300 tall.
        var path = Path.Combine(_tempDir, "rotated.jpg");
        WriteJpegWithOrientation(path, 300, 200, orientation: 6);
        var service = CreateService(decoder: null, box: new DecodeBox(100, 100));
        var presenter = CreatePresenter(service, [path]);

        await presenter.PresentAsync(0);
        Assert.Equal(200, presenter.CurrentOriginalWidth);
        Assert.Equal(300, presenter.CurrentOriginalHeight);

        _viewer.SetZoom(2.0);
        Assert.Equal(400, _viewer.ImageWidth, 6);
        Assert.Equal(600, _viewer.ImageHeight, 6);
        await WhenOriginalShownAsync(presenter);

        var original = Assert.IsAssignableFrom<BitmapSource>(_sink.Current);
        Assert.Equal(200, original.PixelWidth);
        Assert.Equal(300, original.PixelHeight);
        Assert.Equal(400, _viewer.ImageWidth, 6);
        Assert.Equal(600, _viewer.ImageHeight, 6);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Waits for the swap. Without a UI SynchronizationContext an ungated decode can finish before
    /// <see cref="ZoomDetailLoader.PendingLoad"/> is even observed, so poll the outcome instead.
    /// </summary>
    private static async Task WhenOriginalShownAsync(ImagePresenter presenter)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!presenter.ZoomDetail.IsShowingOriginal)
        {
            Assert.True(DateTime.UtcNow < deadline, "The original never replaced the preview.");
            if (presenter.ZoomDetail.PendingLoad is { } load) await load;
            else await Task.Delay(5);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ZoomAndCaptureOriginalAsync(ImagePresenter presenter)
    {
        _viewer.SetZoom(1.0);
        await WhenOriginalShownAsync(presenter);
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        return new WeakReference(presenter.ZoomDetail.HeldOriginal);
    }

    private (ImagePresenter Presenter, PreviewImageService Service) Create(IImageDecoder decoder, params string[] names)
    {
        var paths = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
            paths.Add(path);
        }
        var service = CreateService(decoder, new DecodeBox(1920, 1080));
        return (CreatePresenter(service, paths), service);
    }

    private PreviewImageService CreateService(IImageDecoder? decoder, DecodeBox box) => new(
        _metrics,
        () => _settings.LoadingMode == LoadingMode.Original,
        () => box,
        capacityBytes: 512L * 1024 * 1024,
        disableDiskCacheOverride: true,
        decoder: decoder,
        currentBackend: () => DecoderBackend.Wpf);

    private ImagePresenter CreatePresenter(PreviewImageService service, IReadOnlyList<string> paths)
    {
        _catalog.Reset(paths);
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, new NullPreload(), _compare, new FileHashService(),
            _metrics, () => _settings, _sessionStore, _sink);
        // Mirrors MainViewModel + MainViewModelCompositionRoot wiring.
        _sink.OnImageChanged = () => _viewer.SetSourceSize(presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight);
        _viewer.ZoomModeChanged += (_, _) => presenter.SetViewerZoom(_viewer.EffectiveZoom);
        return presenter;
    }

    private static void WriteJpegWithOrientation(string path, int width, int height, ushort orientation)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Preview = downscaled to <see cref="PreviewWidth"/>; unbounded box = the full original.</summary>
    private sealed class SizedDecoder : IImageDecoder
    {
        private int _originalDecodes;
        public int OriginalWidth { get; init; } = 6000;
        public int OriginalHeight { get; init; } = 4000;
        public int PreviewWidth { get; init; } = 600;
        public SemaphoreSlim? OriginalGate { get; init; }
        public Dictionary<string, SemaphoreSlim> GateByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int OriginalDecodes => Volatile.Read(ref _originalDecodes);

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (request.Box.IsUnbounded)
            {
                Interlocked.Increment(ref _originalDecodes);
                (GateByName.TryGetValue(Path.GetFileName(request.Path), out var gate) ? gate : OriginalGate)?.Wait();
                return new SizedImage(OriginalWidth, OriginalHeight, OriginalWidth, OriginalHeight, downscaled: false);
            }
            return new SizedImage(PreviewWidth, PreviewWidth * OriginalHeight / OriginalWidth, OriginalWidth, OriginalHeight, downscaled: true);
        }

        public ImageInfo ReadInfo(string path) => new(OriginalWidth, OriginalHeight);
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

    /// <summary>Keeps only the current image (no history), so released bitmaps can be collected.</summary>
    private sealed class CurrentOnlySink : IPresentationSink
    {
        public object? Current { get; private set; }
        public Action? OnImageChanged { get; set; }
        public Action? OnApplyInitialViewMode { get; set; }

        public void SetCurrentImage(object? image)
        {
            Current = image;
            OnImageChanged?.Invoke();
        }

        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() => OnApplyInitialViewMode?.Invoke();
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
}
