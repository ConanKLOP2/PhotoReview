using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Decodes a RAW container through WIC when a matching decoder is registered and it produces
/// more detail than the largest embedded preview.
/// </summary>
public sealed class WicRawFullDecoder : IImageDecoder
{
    private const string WicDecoderCategoryPath = @"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";
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
        Func<RawFormat, bool>? availabilityProbe = null)
    {
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        _wicDecoder = wicDecoder ?? throw new ArgumentNullException(nameof(wicDecoder));
        _availabilityProbe = availabilityProbe ?? IsRegisteredInWic;
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
    public IDecodedImage Decode(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        var info = ReadContainerInfo(request.Path, request.Priority, out var headerSource);
        using (headerSource)
        {
            if (!IsCodecAvailable(info.Format))
                throw new NotSupportedException($"No WIC RAW decoder is registered for {info.Format}.");

            var preview = PreviewSelector.SelectPreview(headerSource, info.Previews, DecodeBox.Unbounded, info.Orientation);
            if (preview is not null && (preview.Width <= 0 || preview.Height <= 0))
                throw new NotSupportedException($"The embedded preview dimensions are unavailable for {info.Format}.");

            IDecodedImage decoded;
            try
            {
                decoded = _wicDecoder.Decode(request with { SourceOrientation = info.Orientation });
            }
            catch (COMException ex)
            {
                throw new NotSupportedException($"WIC could not fully decode {info.Format}.", ex);
            }

            if (preview is not null)
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

    private RawContainerInfo ReadContainerInfo(string path, SourceReadPriority priority, out SourceRawHeaderSource headerSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        headerSource = new SourceRawHeaderSource(path, _sourceReader, priority);
        try
        {
            var firstBytes = headerSource.Read(0, Math.Min(64, (int)headerSource.Length));
            var reader = _registry.FindReader(firstBytes, Path.GetExtension(path))
                ?? throw new NotSupportedException($"Unsupported RAW format: {Path.GetExtension(path)}");
            return reader.Read(headerSource, CancellationToken.None);
        }
        catch
        {
            headerSource.Dispose();
            throw;
        }
    }

    private static bool IsRegisteredInWic(RawFormat format)
    {
        var extension = ExtensionFor(format);
        if (extension is null) return false;

        try
        {
            using var category = Registry.ClassesRoot.OpenSubKey(WicDecoderCategoryPath);
            if (category is null) return false;

            foreach (var name in category.GetSubKeyNames())
            {
                using var codec = category.OpenSubKey(name);
                var friendlyName = codec?.GetValue("FriendlyName") as string;
                var extensions = codec?.GetValue("FileExtensions");
                var extensionText = extensions switch
                {
                    string value => value,
                    string[] values => string.Join(';', values),
                    _ => string.Empty
                };

                if (extensionText.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Microsoft registers its general-purpose RAW decoder without listing extensions.
                if (string.IsNullOrWhiteSpace(extensionText) &&
                    friendlyName?.Contains("raw", StringComparison.OrdinalIgnoreCase) == true)
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
