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

    /// <summary>
    /// Returns whether <paramref name="backend"/> has an explicit provider registered with this factory.
    /// A backend that is not registered still decodes (via fallback to <see cref="DecoderBackend.Wpf"/> in
    /// <see cref="Create"/>), but callers such as Settings use this to reflect reality to the user.
    /// </summary>
    bool IsRegistered(DecoderBackend backend);
}
