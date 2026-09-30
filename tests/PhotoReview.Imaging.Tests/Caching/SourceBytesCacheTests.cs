using System.IO;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class SourceBytesCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-source-cache-" + Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Source bytes cache reads once and reuses bytes")]
    public void ReadsOnceAndReusesBytes()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "image.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var cache = new SourceBytesCache(1024);

        var first = cache.GetOrRead(path);
        var second = cache.GetOrRead(path);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);
        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "Source bytes cache retains a preview range without reading the whole source identity")]
    public void GetOrReadRange_CachesOnlyRequestedRanges()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "raw.dng");
        var contents = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        File.WriteAllBytes(path, contents);
        var info = new FileInfo(path);
        var cache = new SourceBytesCache(1024);

        var first = cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 512, 128);
        var firstAgain = cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 512, 128);
        var second = cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 1024, 64);

        Assert.Same(first, firstAgain);
        Assert.Equal(contents[512..640], first);
        Assert.Equal(contents[1024..1088], second);
        Assert.Equal(192, cache.CurrentSize);
        Assert.Equal(2, cache.Count);
    }

    [Fact(DisplayName = "A range larger than the whole cache is read uncached and never evicts the entries already held")]
    public void GetOrReadRange_RangeLargerThanCapacity_IsReadWithoutThrashingTheCache()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "raw.dng");
        var contents = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        File.WriteAllBytes(path, contents);
        var info = new FileInfo(path);
        var cache = new SourceBytesCache(1024);
        var publishAttempts = 0;
        var kept = cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 0, 128);
        cache.BeforePublishForTests = () => publishAttempts++;
        Assert.False(cache.CanCacheRange(2048));

        var oversized = cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 512, 2048);

        Assert.Equal(contents[512..2560], oversized);
        Assert.Equal(0, publishAttempts); // never offered to the cache
        Assert.Equal(1, cache.Count);
        Assert.Equal(128, cache.CurrentSize);
        Assert.Same(kept, cache.GetOrReadRange(path, info.Length, info.LastWriteTimeUtc.Ticks, 0, 128));
    }

    [Fact(DisplayName = "A source larger than a .NET array is rejected cleanly, before any file is opened")]
    public void GetOrRead_SourceLargerThanArrayLimit_ThrowsArgumentOutOfRangeNotOverflow()
    {
        var cache = new SourceBytesCache(1024);

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetOrRead(Path.Combine(_root, "never-opened.bin"), 3_000_000_000L, 1));
    }

    [Theory(DisplayName = "An uncacheable range larger than a .NET array is rejected cleanly, before any file is opened")]
    [InlineData(0)]
    [InlineData(1)]
    public void GetOrReadRange_CountAboveArrayLimitBypassingTheCache_ThrowsArgumentOutOfRangeNotOutOfMemory(int overshoot)
    {
        var cache = new SourceBytesCache(1024); // far too small to ever keep this range: it takes the uncacheable bypass
        var count = int.MaxValue - overshoot; // in (Array.MaxLength, int.MaxValue]
        Assert.True(count > Array.MaxLength);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cache.GetOrReadRange(Path.Combine(_root, "never-opened.bin"), 3_000_000_000L, 1, 0, count));
    }

    [Fact(DisplayName = "Prefetch caches a file that fits and reads nothing for a file larger than the whole cache")]
    public void TryPrefetch_SkipsFilesTheCacheCouldNeverKeep()
    {
        Directory.CreateDirectory(_root);
        var small = Path.Combine(_root, "small.bin");
        var big = Path.Combine(_root, "big.bin");
        File.WriteAllBytes(small, new byte[100]);
        File.WriteAllBytes(big, new byte[5000]);
        var cache = new SourceBytesCache(1024);
        Assert.True(cache.CapacityBytes >= 1024 && cache.CapacityBytes < 5000, "the fixture assumes a 1 KB cache");

        // An exclusive lock makes any attempt to read the big file throw a sharing violation.
        using var exclusive = new FileStream(big, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(cache.TryPrefetch(big));
        Assert.Equal(0, cache.Count);

        Assert.True(cache.TryPrefetch(small));
        Assert.Equal(1, cache.Count);
        Assert.Equal(100, cache.CurrentSize);
    }

    [Fact(DisplayName = "Source bytes cache clear and evict remove entries")]
    public void ClearAndEvictRemoveEntries()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "image.bin");
        File.WriteAllBytes(path, [9]);
        var cache = new SourceBytesCache(1024);
        cache.GetOrRead(path);
        cache.Evict(path);
        Assert.Equal(0, cache.Count);
        cache.GetOrRead(path);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Evicting one path leaves other cached sources untouched and the evicted path re-reads")]
    public void Evict_OnlyAffectsThatPath()
    {
        Directory.CreateDirectory(_root);
        var gone = Path.Combine(_root, "gone.bin");
        var kept = Path.Combine(_root, "kept.bin");
        File.WriteAllBytes(gone, [1]);
        File.WriteAllBytes(kept, [2]);
        var cache = new SourceBytesCache(1024);
        cache.GetOrRead(gone);
        var keptBytes = cache.GetOrRead(kept);

        cache.Evict(gone);

        Assert.Equal(1, cache.Count);
        Assert.Same(keptBytes, cache.GetOrRead(kept));
        cache.GetOrRead(gone); // evicting bumps only that path's version: a later read caches again
        Assert.Equal(2, cache.Count);
    }

    [Fact(DisplayName = "GetOrRead performs the real disk read synchronously on the calling thread (no Task.Run hop)")]
    public void GetOrRead_ReadsOnCallingThread()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sync.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var cache = new SourceBytesCache(1024);

        // Runs on a dedicated (non-pool) thread so a Task.Run-based implementation would provably move
        // the read to a different (pool) managed thread id; mutating GetOrRead back to Task.Run + GetAwaiter().GetResult()
        // makes this assertion fail.
        int? callingThreadId = null;
        var worker = new Thread(() =>
        {
            callingThreadId = Environment.CurrentManagedThreadId;
            cache.GetOrRead(path);
        });
        worker.Start();
        worker.Join();

        Assert.NotNull(callingThreadId);
        Assert.Equal(callingThreadId, cache.LastReadManagedThreadId);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}

