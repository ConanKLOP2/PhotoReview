namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Supported camera RAW file extensions.
/// </summary>
public static class RawFileTypes
{
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2",
        ".cr3",
        ".nef",
        ".nrw",
        ".arw",
        ".dng",
        ".raf",
        ".orf",
        ".rw2",
        ".pef"
    };

    /// <summary>
    /// Set of known camera RAW file extensions (lower-case with leading dot).
    /// </summary>
    public static IReadOnlySet<string> Extensions => s_extensions;

    /// <summary>
    /// Returns true if the file path has a recognized camera RAW extension.
    /// </summary>
    public static bool IsRawExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && s_extensions.Contains(ext);
    }
}
