using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;

namespace PhotoReview.App;

public sealed class FileHashService
{
    private sealed record HashEntry(long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashEntry> _cache = new(
        16L * 1024 * 1024, _ => 128, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (_cache.TryGet(fullPath, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Hash;
        var lazy = _inFlight.GetOrAdd(fullPath, _ => new Lazy<Task<string>>(
            () => ComputeAndCacheAsync(fullPath, info.Length, info.LastWriteTimeUtc, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazy.Value.WaitAsync(cancellationToken); }
        finally { _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(fullPath, lazy)); }
    }

    private async Task<string> ComputeAndCacheAsync(string path, long length, DateTime lastWriteUtc, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        _cache.Set(path, new HashEntry(length, lastWriteUtc, hash));
        return hash;
    }

    public void Clear() => _cache.Clear();
}
