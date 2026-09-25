using System;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

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
    private readonly DecoderBackend _fallbackBackend;
    private readonly ILog _log;
    private readonly ReviewMetrics? _metrics;

    public DecoderBackend PrimaryBackend => _primaryBackend;
    public DecoderBackend FallbackBackend => _fallbackBackend;
    public IImageDecoder PrimaryDecoder => _primary;
    public IImageDecoder FallbackDecoder => _fallback;

    public FallbackImageDecoder(
        IImageDecoder primary,
        DecoderBackend primaryBackend,
        IImageDecoder fallback,
        DecoderBackend fallbackBackend = DecoderBackend.Wpf,
        ILog? log = null,
        ReviewMetrics? metrics = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _primaryBackend = primaryBackend;
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _fallbackBackend = fallbackBackend;
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
            _log.Warn($"Decoder {_primaryBackend} failed for '{request.Path}', falling back to {_fallbackBackend}: {ex.Message}");
            _metrics?.RecordDecoderFallback(_primaryBackend);

            var decoded = _fallback.Decode(request);
            return decoded.ActualBackend == _fallbackBackend
                ? decoded
                : new DecodedImageWithBackend(decoded, _fallbackBackend);
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
            _log.Warn($"Decoder {_primaryBackend}.ReadInfo failed for '{path}', falling back to {_fallbackBackend}: {ex.Message}");
            _metrics?.RecordDecoderFallback(_primaryBackend);
            return _fallback.ReadInfo(path);
        }
    }

    private static bool IsFallbackable(Exception ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException or OperationCanceledException or OutOfMemoryException)
        {
            return false;
        }

        return ex is NotSupportedException
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
        public PhotoReview.Imaging.Metadata.ExifSummary? Exif => _inner.Exif;

        public DecodedImageWithBackend(IDecodedImage inner, DecoderBackend actualBackend)
        {
            _inner = inner;
            ActualBackend = actualBackend;
        }
    }
}
