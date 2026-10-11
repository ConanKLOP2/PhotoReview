using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// The decode-time EWMA drives the slow-link detection of the preload scheduler, so a multi-second LibRaw full decode (zoom on a RAW) must
/// never feed it, while every preview decode (DecodeAndCache) does.
/// </summary>
public sealed class PreviewDecodeEwmaTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-Ewma-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewMetrics _metrics = new();
    private PreviewImageService? _service;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_service is not null) await _service.ShutdownPersistWorkersAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private PreviewImageService CreateService(IImageDecoder decoder, IImageDecoder? rawFullDecoder = null) => _service = new PreviewImageService(
        _metrics,
        () => false,
        () => new DecodeBox(100, 100), WpfBitmapSourceCodec.Instance,
        capacityBytes: 64L * 1024 * 1024,
        diskCacheDirectory: Path.Combine(_root, "cache"),
        disableDiskCacheOverride: true,
        decoder: decoder,
        currentBackend: () => DecoderBackend.Wpf,
        rawFullDecoder: rawFullDecoder,
        isRawFullDecodeEnabled: () => rawFullDecoder is not null);

    [Fact]
    public async Task PreviewDecode_FeedsTheDecodeEwma()
    {
        var path = Path.Combine(_root, "photo.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var service = CreateService(new SlowDecoder());

        await service.GetPreviewAsync(path);

        Assert.True(_metrics.DecodeMillisecondsEwma > 0, "a preview decode must be sampled");
    }

    [Fact]
    public async Task RawFullDecodeOnZoom_DoesNotFeedTheDecodeEwma()
    {
        var path = Path.Combine(_root, "zoom.cr2");
        File.WriteAllBytes(path, [1, 2, 3]);
        var service = CreateService(new SlowDecoder(), new SlowDecoder());

        await service.DecodeOriginalAsync(path, service.GetCurrentCacheKey(path), CancellationToken.None);

        Assert.Equal(0, _metrics.DecodeMillisecondsEwma);
        Assert.True(_metrics.Snapshot().SourceReads >= 1, "the read itself is still counted");
    }

    private sealed class SlowDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            // Busy-wait (no sleep): the elapsed stopwatch time must be at least one whole millisecond to be a positive sample.
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 3) Thread.SpinWait(50);
            return new StubImage();
        }

        public ImageInfo ReadInfo(string path) => new(10, 10);
    }

    private sealed class StubImage : IDecodedImage
    {
        public int PixelWidth => 10;
        public int PixelHeight => 10;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 400;
        public object PlatformImage { get; } = new object();
    }
}
