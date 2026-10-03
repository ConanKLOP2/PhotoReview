using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Router decoder wrapping non-RAW decoder chain and <see cref="RawDecoder"/>.
/// Routes paths with RAW extensions to <see cref="RawDecoder"/> when RAW support is enabled and delegates every other
/// path to the inner decoder chain. A RAW extension arriving while RAW support is disabled is refused with a
/// <see cref="NotSupportedException"/>: the standard chain (WPF/WIC) would either fail obscurely or, for TIFF-based
/// containers such as DNG, silently decode only the tiny IFD0 thumbnail as if it were the photo.
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

        if (RawFileTypes.IsRawExtension(path))
        {
            if (!_isRawEnabled()) throw RawDisabled(path);
            return _rawDecoder.ReadInfo(path);
        }

        return _standardDecoder.ReadInfo(path);
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        if (RawFileTypes.IsRawExtension(request.Path))
        {
            if (!_isRawEnabled()) throw RawDisabled(request.Path);
            return _rawDecoder.Decode(request);
        }

        return _standardDecoder.Decode(request);
    }

    // English message for logs/type-based control flow; the UI shows the localized sentence (UserFacingError.Describe).
    private static NotSupportedException RawDisabled(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        return UserFacingError.Localized(
            new NotSupportedException($"RAW support is disabled; cannot decode '{fileName}'."),
            () => Tr.ImageErrorRawDisabled(fileName));
    }
}
