namespace PhotoReview.App;

/// Shared numeric constants that must stay identical across multiple files.
internal static class AppConstants
{
    public const long ImageCacheCapacityBytes = 16L * 1024 * 1024 * 1024;
    public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
    public const int PreloadWorkerCount = 8;
    public const double PreloadMemoryLoadLimit = 0.80;
}
