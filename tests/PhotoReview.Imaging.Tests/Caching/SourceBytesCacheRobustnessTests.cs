using System.IO;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class SourceBytesCacheRobustnessTests : IDisposable
{
    private readonly TempRoot _root = new("SourceBytesRobust");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "A zero-byte file is read as an empty array (not an error) and is served again from the cache")]
    public void ZeroByteFile_IsEmptyArray()
    {
        var path = _root.File("empty.jpg");
        var cache = new SourceBytesCache(1024);

        var first = cache.GetOrRead(path);
        var second = cache.GetOrRead(path);

        Assert.Empty(first);
        Assert.Same(first, second);
    }

    [Fact(DisplayName = "A failed read (missing file) is not cached or left in flight: the same path works once the file exists")]
    public void FailedRead_IsNotCached()
    {
        var path = _root.Combine("late.jpg");
        var cache = new SourceBytesCache(1024);

        Assert.ThrowsAny<IOException>(() => cache.GetOrRead(path));
        Assert.Equal(0, cache.Count);
        File.WriteAllBytes(path, [7, 8, 9]);

        Assert.Equal(new byte[] { 7, 8, 9 }, cache.GetOrRead(path));
    }

    [Fact(DisplayName = "A file larger than the whole capacity is returned but never cached, and does not push out smaller entries")]
    public void OversizedFile_IsReturnedButNotCached()
    {
        var small = _root.File("small.bin", new byte[100]);
        var big = _root.File("big.bin", new byte[5_000]);
        var cache = new SourceBytesCache(1_000);
        var smallBytes = cache.GetOrRead(small);

        var bigBytes = cache.GetOrRead(big);

        Assert.Equal(5_000, bigBytes.Length);
        Assert.Equal(1, cache.Count);
        Assert.Same(smallBytes, cache.GetOrRead(small));
    }

    [Fact(DisplayName = "A file rewritten with different content and timestamp is re-read: stale bytes are never served for a new identity")]
    public void ReplacedFile_IsReRead()
    {
        var path = _root.File("changing.bin", 1, 2, 3);
        var cache = new SourceBytesCache(1024);
        Assert.Equal(new byte[] { 1, 2, 3 }, cache.GetOrRead(path));

        File.WriteAllBytes(path, [9, 8, 7]); // same length: only the timestamp tells the versions apart
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(new byte[] { 9, 8, 7 }, cache.GetOrRead(path));
    }

    [Fact(DisplayName = "Sixteen threads released together on one uncached path all get the same array (one read is shared)")]
    public async Task ConcurrentReadersOfOnePath_ShareOneRead()
    {
        var path = _root.File("shared.bin", Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        var cache = new SourceBytesCache(1024 * 1024);
        using var barrier = new Barrier(16);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return cache.GetOrRead(path);
        }, TaskCreationOptions.LongRunning)));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "Readers racing Evict/Clear on the same paths always get the exact file bytes, and the cache never exceeds its capacity")]
    public async Task ReadersRacingEvictAndClear_StayCorrectAndBounded()
    {
        const int files = 6;
        const long capacity = 3 * 2048;
        var paths = Enumerable.Range(0, files).Select(i => _root.File($"f{i}.bin", Enumerable.Repeat((byte)(i + 1), 2048).ToArray())).ToArray();
        var cache = new SourceBytesCache(capacity);
        using var barrier = new Barrier(5);
        var stop = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var readers = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
        {
            var rng = new Random(worker);
            barrier.SignalAndWait();
            for (var i = 0; i < 800; i++)
            {
                var index = rng.Next(files);
                var bytes = cache.GetOrRead(paths[index]);
                if (bytes.Length != 2048 || bytes[0] != index + 1 || bytes[^1] != index + 1) failures.Add($"file {index}: wrong bytes");
                if (cache.CurrentSize > cache.CapacityBytes) failures.Add($"size {cache.CurrentSize} > capacity {cache.CapacityBytes}");
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        var evictor = Task.Factory.StartNew(() =>
        {
            var rng = new Random(99);
            barrier.SignalAndWait();
            while (Volatile.Read(ref stop) == 0)
            {
                if (rng.Next(8) == 0) cache.Clear(); else cache.Evict(paths[rng.Next(files)]);
            }
        }, TaskCreationOptions.LongRunning);

        await Task.WhenAll(readers);
        Volatile.Write(ref stop, 1);
        await evictor;

        Assert.Empty(failures);
        Assert.True(cache.CurrentSize <= cache.CapacityBytes);
    }
}
