using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Abstraction for image decoding engines.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Decodes the requested image. Returns the decoded bitmap source and whether target width downscaling was actually honored.
    /// In T31c, the return type will evolve to IDecodedImage.
    /// </summary>
    (BitmapSource Bitmap, bool Downscaled) Decode(DecodeRequest request);

    /// <summary>
    /// Reads header metadata (dimensions, orientation) without decoding full pixel data.
    /// </summary>
    ImageInfo ReadInfo(string path);
}
