namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Byte span of a TIFF/EXIF metadata block within a RAW container.
/// <paramref name="IfdIsExif"/>: the block's IFD0 is itself the Exif IFD (exposure fields directly, no 0x8769 pointer),
/// as in the Canon CR3 "CMT2" box; only meaningful with <paramref name="IsTiffHeader"/>.
/// </summary>
public sealed record ExifBlock(long Offset, long Length, bool IsTiffHeader, bool IfdIsExif = false);
