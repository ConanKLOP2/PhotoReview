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
/// </summary>
public sealed record EmbeddedPreview(
    int Index,
    long Offset,
    long Length,
    EmbeddedPreviewKind Kind,
    int Width,
    int Height,
    PreviewColorSpace ColorSpace);
