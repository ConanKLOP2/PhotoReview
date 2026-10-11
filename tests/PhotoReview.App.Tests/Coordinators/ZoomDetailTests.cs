using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
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
        _thumbnailCache = new ThumbnailCache(WpfBitmapSourceCodec.Instance,
            diskDirectory: Path.Combine(_tempDir, "thumbs"),
            maxRamBytes: 1024 * 1024);
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

    [Fact(DisplayName = "Zooming out from a Fit below the minimum zoom never enlarges the image")]
    public void ZoomOut_FromFitBelowMinZoom_LeavesFitUntouched()
    {
        var viewer = new ViewerState { DpiScale = 1.0 };
        viewer.SetSourceSize(12000, 8000);
        viewer.ResetFit(1500, 1000); // fit = 0.125 < MinZoom

        viewer.ZoomOut();
        viewer.WheelZoom(-120);

        Assert.True(viewer.IsFit);
        Assert.Equal(0.125, viewer.FitZoom, 6);
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

    [Fact(DisplayName = "A failed full-resolution decode is not retried on every further zoom step")]
    public async Task FailedOriginalDecode_IsNotRetriedOnNextZoomStep()
    {
        var decoder = new SizedDecoder { OriginalGate = new SemaphoreSlim(0), FailOriginal = true };
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);

        _viewer.SetZoom(1.0);
        var load = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);
        decoder.OriginalGate.Release();
        await load!;
        Assert.Equal(1, decoder.OriginalDecodes);

        _viewer.SetZoom(2.0);
        _viewer.SetZoom(3.0);

        Assert.Null(presenter.ZoomDetail.PendingLoad);
        Assert.Equal(1, decoder.OriginalDecodes);
        Assert.False(presenter.ZoomDetail.IsShowingOriginal);
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

    [Fact(DisplayName = "R10: a same-file full-resolution swap keeps the file identity, so the next navigation still requests the fade")]
    public async Task NavigatingAfterTheOriginalSwap_StillRequestsTheFileChangeTransition()
    {
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg", "b.jpg");
        await presenter.PresentAsync(0);
        _viewer.SetZoom(2.0);
        await WhenOriginalShownAsync(presenter);
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        var shownBefore = _sink.FileChangeFlags.Count;

        await presenter.PresentAsync(1);

        Assert.True(_sink.FileChangeFlags.Count > shownBefore);
        Assert.True(_sink.FileChangeFlags[shownBefore]); // first bitmap of the different file fades
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

    /// <summary>
    /// Replaces the loader's indicator delay with one gate per RAW load that the test completes itself (cancelled with the
    /// load's token like the real delay), so the "300 ms" never depends on the clock.
    /// </summary>
    private static List<TaskCompletionSource> GateIndicatorDelays(ImagePresenter presenter)
    {
        var delays = new List<TaskCompletionSource>();
        presenter.ZoomDetail.IndicatorDelay = (_, token) =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => gate.TrySetCanceled(token));
            lock (delays) delays.Add(gate);
            return gate.Task;
        };
        return delays;
    }

    [Fact]
    public async Task RawOnZoom_ShowsDelayedIndicatorAndUsesRawFullDecoder()
    {
        var rawPath = Path.Combine(_tempDir, "zoom.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var previewDecoder = new SizedDecoder();
        var rawDecoder = new SizedDecoder { OriginalGate = new SemaphoreSlim(0) };
        var service = new PreviewImageService(_metrics, () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: previewDecoder, currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            var delays = GateIndicatorDelays(presenter);
            var indicatorShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            presenter.ZoomDetail.RawDecodeIndicatorChanged += visible =>
            {
                if (visible) indicatorShown.TrySetResult();
            };
            await presenter.PresentAsync(0);
            _viewer.SetZoom(1.0);
            var load = presenter.ZoomDetail.PendingLoad;
            Assert.NotNull(load);
            Assert.False(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);
            delays.Single().TrySetResult(); // the 300 ms have "elapsed"
            await indicatorShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);

            rawDecoder.OriginalGate.Release();
            await load!;

            Assert.False(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);
            Assert.Equal(1, rawDecoder.OriginalDecodes);
            Assert.Equal(0, previewDecoder.OriginalDecodes);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public async Task RawOriginalMode_EmbeddedJpegBelowSensorSize_OffersTheFullDecodeOnZoom()
    {
        var rawPath = Path.Combine(_tempDir, "original-mode.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var embeddedJpegDecoder = new EmbeddedRawDecoder();
        var rawDecoder = new SizedDecoder();
        var service = new PreviewImageService(_metrics, () => true, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: embeddedJpegDecoder, currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);

            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter);

            Assert.Equal(1, rawDecoder.OriginalDecodes);
            Assert.Equal(6000, presenter.ZoomDetail.HeldOriginal!.PixelWidth);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact(DisplayName = "Q-RAW-03: the RAW preview size is in the photo info while the preview shows, gone while the full decode shows, back on zoom-out")]
    public async Task RawPreviewSize_FollowsWhichImageIsDisplayed()
    {
        var rawPath = Path.Combine(_tempDir, "info.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var service = new PreviewImageService(_metrics, () => true, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: new EmbeddedRawDecoder(), currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: new SizedDecoder(), isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);
            Assert.Equal((2000, 1333), (presenter.CurrentPhotoInfo!.RawPreviewWidth, presenter.CurrentPhotoInfo.RawPreviewHeight));
            Assert.Equal((6000, 4000), (presenter.CurrentPhotoInfo.Width, presenter.CurrentPhotoInfo.Height)); // sensor size stays

            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter);
            Assert.Equal((0, 0), (presenter.CurrentPhotoInfo!.RawPreviewWidth, presenter.CurrentPhotoInfo.RawPreviewHeight));
            Assert.Equal((6000, 4000), (presenter.CurrentPhotoInfo.Width, presenter.CurrentPhotoInfo.Height));

            _viewer.ResetFit(1280, 720);
            Assert.False(presenter.ZoomDetail.IsShowingOriginal);
            Assert.Equal((2000, 1333), (presenter.CurrentPhotoInfo!.RawPreviewWidth, presenter.CurrentPhotoInfo.RawPreviewHeight));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public async Task RawPreviewSize_WhenTheHeldOriginalIsStillTheEmbeddedJpeg_StaysInThePhotoInfo()
    {
        var rawPath = Path.Combine(_tempDir, "info-embedded.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        // The "full" decoder fell back to the embedded JPEG: the held original still is the RAW preview.
        var service = new PreviewImageService(_metrics, () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: new EmbeddedRawDecoder(), currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: new EmbeddedRawDecoder(), isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);

            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter);

            Assert.True(presenter.ZoomDetail.IsShowingOriginal);
            Assert.Equal((2000, 1333), (presenter.CurrentPhotoInfo!.RawPreviewWidth, presenter.CurrentPhotoInfo.RawPreviewHeight));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public async Task RawPreviewInfo_OfAnEarlierPhoto_IsNeverShownForTheNextPhotoWhoseFullDecodeFellBackToTheEmbeddedJpeg()
    {
        var pathA = Path.Combine(_tempDir, "a.cr2");
        var pathB = Path.Combine(_tempDir, "b.cr2");
        File.WriteAllBytes(pathA, [0x49, 0x49, 0x2A, 0x00]);
        File.WriteAllBytes(pathB, [0x49, 0x49, 0x2A, 0x00]);
        // A gets a true full decode; B's "full" decode falls back to its embedded JPEG (still a RAW preview).
        var service = new PreviewImageService(_metrics, () => true, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: new EmbeddedRawDecoder(), currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: new FullForNamesDecoder("a.cr2"), isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [pathA, pathB]);
            await presenter.PresentAsync(0);
            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter);
            Assert.Equal("a.cr2", presenter.CurrentPhotoInfo!.FileName);
            Assert.Equal(0, presenter.CurrentPhotoInfo.RawPreviewWidth); // the full decode of A is on screen

            await presenter.PresentAsync(1);
            _viewer.ResetFit(1280, 720);
            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter); // B's held original is the embedded JPEG

            Assert.Equal("b.cr2", presenter.CurrentPhotoInfo!.FileName);
            Assert.Equal(2000, presenter.CurrentPhotoInfo.RawPreviewWidth);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact(DisplayName = "Q-RAW-03: a non-RAW photo never gets a preview size, and a stale one is not restored after a zoom swap")]
    public async Task NonRawPhoto_NeverHasARawPreviewSize()
    {
        var decoder = new SizedDecoder();
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        Assert.Equal(0, presenter.CurrentPhotoInfo!.RawPreviewWidth);

        _viewer.SetZoom(2.0);
        await WhenOriginalShownAsync(presenter);
        Assert.Equal(0, presenter.CurrentPhotoInfo!.RawPreviewWidth);
        _viewer.ResetFit(1280, 720);
        Assert.Equal(0, presenter.CurrentPhotoInfo!.RawPreviewWidth);
    }

    [Fact]
    public async Task RawOriginalMode_FullDecodeDisabled_NeverDecodesAgain()
    {
        var rawPath = Path.Combine(_tempDir, "original-mode-off.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var embeddedJpegDecoder = new EmbeddedRawDecoder();
        var rawDecoder = new SizedDecoder();
        var service = new PreviewImageService(_metrics, () => true, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: embeddedJpegDecoder, currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => false);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);

            _viewer.SetZoom(1.0);

            Assert.Null(presenter.ZoomDetail.PendingLoad);
            Assert.Equal(0, rawDecoder.OriginalDecodes);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public async Task RawOriginalMode_ImageAlreadyAtSensorSize_NeverDecodesAgain()
    {
        var rawPath = Path.Combine(_tempDir, "original-mode-full.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var fullSizeDecoder = new EmbeddedRawDecoder(downscaled: false);
        var rawDecoder = new SizedDecoder();
        var service = new PreviewImageService(_metrics, () => true, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: fullSizeDecoder, currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);

            _viewer.SetZoom(1.0);

            Assert.Null(presenter.ZoomDetail.PendingLoad);
            Assert.Equal(0, rawDecoder.OriginalDecodes);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public async Task RawOnZoom_SupersededLoadFinishingLate_DoesNotClearTheNewerLoadsIndicator()
    {
        var pathA = Path.Combine(_tempDir, "a.cr2");
        var pathB = Path.Combine(_tempDir, "b.cr2");
        File.WriteAllBytes(pathA, [0x49, 0x49, 0x2A, 0x00]);
        File.WriteAllBytes(pathB, [0x49, 0x49, 0x2A, 0x00]);
        using var gateA = new SemaphoreSlim(0);
        using var gateB = new SemaphoreSlim(0);
        var rawDecoder = new SizedDecoder();
        rawDecoder.GateByName["a.cr2"] = gateA;
        rawDecoder.GateByName["b.cr2"] = gateB;
        var service = new PreviewImageService(_metrics, () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: new SizedDecoder(), currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [pathA, pathB]);
            var delays = GateIndicatorDelays(presenter);
            var indicatorShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            presenter.ZoomDetail.RawDecodeIndicatorChanged += visible =>
            {
                if (visible) indicatorShown.TrySetResult();
            };
            _sink.OnApplyInitialViewMode = () => _viewer.ApplyInitialViewMode(InitialViewMode.Percent200, 1280, 720);
            await presenter.PresentAsync(0);
            var loadA = presenter.ZoomDetail.PendingLoad;
            Assert.NotNull(loadA);

            await presenter.PresentAsync(1); // supersedes A while its RAW decode is still running
            var loadB = presenter.ZoomDetail.PendingLoad;
            Assert.NotNull(loadB);
            Assert.NotSame(loadA, loadB);
            Assert.Equal(2, delays.Count);
            Assert.True(delays[0].Task.IsCanceled); // A's indicator was stopped when B superseded it
            delays[1].TrySetResult(); // B's indicator delay elapses while A's decode is still blocked on its gate
            await indicatorShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);

            gateA.Release(); // the superseded load finishes late
            await loadA!;

            Assert.True(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);

            gateB.Release();
            await loadB!;
            Assert.False(presenter.ZoomDetail.IsRawDecodeIndicatorVisible);
        }
        finally
        {
            gateA.Release();
            gateB.Release();
            await service.ShutdownPersistWorkersAsync();
        }
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

    // ---- R4: the decoded original is shown with ITS OWN size (ADR 0008 amendment) ----------------------

    // Reader (camera-visible) 6720x4480 vs LibRaw 6744x4502 (larger) and Canon R6 style 1 px smaller.
    [Theory]
    [InlineData(6744, 4502, 1.0, 1.0)]
    [InlineData(6744, 4502, 2.0, 1.25)]
    [InlineData(6744, 4502, 0.5, 1.5)]
    [InlineData(6719, 4479, 1.0, 1.0)]
    [InlineData(6719, 4479, 3.0, 1.25)]
    public async Task OriginalSwap_ShowsDecodedSizeTimesZoomOverDpi_NotThePreviewsSize(int decodedWidth, int decodedHeight, double zoom, double dpi)
    {
        var decoder = new SizedDecoder
        {
            OriginalWidth = 6720, OriginalHeight = 4480, DecodedWidth = decodedWidth, DecodedHeight = decodedHeight,
            OriginalGate = new SemaphoreSlim(0),
        };
        var (presenter, _) = Create(decoder, "a.jpg");
        _viewer.DpiScale = dpi;
        await presenter.PresentAsync(0);
        Assert.Equal((6720, 4480), (presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight));

        _viewer.SetZoom(zoom);
        Assert.Equal(6720 * zoom / dpi, _viewer.ImageWidth, 6); // preview on screen: its own original size
        var load = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);
        decoder.OriginalGate.Release();
        await load!;

        Assert.Equal((decodedWidth, decodedHeight), (presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight));
        Assert.Equal(decodedWidth * zoom / dpi, _viewer.ImageWidth, 6);
        Assert.Equal(decodedHeight * zoom / dpi, _viewer.ImageHeight, 6);
        Assert.Equal(zoom, _viewer.Zoom, 9); // the user's percent is kept, only the original dimensions change
        Assert.Equal((int)Math.Round(zoom * 100), _viewer.DisplayZoomPercent);
        var held = presenter.ZoomDetail.HeldOriginal!;
        Assert.Equal((decodedWidth, decodedHeight), (held.PixelWidth, held.PixelHeight));
    }

    [Fact]
    public async Task BackToFit_RevertsToThePreviewsOriginalSize_AndZoomingAgainReusesTheDecodedSize()
    {
        var decoder = new SizedDecoder { OriginalWidth = 6720, OriginalHeight = 4480, DecodedWidth = 6744, DecodedHeight = 4502 };
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        var preview = _sink.Current;
        _viewer.SetZoom(1.0);
        await WhenOriginalShownAsync(presenter);
        Assert.Equal(6744, presenter.CurrentOriginalWidth);

        _viewer.ResetFit(1500, 1000);

        Assert.Same(preview, _sink.Current);
        Assert.Equal((6720, 4480), (presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight));
        Assert.Equal((6720, 4480), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
        Assert.Equal(1500.0 / 6720, _viewer.FitZoom, 9); // Fit is laid out from the preview's size, as before the swap

        _viewer.SetZoom(1.0);

        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Equal(1, decoder.OriginalDecodes); // held original re-shown, no second decode
        Assert.Equal((6744, 4502), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
        Assert.Equal(6744, _viewer.ImageWidth, 6);
    }

    [Fact]
    public async Task OriginalSwap_OfAnOriginalWithThePreviewsSize_RaisesNoSizeSwapAndNoLayoutChange()
    {
        var decoder = new SizedDecoder { OriginalGate = new SemaphoreSlim(0) };
        var (presenter, _) = Create(decoder, "a.jpg");
        await presenter.PresentAsync(0);
        _viewer.SetZoom(2.0);
        var swapping = 0;
        _viewer.SourceSizeSwapping += (_, _) => swapping++;
        var load = presenter.ZoomDetail.PendingLoad;
        decoder.OriginalGate.Release();
        await load!;

        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Equal(0, swapping);
        Assert.Equal(12000, _viewer.ImageWidth, 6);
    }

    [Fact]
    public async Task SupersededOriginalFinishingLate_DoesNotChangeTheCurrentImagesSize()
    {
        var decoder = new SizedDecoder { DecodedWidth = 6032, DecodedHeight = 4032 };
        using var gateA = new SemaphoreSlim(0);
        decoder.GateByName["a.jpg"] = gateA;
        var (presenter, _) = Create(decoder, "a.jpg", "b.jpg");
        await presenter.PresentAsync(0);
        _viewer.SetZoom(1.0);
        var load = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(load);

        _viewer.ResetFit(1500, 1000); // back to Fit, then on to the next image while a's decode is still running
        await presenter.PresentAsync(1);
        var currentBefore = _sink.Current;
        gateA.Release();
        await load!;

        Assert.Same(currentBefore, _sink.Current);
        Assert.Equal((6000, 4000), (presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight));
        Assert.Equal((6000, 4000), (_viewer.SourcePixelWidth, _viewer.SourcePixelHeight));
        Assert.Null(presenter.ZoomDetail.HeldOriginal);
    }

    [Fact]
    public async Task RawFullDecode_NeverChangesThePreviewsRecordedDimensions()
    {
        var rawPath = Path.Combine(_tempDir, "dims.cr2");
        File.WriteAllBytes(rawPath, [0x49, 0x49, 0x2A, 0x00]);
        var previewDecoder = new SizedDecoder { OriginalWidth = 6720, OriginalHeight = 4480 };
        var rawDecoder = new SizedDecoder { OriginalWidth = 6720, OriginalHeight = 4480, DecodedWidth = 6744, DecodedHeight = 4502 };
        var service = new PreviewImageService(_metrics, () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: previewDecoder, currentBackend: () => DecoderBackend.Wpf,
            rawFullDecoder: rawDecoder, isRawFullDecodeEnabled: () => true);
        try
        {
            var presenter = CreatePresenter(service, [rawPath]);
            await presenter.PresentAsync(0);
            _viewer.SetZoom(1.0);
            await WhenOriginalShownAsync(presenter);
            Assert.Equal(6744, presenter.CurrentOriginalWidth);

            var key = service.GetCurrentCacheKey(rawPath);
            Assert.True(service.TryGetKnownOriginalDimensions(key, out var known));
            Assert.Equal((6720, 4480), known);
            Assert.Equal((6720, 4480), await service.GetOriginalDimensionsAsync(rawPath, key));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    // ---- ViewerState.SwapSourceSize -------------------------------------------------------------------

    [Fact]
    public void SwapSourceSize_KeepsTheZoomPercent_AndRaisesSwappingBeforeTheSizeChanges()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(6720, 4480);
        viewer.SetZoom(2.0);
        int? widthSeenBySwapping = null;
        viewer.SourceSizeSwapping += (_, _) => widthSeenBySwapping = viewer.SourcePixelWidth;

        viewer.SwapSourceSize(6744, 4502);

        Assert.Equal(6720, widthSeenBySwapping);
        Assert.Equal(2.0, viewer.Zoom, 9);
        Assert.Equal(6744 * 2.0, viewer.ImageWidth, 6);
    }

    [Fact]
    public void SwapSourceSize_InFit_ChangesSizeSilently()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(6720, 4480);
        viewer.ResetFit(1500, 1000);
        var swapping = 0;
        viewer.SourceSizeSwapping += (_, _) => swapping++;

        viewer.SwapSourceSize(6744, 4502);

        Assert.Equal(0, swapping);
        Assert.Equal(6744, viewer.SourcePixelWidth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwapSourceSize_AfterFitWidthOrHeight_KeepsTheFittedDimensionFillingTheViewport(bool width)
    {
        var viewer = new ViewerState { DpiScale = 1.25 };
        viewer.SetSourceSize(6720, 4480);
        viewer.UpdateViewport(1500, 1000, force: true);
        if (width) viewer.ZoomToFitWidth(); else viewer.ZoomToFitHeight();
        Assert.Equal(width ? 1500 : 1000, width ? viewer.ImageWidth : viewer.ImageHeight, 6);

        viewer.SwapSourceSize(6744, 4502);

        Assert.Equal(width ? 1500 : 1000, width ? viewer.ImageWidth : viewer.ImageHeight, 6);
    }

    [Fact]
    public void SwapSourceSize_AfterAFitWidthThenAnExplicitZoom_KeepsThatZoomPercent()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(6720, 4480);
        viewer.UpdateViewport(1500, 1000, force: true);
        viewer.ZoomToFitWidth();
        viewer.SetZoom(0.5); // the user moved on: no longer a Fit-width zoom

        viewer.SwapSourceSize(6744, 4502);

        Assert.Equal(0.5, viewer.Zoom, 9);
        Assert.Equal(6744 * 0.5, viewer.ImageWidth, 6);
    }

    [Fact]
    public void SetSourceSize_ForANewImage_NeverRaisesSwapping()
    {
        var viewer = new ViewerState();
        viewer.SetSourceSize(6720, 4480);
        viewer.SetZoom(2.0);
        var swapping = 0;
        viewer.SourceSizeSwapping += (_, _) => swapping++;

        viewer.SetSourceSize(4000, 6000);

        Assert.Equal(0, swapping);
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
        _sink.OnImageChanged = () =>
        {
            if (presenter.IsSameSourceSwap) _viewer.SwapSourceSize(presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight);
            else _viewer.SetSourceSize(presenter.CurrentOriginalWidth, presenter.CurrentOriginalHeight);
        };
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
        /// <summary>Size of the full decode when it differs from the reader's size (RAW: LibRaw sensor area); 0 = same.</summary>
        public int DecodedWidth { get; init; }
        public int DecodedHeight { get; init; }
        public SemaphoreSlim? OriginalGate { get; init; }
        public bool FailOriginal { get; init; }
        public Dictionary<string, SemaphoreSlim> GateByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int OriginalDecodes => Volatile.Read(ref _originalDecodes);

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (request.Box.IsUnbounded)
            {
                Interlocked.Increment(ref _originalDecodes);
                (GateByName.TryGetValue(Path.GetFileName(request.Path), out var gate) ? gate : OriginalGate)?.Wait();
                if (FailOriginal) throw new InvalidOperationException("original decode failed");
                var w = DecodedWidth > 0 ? DecodedWidth : OriginalWidth;
                var h = DecodedHeight > 0 ? DecodedHeight : OriginalHeight;
                return new SizedImage(w, h, w, h, downscaled: false);
            }
            return new SizedImage(PreviewWidth, PreviewWidth * OriginalHeight / OriginalWidth, OriginalWidth, OriginalHeight, downscaled: true);
        }

        public ImageInfo ReadInfo(string path) => new(OriginalWidth, OriginalHeight);
    }

    /// <summary>A RAW's regular decode: the embedded JPEG (2000 px wide) of a 6000x4000 sensor, whatever the box.</summary>
    private sealed class EmbeddedRawDecoder(bool downscaled = true) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => downscaled
            ? new SizedImage(2000, 1333, 6000, 4000, downscaled: true, embeddedPreviewWidth: 2000, embeddedPreviewHeight: 1333)
            : new SizedImage(6000, 4000, 6000, 4000, downscaled: false);

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class FullForNamesDecoder(params string[] fullNames) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) =>
            fullNames.Contains(Path.GetFileName(request.Path), StringComparer.OrdinalIgnoreCase)
                ? new SizedImage(6000, 4000, 6000, 4000, downscaled: false)
                : new SizedImage(6000, 4000, 6000, 4000, downscaled: false, embeddedPreviewWidth: 2000, embeddedPreviewHeight: 1333);

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class SizedImage(int width, int height, int originalWidth, int originalHeight, bool downscaled,
        int embeddedPreviewWidth = 0, int embeddedPreviewHeight = 0) : IDecodedImage, IRawPreviewInfo
    {
        public int EmbeddedPreviewWidth => embeddedPreviewWidth;
        public int EmbeddedPreviewHeight => embeddedPreviewHeight;
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
        public List<bool> FileChangeFlags { get; } = [];

        public void SetCurrentImage(object? image, bool isFileChange = false)
        {
            Current = image;
            FileChangeFlags.Add(isFileChange);
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
