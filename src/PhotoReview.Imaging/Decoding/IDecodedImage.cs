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
}
