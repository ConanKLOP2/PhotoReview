namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Parsed structural metadata from a camera RAW container header.
/// </summary>
public sealed record RawContainerInfo(
    RawFormat Format,
    int SensorWidth,
    int SensorHeight,
    int Orientation,
    IReadOnlyList<EmbeddedPreview> Previews,
    IReadOnlyList<ExifBlock> ExifBlocks);
