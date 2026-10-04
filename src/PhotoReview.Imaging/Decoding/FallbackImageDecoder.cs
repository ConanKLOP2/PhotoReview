using System.IO;
using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Wraps a primary decoder with a fallback decoder.
/// When primary decoding fails with a supported fallback exception (INV-12),
/// logs the event, updates fallback metrics, and delegates to the fallback decoder.
/// Missing file errors (FileNotFoundException, DirectoryNotFoundException) are thrown immediately without fallback.
/// </summary>
public sealed class FallbackImageDecoder : IImageDecoder
{
    private readonly IImageDecoder _primary;
    private readonly DecoderBackend _primaryBackend;
    private readonly IImageDecoder _fallback;
    private readonly ILog _log;
    private readonly ReviewMetrics? _metrics;

    /// <summary>The fallback is always WPF (the only caller, ImageDecoderFactory, never used another backend).</summary>
    public const DecoderBackend FallbackBackend = DecoderBackend.Wpf;

    public FallbackImageDecoder(
        IImageDecoder primary,
        DecoderBackend primaryBackend,
        IImageDecoder fallback,
        ILog? log = null,
        ReviewMetrics? metrics = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _primaryBackend = primaryBackend;
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _log = log ?? NullLog.Instance;
        _metrics = metrics;
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        try
        {
            var decoded = _primary.Decode(request);
            return decoded.ActualBackend == _primaryBackend
                ? decoded
                : new DecodedImageWithBackend(decoded, _primaryBackend);
        }
        catch (Exception ex) when (IsFallbackable(ex))
        {
            _log.Warn($"Decoder {_primaryBackend} failed for '{request.Path}', falling back to {FallbackBackend}: {ex.Message}");
            _metrics?.RecordDecoderFallback(_primaryBackend);

            // The primary may already have read the whole file (TurboJpeg): decode that copy instead of re-reading it.
            var fallbackRequest = !request.Bytes.HasValue && DecodeFailureSourceBytes.TryGet(ex, out var sourceBytes)
                ? request with { Bytes = sourceBytes }
                : request;
            var decoded = _fallback.Decode(fallbackRequest);
            return decoded.ActualBackend == FallbackBackend
                ? decoded
                : new DecodedImageWithBackend(decoded, FallbackBackend);
        }
    }

    public ImageInfo ReadInfo(string path)
    {
        try
        {
            return _primary.ReadInfo(path);
        }
        catch (Exception ex) when (IsFallbackable(ex))
        {
            _log.Warn($"Decoder {_primaryBackend}.ReadInfo failed for '{path}', falling back to {FallbackBackend}: {ex.Message}");
            _metrics?.RecordDecoderFallback(_primaryBackend);
            return _fallback.ReadInfo(path);
        }
    }

    public static bool IsFallbackable(Exception ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException or OperationCanceledException or OutOfMemoryException
            // Transient "queue full" from a gated decoder: the fallback would decode the same file outside that gate.
            or DecoderBusyException)
        {
            return false;
        }

        // InvalidCast (a failed COM interface cast) and Overflow (checked size math) are backend-specific failures the WPF
        // path can decode around. InvalidOperationException stays non-fallbackable: it signals a programming error.
        return ex is NotSupportedException
            or InvalidCastException
            or OverflowException
            or FileFormatException
            or InvalidDataException
            or COMException
            or DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException;
    }

    private sealed class DecodedImageWithBackend : IDecodedImage
    {
        private readonly IDecodedImage _inner;

        public int PixelWidth => _inner.PixelWidth;
        public int PixelHeight => _inner.PixelHeight;
        public bool Downscaled => _inner.Downscaled;
        public int Orientation => _inner.Orientation;
        public long EstimatedBytes => _inner.EstimatedBytes;
        public object PlatformImage => _inner.PlatformImage;
        public DecoderBackend ActualBackend { get; }
        public int OriginalWidth => _inner.OriginalWidth;
        public int OriginalHeight => _inner.OriginalHeight;
        public bool IsDegradedFallback => _inner.IsDegradedFallback;
        public PhotoReview.Imaging.Metadata.ExifSummary? Exif => _inner.Exif;

        public DecodedImageWithBackend(IDecodedImage inner, DecoderBackend actualBackend)
        {
            _inner = inner;
            ActualBackend = actualBackend;
        }
    }
}
