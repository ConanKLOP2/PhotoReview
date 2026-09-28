namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Byte span of a TIFF/EXIF metadata block within a RAW container.
/// </summary>
public sealed record ExifBlock(long Offset, long Length, bool IsTiffHeader);
