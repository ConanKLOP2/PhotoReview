using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>RV-T49: EvictCachedPath invalidation callback and DecodeOriginalAsync gate hygiene on pre-cancelled calls.</summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceGapTests2 : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-PisGap2-" + Guid.NewGuid().ToString("N"));
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

    private PreviewImageService CreateService(IImageDecoder decoder) => _service = new PreviewImageService(
        new ReviewMetrics(),
        () => false,
        () => new DecodeBox(100, 100),
        capacityBytes: 64L * 1024 * 1024,
        diskCacheDirectory: Path.Combine(_root, "cache"),
        disableDiskCacheOverride: true,
        decoder: decoder,
        currentBackend: () => DecoderBackend.Wpf);

    [Fact]
    public async Task EvictCachedPath_GivenAnUnnormalizedLowerCasePath_InvalidatesWithTheUpperCasedFullPathAndKeepsOtherEntries()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        var evicted = Path.Combine(sub, "a.jpg");
        var kept = Path.Combine(sub, "b.jpg");
        File.WriteAllBytes(evicted, [1, 2, 3]);
        File.WriteAllBytes(kept, [1, 2, 3, 4]);
        var service = CreateService(new CountingDecoder());
        await service.GetPreviewAsync(evicted);
        await service.GetPreviewAsync(kept);
        Assert.True(service.TryGetCachedPreview(evicted, out _));
        var seen = new List<string>();
        var unnormalized = Path.Combine(sub, "..", "sub", "a.jpg"); // not a full normalized path

        service.EvictCachedPath(unnormalized, seen.Add);

        Assert.Equal([Path.GetFullPath(evicted).ToUpperInvariant()], seen);
        Assert.False(service.TryGetCachedPreview(evicted, out _));
        Assert.True(service.TryGetCachedPreview(kept, out _));
        Assert.Equal(1, service.CacheCount);
    }

    [Fact]
    public async Task DecodeOriginalAsync_CancelledBeforeTheGate_NeverDecodesAndLeavesTheGateUsable()
    {
        var first = Path.Combine(_root, "g1.jpg");
        var second = Path.Combine(_root, "g2.jpg");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [1, 2, 3]);
        var decoder = new CountingDecoder();
        var service = CreateService(decoder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DecodeOriginalAsync(first, service.GetCurrentCacheKey(first), cts.Token));
        Assert.Empty(decoder.Requests);

        // The single gate slot must still be available, and must be handed back after every completed decode.
        await service.DecodeOriginalAsync(first, service.GetCurrentCacheKey(first), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await service.DecodeOriginalAsync(second, service.GetCurrentCacheKey(second), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, decoder.Requests.Count);
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