namespace PhotoReview.Core.Settings;

/// <summary>
/// Shared performance defaults used by the app, benchmark and <c>PreloadOptions</c>.
/// </summary>
public static class PerformanceOptions
{
    public const long ImageCacheCapacityBytes = 16L * 1024 * 1024 * 1024;
    public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
    public const int PreloadWorkerCount = 8;
    public const double PreloadMemoryLoadLimit = 0.90;
    public const long PreviewDiskCacheCapacityBytes = 4L * 1024 * 1024 * 1024;
    public const bool UseSourceBytesCache = false;
    public const long SourceBytesCapacityBytes = 16L * 1024 * 1024 * 1024;
}
