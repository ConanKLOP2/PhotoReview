namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Type of preview image embedded inside a RAW container.
/// </summary>
public enum EmbeddedPreviewKind
{
    Jpeg,
    UncompressedRgb,
    Other
}

/// <summary>
/// Color space hint for an embedded preview.
/// </summary>
public enum PreviewColorSpace
{
    Unknown,
    Srgb,
    AdobeRgb
}

/// <summary>
/// Describes an embedded preview image within a camera RAW container.
/// Width/Height may be 0 if unknown until the JPEG SOF header is parsed.
/// <see cref="HeaderResolved"/> records that <see cref="PreviewSelector"/> already walked the JPEG header for this preview:
/// dimensions and colour space are then final (a size still 0 or a colour space still Unknown means the header has none),
/// so a cached container info never triggers the same walk again.
/// </summary>
public sealed record EmbeddedPreview(
    int Index,
    long Offset,
    long Length,
    EmbeddedPreviewKind Kind,
    int Width,
    int Height,
    PreviewColorSpace ColorSpace,
    bool HeaderResolved = false);
