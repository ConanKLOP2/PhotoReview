using System.IO;
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
