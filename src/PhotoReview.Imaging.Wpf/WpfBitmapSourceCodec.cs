using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Wpf;

/// <summary>
/// C-02 codec của bản WPF: ảnh nền tảng là <see cref="BitmapSource"/> đã Freeze. Là codec DUY NHẤT của bản WPF (WP-06 gộp
/// <c>WpfBitmapSourceCodec</c> của WP-03 và <c>WpfCacheImageCodec</c> của WP-04): mọi nơi dùng (decoder WIC/TurboJpeg/LibRaw,
/// cache đĩa, thumbnail) nhận nó qua constructor, không còn giá trị mặc định trong PhotoReview.Imaging.
/// Stateless, thread-safe; chạy trên luồng decode/persist.
/// </summary>
public sealed class WpfBitmapSourceCodec : IPlatformImageCodec
{
    public static readonly WpfBitmapSourceCodec Instance = new();

    private WpfBitmapSourceCodec()
    {
    }

    public string Name => "wpf";

    /// <summary>
    /// Copies <paramref name="pixels"/> into a new frozen <see cref="BitmapSource"/> (Bgr32 / Pbgra32, 96 DPI, no palette) and
    /// disposes <paramref name="pixels"/> -- also when the copy throws, since ownership was handed over. This is the single
    /// WIC-buffer-to-MIL copy the WPF path always paid (<c>BitmapSource.Create</c> from a native scratch buffer).
    /// </summary>
    public object FromPixels(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        try
        {
            ObjectDisposedException.ThrowIf(pixels.IsDisposed, pixels);
            var bitmap = BitmapSource.Create(
                pixels.Width,
                pixels.Height,
                96,
                96,
                ToPixelFormat(pixels.Layout),
                null,
                pixels.Address,
                checked((int)pixels.ByteCount),
                pixels.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pixels.Dispose();
        }
    }

    /// <summary>
    /// A new owned <see cref="PixelBuffer"/> copy of <paramref name="platformImage"/> (a <see cref="BitmapSource"/>): Bgr32 and
    /// Pbgra32 are copied as they are; any other format is converted to Pbgra32 when the source can carry alpha
    /// (<see cref="HasAlpha(BitmapSource)"/>: an alpha pixel format, or an indexed format with a non-opaque palette colour),
    /// to Bgr32 otherwise -- the two formats the pre-WP-04 WPF cache path produced. Throws <see cref="ArgumentException"/>
    /// for any other type (a <see cref="PixelBuffer"/> included: this codec's platform image is always a BitmapSource).
    /// </summary>
    public PixelLease ToPixels(object platformImage)
    {
        ArgumentNullException.ThrowIfNull(platformImage);
        if (platformImage is not BitmapSource source)
            throw new ArgumentException($"Expected a {nameof(BitmapSource)}, got {platformImage.GetType().FullName}.", nameof(platformImage));

        var layout = source.Format == PixelFormats.Bgr32 ? PixelLayout.Bgr32
            : source.Format == PixelFormats.Pbgra32 ? PixelLayout.Pbgra32
            : HasAlpha(source) ? PixelLayout.Pbgra32 : PixelLayout.Bgr32;
        var format = ToPixelFormat(layout);
        BitmapSource readable = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);

        var pixels = PixelBuffer.Allocate(readable.PixelWidth, readable.PixelHeight, layout);
        try
        {
            if (pixels.ByteCount > int.MaxValue) throw new ArgumentException("Bitmap is too large to copy.", nameof(platformImage));
            readable.CopyPixels(System.Windows.Int32Rect.Empty, pixels.Address, (int)pixels.ByteCount, pixels.Stride);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }

        return new PixelLease(pixels, owned: true);
    }

    internal static PixelFormat ToPixelFormat(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgr32 => PixelFormats.Bgr32,
        PixelLayout.Pbgra32 => PixelFormats.Pbgra32,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown pixel layout."),
    };

    /// <summary>
    /// True when <paramref name="image"/> can carry transparency (an alpha-capable BitmapSource format, or a palette with a
    /// non-opaque colour) -- such previews go through the JPEG cache only once every pixel is verified opaque (Q-R7).
    /// Moved here from <c>PreviewCacheFile.HasAlpha</c> (WP-06): the question only makes sense for a WPF platform image.
    /// </summary>
    public static bool HasAlpha(IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.PlatformImage is BitmapSource bitmap && HasAlpha(bitmap);
    }

    /// <summary>
    /// True when <paramref name="bmp"/> can carry transparency (alpha pixel format, or an indexed
    /// format whose palette has a non-opaque color). Such previews must not go through the JPEG cache
    /// unless every pixel is opaque (Q-R7). Moved here unchanged from PreviewCacheFile (WP-04).
    /// </summary>
    public static bool HasAlpha(BitmapSource bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        var format = bmp.Format;
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64
            || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
            return true;
        if (format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8)
            return bmp.Palette?.Colors.Any(c => c.A < 255) == true;
        return false;
    }

    /// <summary>
    /// Q-R7 on a BitmapSource without copying it whole: formats that cannot carry alpha are opaque without a scan;
    /// Bgra32/Pbgra32 are scanned band by band through a small pooled buffer (early exit on the first A&lt;255 pixel);
    /// other alpha formats (16-bit/float) are conservatively not opaque. Moved here unchanged from PreviewCacheFile (WP-04).
    /// </summary>
    public static bool IsFullyOpaque(BitmapSource bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        if (!HasAlpha(bmp)) return true;
        var format = bmp.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32) return false;

        var width = bmp.PixelWidth;
        var height = bmp.PixelHeight;
        if (width <= 0 || height <= 0) return true;
        var stride = width * 4;
        var rowsPerBand = Math.Clamp(65536 / stride, 1, height);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(rowsPerBand * stride);
        try
        {
            for (var y = 0; y < height; y += rowsPerBand)
            {
                var rows = Math.Min(rowsPerBand, height - y);
                bmp.CopyPixels(new System.Windows.Int32Rect(0, y, width, rows), buffer, stride, 0);
                if (!AllAlphaOpaque(buffer.AsSpan(0, rows * stride))) return false;
            }
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>True when every 4th byte (alpha of BGRA) of <paramref name="pixels"/> is 255.</summary>
    internal static bool AllAlphaOpaque(ReadOnlySpan<byte> pixels)
    {
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels);
        const uint AlphaMask = 0xFF000000u; // little-endian: byte 3 is the top byte
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && px.Length >= System.Numerics.Vector<uint>.Count)
        {
            var mask = new System.Numerics.Vector<uint>(AlphaMask);
            var last = px.Length - System.Numerics.Vector<uint>.Count;
            for (; i <= last; i += System.Numerics.Vector<uint>.Count)
            {
                var v = new System.Numerics.Vector<uint>(px.Slice(i));
                if (!System.Numerics.Vector.EqualsAll(v & mask, mask)) return false;
            }
        }
        for (; i < px.Length; i++)
            if ((px[i] & AlphaMask) != AlphaMask) return false;
        return true;
    }
}
