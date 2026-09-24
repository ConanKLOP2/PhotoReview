using System.IO;
using System.Threading;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheTests : IDisposable
{
    private static readonly byte[] ValidPng1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _tempDir;
    private readonly string _diskDir;
    private readonly string _imagePath;
    private readonly string _jpegWithThumbnailPath;
    private readonly string _jpegWithoutThumbnailPath;

    public ThumbnailCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbTests-" + Guid.NewGuid().ToString("N"));
        _diskDir = Path.Combine(_tempDir, "disk");
        Directory.CreateDirectory(_diskDir);
        _imagePath = Path.Combine(_tempDir, "test.png");
        File.WriteAllBytes(_imagePath, ValidPng1x1);

        _jpegWithThumbnailPath = Path.Combine(_tempDir, "with-thumb.jpg");
        File.WriteAllBytes(_jpegWithThumbnailPath, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 64, thumbnailSize: 16));

        _jpegWithoutThumbnailPath = Path.Combine(_tempDir, "without-thumb.jpg");
        File.WriteAllBytes(_jpegWithoutThumbnailPath, EmbeddedThumbnailJpegFixture.CreateWithoutThumbnail(size: 64));
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

        var first = await cache.GetAsync(_jpegWithThumbnailPath);
        Assert.NotNull(first);
        Assert.True(first!.PixelWidth > 0);
        Assert.True(first.EstimatedBytes > 0);

        var second = await cache.GetAsync(_jpegWithThumbnailPath);
        Assert.Same(first, second);
    }

    [Fact(DisplayName = "ClearMemory drops RAM cache entries")]
    public async Task ClearMemoryDropsRamCache()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);

        var first = await cache.GetAsync(_jpegWithThumbnailPath);
        cache.ClearMemory();
        var second = await cache.GetAsync(_jpegWithThumbnailPath);

        Assert.NotSame(first, second);
    }

    [Fact(DisplayName = "On a cache miss, GetAsync returns the JPEG's embedded EXIF thumbnail, not a full source decode")]
    public async Task GetAsyncReturnsEmbeddedThumbnailOnMiss()
    {
        // The embedded thumbnail (16px) and the main frame (64px) are deliberately different
        // sizes: if this ever fell back to a full source decode, PixelWidth would be 64, not 16.
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);

        var thumbnail = await cache.GetAsync(_jpegWithThumbnailPath);

        Assert.NotNull(thumbnail);
        Assert.Equal(16, thumbnail!.PixelWidth);
        Assert.Equal(16, thumbnail.PixelHeight);
        // perf(dims): the reader already parsed the *main* frame's header (to read the embedded
        // thumbnail from it), so OriginalWidth/Height report the main image's real size (64) --
        // not the embedded thumbnail's own size (16) that PixelWidth/Height report above.
        Assert.Equal(64, thumbnail.OriginalWidth);
        Assert.Equal(64, thumbnail.OriginalHeight);
    }

    [Fact(DisplayName = "The embedded-thumbnail reader is used instead of a full source decode -- proven via an injected fake")]
    public async Task GetAsyncNeverPerformsAFullSourceDecodeOnMiss()
    {
        var readerCalls = 0;
        using var cache = new ThumbnailCache(
            _diskDir,
            maxRamBytes: 16 * 1024 * 1024,
            persistNewThumbnails: false,
            embeddedThumbnailReader: (path, ct) =>
            {
                Interlocked.Increment(ref readerCalls);
                return Task.FromResult(EmbeddedThumbnailReader.TryRead(path));
            });

        await cache.GetAsync(_jpegWithThumbnailPath);

        Assert.Equal(1, readerCalls);
    }

    [Fact(DisplayName = "A source with no embedded EXIF thumbnail produces no thumbnail (not a full decode fallback)")]
    public async Task GetAsyncReturnsNullWhenSourceHasNoEmbeddedThumbnail()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);

        var thumbnail = await cache.GetAsync(_jpegWithoutThumbnailPath);

        Assert.Null(thumbnail);
    }

    [Fact(DisplayName = "A non-JPEG source (no EXIF thumbnails at all) produces no thumbnail")]
    public async Task GetAsyncReturnsNullForNonJpegSource()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, persistNewThumbnails: false);

        var thumbnail = await cache.GetAsync(_imagePath); // .png fixture from the constructor

        Assert.Null(thumbnail);
    }
}

