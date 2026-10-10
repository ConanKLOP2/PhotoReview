using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// WP-04 (tạm, tới WP-06): codec C-02 của bản WPF dành cho cache đĩa - <see cref="BitmapSource"/> đã Freeze &lt;-&gt;
/// <see cref="PixelBuffer"/>. Nhờ nó, <c>PhotoReview.Imaging.Caching</c> chỉ còn làm việc với PixelBuffer + WIC và không còn
/// dùng kiểu WPF nào. WP-03 tạo song song <c>WpfBitmapSourceCodec</c> (chiều FromPixels cho decoder); khi WP-06 dời phần WPF
/// sang <c>PhotoReview.Imaging.Wpf</c>, hai lớp gộp làm một và các nơi dùng nhận codec bắt buộc qua DI (không còn mặc định).
/// </summary>
/// <remarks>
/// ToPixels: Bgr32/Pbgra32 chép thẳng; định dạng khác đi qua <see cref="FormatConvertedBitmap"/> sang Pbgra32 nếu nguồn có thể
/// mang alpha (<see cref="HasAlpha(BitmapSource)"/>), ngược lại Bgr32 - đúng hai định dạng mà đường cache WPF cũ trả về khi đọc.
/// Một <see cref="PixelBuffer"/> (ví dụ do decoder không-WPF tạo) được cho mượn nguyên. Thread-safe: chỉ đọc bitmap đã Freeze.
/// </remarks>
internal sealed class WpfCacheImageCodec : IPlatformImageCodec
{
    public static readonly WpfCacheImageCodec Instance = new();

    private WpfCacheImageCodec()
    {
    }

    public string Name => "wpf";

    /// <summary>Chép <paramref name="pixels"/> vào một BitmapSource Bgr32/Pbgra32 96 dpi đã Freeze rồi Dispose buffer (nhận quyền sở hữu).</summary>
    public object FromPixels(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        try
        {
            if (pixels.ByteCount > int.MaxValue) throw new ArgumentException("Pixel buffer is too large for a BitmapSource.", nameof(pixels));
            var format = pixels.Layout == PixelLayout.Pbgra32 ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, format, null, pixels.Address, (int)pixels.ByteCount, pixels.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pixels.Dispose();
        }
    }

    /// <summary>BitmapSource -&gt; bản sao PixelBuffer (Owned); PixelBuffer -&gt; mượn. Kiểu khác: <see cref="ArgumentException"/>.</summary>
    public PixelLease ToPixels(object platformImage)
    {
        ArgumentNullException.ThrowIfNull(platformImage);
        if (platformImage is PixelBuffer borrowed)
        {
            ObjectDisposedException.ThrowIf(borrowed.IsDisposed, borrowed);
            return new PixelLease(borrowed, owned: false);
        }

        if (platformImage is not BitmapSource bitmap)
            throw new ArgumentException($"Expected a {nameof(BitmapSource)} or {nameof(PixelBuffer)}, got {platformImage.GetType().FullName}.", nameof(platformImage));

        var layout = HasAlpha(bitmap) ? PixelLayout.Pbgra32 : PixelLayout.Bgr32;
        var format = layout == PixelLayout.Pbgra32 ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        BitmapSource source = bitmap.Format == format ? bitmap : new FormatConvertedBitmap(bitmap, format, null, 0);
        var pixels = PixelBuffer.Allocate(source.PixelWidth, source.PixelHeight, layout);
        try
        {
            if (pixels.ByteCount > int.MaxValue) throw new ArgumentException("Bitmap is too large for the disk cache.", nameof(platformImage));
            source.CopyPixels(System.Windows.Int32Rect.Empty, pixels.Address, (int)pixels.ByteCount, pixels.Stride);
            return new PixelLease(pixels, owned: true);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    /// <summary>True khi ảnh nền tảng có thể mang độ trong suốt (định dạng có alpha / palette có màu không đục).</summary>
    internal static bool CanCarryAlpha(object platformImage) => platformImage switch
    {
        PixelBuffer pixels => pixels.Layout == PixelLayout.Pbgra32,
        BitmapSource bitmap => HasAlpha(bitmap),
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="bmp"/> can carry transparency (alpha pixel format, or an indexed
    /// format whose palette has a non-opaque color). Such previews must not go through the JPEG cache
    /// unless every pixel is opaque (Q-R7). Moved here unchanged from PreviewCacheFile (WP-04).
    /// </summary>
    internal static bool HasAlpha(BitmapSource bmp)
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
    internal static bool IsFullyOpaque(BitmapSource bmp)
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
