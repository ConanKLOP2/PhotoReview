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

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}

