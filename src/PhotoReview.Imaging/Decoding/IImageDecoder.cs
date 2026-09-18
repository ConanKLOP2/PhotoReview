namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Abstraction for image decoding engines.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Decodes the requested image into an <see cref="IDecodedImage"/>.
    /// </summary>
    IDecodedImage Decode(DecodeRequest request);

    /// <summary>
    /// Reads header metadata (dimensions, orientation) without decoding full pixel data.
    /// </summary>
    ImageInfo ReadInfo(string path);
}
