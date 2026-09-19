namespace PhotoReview.Core.Settings;

/// <summary>
/// Shared performance defaults that must stay identical across the app, the benchmark and
/// <c>PreloadOptions</c>. Not exposed in Settings yet; bind them from settings before surfacing on the UI.
/// </summary>
public static class PerformanceOptions
{
    public const long ImageCacheCapacityBytes = 16L * 1024 * 1024 * 1024;
    public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
    public const int PreloadWorkerCount = 8;
    public const double PreloadMemoryLoadLimit = 0.80;
    public const long PreviewDiskCacheCapacityBytes = 4L * 1024 * 1024 * 1024;
}
