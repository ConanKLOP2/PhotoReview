using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Factory abstraction for creating image decoders based on the requested backend.
/// </summary>
public interface IImageDecoderFactory
{
    /// <summary>
    /// Creates or resolves an <see cref="IImageDecoder"/> configured for the requested <paramref name="backend"/>,
    /// wrapped in a fallback decoder if necessary.
    /// </summary>
    IImageDecoder Create(DecoderBackend backend);
}
