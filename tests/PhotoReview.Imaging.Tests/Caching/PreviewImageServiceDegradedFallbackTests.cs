using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// A degraded fallback image (<see cref="IDecodedImage.IsDegradedFallback"/>, e.g. a RAW's next-best preview accepted
/// after the larger one failed) is returned to the caller but never cached in RAM or on disk, so a later view retries.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceDegradedFallbackTests : IDisposable
{
    private readonly TempRoot _root = new("preview-degraded");
    private readonly string _source;

    public PreviewImageServiceDegradedFallbackTests()
    {
        _source = Path.Combine(_root.Dir("source"), "a.png");
        File.WriteAllBytes(_source, TestImages.OpaquePng);
    }

    public void Dispose() => _root.Dispose();

    private sealed class FlaggingDecoder(bool degraded, ManualResetEventSlim? gate = null) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _calls);
            var decoded = _inner.Decode(request);
            gate?.Wait(TimeSpan.FromSeconds(20));
            return new Flagged(decoded, degraded);
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    private sealed class Flagged(IDecodedImage inner, bool degraded) : IDecodedImage
    {
        public int PixelWidth => inner.PixelWidth;
        public int PixelHeight => inner.PixelHeight;
        public bool Downscaled => true; // would be persisted to disk were it not degraded
        public int Orientation => inner.Orientation;
        public long EstimatedBytes => inner.EstimatedBytes;
        public object PlatformImage => inner.PlatformImage;
        public bool IsDegradedFallback => degraded;
    }

    private PreviewImageService NewService(string diskDir, IImageDecoder decoder) =>
        new(new ReviewMetrics(), () => false, () => 32, capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: diskDir, decoder: decoder);

    [Fact]
    public async Task GetPreviewAsync_DegradedFallback_IsReturnedButNotCachedInRamOrOnDisk()
    {
        var diskDir = _root.Dir("disk-degraded");
        var decoder = new FlaggingDecoder(degraded: true);
        var service = NewService(diskDir, decoder);

        var first = await service.GetPreviewAsync(_source);
        await service.ShutdownPersistWorkersAsync();

        Assert.True(first.PixelWidth > 0);
        Assert.True(first.IsDegradedFallback);
        Assert.Equal(0, service.CacheCount);
        Assert.Empty(Directory.GetFiles(diskDir, "*.pv4"));

        await service.GetPreviewAsync(_source);
        Assert.Equal(2, decoder.Calls); // retried, not served from any cache
    }

    [Fact]
    public async Task GetPreviewAsync_DegradedFallbackSharedByJoiners_EveryJoinerGetsTheImage()
    {
        var diskDir = _root.Dir("disk-joiners");
        using var gate = new ManualResetEventSlim();
        var decoder = new FlaggingDecoder(degraded: true, gate);
        var service = NewService(diskDir, decoder);

        var a = service.GetPreviewAsync(_source);
        var b = service.GetPreviewAsync(_source);
        gate.Set();
        var images = await Task.WhenAll(a, b);

        Assert.All(images, i => Assert.True(i.PixelWidth > 0));
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task GetPreviewAsync_NormalDecode_IsCachedInRamAndPersistedToDisk()
    {
        var diskDir = _root.Dir("disk-normal");
        var decoder = new FlaggingDecoder(degraded: false);
        var service = NewService(diskDir, decoder);

        await service.GetPreviewAsync(_source);
        await service.ShutdownPersistWorkersAsync();
        await service.WaitForPruneAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, service.CacheCount);
        Assert.Single(Directory.GetFiles(diskDir, "*.pv4"));
        await service.GetPreviewAsync(_source);
        Assert.Equal(1, decoder.Calls);
    }
}
