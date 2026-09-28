namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Hostile-input caps and bounds shared across all RAW container readers to prevent OOM, hang, or unbounded loops.
/// </summary>
public static class RawContainerLimits
{
    /// <summary>Maximum number of IFD structures allowed in a TIFF-based RAW container.</summary>
    public const int MaxIfdCount = 64;

    /// <summary>Maximum number of directory entries permitted per IFD.</summary>
    public const int MaxEntriesPerIfd = 4096;

    /// <summary>Maximum nesting depth for ISO-BMFF box structures (e.g. CR3).</summary>
    public const int MaxBoxDepth = 16;

    /// <summary>Hard cap on total bytes that may be read from the header (8 MB).</summary>
    public const int MaxHeaderBytes = 8 << 20;
}
