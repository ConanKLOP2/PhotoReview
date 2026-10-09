
namespace PhotoReview.Core.Catalog;

/// <summary>An image format decoded only through an optional Windows (WIC) codec (Q-FMT-WEBP-HEIC).</summary>
public enum WicImageFormat
{
    /// <summary>Not one of the optional-codec formats.</summary>
    None,

    /// <summary>WebP (.webp): the Windows WebP codec.</summary>
    WebP,

    /// <summary>HEIC/HEIF (.heic, .heif): "HEIF Image Extensions" + "HEVC Video Extensions".</summary>
    Heif,
}

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

    private static readonly HashSet<string> s_webpHeicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp", ".heic", ".heif"
    };

    /// <summary>
    /// WebP and HEIC/HEIF (Q-FMT-WEBP-HEIC): listed when <c>WebpHeicSupportEnabled</c> is on and decoded only through the
    /// Windows codecs (WIC). Whether this PC has those codecs is a decode-time question (a missing codec is a clear,
    /// localized error on that file), not a listing one: the user sees the files and is told what to install.
    /// </summary>
    public static IReadOnlySet<string> WebpHeicExtensions => s_webpHeicExtensions;

    /// <summary>True when <paramref name="path"/> has a RAW extension. Allocation-free (the extension is tested as a span), for per-entry loops over large catalogs.</summary>
    public static bool IsRawPath(ReadOnlySpan<char> path) =>
        s_rawExtensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(Path.GetExtension(path));

    /// <inheritdoc cref="IsRawPath(ReadOnlySpan{char})"/>
    public static bool IsRawPath(string? path) => path is not null && IsRawPath(path.AsSpan());

    /// <summary>Which optional-codec format <paramref name="path"/> is, by extension (allocation-free).</summary>
    public static WicImageFormat GetWicFormat(ReadOnlySpan<char> path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)) return WicImageFormat.WebP;
        if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase)) return WicImageFormat.Heif;
        return WicImageFormat.None;
    }

    /// <inheritdoc cref="GetWicFormat(ReadOnlySpan{char})"/>
    public static WicImageFormat GetWicFormat(string? path) => path is null ? WicImageFormat.None : GetWicFormat(path.AsSpan());

    public static bool IsSupported(string path) => IsSupported(path, rawEnabled: false, webpHeicEnabled: false);

    public static bool IsSupported(string path, bool rawEnabled) => IsSupported(path, rawEnabled, webpHeicEnabled: false);

    public static bool IsSupported(string path, bool rawEnabled, bool webpHeicEnabled)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        if (SupportedExtensions.Contains(ext)) return true;
        if (rawEnabled && RawExtensions.Contains(ext)) return true;
        return webpHeicEnabled && s_webpHeicExtensions.Contains(ext);
    }
}
