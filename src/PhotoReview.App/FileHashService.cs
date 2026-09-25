using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;
using PhotoReview.Core.Caching;
using PhotoReview.Imaging.Caching;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

public sealed class FileHashService
{
    private sealed record HashEntry(string Path, long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashEntry> _cache = new(
        16L * 1024 * 1024, entry => (entry.Path.Length * 2L) + 64, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private readonly SourceBytesCache? _sourceBytesCache;

    public FileHashService(SourceBytesCache? sourceBytesCache = null) => _sourceBytesCache = sourceBytesCache;

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (_cache.TryGet(fullPath, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Hash;
        var generation = Volatile.Read(ref _generation);
        // ADR 0005: callers (CompareViewModel, DuplicateFinder) start this on the UI thread, and
        // ComputeAndCacheAsync reads + hashes synchronously on the SourceBytesCache path, so the
        // whole computation runs on the thread pool.
        var lazy = _inFlight.GetOrAdd(fullPath, _ => new Lazy<Task<string>>(
            () => Task.Run(() => ComputeAndCacheAsync(fullPath, info.Length, info.LastWriteTimeUtc, generation, CancellationToken.None)),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(fullPath, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken);
    }

    private async Task<string> ComputeAndCacheAsync(string path, long length, DateTime lastWriteUtc, int generation, CancellationToken cancellationToken)
    {
        if (_sourceBytesCache is not null)
        {
            var bytes = _sourceBytesCache.GetOrRead(path);
            return CompleteIfUnchanged(path, length, lastWriteUtc, generation, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return CompleteIfUnchanged(path, length, lastWriteUtc, generation, hash);
    }

    /// <summary>
    /// Re-stats the file after hashing: a changed length/mtime means the hash may mix two versions, so it is rejected;
    /// otherwise it is cached unless <see cref="Clear"/> ran since the request started.
    /// </summary>
    private string CompleteIfUnchanged(string path, long length, DateTime lastWriteUtc, int generation, string hash)
    {
        var current = new FileInfo(path);
        if (current.Length != length || current.LastWriteTimeUtc != lastWriteUtc)
            throw UserFacingError.Localized(new IOException($"File changed while hashing: {path}"), () => Tr.ErrIoFileChangedWhileHashing(path));
        if (generation == Volatile.Read(ref _generation))
            _cache.Set(path, new HashEntry(path, length, lastWriteUtc, hash));
        return hash;
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }
}
