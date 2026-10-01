using System.IO;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>RV-T42 (TryGetRange) and RV-T44 (Evict while a read of that path is blocked) gap tests.</summary>
[Trait("Category", "HotPath")]
public sealed class SourceBytesCacheGapTests : IDisposable
{
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("SourceBytesGap");

    public void Dispose() => _root.Dispose();

    private (string Path, long Length, long Ticks, byte[] Content) Fixture(string name, int size = 4096)
    {
        var content = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        var path = _root.File(name, content);
        var info = new FileInfo(path);
        return (path, info.Length, info.LastWriteTimeUtc.Ticks, content);
    }

    [Fact(DisplayName = "TryGetRange returns the cached bytes of a range after GetOrReadRange")]
    public void TryGetRange_AfterGetOrReadRange_IsAHit()
    {
        var f = Fixture("raw.dng");
        var cache = new SourceBytesCache(1024 * 1024);
        var read = cache.GetOrReadRange(f.Path, f.Length, f.Ticks, 512, 128);

        Assert.True(cache.TryGetRange(f.Path, f.Length, f.Ticks, 512, 128, out var hit));

        Assert.Same(read, hit);
        Assert.Equal(f.Content[512..640], hit);
    }

    [Fact(DisplayName = "TryGetRange is a miss for a range never read, a different range, or another source identity, and reads nothing")]
    public void TryGetRange_NotCached_IsAMissWithoutReading()
    {
        var f = Fixture("raw.dng");
        var cache = new SourceBytesCache(1024 * 1024);
        cache.GetOrReadRange(f.Path, f.Length, f.Ticks, 512, 128);

        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks, 0, 128, out _));
        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks, 512, 64, out _));
        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks + 1, 512, 128, out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "TryGetRange leaves the non-null empty array in the out value on a cache miss")]
    public void TryGetRange_CacheMiss_OutValueIsEmptyNotNull()
    {
        var f = Fixture("raw.dng");
        var cache = new SourceBytesCache(1024 * 1024);

        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks, 0, 128, out var bytes));

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }

    [Theory(DisplayName = "TryGetRange rejects non-positive counts and ranges outside the source")]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 16)]
    [InlineData(5000, 16)]   // offset past the end
    [InlineData(4090, 16)]   // runs past the end
    public void TryGetRange_InvalidRange_ReturnsFalse(long offset, int count)
    {
        var f = Fixture("raw.dng");
        var cache = new SourceBytesCache(1024 * 1024);
        cache.GetOrRead(f.Path); // the whole file is cached: only the range validation can say no

        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks, offset, count, out var bytes));
        Assert.Empty(bytes);
    }

    [Fact(DisplayName = "TryGetRange is a miss again after Evict of that path")]
    public void TryGetRange_AfterEvict_IsAMiss()
    {
        var f = Fixture("raw.dng");
        var cache = new SourceBytesCache(1024 * 1024);
        cache.GetOrReadRange(f.Path, f.Length, f.Ticks, 512, 128);
        Assert.True(cache.TryGetRange(f.Path, f.Length, f.Ticks, 512, 128, out _));

        cache.Evict(f.Path);

        Assert.False(cache.TryGetRange(f.Path, f.Length, f.Ticks, 512, 128, out _));
    }

    [Fact(DisplayName = "Evict of a path whose read is blocked after the read: bytes returned but not cached; an unrelated path stays cached")]
    public void Evict_WhileReadInFlight_ReadNotCached_OtherPathUntouched()
    {
        var evicted = _root.File("evicted.bin", new byte[64]);
        var other = _root.File("other.bin", new byte[32]);
        var cache = new SourceBytesCache(1024 * 1024);
        var otherBytes = cache.GetOrRead(other);
        using var readDone = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.AfterReadForTests = () => { readDone.Set(); release.Wait(RaceTimeout); };
        byte[]? result = null;
        var reader = new Thread(() => result = cache.GetOrRead(evicted));
        reader.Start();
        try
        {
            Assert.True(readDone.Wait(RaceTimeout), "Reader never finished its read.");
            cache.AfterReadForTests = null;
            cache.Evict(evicted);
        }
        finally { release.Set(); }
        Assert.True(reader.Join(RaceTimeout), "Reader thread did not finish after release.");

        Assert.NotNull(result);
        Assert.Equal(64, result!.Length);
        Assert.Equal(1, cache.Count); // only the unrelated path
        Assert.Same(otherBytes, cache.GetOrRead(other));
        Assert.Equal(32, cache.CurrentSize);
    }
}
