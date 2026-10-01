namespace PhotoReview.Imaging.Decoding;

/// <summary>An image decoder that can stop an in-progress native decode when its request is superseded.</summary>
public interface ICancellableImageDecoder : IImageDecoder
{
    /// <summary>Decodes the source and observes cancellation throughout the operation.</summary>
    IDecodedImage Decode(DecodeRequest request, CancellationToken cancellationToken);
}
