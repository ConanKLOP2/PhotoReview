using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Decodes a RAW container through WIC when a matching decoder is registered and it produces
/// more detail than the largest embedded preview.
/// </summary>
public sealed class WicRawFullDecoder : ICancellableImageDecoder
{
    private readonly Func<RawFormat, bool> _availabilityProbe;
    private readonly ConcurrentDictionary<RawFormat, Lazy<bool>> _availability = new();
    private readonly ISourceReader _sourceReader;
    private readonly IImageDecoder _wicDecoder;
    private readonly RawContainerReaderRegistry _registry = new();

    /// <summary>Creates a WIC RAW full decoder. The optional delegate is a test seam.</summary>
    public WicRawFullDecoder(Func<RawFormat, bool>? availabilityProbe = null)
        : this(PhysicalSourceReader.Instance, new WicDirectDecoder(), availabilityProbe)
    {
    }

    internal WicRawFullDecoder(
        ISourceReader sourceReader,
        IImageDecoder wicDecoder,
        Func<RawFormat, bool>? availabilityProbe = null,
        IWicCodecRegistry? codecRegistry = null)
    {
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        _wicDecoder = wicDecoder ?? throw new ArgumentNullException(nameof(wicDecoder));
        var registry = codecRegistry ?? WindowsWicCodecRegistry.Instance;
        _availabilityProbe = availabilityProbe ?? (format => IsRegisteredInWic(registry, format));
    }

    /// <summary>Returns the cached codec-registration result for <paramref name="format"/>.</summary>
    public bool IsCodecAvailable(RawFormat format) =>
        _availability.GetOrAdd(format, key => new Lazy<bool>(() => _availabilityProbe(key))).Value;

    /// <inheritdoc />
    public ImageInfo ReadInfo(string path)
    {
        var info = ReadContainerInfo(path, SourceReadPriority.Viewer, out var headerSource);
        using (headerSource)
        {
            int width = info.SensorWidth;
            int height = info.SensorHeight;
            if (width <= 0 || height <= 0)
            {
                var preview = PreviewSelector.SelectPreview(headerSource, info.Previews, DecodeBox.Unbounded, info.Orientation);
                if (preview is not null) (width, height) = (preview.Width, preview.Height);
            }

            return new ImageInfo(width, height, info.Orientation);
        }
    }

    /// <inheritdoc />
    public IDecodedImage Decode(DecodeRequest request) => Decode(request, CancellationToken.None);

    /// <summary>
    /// Decodes the RAW through WIC. Cancellation is observed before each step and again after WIC returns (the pixels
    /// of a superseded request are dropped); the WIC call itself cannot be interrupted, so a request cancelled while
    /// it runs still occupies the caller's single full-decode slot until WIC returns.
    /// </summary>
    public IDecodedImage Decode(DecodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        cancellationToken.ThrowIfCancellationRequested();
        var info = ReadContainerInfo(request.Path, request.Priority, cancellationToken, out var headerSource);
        using (headerSource)
        {
            if (!IsCodecAvailable(info.Format))
                throw new NotSupportedException($"No WIC RAW decoder is registered for {info.Format}.");

            var preview = PreviewSelector.SelectPreview(headerSource, info.Previews, DecodeBox.Unbounded, info.Orientation);
            cancellationToken.ThrowIfCancellationRequested();

            IDecodedImage decoded;
            try
            {
                // Bytes must not be forwarded: a pre-read buffer belongs to the caller's (JPEG/preview) decode and WIC
                // would decode those bytes instead of the RAW file. The full decode always reads the RAW itself.
                decoded = _wicDecoder.Decode(request with { SourceOrientation = info.Orientation, Bytes = null });
            }
            catch (Exception ex) when (ex is COMException or InvalidDataException or OutOfMemoryException or FileFormatException)
            {
                // Any WIC-side failure to fully decode this RAW means "use the embedded preview instead" to the caller.
                throw new NotSupportedException($"WIC could not fully decode {info.Format}.", ex);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (preview is { Width: > 0, Height: > 0 })
            {
                var (previewWidth, previewHeight) = ExifOrientation.IsTransposed(info.Orientation)
                    ? (preview.Height, preview.Width)
                    : (preview.Width, preview.Height);
                if (decoded.OriginalWidth == previewWidth && decoded.OriginalHeight == previewHeight)
                    throw new NotSupportedException($"WIC returned only the embedded preview for {info.Format}.");
            }

            return decoded;
        }
    }

    private RawContainerInfo ReadContainerInfo(string path, SourceReadPriority priority, out SourceRawHeaderSource headerSource) =>
        ReadContainerInfo(path, priority, CancellationToken.None, out headerSource);

    private RawContainerInfo ReadContainerInfo(string path, SourceReadPriority priority, CancellationToken cancellationToken, out SourceRawHeaderSource headerSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        headerSource = new SourceRawHeaderSource(path, _sourceReader, priority);
        try
        {
            var firstBytes = headerSource.Read(0, RawContainerLimits.InitialProbeLength(headerSource.Length));
            var reader = _registry.FindReader(firstBytes, Path.GetExtension(path))
                ?? throw new NotSupportedException($"Unsupported RAW format: {Path.GetExtension(path)}");
            return reader.Read(headerSource, cancellationToken);
        }
        catch
        {
            headerSource.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when any registered WIC decoder lists the RAW extension of <paramref name="format"/> in its
    /// <c>FileExtensions</c> (case-insensitive). No name-based guess: a decoder that lists no extensions is not a match.
    /// </summary>
    internal static bool IsRegisteredInWic(IWicCodecRegistry registry, RawFormat format)
    {
        var extension = ExtensionFor(format);
        if (extension is null) return false;

        try
        {
            foreach (var codec in registry.ReadDecoders())
            {
                if (string.IsNullOrWhiteSpace(codec.FileExtensions)) continue;
                if (codec.FileExtensions.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }

    private static string? ExtensionFor(RawFormat format) => format switch
    {
        RawFormat.Cr2 => ".cr2",
        RawFormat.Cr3 => ".cr3",
        RawFormat.Nef => ".nef",
        RawFormat.Nrw => ".nrw",
        RawFormat.Arw => ".arw",
        RawFormat.Dng => ".dng",
        RawFormat.Raf => ".raf",
        RawFormat.Orf => ".orf",
        RawFormat.Rw2 => ".rw2",
        RawFormat.Pef => ".pef",
        _ => null
    };
}
