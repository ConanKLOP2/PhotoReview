using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="SourceBytesCache"/> (docs/MUTATION-TESTING.md): range-validation edges,
/// the injected source reader, partial reads, the per-path eviction version and the capacity predicates.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class SourceBytesCacheMutationTests : IDisposable
{
    private readonly TempRoot _root = new("SourceBytesMut");

    public void Dispose() => _root.Dispose();

    /// <summary>Serves fixed bytes (not the disk's) in chunks of at most <c>chunk</c> bytes; <c>Length</c> can be forced.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk, long? lengthOverride = null, int? eofAfter = null) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => lengthOverride ?? data.Length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((long)offset + count, buffer.Length);
            var limit = eofAfter ?? data.Length;
            var n = (int)Math.Max(0, Math.Min(Math.Min(count, chunk), limit - _pos));
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => Length + offset,
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeReader(Func<Stream> open) : ISourceReader
    {
        public int Opens { get; private set; }
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024)
        {
            Opens++;
            return open();
        }
    }

    private static byte[] Pattern(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    private (string Path, long Length, long Ticks, byte[] Content) Fixture(string name, int size = 4096)
    {
        var content = Pattern(size);
        var path = _root.File(name, content);
        var info = new FileInfo(path);
        return (path, info.Length, info.LastWriteTimeUtc.Ticks, content);
    }

    [Fact(DisplayName = "An injected source reader is the one the cache reads through (not the physical file reader)")]
    public void GetOrRead_InjectedReader_ServesItsBytes()
    {
        var f = Fixture("a.bin", 64);
        var served = Enumerable.Repeat((byte)0xEE, 64).ToArray();
        var reader = new FakeReader(() => new ChunkedStream(served, 1024));
        var cache = new SourceBytesCache(1024 * 1024, reader);

        var bytes = cache.GetOrRead(f.Path);

        Assert.Equal(served, bytes);
        Assert.Equal(1, reader.Opens);
    }

    [Fact(DisplayName = "A source whose Read returns short chunks is assembled completely and correctly")]
    public void GetOrRead_ShortReads_AreAssembledInOrder()
    {
        var f = Fixture("short.bin", 100);
        var reader = new FakeReader(() => new ChunkedStream(f.Content, 7));
        var cache = new SourceBytesCache(1024 * 1024, reader);

        var bytes = cache.GetOrRead(f.Path);

        Assert.Equal(f.Content, bytes);
    }

    [Fact(DisplayName = "A source that ends before its stated length fails with a clean EOF error and caches nothing")]
    public void GetOrRead_EarlyEof_ThrowsEndOfStreamAndIsNotCached()
    {
        var f = Fixture("eof.bin", 100);
        var reader = new FakeReader(() => new ChunkedStream(f.Content, 10, eofAfter: 40));
        var cache = new SourceBytesCache(1024 * 1024, reader);

        Assert.ThrowsAny<EndOfStreamException>(() => cache.GetOrRead(f.Path));

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "GetOrReadRange of an empty range at the end of the source (offset == length, count 0) returns empty without reading or caching")]
    public void GetOrReadRange_EmptyRangeAtEnd_ReturnsEmptyWithoutReading()
    {
        var f = Fixture("end.bin");
        var reader = new FakeReader(() => throw new InvalidOperationException("must not open"));
        var cache = new SourceBytesCache(1024 * 1024, reader);

        Assert.Empty(cache.GetOrReadRange(f.Path, f.Length, f.Ticks, f.Length, 0));
        Assert.Empty(cache.GetOrReadRange(f.Path, f.Length, f.Ticks, 0, 0));

        Assert.Equal(0, reader.Opens);
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "GetOrReadRange of the last bytes (offset == length - count) is valid and returns exactly those bytes")]
    public void GetOrReadRange_RangeEndingExactlyAtEnd_IsValid()
    {
        var f = Fixture("tail.bin");
        var cache = new SourceBytesCache(1024 * 1024);

        var tail = cache.GetOrReadRange(f.Path, f.Length, f.Ticks, f.Length - 16, 16);

        Assert.Equal(f.Content[^16..], tail);
    }

    [Theory(DisplayName = "GetOrReadRange rejects negative, past-the-end and overrunning ranges with ArgumentOutOfRange(offset) before touching the source")]
    [InlineData(-1L, 5)]       // negative offset only
    [InlineData(0L, -1)]       // negative count only
    [InlineData(4097L, 0)]     // offset past the end
    [InlineData(0L, 4097)]     // count longer than the file
    [InlineData(4090L, 16)]    // starts inside, runs past the end (offset + count > length, offset <= length)
    [InlineData(4096L, 1)]     // starts exactly at the end with a byte requested
    public void GetOrReadRange_InvalidRange_Throws(long offset, int count)
    {
        var f = Fixture("bad.bin");
        var reader = new FakeReader(() => throw new InvalidOperationException("must not open"));
        var cache = new SourceBytesCache(1024 * 1024, reader);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetOrReadRange(f.Path, f.Length, f.Ticks, offset, count));

        Assert.Equal("offset", ex.ParamName);
        Assert.Equal(0, reader.Opens);
    }

    [Fact(DisplayName = "A count of exactly Array.MaxLength passes validation (fails later on the source), one more is rejected up front")]
    public void GetOrReadRange_CountAtArrayMaxLength_IsNotRejectedByValidation()
    {
        // The fake source reports Length 1, so the read fails with the file-changed IOException BEFORE any 2 GB allocation.
        var reader = new FakeReader(() => new ChunkedStream([1], 1));
        var cache = new SourceBytesCache(1024 * 1024, reader);
        var path = _root.Combine("never-read.bin");

        Assert.ThrowsAny<IOException>(() => cache.GetOrReadRange(path, Array.MaxLength, 1, 0, Array.MaxLength));
        Assert.Equal(1, reader.Opens);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            cache.GetOrReadRange(path, Array.MaxLength + 1L, 1, 0, Array.MaxLength + 1));
        Assert.Equal("count", ex.ParamName);
        Assert.Equal(1, reader.Opens);
    }

    [Fact(DisplayName = "TryGetRange is a hit for a range cached at offset 0")]
    public void TryGetRange_OffsetZero_IsAHit()
    {
        var f = Fixture("zero.bin");
        var cache = new SourceBytesCache(1024 * 1024);
        var read = cache.GetOrReadRange(f.Path, f.Length, f.Ticks, 0, 128);

        Assert.True(cache.TryGetRange(f.Path, f.Length, f.Ticks, 0, 128, out var hit));

        Assert.Same(read, hit);
    }

    [Fact(DisplayName = "CanCache and CanCacheRange accept exactly up to the capacity (and a zero-length range), not one byte more")]
    public void CanCache_CapacityBoundary()
    {
        var cache = new SourceBytesCache(1024);

        Assert.True(cache.CanCache(cache.CapacityBytes));
        Assert.False(cache.CanCache(cache.CapacityBytes + 1));
        Assert.True(cache.CanCacheRange(0));
        Assert.True(cache.CanCacheRange(cache.CapacityBytes));
        Assert.False(cache.CanCacheRange(cache.CapacityBytes + 1));
        Assert.False(cache.CanCacheRange(-1));
        Assert.False(cache.CanCache(long.MaxValue));
    }

    [Fact(DisplayName = "CanCache and CanCacheRange accept exactly Array.MaxLength when the capacity allows it, never more")]
    public void CanCache_ArrayMaxLengthBoundary()
    {
        var cache = new SourceBytesCache(long.MaxValue);

        // The capacity is clamped to 20% of physical RAM; on a machine too small for a 2 GB array the boundary is unreachable.
        if (cache.CapacityBytes > Array.MaxLength)
        {
            Assert.True(cache.CanCache(Array.MaxLength));
            Assert.True(cache.CanCacheRange(Array.MaxLength));
        }
        Assert.False(cache.CanCache(Array.MaxLength + 1L));
        Assert.False(cache.CanCacheRange(Array.MaxLength + 1L));
    }

    [Fact(DisplayName = "Two Evicts of one path while a read of it is in flight still stop that read from publishing")]
    public void Evict_TwiceDuringRead_DoesNotRepublish()
    {
        var f = Fixture("twice.bin", 256);
        var cache = new SourceBytesCache(1024 * 1024);
        // Runs on the reading thread after the bytes are read and before the publish check: a deterministic "evicted twice mid-read".
        cache.AfterReadForTests = () =>
        {
            cache.AfterReadForTests = null;
            cache.Evict(f.Path);
            cache.Evict(f.Path);
        };

        var bytes = cache.GetOrRead(f.Path);

        Assert.Equal(f.Content, bytes);
        Assert.Equal(0, cache.Count);
        Assert.Equal(f.Content, cache.GetOrRead(f.Path)); // a later read caches normally
        Assert.Equal(1, cache.Count);
    }
}
