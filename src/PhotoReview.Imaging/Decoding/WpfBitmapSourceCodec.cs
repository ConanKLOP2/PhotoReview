using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02 codec of the WPF app: the platform image is a frozen <see cref="BitmapSource"/>. WP-03 temporary home (the
/// "BitmapSourceFromPixels" adapter of the card) inside PhotoReview.Imaging; WP-06 moves it to <c>PhotoReview.Imaging.Wpf</c>.
/// Stateless, so thread-safe; runs on decode/persist threads.
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
    /// Pbgra32 are copied as they are; any other format is converted to Pbgra32 (Bgr32 when the format has no alpha) by
    /// <see cref="FormatConvertedBitmap"/>. Throws <see cref="ArgumentException"/> for any other type.
    /// </summary>
    public PixelLease ToPixels(object platformImage)
    {
        ArgumentNullException.ThrowIfNull(platformImage);
        if (platformImage is not BitmapSource source)
            throw new ArgumentException($"Expected a {nameof(BitmapSource)}, got {platformImage.GetType().FullName}.", nameof(platformImage));

        var layout = source.Format == PixelFormats.Bgr32 ? PixelLayout.Bgr32
            : source.Format == PixelFormats.Pbgra32 ? PixelLayout.Pbgra32
            : HasAlphaChannel(source.Format) ? PixelLayout.Pbgra32 : PixelLayout.Bgr32;
        var format = ToPixelFormat(layout);
        BitmapSource readable = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);

        var pixels = PixelBuffer.Allocate(readable.PixelWidth, readable.PixelHeight, layout);
        try
        {
            readable.CopyPixels(System.Windows.Int32Rect.Empty, pixels.Address, checked((int)pixels.ByteCount), pixels.Stride);
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

    private static bool HasAlphaChannel(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba64
        || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float
        || format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4
        || format == PixelFormats.Indexed8;
}
