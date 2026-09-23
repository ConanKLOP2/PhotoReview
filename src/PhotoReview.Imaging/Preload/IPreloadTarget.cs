using PhotoReview.Core.Catalog;

namespace PhotoReview.Imaging.Preload;

/// <summary>
/// Abstraction of the target preview cache that PreloadScheduler warms and queries.
/// </summary>
public interface IPreloadTarget
{
    bool TryGetCachedPreview(string path);
    bool TryGetCachedPreview(ImageCacheKey key);
    Task PreloadAsync(string path, CancellationToken cancellationToken = default);
    ImageCacheKey GetCurrentCacheKey(string path);

    /// <summary>Reuses the entry's Length/LastWriteUtc (from the folder scan) instead of stat-ing the path.</summary>
    ImageCacheKey GetCurrentCacheKey(CatalogEntry entry);
    int CacheCount { get; }
    long CacheBytes { get; }
}
