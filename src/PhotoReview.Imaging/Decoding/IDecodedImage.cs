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
}
