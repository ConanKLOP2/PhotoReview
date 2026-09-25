using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// feat(zoom): <see cref="PreviewImageService.DecodeOriginalAsync"/> decodes the full source for the
/// zoomed viewer without touching the preview RAM/disk caches, and is dropped before it starts when
/// the caller already navigated away.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecodeOriginalTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PhotoReview-DecodeOriginal-" + Guid.NewGuid().ToString("N"));
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

    private PreviewImageService CreateService(IImageDecoder? decoder = null) => _service = new PreviewImageService(
        new ReviewMetrics(),
        () => false,
        () => new DecodeBox(100, 100),
        capacityBytes: 64L * 1024 * 1024,
        diskCacheDirectory: Path.Combine(_root, "cache"),
        disableDiskCacheOverride: true,
        decoder: decoder,
        currentBackend: () => DecoderBackend.Wpf);

    [Fact]
    public async Task DecodeOriginal_ExifRotated_ReturnsFullSizeAfterOrientation_AndSkipsTheRamCache()
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(Path.Combine(_root, "rotated.jpg"), 300, 200, orientation: 6);
        var service = CreateService();
        var key = service.GetCurrentCacheKey(path);
        var preview = await service.GetPreviewAsync(path, key);
        var cachedBytes = service.CacheBytes;

        var original = await service.DecodeOriginalAsync(path, key, CancellationToken.None);

        Assert.True(preview.Downscaled);
        Assert.Equal(200, original.PixelWidth);
        Assert.Equal(300, original.PixelHeight);
        Assert.False(original.Downscaled);
        Assert.Equal(cachedBytes, service.CacheBytes);
        Assert.False(service.TryGetCachedPreview(ImageCacheKey.CreateOriginal(key), out _));
        Assert.True(service.TryGetKnownOriginalDimensions(key, out var dims));
        Assert.Equal((200, 300), dims);
    }

    [Fact]
    public async Task DecodeOriginal_AlreadyCancelled_NeverStartsTheDecode()
    {
        var path = Path.Combine(_root, "a.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var decoder = new CountingDecoder();
        var service = CreateService(decoder);
        var key = service.GetCurrentCacheKey(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DecodeOriginalAsync(path, key, cts.Token));
        Assert.Empty(decoder.Requests);
    }

    [Fact]
    public async Task DecodeOriginal_RequestsAnUnboundedBox()
    {
        var path = Path.Combine(_root, "b.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var decoder = new CountingDecoder();
        var service = CreateService(decoder);

        await service.DecodeOriginalAsync(path, service.GetCurrentCacheKey(path), CancellationToken.None);

        var request = Assert.Single(decoder.Requests);
        Assert.True(request.Box.IsUnbounded);
    }

    [Fact]
    public async Task DecodeOriginal_RunsAtMostOneFullResolutionDecodeAtATime_AndDropsCancelledWaiters()
    {
        var first = Path.Combine(_root, "g1.jpg");
        var second = Path.Combine(_root, "g2.jpg");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [1, 2, 3]);
        var decoder = new BlockingDecoder();
        var service = CreateService(decoder);
        using var cts = new CancellationTokenSource();

        var firstTask = service.DecodeOriginalAsync(first, service.GetCurrentCacheKey(first), CancellationToken.None);
        Assert.True(decoder.FirstEntered.Wait(TimeSpan.FromSeconds(10)), "the first decode never started");
        var secondTask = service.DecodeOriginalAsync(second, service.GetCurrentCacheKey(second), cts.Token);

        // Negative wait: with the gate the second decode must not reach the decoder while the first one is running.
        Assert.False(decoder.SecondEntered.Wait(TimeSpan.FromMilliseconds(300)), "a second full-resolution decode started concurrently");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);

        decoder.Release.Set();
        await firstTask;
        Assert.Equal(1, decoder.Started);
    }

    private sealed class BlockingDecoder : IImageDecoder
    {
        private int _started;
        public ManualResetEventSlim FirstEntered { get; } = new(false);
        public ManualResetEventSlim SecondEntered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int Started => Volatile.Read(ref _started);

        public IDecodedImage Decode(DecodeRequest request)
        {
            var n = Interlocked.Increment(ref _started);
            (n == 1 ? FirstEntered : SecondEntered).Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return new FakeImage();
        }

        public ImageInfo ReadInfo(string path) => new(10, 10);
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        public ConcurrentQueue<DecodeRequest> Requests { get; } = new();

        public IDecodedImage Decode(DecodeRequest request)
        {
            Requests.Enqueue(request);
            return new FakeImage();
        }

        public ImageInfo ReadInfo(string path) => new(10, 10);
    }

    private sealed class FakeImage : IDecodedImage
    {
        public int PixelWidth => 10;
        public int PixelHeight => 10;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 400;
        public object PlatformImage { get; } = new object();
    }
}
