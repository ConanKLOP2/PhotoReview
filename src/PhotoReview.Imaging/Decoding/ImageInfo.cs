namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Basic metadata for an image (dimensions and EXIF orientation).
/// </summary>
public readonly record struct ImageInfo(
    int PixelWidth,
    int PixelHeight,
    int Orientation = 1)
{
    public int Width => PixelWidth;
    public int Height => PixelHeight;
}
