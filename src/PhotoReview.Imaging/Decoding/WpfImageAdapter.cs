using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Converts raw pixel buffers to WPF <see cref="BitmapSource"/> instances.
/// Used at the boundary when decoders produce BGRA32 buffers (prepared for WicDirect and TurboJpeg).
/// </summary>
public static class WpfImageAdapter
{
    public static BitmapSource FromBgra32(ReadOnlySpan<byte> bgraBytes, int width, int height, int stride)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgraBytes.ToArray(),
            stride);
        bitmap.Freeze();
        return bitmap;
    }
}
