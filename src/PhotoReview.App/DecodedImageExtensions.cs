using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App;

/// <summary>
/// UI-boundary extensions for converting an <see cref="IDecodedImage"/> into WPF UI image sources.
/// </summary>
public static class DecodedImageExtensions
{
    public static BitmapSource AsBitmapSource(this IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PlatformImage is BitmapSource bitmapSource)
        {
            return bitmapSource;
        }

        throw new InvalidOperationException($"PlatformImage of type '{image.PlatformImage?.GetType().FullName ?? "null"}' is not a BitmapSource.");
    }

    public static BitmapSource? AsBitmapSourceOrNull(this IDecodedImage? image)
    {
        if (image is null) return null;
        return image.PlatformImage as BitmapSource;
    }
}
