using System.IO;

namespace PhotoReview.Core.Catalog;

public static class ImageFileTypes
{
    public static IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    public static IReadOnlySet<string> RawExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
    };

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
