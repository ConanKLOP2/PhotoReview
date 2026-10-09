using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Routes WebP and HEIC/HEIF paths (Q-FMT-WEBP-HEIC) to the WIC decoder chain whatever backend the user picked (TurboJPEG
/// and LibRaw cannot read them; WIC is the only route, through the codecs Windows provides), and every other path to the
/// inner decoder unchanged. Before any disk read it refuses, with a localized <see cref="NotSupportedException"/>:
/// <list type="bullet">
/// <item>these formats while <c>WebpHeicSupportEnabled</c> is off (the same rule as RAW in FormatRoutingDecoder), and</item>
/// <item>a format whose Windows codec this PC lacks (<see cref="MissingImageCodecException"/>, carrying the install
/// guidance), logged once per format per process instead of once per file -- the other images keep browsing normally.</item>
/// </list>
/// </summary>
public sealed class WebpHeicRoutingDecoder : IImageDecoder
{
    private readonly IImageDecoder _inner;
    private readonly IImageDecoder _wicDecoder;
    private readonly Func<bool> _isEnabled;
    private readonly Func<WicCodecSupport> _codecs;
    private readonly ILog _log;
    private int _webpMissingLogged;
    private int _heifMissingLogged;

    /// <param name="inner">Decoder for every other path (the user's backend chain).</param>
    /// <param name="wicDecoder">WIC chain for WebP/HEIC (WicDirect with the usual WPF fallback).</param>
    /// <param name="isEnabled">Reads <c>WebpHeicSupportEnabled</c> on every call (a Settings change applies at once).</param>
    /// <param name="codecs">The codec probe; invoked only for a WebP/HEIC path, so JPEG-only sessions never run it.</param>
    public WebpHeicRoutingDecoder(IImageDecoder inner, IImageDecoder wicDecoder, Func<bool> isEnabled, Func<WicCodecSupport> codecs, ILog? log = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _wicDecoder = wicDecoder ?? throw new ArgumentNullException(nameof(wicDecoder));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// True when <see cref="Decode"/> would refuse <paramref name="path"/> without reading it (a WebP/HEIC file while the switch is
    /// off or its codec is missing). Lets preload skip prefetching such a file's bytes, which no decode could use.
    /// </summary>
    public static bool WillRefuse(string? path, Func<bool> isEnabled, Func<WicCodecSupport> codecs)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(codecs);
        var format = ImageFileTypes.GetWicFormat(path);
        return format != WicImageFormat.None && (!isEnabled() || !codecs().Supports(format));
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Route(path).ReadInfo(path);
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        return Route(request.Path).Decode(request);
    }

    private IImageDecoder Route(string? path)
    {
        var format = ImageFileTypes.GetWicFormat(path);
        if (format == WicImageFormat.None) return _inner;

        var fileName = System.IO.Path.GetFileName(path) ?? string.Empty;
        if (!_isEnabled())
        {
            // English message for logs/type-based control flow; the UI shows the localized sentence (UserFacingError.Describe).
            throw UserFacingError.Localized(
                new NotSupportedException($"WebP/HEIC support is disabled; cannot decode '{fileName}'."),
                () => Tr.ImageErrorWebpHeicDisabled(fileName));
        }

        var codecs = _codecs();
        if (!codecs.Supports(format)) throw MissingCodec(format, fileName, codecs);
        return _wicDecoder;
    }

    private MissingImageCodecException MissingCodec(WicImageFormat format, string fileName, WicCodecSupport codecs)
    {
        var isHeif = format == WicImageFormat.Heif;
        ref var logged = ref isHeif ? ref _heifMissingLogged : ref _webpMissingLogged;
        var message = isHeif
            ? "HEIC/HEIF needs the Windows \"HEIF Image Extensions\" and \"HEVC Video Extensions\" (Microsoft Store)"
            : "WebP needs the Windows \"WebP Image Extensions\" (Microsoft Store)";
        if (Interlocked.Exchange(ref logged, 1) == 0)
        {
            _log.Warn($"{message}; files of this format are skipped until the codec is installed and the app restarted. Probe: {codecs.Detail}");
        }

        return UserFacingError.Localized(
            new MissingImageCodecException($"{message}; cannot decode '{fileName}'."),
            isHeif ? () => Tr.ImageErrorHeifCodecMissing(fileName) : () => Tr.ImageErrorWebpCodecMissing(fileName));
    }
}
