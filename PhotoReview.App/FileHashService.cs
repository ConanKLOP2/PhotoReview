using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;
using PhotoReview.Core.Caching;

namespace PhotoReview.App;

public sealed class FileHashService
{
    private sealed record HashEntry(string Path, long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashEntry> _cache = new(
        16L * 1024 * 1024, entry => (entry.Path.Length * 2L) + 64, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (_cache.TryGet(fullPath, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Hash;
        var generation = Volatile.Read(ref _generation);
        var lazy = _inFlight.GetOrAdd(fullPath, _ => new Lazy<Task<string>>(
            () => ComputeAndCacheAsync(fullPath, info.Length, info.LastWriteTimeUtc, generation, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(fullPath, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken);
    }

    private async Task<string> ComputeAndCacheAsync(string path, long length, DateTime lastWriteUtc, int generation, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var current = new FileInfo(path);
        if (current.Length != length || current.LastWriteTimeUtc != lastWriteUtc)
            throw new IOException($"File changed while hashing: {path}");
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
