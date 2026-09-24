using System.Runtime.InteropServices;
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

    /// <summary>
    /// Forces a lazy WPF pipeline (e.g. <see cref="TransformedBitmap"/>, <see cref="FormatConvertedBitmap"/>)
    /// to run now, on the calling (worker) thread, and returns a frozen in-memory bitmap with the same
    /// pixel format. Without this the rotate/scale work runs on the UI/render thread at first render.
    /// The result holds no reference to <paramref name="source"/>, so the (larger) source can be collected.
    /// </summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        var format = source.Format;
        int stride = checked((width * format.BitsPerPixel + 7) / 8);
        stride = checked((stride + 3) & ~3); // DWORD-aligned rows, as WIC prefers
        int bufferSize = checked(stride * height);

        // Native scratch buffer: BitmapSource.Create copies it, so a managed array would only add
        // a large LOH allocation per decode.
        BitmapSource bitmap;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            source.CopyPixels(System.Windows.Int32Rect.Empty, buffer, bufferSize, stride);
            bitmap = BitmapSource.Create(
                width,
                height,
                source.DpiX,
                source.DpiY,
                format,
                source.Palette,
                buffer,
                bufferSize,
                stride);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        bitmap.Freeze();
        return bitmap;
    }
}
