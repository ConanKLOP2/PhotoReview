using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Caching;

namespace PhotoReview.Imaging.Caching;

/// <summary>Optional byte cache keyed by normalized path and source identity.</summary>
public sealed class SourceBytesCache
{
    private readonly BoundedLruCache<Key, byte[]> _cache;
    private readonly ConcurrentDictionary<Key, Lazy<Task<byte[]>>> _inFlight = new();
    private int _generation;

    public SourceBytesCache(long capacityBytes)
    {
        // IMG-11/R2-A-06: never claim more than 20% of physical RAM (so it cannot starve the preview cache).
        CapacityBytes = PhotoReview.Imaging.Preload.RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(
            capacityBytes, PhotoReview.Imaging.Preload.RamBudgetPolicy.GetPhysicalMemoryBytes());
        capacityBytes = CapacityBytes;
        _cache = new BoundedLruCache<Key, byte[]>(capacityBytes, bytes => bytes.LongLength);
    }

    /// <summary>Effective (post-clamp) capacity in bytes.</summary>
    public long CapacityBytes { get; }
    public long CurrentSize => _cache.CurrentSize;
    public int Count => _cache.Count;

    /// <summary>
    /// Returns the source bytes, reading the file if it is not cached yet.
    /// WARNING: this blocks the calling thread until the read completes (it waits on a thread-pool
    /// task). Call it only from a worker or dedicated decode thread -- never from a UI or other
    /// <c>SynchronizationContext</c>-bound thread (IMG-09).
    /// </summary>
    public byte[] GetOrRead(string path)
    {
        var key = CreateKey(path);
        if (_cache.TryGet(key, out var cached)) return cached;
        var generation = Volatile.Read(ref _generation);
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(
            () => Task.Run(() => ReadAndCache(key, generation)), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value.GetAwaiter().GetResult(); }
        finally { _inFlight.TryRemove(new KeyValuePair<Key, Lazy<Task<byte[]>>>(key, lazy)); }
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    public void Evict(string path)
    {
        // In-flight reads capture the generation before opening the file.  Advance it
        // before removing the entry so a read that completes after eviction cannot
        // republish bytes for the evicted source.
        Interlocked.Increment(ref _generation);
        var full = Path.GetFullPath(path);
        _cache.RemoveWhere(key => string.Equals(key.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    private byte[] ReadAndCache(Key key, int generation)
    {
        using var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException($"Unexpected EOF while reading {key.Path}");
            offset += read;
        }
        var current = new FileInfo(key.Path);
        if (current.Length != key.Length || current.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks)
            throw new IOException($"File changed while reading: {key.Path}");
        if (generation == Volatile.Read(ref _generation)) _cache.Set(key, bytes);
        return bytes;
    }

    private static Key CreateKey(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        return new Key(full, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private readonly record struct Key(string Path, long Length, long LastWriteUtcTicks);
}
