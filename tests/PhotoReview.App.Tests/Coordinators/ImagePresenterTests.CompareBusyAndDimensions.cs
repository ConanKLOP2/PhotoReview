using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed partial class ImagePresenterTests
{
    /// <summary>Real decoder; the first <c>busyCount</c> decodes of <c>busyPath</c> fail like a LibRaw gate fail-fast, and the thread priority of each is recorded.</summary>
    private sealed class BusyThenOkDecoder(string busyPath, int busyCount) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        private int _calls;

        public int BusyPathCalls => Volatile.Read(ref _calls);
        public ThreadPriority? BusyPathPriority { get; private set; }

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (string.Equals(request.Path, busyPath, StringComparison.OrdinalIgnoreCase))
            {
                BusyPathPriority = Thread.CurrentThread.Priority;
                if (Interlocked.Increment(ref _calls) <= busyCount) throw new DecoderBusyException();
            }
            return _inner.Decode(request);
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    private sealed class UnknownSizeDecoder : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        public IDecodedImage Decode(DecodeRequest request) => _inner.Decode(request);
        public ImageInfo ReadInfo(string path) => throw new InvalidDataException("no sensor size and no preview size");
    }

    private sealed class BuggyInfoDecoder : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        public IDecodedImage Decode(DecodeRequest request) => _inner.Decode(request);
        public ImageInfo ReadInfo(string path) => throw new ArgumentException("decoder bug");
    }

    private PreviewImageService CreateServiceWith(IImageDecoder decoder) => new(
        _metrics,
        () => false,
        (Func<DecodeBox>)(() => new DecodeBox(1920, 0)),
        capacityBytes: 64 * 1024 * 1024,
        currentBackend: () => DecoderBackend.Wpf,
        disableDiskCacheOverride: true,
        decoder: decoder);

    private (ImagePresenter Presenter, Func<int> RetryDelays, string Jpeg, string Partner) CreateComparePresenter(int busyCount, out BusyThenOkDecoder decoder)
    {
        var jpeg = CreateFakeImageFile("busy-pair.jpg");
        var partner = CreateFakeImageFile("busy-pair-member.png");
        decoder = new BusyThenOkDecoder(partner, busyCount);
        var delays = 0;
        var presenter = new ImagePresenter(
            _catalog, _clock, CreateServiceWith(decoder), _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null,
            busyRetryDelay: (_, _) => { Interlocked.Increment(ref delays); return Task.CompletedTask; });
        _catalog.Reset([new CatalogEntry(jpeg) { CaptureGroup = new CaptureGroup(jpeg, partner) }], RawPairMode.Separate);
        return (presenter, () => Volatile.Read(ref delays), jpeg, partner);
    }

    [Fact]
    public async Task PresentAsync_ComparePartnerBusyOnce_RetriesAtViewerPriorityAndShowsThePartner()
    {
        var (presenter, retryDelays, jpeg, partner) = CreateComparePresenter(busyCount: 1, out var decoder);

        await presenter.PresentAsync(0, includeCaptureGroupInCompare: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_compareViewModel.IsVisible);
        Assert.Equal(jpeg, _compareViewModel.LeftPath);
        Assert.Equal(partner, _compareViewModel.RightPath);
        Assert.NotNull(_compareViewModel.RightImage);
        Assert.False(presenter.StatusNeedsAttention);
        Assert.Equal(1, retryDelays());
        Assert.Equal(2, decoder.BusyPathCalls);
        Assert.Equal(ThreadPriority.AboveNormal, decoder.BusyPathPriority); // the viewer lane, not the preload lane
    }

    [Fact]
    public async Task PresentAsync_ComparePartnerBusyTwiceInARow_ShowsTheErrorAfterOneBoundedRetry()
    {
        var (presenter, retryDelays, jpeg, partner) = CreateComparePresenter(busyCount: 2, out var decoder);

        await presenter.PresentAsync(0, includeCaptureGroupInCompare: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(_compareViewModel.IsVisible);
        Assert.NotNull(presenter.CurrentImage); // the healthy current image stays
        Assert.True(presenter.StatusNeedsAttention);
        Assert.Contains(Path.GetFileName(partner), presenter.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, retryDelays());
        Assert.Equal(2, decoder.BusyPathCalls);
        Assert.Equal(jpeg, presenter.CurrentPresentedPath);
    }

    [Fact]
    public async Task PresentAsync_DimensionLookupThrowsInvalidData_KeepsTheImageAndShowsTheStatusWithoutDimensions()
    {
        var file = CreateFakeImageFile("unknown-size.png");
        _catalog.Reset([file]);
        var service = CreateServiceWith(new UnknownSizeDecoder());
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null,
            // The dimensions the decode seeded are evicted right after the image is shown, so the status lookup must ReadInfo.
            onPresentedHook: _ => service.ClearCache());

        await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(presenter.CurrentImage);
        Assert.False(presenter.StatusNeedsAttention);
        Assert.Equal(StatusFormatter.Ready(0, 1, new FileInfo(file).Length, "unknown-size.png"), presenter.StatusText); // no "WxH"
        Assert.NotNull(presenter.CurrentPhotoInfo);
    }

    [Fact]
    public async Task PresentAsync_DimensionLookupThrowsUnexpectedBug_IsNotHiddenAsReadyStatus()
    {
        var file = CreateFakeImageFile("buggy-size.png");
        _catalog.Reset([file]);
        var service = CreateServiceWith(new BuggyInfoDecoder());
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null,
            onPresentedHook: _ => service.ClearCache());

        await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(StatusFormatter.Ready(0, 1, new FileInfo(file).Length, "buggy-size.png"), presenter.StatusText);
    }
}
