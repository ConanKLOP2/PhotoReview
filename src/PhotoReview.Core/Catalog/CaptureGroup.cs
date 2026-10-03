using PhotoReview.Core.Model;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// A same-name JPEG+RAW capture and its optional XMP sidecar.
/// </summary>
public sealed record CaptureGroup
{
    private readonly IReadOnlyList<string> _imagePaths;
    private readonly IReadOnlyList<string> _paths;

    public string JpegPath { get; }
    public string RawPath { get; }
    public string? XmpPath { get; }

    public CaptureGroup(string jpegPath, string rawPath, string? xmpPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        if (xmpPath is not null) ArgumentException.ThrowIfNullOrWhiteSpace(xmpPath);
        JpegPath = jpegPath;
        RawPath = rawPath;
        XmpPath = xmpPath;
        _imagePaths = Array.AsReadOnly(new[] { JpegPath, RawPath });
        _paths = Array.AsReadOnly(xmpPath is null
            ? new[] { JpegPath, RawPath }
            : new[] { JpegPath, RawPath, xmpPath });
    }

    // The cached path lists are fresh collections per instance (a compiler-generated record Equals would compare them by
    // reference and never report two equal captures as equal, which also broke CatalogEntry equality), so equality is
    // defined on the three paths only. Windows paths: ordinal, ignoring case (same rule as CaptureGroupBuilder).
    public bool Equals(CaptureGroup? other) =>
        other is not null
        && string.Equals(JpegPath, other.JpegPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RawPath, other.RawPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(XmpPath, other.XmpPath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(JpegPath, StringComparer.OrdinalIgnoreCase);
        hash.Add(RawPath, StringComparer.OrdinalIgnoreCase);
        hash.Add(XmpPath, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    /// <summary>Gets the path shown for this pair under the selected review mode.</summary>
    public string GetRepresentativePath(RawPairMode mode) => mode switch
    {
        RawPairMode.PreferJpeg => JpegPath,
        RawPairMode.PreferRaw => RawPath,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Separate mode does not create capture groups.")
    };

    /// <summary>Gets the paths that must move together as one capture.</summary>
    public IReadOnlyList<string> ImagePaths => _imagePaths;

    /// <summary>Gets the image paths and optional sidecar that must move together.</summary>
    public IReadOnlyList<string> Paths => _paths;
}
