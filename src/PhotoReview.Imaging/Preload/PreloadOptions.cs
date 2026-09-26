namespace PhotoReview.Imaging.Preload;

/// <summary>
/// Options configuring background preload behavior and memory safety limits.
/// Defaults match PerformanceOptions.
/// </summary>
public sealed record PreloadOptions(
    int WorkerCount = 8,
    double MemoryLoadLimit = 0.80,
    long ReserveBytes = 2L * 1024 * 1024 * 1024,
    long FullFolderThresholdBytes = 16L * 1024 * 1024 * 1024)
{
    /// <summary>feat/preload-window-setting: forward/backward preload lookahead; defaults to the hard-coded window.</summary>
    public PreloadWindow Window { get; init; } = PreloadWindow.Default;
}
