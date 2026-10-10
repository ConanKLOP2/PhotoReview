using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.TestSupport.Windows;

/// <summary>Thrown by <see cref="PixelAssert"/> when two images differ; the message names the first differing pixel.</summary>
public sealed class PixelMismatchException(string message) : Exception(message);

/// <summary>
/// WP-02 (NO-WPF-EXEC-PLAN mục 4.3): helper test trên <see cref="PixelBuffer"/> thay cho assert trên BitmapSource, dùng
/// chung cho WP-03..05. Nằm ở TestSupport.Windows (không phải TestSupport) vì TestSupport là net10.0 còn
/// <c>PhotoReview.Imaging</c> là net10.0-windows; các cầu nối BitmapSource cũng cần WPF. Không phụ thuộc xunit: lỗi là
/// <see cref="PixelMismatchException"/>.
/// </summary>
public static class PixelAssert
{
    /// <summary>
    /// Buffer ngẫu nhiên có hạt giống (mỗi pixel khác nhau gần như chắc chắn, nên phép lật/xoay sai bị lộ). Pbgra32 là
    /// premultiplied hợp lệ (B,G,R &lt;= A), với A = 255 ở khoảng 3/4 pixel; Bgr32 có byte X ngẫu nhiên.
    /// </summary>
    public static PixelBuffer CreatePattern(int width, int height, PixelLayout layout, int seed)
    {
        var rng = new Random(seed);
        var buffer = PixelBuffer.Allocate(width, height, layout);
        for (var y = 0; y < height; y++)
        {
            var row = buffer.GetRow(y);
            rng.NextBytes(row);
            if (layout != PixelLayout.Pbgra32) continue;
            for (var i = 0; i < row.Length; i += 4)
            {
                var alpha = rng.Next(4) == 0 ? (byte)rng.Next(256) : (byte)255;
                row[i + 3] = alpha;
                row[i] = (byte)(row[i] * alpha / 255);
                row[i + 1] = (byte)(row[i + 1] * alpha / 255);
                row[i + 2] = (byte)(row[i + 2] * alpha / 255);
            }
        }

        return buffer;
    }

    /// <summary>Bản sao độc lập (cùng kích thước, layout, byte).</summary>
    public static PixelBuffer Clone(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = PixelBuffer.Allocate(source.Width, source.Height, source.Layout);
        for (var y = 0; y < source.Height; y++) source.GetRow(y).CopyTo(copy.GetRow(y));
        return copy;
    }

    /// <summary>Định dạng WPF tương ứng (Bgr32 / Pbgra32).</summary>
    public static PixelFormat ToPixelFormat(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgr32 => PixelFormats.Bgr32,
        PixelLayout.Pbgra32 => PixelFormats.Pbgra32,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null),
    };

    /// <summary>BitmapSource (đã Freeze) chứa đúng các byte của <paramref name="pixels"/>.</summary>
    public static BitmapSource ToBitmapSource(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (!pixels.TryGetSpan(out var span)) throw new ArgumentException("Test images must be smaller than 2 GB.", nameof(pixels));
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, ToPixelFormat(pixels.Layout), null, span.ToArray(), pixels.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Đọc <paramref name="source"/> vào PixelBuffer mới theo <paramref name="layout"/>; khác định dạng thì chuyển bằng
    /// <see cref="FormatConvertedBitmap"/> (đường WPF hiện tại dùng).
    /// </summary>
    public static PixelBuffer FromBitmapSource(BitmapSource source, PixelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(source);
        var format = ToPixelFormat(layout);
        BitmapSource converted = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
        var buffer = PixelBuffer.Allocate(converted.PixelWidth, converted.PixelHeight, layout);
        var row = new byte[buffer.Stride];
        for (var y = 0; y < buffer.Height; y++)
        {
            converted.CopyPixels(new System.Windows.Int32Rect(0, y, buffer.Width, 1), row, buffer.Stride, 0);
            row.CopyTo(buffer.GetRow(y));
        }

        return buffer;
    }

    /// <summary>
    /// Kích thước, layout và từng byte bằng nhau. <paramref name="compareUnusedByte"/> = false bỏ qua byte X của Bgr32
    /// (không có nghĩa, một số đường WPF đặt lại nó).
    /// </summary>
    public static void Equal(PixelBuffer expected, PixelBuffer actual, bool compareUnusedByte = true)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height || expected.Layout != actual.Layout)
        {
            throw new PixelMismatchException(string.Create(CultureInfo.InvariantCulture,
                $"Expected {expected.Width}x{expected.Height} {expected.Layout}, got {actual.Width}x{actual.Height} {actual.Layout}."));
        }

        var skipX = !compareUnusedByte && expected.Layout == PixelLayout.Bgr32;
        for (var y = 0; y < expected.Height; y++)
        {
            var e = expected.GetRow(y);
            var a = actual.GetRow(y);
            if (e.SequenceEqual(a)) continue;
            for (var i = 0; i < e.Length; i++)
            {
                if (e[i] == a[i] || (skipX && i % 4 == 3)) continue;
                throw new PixelMismatchException(string.Create(CultureInfo.InvariantCulture,
                    $"Pixel ({i / 4}, {y}) channel {"BGRA"[i % 4]}: expected {e[i]}, got {a[i]}."));
            }
        }
    }

    /// <summary>So với BitmapSource (đọc qua <see cref="FromBitmapSource"/> theo layout của <paramref name="actual"/>).</summary>
    public static void Equal(BitmapSource expected, PixelBuffer actual, bool compareUnusedByte = true)
    {
        ArgumentNullException.ThrowIfNull(actual);
        using var reference = FromBitmapSource(expected, actual.Layout);
        Equal(reference, actual, compareUnusedByte);
    }

    /// <summary>Sai lệch tuyệt đối trung bình mỗi kênh (0..255) trên B,G,R (+A khi cả hai là Pbgra32). Cùng kích thước.</summary>
    public static double MeanAbsoluteError(PixelBuffer expected, PixelBuffer actual)
    {
        var (sum, count, _) = Differences(expected, actual);
        return (double)sum / count;
    }

    /// <summary>PSNR (dB, đỉnh 255) trên cùng các kênh như <see cref="MeanAbsoluteError"/>; ảnh giống hệt -&gt; +∞.</summary>
    public static double Psnr(PixelBuffer expected, PixelBuffer actual)
    {
        var (_, count, squares) = Differences(expected, actual);
        if (squares == 0) return double.PositiveInfinity;
        var mse = squares / count;
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static (long Sum, long Count, double Squares) Differences(PixelBuffer expected, PixelBuffer actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            throw new ArgumentException("Images must have the same size.", nameof(actual));

        var channels = expected.Layout == PixelLayout.Pbgra32 && actual.Layout == PixelLayout.Pbgra32 ? 4 : 3;
        long sum = 0;
        long count = 0;
        double squares = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            var e = expected.GetRow(y);
            var a = actual.GetRow(y);
            for (var i = 0; i < e.Length; i += 4)
            {
                for (var c = 0; c < channels; c++)
                {
                    var d = Math.Abs(e[i + c] - a[i + c]);
                    sum += d;
                    squares += (double)d * d;
                    count++;
                }
            }
        }

        return (sum, count, squares);
    }
}
