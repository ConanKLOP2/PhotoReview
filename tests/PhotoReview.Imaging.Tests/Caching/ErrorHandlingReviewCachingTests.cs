using System.IO;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Error-handling review (imaging): cache-layer fixes with focused regression tests.</summary>
[Trait("Category", "HotPath")]
public sealed class ErrorHandlingReviewCachingTests : IDisposable
{
    private readonly TempRoot _root = new("errh-cache");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "AtomicCacheFile: a token cancelled before the write throws and creates nothing")]
    public async Task AtomicWrite_CancelledUpFront_WritesNothing()
    {
        var target = _root.Combine("sub", "entry.pv4");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicCacheFile.WriteAsync(target, s => s.WriteByte(1), log: null, cts.Token));

        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(_root.Combine("sub")));
    }

    [Fact(DisplayName = "AtomicCacheFile: cancellation during the payload write never publishes the file and removes the temp")]
    public async Task AtomicWrite_CancelledBeforeMove_DoesNotPublish()
    {
        var dir = _root.Dir("c");
        var target = Path.Combine(dir, "entry.pv4");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicCacheFile.WriteAsync(target, s => { s.WriteByte(1); cts.Cancel(); }, log: null, cts.Token));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact(DisplayName = "ThumbnailCache.GetAsync reports a missing source through the returned task, not a synchronous throw")]
    public async Task ThumbnailGet_MissingSource_ReturnsFaultedTask()
    {
        using var cache = new ThumbnailCache(_root.Dir("disk"), persistNewThumbnails: false);
        var missing = _root.Combine("nope.jpg");

        Task<IDecodedImage?> task = null!;
        var thrown = Record.Exception(() => { task = cache.GetAsync(missing, null); });

        Assert.Null(thrown);
        await Assert.ThrowsAnyAsync<IOException>(() => task);
    }

    [Fact(DisplayName = "SourceBytesCache: per-path eviction versions are bounded, and an in-flight read still never republishes evicted bytes")]
    public void Evict_BoundsPathVersions_AndKeepsInvalidation()
    {
        var cache = new SourceBytesCache(1024 * 1024);
        for (var i = 0; i < SourceBytesCache.MaxTrackedPathVersions; i++)
            cache.Evict(_root.Combine($"gone{i}.bin"));
        Assert.Equal(SourceBytesCache.MaxTrackedPathVersions, cache.PathVersionCountForTests);

        var racing = _root.File("racing.bin", new byte[32]);
        // The read has loaded the bytes and is about to publish when the path is evicted (map is full: global bump path).
        cache.AfterReadForTests = () => cache.Evict(racing);
        Assert.Equal(32, cache.GetOrRead(racing).Length);

        Assert.Equal(0, cache.Count); // evicted while in flight: not cached
        Assert.True(cache.PathVersionCountForTests <= SourceBytesCache.MaxTrackedPathVersions);

        for (var i = 0; i < 50; i++) cache.Evict(_root.Combine($"more{i}.bin"));
        Assert.True(cache.PathVersionCountForTests <= SourceBytesCache.MaxTrackedPathVersions);

        cache.AfterReadForTests = null;
        Assert.Equal(32, cache.GetOrRead(racing).Length); // cacheable again afterwards
        Assert.Equal(1, cache.Count);
    }
}
