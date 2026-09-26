using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;
using PhotoReview.Core.Caching;
using PhotoReview.Imaging.Caching;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

/// <summary>Content-hash lookup used by the duplicate check; a seam so tests can block/cancel a hash deterministically.</summary>
public interface IFileHasher
{
    Task<string> GetAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class FileHashService : IFileHasher
{
    /// <summary>One shared computation; it is cancelled only when every caller waiting on it has cancelled.</summary>
    private sealed class InFlight : IDisposable
    {
        public readonly CancellationTokenSource Cts = new();
        public Lazy<Task<string>>? Lazy;
        public int Waiters;

        public void CancelQuietly()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { } // the computation already finished and released it
        }

        public void Dispose() => Cts.Dispose();
    }

    private sealed record HashEntry(string Path, long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashEntry> _cache = new(
        16L * 1024 * 1024, entry => (entry.Path.Length * 2L) + 64, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InFlight> _inFlight = new(StringComparer.OrdinalIgnoreCase);
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ADR 0005: callers (CompareViewModel, DuplicateFinder) start this on the UI thread, and
            // ComputeAndCacheAsync reads + hashes synchronously on the SourceBytesCache path, so the
            // whole computation runs on the thread pool.
            var entry = _inFlight.GetOrAdd(fullPath, _ =>
            {
                var created = new InFlight();
                created.Lazy = new Lazy<Task<string>>(
                    () => Task.Run(() => ComputeAndCacheAsync(fullPath, info.Length, info.LastWriteTimeUtc, generation, created.Cts.Token)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                return created;
            });
            Interlocked.Increment(ref entry.Waiters);
            var task = entry.Lazy!.Value;
            _ = task.ContinueWith(
                _ =>
                {
                    _inFlight.TryRemove(new KeyValuePair<string, InFlight>(fullPath, entry));
                    entry.Dispose();
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                return await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Last waiter gone: stop the read so the file handle is released promptly.
                if (Interlocked.Decrement(ref entry.Waiters) == 0) entry.CancelQuietly();
                throw;
            }
            catch (OperationCanceledException) when (entry.Cts.IsCancellationRequested)
            {
                // Joined a computation that its previous waiters had just cancelled: start a fresh one.
                Interlocked.Decrement(ref entry.Waiters);
                _inFlight.TryRemove(new KeyValuePair<string, InFlight>(fullPath, entry));
            }
        }
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
