using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>IMG-02: the original-dimensions cache is bounded, not process-lifetime unbounded.</summary>
public sealed class OriginalDimensionsCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PhotoReview-OrigDims-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task GetOriginalDimensions_ManyDistinctKeys_StaysWithinCapacity()
    {
        _service = new PreviewImageService(
            new ReviewMetrics(), () => false, () => 100,
            capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: Path.Combine(_root, "cache"),
            disableDiskCacheOverride: true, decoder: new StubDecoder(), currentBackend: () => DecoderBackend.Wpf,
            originalDimensionsCapacity: 100);

        string? lastPath = null;
        for (var i = 0; i < 300; i++)
        {
            lastPath = Path.Combine(_root, $"img{i}.jpg");
            File.WriteAllBytes(lastPath, [1, 2, 3]);
            var dims = await _service.GetOriginalDimensionsAsync(lastPath);
            Assert.Equal((7, 5), dims);
        }

        Assert.Equal(100, _service.KnownOriginalDimensionsCount);
        // Most recent entry is still known; the oldest was evicted.
        Assert.True(_service.TryGetKnownOriginalDimensions(_service.GetCurrentCacheKey(lastPath!), out _));
        Assert.False(_service.TryGetKnownOriginalDimensions(
            _service.GetCurrentCacheKey(Path.Combine(_root, "img0.jpg")), out _));
    }

    private sealed class StubDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException();
        public ImageInfo ReadInfo(string path) => new(7, 5);
    }
}
