using System.IO;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheTests : IDisposable
{
    private static readonly byte[] ValidPng1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _tempDir;
    private readonly string _diskDir;
    private readonly string _imagePath;

    public ThumbnailCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbTests-" + Guid.NewGuid().ToString("N"));
        _diskDir = Path.Combine(_tempDir, "disk");
        Directory.CreateDirectory(_diskDir);
        _imagePath = Path.Combine(_tempDir, "test.png");
        File.WriteAllBytes(_imagePath, ValidPng1x1);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact(DisplayName = "Constructor validates arguments")]
    public void ConstructorValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailCache(_diskDir, maxRamBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailCache(_diskDir, maxDiskBytes: 0));
    }

    [Fact(DisplayName = "GetAsync returns IDecodedImage and serves from RAM on second call")]
    public async Task GetAsyncReturnsDecodedImageAndCachesInRam()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: true);

        var first = await cache.GetAsync(_imagePath);
        Assert.NotNull(first);
        Assert.True(first.PixelWidth > 0);
        Assert.True(first.EstimatedBytes > 0);

        var second = await cache.GetAsync(_imagePath);
        Assert.Same(first, second);
    }

    [Fact(DisplayName = "ClearMemory drops RAM cache entries")]
    public async Task ClearMemoryDropsRamCache()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);

        var first = await cache.GetAsync(_imagePath);
        cache.ClearMemory();
        var second = await cache.GetAsync(_imagePath);

        Assert.NotSame(first, second);
    }
}

