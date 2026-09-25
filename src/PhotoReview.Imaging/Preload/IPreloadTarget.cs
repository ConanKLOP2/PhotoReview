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

    /// <summary>
    /// perf(preload): viewer decodes (the image being navigated to) running right now.
    /// While non-zero, preload holds back to its burst concurrency so the viewer's decode gets the CPU.
    /// Default 0 for targets without a viewer (test fakes).
    /// </summary>
    int ActiveViewerDecodes => 0;

    /// <summary>
    /// Q-R17: decoded size of the cached preview for <paramref name="key"/>, or null when it is not
    /// cached. Feeds the measured whole-folder estimate. Default null for targets without real sizes.
    /// </summary>
    long? CachedPreviewBytes(ImageCacheKey key) => null;

    /// <summary>
    /// True when the on-disk preview cache holds an entry for <paramref name="key"/>, so a decode would read it
    /// instead of the original (the source-bytes prefetch is skipped then). Default false for targets without one.
    /// </summary>
    bool HasDiskCachedPreview(ImageCacheKey key) => false;
}
