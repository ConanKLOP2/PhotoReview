namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Basic metadata for an image (dimensions and EXIF orientation).
/// </summary>
public readonly record struct ImageInfo(
    int PixelWidth,
    int PixelHeight,
    int Orientation = 1)
{
    /// <summary>
    /// Effective visual width after EXIF orientation is applied.
    /// Orientations 5, 6, 7, 8 swap width and height.
    /// </summary>
    public int Width => (Orientation is >= 5 and <= 8) ? PixelHeight : PixelWidth;

    /// <summary>
    /// Effective visual height after EXIF orientation is applied.
    /// Orientations 5, 6, 7, 8 swap width and height.
    /// </summary>
    public int Height => (Orientation is >= 5 and <= 8) ? PixelWidth : PixelHeight;
}
