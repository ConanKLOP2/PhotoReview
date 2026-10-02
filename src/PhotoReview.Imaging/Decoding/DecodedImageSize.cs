namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// <see cref="IDecodedImage.OriginalWidth"/>/<see cref="IDecodedImage.OriginalHeight"/> default to the decoded pixel size when a
/// decoder could not learn the source size. For a full-resolution decode that default is exactly right; for a downscaled one it
/// is the SMALL size and must not be stored as "original".
/// </summary>
internal static class DecodedImageSize
{
    /// <summary>
    /// True when the original dimensions are real: a full decode (the pixels are the original) or a downscaled decode whose
    /// reported original is larger than the pixels (a genuine downscale always shrinks at least one axis).
    /// </summary>
    internal static bool HasKnownOriginal(IDecodedImage image) =>
        !image.Downscaled || image.OriginalWidth > image.PixelWidth || image.OriginalHeight > image.PixelHeight;
}
