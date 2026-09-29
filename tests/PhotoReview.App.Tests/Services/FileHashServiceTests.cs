using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.App;
namespace PhotoReview.App.Tests.Services;

/// <summary>File hash caching and deduplication.</summary>
[Trait("Category", "HotPath")]
public sealed class FileHashServiceTests : IDisposable
{
    private readonly TempRoot _root = new("hash");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "File hash service caches and invalidates by file fingerprint")]
    public async Task FileHashServiceCachesAndInvalidates()
    {
        var fixture = _root.File("hash-fixture.bin", 1, 2, 3);
        var service = new FileHashService();
        var first = await service.GetAsync(fixture);
        var cached = await service.GetAsync(fixture);
        File.WriteAllBytes(fixture, [1, 2, 4]);
        // FileHashService fingerprints by (Length, LastWriteTimeUtc). Two same-size
        // writes issued back to back can land within the same filesystem timestamp
        // tick, so force a detectable mtime change instead of relying on wall-clock
        // granularity - otherwise this assertion is flaky under fast test runners.
        File.SetLastWriteTimeUtc(fixture, DateTime.UtcNow.AddSeconds(1));
        var changed = await service.GetAsync(fixture);
        Assert.True(first == cached && first != changed);
    }

    [Fact(DisplayName = "File hash service streams a file larger than the source-bytes cache instead of reading it whole")]
    public async Task FileHashServiceStreamsFileTooLargeForCache()
    {
        const int size = 24 * 1024 * 1024;
        var fixture = Path.Combine(_root.Path, "big.bin");
        var content = new byte[size];
        new Random(7).NextBytes(content);
        File.WriteAllBytes(fixture, content);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        content = [];
        var cache = new PhotoReview.Imaging.Caching.SourceBytesCache(1024 * 1024);
        Assert.False(cache.CanCache(size));
        var service = new FileHashService(cache);
        var actual = await service.GetAsync(fixture);
        Assert.Equal(expected, actual);
        Assert.Null(cache.LastReadManagedThreadId); // the cache never read the file whole
    }

    [Theory(DisplayName = "File hash service streams a RAW file instead of routing it through the whole-file byte cache")]
    [InlineData("shot.cr3")]
    [InlineData("shot.NEF")]
    public async Task FileHashServiceStreamsRawFilesInsteadOfUsingTheWholeFileByteCache(string name)
    {
        var fixture = Path.Combine(_root.Path, name);
        var content = new byte[64 * 1024];
        new Random(11).NextBytes(content);
        File.WriteAllBytes(fixture, content);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        var cache = new PhotoReview.Imaging.Caching.SourceBytesCache(1024 * 1024);
        Assert.True(cache.CanCache(content.Length)); // it would fit: only the RAW rule keeps it out
        var service = new FileHashService(cache);

        var actual = await service.GetAsync(fixture);

        Assert.Equal(expected, actual);
        Assert.Null(cache.LastReadManagedThreadId);
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "File hash service still uses the byte cache for a small non-RAW file")]
    public async Task FileHashServiceUsesTheByteCacheForSmallNonRawFiles()
    {
        var fixture = Path.Combine(_root.Path, "small.jpg");
        var content = new byte[4096];
        new Random(5).NextBytes(content);
        File.WriteAllBytes(fixture, content);
        var cache = new PhotoReview.Imaging.Caching.SourceBytesCache(1024 * 1024);
        var service = new FileHashService(cache);

        var actual = await service.GetAsync(fixture);

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), actual);
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "File hash service deduplicates concurrent reads")]
    public async Task FileHashServiceDeduplicatesConcurrentReads()
    {
        var fixture = _root.File("hash-fixture.bin", 1, 2, 4);
        var service = new FileHashService();
        var expected = await service.GetAsync(fixture);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => service.GetAsync(fixture)));
        Assert.True(concurrent.All(hash => hash == expected));
    }
}

