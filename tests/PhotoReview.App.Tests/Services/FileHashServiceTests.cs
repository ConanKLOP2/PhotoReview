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

