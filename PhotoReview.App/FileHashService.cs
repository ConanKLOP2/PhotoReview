using System.IO;
using System.Security.Cryptography;

namespace PhotoReview.App;

public sealed class FileHashService
{
    private sealed record HashEntry(long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashEntry> _cache = new(
        16L * 1024 * 1024, _ => 128, StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (_cache.TryGet(path, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Hash;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        _cache.Set(path, new HashEntry(info.Length, info.LastWriteTimeUtc, hash));
        return hash;
    }

    public void Clear() => _cache.Clear();
}
