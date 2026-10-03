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

    /// <summary>
    /// Largest embedded preview (bytes) a RAW decode will read into memory (128 MiB). Real previews are 10 KB to a few MB
    /// (a full-size JpgFromRaw is at most ~30 MB); a container declaring more is treated as corrupt before anything is allocated,
    /// so a hostile length cannot reserve up to the file size. The LibRaw thumbnail fallback has its own 32 MiB cap.
    /// </summary>
    public const int MaxPreviewBytes = 128 << 20;

    /// <summary>Hard cap on total bytes that may be read from the header (8 MB).</summary>
    public const int MaxHeaderBytes = 8 << 20;

    /// <summary>Default (minimum) size of the TIFF-header EXIF block that readers declare: 128 KiB.</summary>
    public const int DefaultExifBlockBytes = 128 * 1024;

    /// <summary>Upper bound for an EXIF block grown to reach IFDs written after the pixel data (4 MiB, half the header budget).</summary>
    public const int MaxExifBlockBytes = 4 << 20;

    /// <summary>Most JPEG marker segments the bounded JPEG walkers (PreviewSelector, JpegMarkerProbe) visit.</summary>
    internal const int MaxJpegMarkerSegments = 512;

    /// <summary>Fill bytes (extra 0xFF before a marker) the JPEG walkers skip without using a segment iteration, up to this many in a row.</summary>
    internal const int MaxJpegFillBytes = 64 * 1024;

    /// <summary>Returns a safe initial container probe length without narrowing a potentially large file length first.</summary>
    internal static int InitialProbeLength(long sourceLength) => (int)Math.Min(64L, sourceLength);

}
