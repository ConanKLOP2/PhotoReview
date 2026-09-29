using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Router decoder wrapping non-RAW decoder chain and <see cref="RawDecoder"/>.
/// Routes paths with RAW extensions to <see cref="RawDecoder"/> when RAW support is enabled;
/// otherwise delegates to the inner decoder chain.
/// Failure mapping: container errors throw <see cref="InvalidDataException"/>, never falling back to WPF for RAW.
/// </summary>
public sealed class FormatRoutingDecoder : IImageDecoder
{
    private readonly IImageDecoder _standardDecoder;
    private readonly IImageDecoder _rawDecoder;
    private readonly Func<bool> _isRawEnabled;

    public FormatRoutingDecoder(
        IImageDecoder standardDecoder,
        IImageDecoder rawDecoder,
        Func<bool> isRawEnabled)
    {
        _standardDecoder = standardDecoder ?? throw new ArgumentNullException(nameof(standardDecoder));
        _rawDecoder = rawDecoder ?? throw new ArgumentNullException(nameof(rawDecoder));
        _isRawEnabled = isRawEnabled ?? throw new ArgumentNullException(nameof(isRawEnabled));
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_isRawEnabled() && RawFileTypes.IsRawExtension(path))
        {
            return _rawDecoder.ReadInfo(path);
        }

        return _standardDecoder.ReadInfo(path);
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        if (_isRawEnabled() && RawFileTypes.IsRawExtension(request.Path))
        {
            return _rawDecoder.Decode(request);
        }

        return _standardDecoder.Decode(request);
    }
}
