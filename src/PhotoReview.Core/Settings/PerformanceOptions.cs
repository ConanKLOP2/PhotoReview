namespace PhotoReview.Core.Settings;

/// <summary>
/// Shared performance defaults used by the app, benchmark and <c>PreloadOptions</c>.
/// </summary>
public static class PerformanceOptions
{
    /// <summary>
    /// Byte budget of the in-memory preview cache, now used only when physical RAM cannot be determined; otherwise
    /// <see cref="ImageCacheRamPercent"/> decides the budget.
    /// </summary>
    public const long ImageCacheCapacityBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Default share of physical RAM (percent) for the in-memory preview (+ source-bytes) cache: 16 GiB on a 32 GB machine.</summary>
    public const int ImageCacheRamPercent = 50;

    /// <summary>Largest selectable <see cref="ImageCacheRamPercent"/>; the minimum is decided at runtime from the preload window.</summary>
    public const int MaxImageCacheRamPercent = 90;

    /// <summary>Absolute floor of <see cref="ImageCacheRamPercent"/> when physical RAM is unknown (the real minimum depends on it).</summary>
    public const int MinImageCacheRamPercent = 1;
    public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
    public const int PreloadWorkerCount = 8;

    /// <summary>Upper bound accepted for a hand-edited <c>PreloadWorkerCount</c>: each worker holds a decoded image, so a huge value is a memory bomb.</summary>
    public const int MaxPreloadWorkerCount = 64;
    public const double PreloadMemoryLoadLimit = 0.90;
    public const long PreviewDiskCacheCapacityBytes = 4L * 1024 * 1024 * 1024;
    public const bool UseSourceBytesCache = false;
    public const long SourceBytesCapacityBytes = 16L * 1024 * 1024 * 1024;
}
