using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Localization;

namespace PhotoReview.Imaging.Caching;

/// <summary>Optional byte cache keyed by normalized path and source identity.</summary>
public sealed class SourceBytesCache
{
    private readonly ISourceReader _sourceReader;
    private readonly BoundedLruCache<RangeKey, byte[]> _cache;
    private readonly ConcurrentDictionary<RangeKey, Lazy<byte[]>> _inFlight = new();
    private int _generation;
    // Makes "version still current -> Set" atomic against Evict/Clear's "bump -> remove", so a read that finishes
    // after an eviction can never republish the evicted bytes. Held only around in-memory work, never a disk read.
    private readonly object _publishGate = new();
    // Test seam: runs inside the gate between the version check and the Set.
    internal Action? BeforePublishForTests { get; set; }
    // Test seam: runs after the read loop, just before the Length/LastWriteTime re-check, so a test can change the file at exactly that point.
    internal Action? AfterReadForTests { get; set; }
    // Per-path eviction versions: evicting one moved/deleted file must not invalidate in-flight reads of OTHER paths
    // (a global bump would make them skip caching and re-read from disk). One small entry per evicted path.
    private readonly ConcurrentDictionary<string, int> _pathVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most per-path versions kept before <see cref="Evict"/> switches to a global generation bump.</summary>
    internal const int MaxTrackedPathVersions = 4096;

    /// <summary>Test seam (RV-I16): number of per-path eviction versions still tracked.</summary>
    internal int PathVersionCountForTests => _pathVersions.Count;

    /// <param name="sourceReader">
    /// Q-R29 option C-2 seam: null (every existing caller) uses <see cref="PhysicalSourceReader"/>,
    /// byte-for-byte the direct <see cref="FileStream"/> this cache opened before the seam existed.
    /// </param>
    public SourceBytesCache(long capacityBytes, ISourceReader? sourceReader = null)
    {
        _sourceReader = sourceReader ?? PhysicalSourceReader.Instance;
        // IMG-11/R2-A-06: never claim more than 20% of physical RAM (so it cannot starve the preview cache).
        CapacityBytes = PhotoReview.Imaging.Preload.RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(
            capacityBytes, PhotoReview.Imaging.Preload.RamBudgetPolicy.GetPhysicalMemoryBytes());
        capacityBytes = CapacityBytes;
        _cache = new BoundedLruCache<RangeKey, byte[]>(capacityBytes, bytes => bytes.LongLength);
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
    public byte[] GetOrRead(string path) => GetOrRead(path, SourceReadPriority.Viewer);

    /// <summary>
    /// Q-R29 option C-2: <paramref name="priority"/> is metadata only (see <see cref="SourceReadPriority"/>)
    /// for a perf-harness throttling decorator; it never changes which bytes are cached or returned, and
    /// the default (<see cref="PhysicalSourceReader"/>) ignores it -- pure refactor, no behavior change.
    /// </summary>
    public byte[] GetOrRead(string path, SourceReadPriority priority) => GetOrRead(CreateKey(path), priority);

    /// <summary>
    /// Returns only the requested byte range and caches it under the source identity plus its offset and length.
    /// Used for embedded previews so a small range of a large RAW file never causes the whole file to be read or cached.
    /// </summary>
    public byte[] GetOrReadRange(string path, long length, long lastWriteUtcTicks, long offset, int count,
        SourceReadPriority priority = SourceReadPriority.Viewer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset < 0 || count < 0 || offset > length || count > length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Requested byte range is outside the source file.");
        // Past the .NET array limit no read can succeed. Checked here, not only in GetOrRead(RangeKey): the uncacheable-range
        // bypass below skips that method and would otherwise reach the allocation and fail with OutOfMemory/Overflow.
        if (count > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The requested range is larger than a .NET array can hold; stream it instead.");
        var key = CreateRangeKey(path, length, lastWriteUtcTicks, offset, count);
        if (count == 0) return [];
        // A range the cache could never keep is read straight through: no in-flight entry and no publish attempt, so it
        // can never displace the ranges the viewer relies on (the LRU would drop it anyway, after the bookkeeping).
        if (!CanCacheRange(count)) return ReadAndCache(key, generation: 0, pathVersion: 0, priority, publish: false);
        return GetOrRead(key, priority);
    }

    /// <summary>
    /// Returns the cached bytes of this exact range without reading anything; false when the range is not (or no longer)
    /// cached. Lets a caller tell a cache hit (0 source bytes read) from a real read when accounting source I/O.
    /// </summary>
    public bool TryGetRange(string path, long length, long lastWriteUtcTicks, long offset, int count, out byte[] bytes)
    {
        bytes = [];
        if (offset < 0 || count <= 0 || offset > length || count > length - offset) return false;
        // Read into a local: TryGet nulls its out value on a miss, which must not leak into the non-null `bytes`.
        if (!_cache.TryGet(CreateRangeKey(path, length, lastWriteUtcTicks, offset, count), out var found)) return false;
        bytes = found;
        return true;
    }

    /// <summary>
    /// Read-ahead entry point: caches the file's bytes unless this cache could never keep them (see <see cref="CanCache"/>),
    /// in which case nothing is read at all. Returns whether the file is (now) cached. Same threading rules as <see cref="GetOrRead(string)"/>.
    /// </summary>
    public bool TryPrefetch(string path)
    {
        var key = CreateKey(path);
        if (!CanCache(key.SourceLength)) return false;
        GetOrRead(key, SourceReadPriority.Preload);
        return true;
    }

    private byte[] GetOrRead(RangeKey key, SourceReadPriority priority)
    {
        // Past the .NET array limit no read can succeed; fail cleanly instead of an OverflowException from the cast below.
        if (key.Count > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(key), key.Count, "The requested source is larger than a .NET array can hold; stream it instead (see CanCache).");
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
        // Priority is captured from whichever caller wins the race to create this Lazy -- same "first caller
        // decides" semantics the dedup itself already has; a joiner's own priority is not consulted (it is
        // waiting for the same bytes either way, not starting its own read).
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<byte[]>(
            () => ReadAndCache(key, generation, pathVersion, priority), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        finally { _inFlight.TryRemove(new KeyValuePair<RangeKey, Lazy<byte[]>>(key, lazy)); }
    }

    /// <summary>
    /// False for a file this cache could never keep: larger than its whole capacity, or larger than a .NET array can be.
    /// Callers should stream such a file instead of reading it whole into RAM only to drop it (or, past 2 GB, to fail with an
    /// <see cref="OverflowException"/>).
    /// </summary>
    public bool CanCache(long length) => length <= CapacityBytes && length <= Array.MaxLength;

    /// <summary>Whether the byte cache can retain this range without exceeding its capacity.</summary>
    public bool CanCacheRange(long count) => count >= 0 && count <= CapacityBytes && count <= Array.MaxLength;

    public void Clear()
    {
        lock (_publishGate)
        {
            // RV-I16: the generation bump below already invalidates every in-flight read, so per-path versions are no
            // longer needed. Cleared BEFORE the bump: a reader that sees the new generation then reads version 0 (or a
            // later Evict's), never a pre-Clear version that would make its publish fail spuriously.
            _pathVersions.Clear();
            Interlocked.Increment(ref _generation);
            _cache.Clear();
        }
    }

    public void Evict(string path)
    {
        // In-flight reads of this path capture its version before opening the file. Advance it before removing
        // the entry so a read that completes after eviction cannot republish bytes for the evicted source.
        var full = Path.GetFullPath(path);
        lock (_publishGate)
        {
            if (_pathVersions.Count >= MaxTrackedPathVersions && !_pathVersions.ContainsKey(full))
            {
                // Bound the map (a long session evicts many distinct paths): fall back to the global invalidation Clear uses.
                // Bumping the generation makes every in-flight read skip its publish (it only costs a re-read), exactly what the
                // per-path versions guarantee for the evicted path, so the dropped versions are no longer needed.
                _pathVersions.Clear();
                Interlocked.Increment(ref _generation);
            }
            else
            {
                _pathVersions.AddOrUpdate(full, 1, (_, version) => version + 1);
            }
            _cache.RemoveWhere(key => string.Equals(key.Path, full, StringComparison.OrdinalIgnoreCase));
        }
    }

    private byte[] ReadAndCache(RangeKey key, int generation, int pathVersion, SourceReadPriority priority, bool publish = true)
    {
        LastReadManagedThreadId = Environment.CurrentManagedThreadId;
        using var stream = _sourceReader.OpenSource(key.Path, priority);
        if (stream.Length != key.SourceLength)
            throw UserFacingError.Localized(new IOException($"File changed while reading: {key.Path}"), () => Tr.ErrIoFileChangedWhileReading(key.Path));
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)key.Count));
        stream.Seek(key.Offset, SeekOrigin.Begin);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw UserFacingError.Localized(new EndOfStreamException($"Unexpected EOF while reading {key.Path}"), () => Tr.ErrIoUnexpectedEof(key.Path));
            offset += read;
        }
        AfterReadForTests?.Invoke();
        var current = new FileInfo(key.Path);
        if (current.Length != key.SourceLength || current.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks)
            throw UserFacingError.Localized(new IOException($"File changed while reading: {key.Path}"), () => Tr.ErrIoFileChangedWhileReading(key.Path));
        if (!publish) return bytes;
        lock (_publishGate)
        {
            if (generation == Volatile.Read(ref _generation) && pathVersion == _pathVersions.GetValueOrDefault(key.Path))
            {
                BeforePublishForTests?.Invoke();
                _cache.Set(key, bytes);
            }
        }
        return bytes;
    }

    private static RangeKey CreateKey(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        return new RangeKey(full, info.Length, info.LastWriteTimeUtc.Ticks, 0, info.Length);
    }

    private static RangeKey CreateRangeKey(string path, long length, long lastWriteUtcTicks, long offset, int count) =>
        new(Path.GetFullPath(path), length, lastWriteUtcTicks, offset, count);

    private readonly record struct RangeKey(string Path, long SourceLength, long LastWriteUtcTicks, long Offset, long Count);
}
