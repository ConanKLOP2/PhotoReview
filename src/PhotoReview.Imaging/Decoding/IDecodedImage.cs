using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Represents a decoded image independent of any specific UI framework.
/// Exposes dimensions, downscaling indicator, EXIF orientation, memory estimate, and platform-specific handle.
/// </summary>
public interface IDecodedImage
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    bool Downscaled { get; }
    int Orientation { get; }
    long EstimatedBytes { get; }
    object PlatformImage { get; }
    DecoderBackend ActualBackend => DecoderBackend.Wpf;

    /// <summary>
    /// Full-resolution source pixel width, after EXIF orientation is applied (i.e. already
    /// swapped for a transposing orientation), as reported by the decoder that produced this
    /// image -- without any extra file open/header re-read. Decoders that always decode the
    /// full source (never downscaled) or that have no cheaper way to learn it default to
    /// <see cref="PixelWidth"/>, which is exactly correct in that case.
    /// </summary>
    int OriginalWidth => PixelWidth;

    /// <summary>See <see cref="OriginalWidth"/>.</summary>
    int OriginalHeight => PixelHeight;

    /// <summary>
    /// EXIF fields for the photo information line, read by the decoder from the metadata it already parses during
    /// this decode (or restored from the preview disk-cache entry) -- never by an extra file read. Null when the
    /// source has no usable EXIF, the backend could not read it, or the image came from an older cache entry.
    /// </summary>
    PhotoReview.Imaging.Metadata.ExifSummary? Exif => null;

    /// <summary>
    /// True when this image is a next-best fallback accepted after a larger/better source failed to decode (e.g. a RAW's
    /// smaller preview after the chosen one was corrupt). It is returned to the caller but must never be cached (RAM or
    /// disk) so that a later view retries the better source.
    /// </summary>
    bool IsDegradedFallback => false;
}

/// <summary>Optional diagnostics for decoders that read only a range of a larger source file.</summary>
public interface ISourceReadMetrics
{
    /// <summary>Logical source bytes consumed to produce this decoded image.</summary>
    long SourceBytesRead { get; }
}

/// <summary>
/// Optional: implemented by a RAW image whose pixels come from the file's embedded JPEG preview (Q-RAW-03: the photo
/// information line then shows the preview's own size next to the sensor size). Not persisted in the preview disk
/// cache, so an image restored from it does not implement this (the label is omitted rather than guessed).
/// </summary>
public interface IRawPreviewInfo
{
    /// <summary>Full pixel width of the embedded JPEG the image was decoded from (after EXIF orientation); 0 = not a preview.</summary>
    int EmbeddedPreviewWidth { get; }

    /// <summary>See <see cref="EmbeddedPreviewWidth"/>.</summary>
    int EmbeddedPreviewHeight { get; }
}
