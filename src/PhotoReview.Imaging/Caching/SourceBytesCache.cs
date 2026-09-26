using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Localization;

namespace PhotoReview.Imaging.Caching;

/// <summary>Optional byte cache keyed by normalized path and source identity.</summary>
public sealed class SourceBytesCache
{
    private readonly BoundedLruCache<Key, byte[]> _cache;
    private readonly ConcurrentDictionary<Key, Lazy<byte[]>> _inFlight = new();
    private int _generation;
    // Per-path eviction versions: evicting one moved/deleted file must not invalidate in-flight reads of OTHER paths
    // (a global bump would make them skip caching and re-read from disk). One small entry per evicted path.
    private readonly ConcurrentDictionary<string, int> _pathVersions = new(StringComparer.OrdinalIgnoreCase);

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
    /// Test-only diagnostic: the managed thread id that actually performed the most recent real disk
    /// read (as opposed to a cache hit). Lets a test assert <see cref="GetOrRead(string)"/> runs the
    /// read on the calling thread instead of hopping to a thread-pool thread.
    /// </summary>
    internal int? LastReadManagedThreadId { get; private set; }

    /// <summary>
    /// Returns the source bytes, reading the file if it is not cached yet.
    /// WARNING: this reads synchronously on the calling thread (no thread-pool hop). Call it only
    /// from a worker or dedicated decode thread -- never from the UI thread or any other
    /// <c>SynchronizationContext</c>-bound thread (IMG-09): a UI-thread call would block the message
    /// pump for the duration of the disk read.
    /// </summary>
    public byte[] GetOrRead(string path) => GetOrRead(CreateKey(path));

    /// <summary>
    /// Read-ahead entry point: caches the file's bytes unless this cache could never keep them (see <see cref="CanCache"/>),
    /// in which case nothing is read at all. Returns whether the file is (now) cached. Same threading rules as <see cref="GetOrRead(string)"/>.
    /// </summary>
    public bool TryPrefetch(string path)
    {
        var key = CreateKey(path);
        if (!CanCache(key.Length)) return false;
        GetOrRead(key);
        return true;
    }

    private byte[] GetOrRead(Key key)
    {
        if (_cache.TryGet(key, out var cached)) return cached;
        System.Diagnostics.Debug.Assert(SynchronizationContext.Current is null,
            "SourceBytesCache.GetOrRead must never be called from a UI (or other SynchronizationContext-bound) thread -- it reads synchronously.");
        var generation = Volatile.Read(ref _generation);
        var pathVersion = _pathVersions.GetValueOrDefault(key.Path);
        // Lazy<T> (ExecutionAndPublication) runs ReadAndCache on whichever caller's thread wins the race to
        // create this entry, and every other concurrent caller for the same key blocks on that same Lazy<T>
        // instead of starting its own read -- same dedup-by-identity as before, but without a Task.Run hop:
        // every caller is already a worker/decode thread (see the threading warning on GetOrRead), so the
        // read happens on the calling thread instead of costing a second thread-pool slot per call.
        // Lazy<T> also caches a thrown exception and replays it to every waiter of this lazy (matching the
        // old Task-based behavior); the finally below removes the entry either way, so the next GetOrRead
        // for this key (e.g. after the file reappears) starts a fresh attempt rather than replaying a stale one.
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<byte[]>(
            () => ReadAndCache(key, generation, pathVersion), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        finally { _inFlight.TryRemove(new KeyValuePair<Key, Lazy<byte[]>>(key, lazy)); }
    }

    /// <summary>
    /// False for a file this cache could never keep: larger than its whole capacity, or larger than a .NET array can be.
    /// Callers should stream such a file instead of reading it whole into RAM only to drop it (or, past 2 GB, to fail with an
    /// <see cref="OverflowException"/>).
    /// </summary>
    public bool CanCache(long length) => length <= CapacityBytes && length <= Array.MaxLength;

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    public void Evict(string path)
    {
        // In-flight reads of this path capture its version before opening the file. Advance it before removing
        // the entry so a read that completes after eviction cannot republish bytes for the evicted source.
        var full = Path.GetFullPath(path);
        _pathVersions.AddOrUpdate(full, 1, (_, version) => version + 1);
        _cache.RemoveWhere(key => string.Equals(key.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    private byte[] ReadAndCache(Key key, int generation, int pathVersion)
    {
        LastReadManagedThreadId = Environment.CurrentManagedThreadId;
        using var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw UserFacingError.Localized(new EndOfStreamException($"Unexpected EOF while reading {key.Path}"), () => Tr.ErrIoUnexpectedEof(key.Path));
            offset += read;
        }
        var current = new FileInfo(key.Path);
        if (current.Length != key.Length || current.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks)
            throw UserFacingError.Localized(new IOException($"File changed while reading: {key.Path}"), () => Tr.ErrIoFileChangedWhileReading(key.Path));
        if (generation == Volatile.Read(ref _generation) && pathVersion == _pathVersions.GetValueOrDefault(key.Path)) _cache.Set(key, bytes);
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
