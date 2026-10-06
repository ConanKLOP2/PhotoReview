using System.IO;

namespace PhotoReview.Imaging.Tests.Properties;

/// <summary>
/// Random range reads against a real temp file: every range returns exactly the file's slice (ranges that share an offset or
/// a length never alias), the cache never exceeds its capacity, a range just read is cached iff it can be cached, an eviction
/// drops every range of the path, and out-of-file ranges are refused.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class SourceBytesCacheRangePropertyTests : IDisposable
{
    private const int FileLength = 4096;
    private const int Capacity = 1500;

    private readonly TempRoot _root = new("range-prop");
    private readonly byte[] _content = new byte[FileLength];
    private readonly string _path;
    private readonly long _ticks;

    public SourceBytesCacheRangePropertyTests()
    {
        new Random(99).NextBytes(_content);
        _path = _root.File("source.bin", _content);
        _ticks = new FileInfo(_path).LastWriteTimeUtc.Ticks;
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "GetOrReadRange returns the exact slice for random ranges; capacity, cacheability and eviction hold after every step")]
    public void RandomRanges_ReturnExactSlices_AndRespectCapacity()
    {
        PropertyRunner.Check("SourceBytesCache ranges", iterations: 15, (rng, _) =>
        {
            var cache = new SourceBytesCache(Capacity);
            Assert.Equal(Capacity, cache.CapacityBytes); // not clamped on any machine this suite runs on

            for (var step = 0; step < 80; step++)
            {
                var offset = rng.Next(0, FileLength + 1);
                var count = rng.Next(5) == 0 ? rng.Next(0, FileLength - offset + 1) : rng.Next(0, Math.Min(400, FileLength - offset) + 1);
                var expected = _content.AsSpan(offset, count).ToArray();

                switch (rng.Next(8))
                {
                    case <= 4:
                        {
                            var bytes = cache.GetOrReadRange(_path, FileLength, _ticks, offset, count);
                            Assert.Equal(expected, bytes);
                            if (count > 0)
                            {
                                var cached = cache.TryGetRange(_path, FileLength, _ticks, offset, count, out var again);
                                Assert.Equal(cache.CanCacheRange(count), cached);
                                if (cached) Assert.Equal(expected, again);
                            }
                            break;
                        }
                    case 5:
                        if (cache.TryGetRange(_path, FileLength, _ticks, offset, count, out var hit)) Assert.Equal(expected, hit);
                        break;
                    case 6:
                        cache.Evict(_path);
                        Assert.Equal(0, cache.Count);
                        Assert.Equal(0, cache.CurrentSize);
                        Assert.False(cache.TryGetRange(_path, FileLength, _ticks, offset, Math.Max(1, count), out byte[] _));
                        break;
                    default:
                        if (rng.Next(6) == 0) { cache.Clear(); Assert.Equal(0, cache.Count); }
                        break;
                }

                Assert.True(cache.CurrentSize <= cache.CapacityBytes, $"size {cache.CurrentSize} > capacity {cache.CapacityBytes}");
            }
        }, fixedSeeds: 2);
    }

    [Fact(DisplayName = "Ranges outside the file are refused, never read")]
    public void OutOfFileRanges_AreRefused()
    {
        var cache = new SourceBytesCache(Capacity);
        PropertyRunner.Check("SourceBytesCache invalid ranges", iterations: 500, (rng, _) =>
        {
            var (offset, count) = rng.Next(3) switch
            {
                0 => (rng.Next(FileLength + 1, FileLength + 100), rng.Next(0, 10)),
                1 => (rng.Next(0, FileLength + 1), rng.Next(FileLength + 1, FileLength + 100)),
                _ => (rng.Next(-50, 0), rng.Next(0, 10)),
            };
            if (offset >= 0 && count <= FileLength - offset) return; // valid combination, nothing to refuse

            Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetOrReadRange(_path, FileLength, _ticks, offset, count));
            Assert.Equal(0, cache.Count);
        });
    }
}
