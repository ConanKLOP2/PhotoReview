using System.IO;

namespace PhotoReview.Core.Catalog;

public static class ImageFileTypes
{
    public static IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>
    /// The only extensions that form the "JPEG" side of a same-name JPEG+RAW capture group. An edited A.tif / A.png
    /// next to the original A.dng is a different file, not the camera JPEG of that shot, so it must never be grouped
    /// (a group action on it would also recycle/move the RAW).
    /// </summary>
    public static IReadOnlySet<string> JpegExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    private static readonly HashSet<string> s_rawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
    };

    public static IReadOnlySet<string> RawExtensions => s_rawExtensions;

    /// <summary>True when <paramref name="path"/> has a RAW extension. Allocation-free (the extension is tested as a span), for per-entry loops over large catalogs.</summary>
    public static bool IsRawPath(ReadOnlySpan<char> path) =>
        s_rawExtensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(Path.GetExtension(path));

    /// <inheritdoc cref="IsRawPath(ReadOnlySpan{char})"/>
    public static bool IsRawPath(string? path) => path is not null && IsRawPath(path.AsSpan());

    public static bool IsSupported(string path) => IsSupported(path, rawEnabled: false);

    public static bool IsSupported(string path, bool rawEnabled)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        if (SupportedExtensions.Contains(ext)) return true;
        return rawEnabled && RawExtensions.Contains(ext);
    }
}
