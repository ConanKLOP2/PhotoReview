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
    int CacheCount { get; }
    long CacheBytes { get; }
}
