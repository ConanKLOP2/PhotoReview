using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02: thay WpfDecodedImage cho mọi decoder không-WPF. OriginalWidth/Height = pixel khi truyền 0. Thực thi ở WP-02.
/// Bất biến: ảnh nền tảng khác null, kích thước &gt;= 1, <c>estimatedBytes</c> &gt;= 0; nếu ảnh nền tảng là
/// <see cref="PixelBuffer"/> thì kích thước phải khớp buffer (bắt sớm lỗi truyền nhầm kích thước trước/sau orientation).
/// </summary>
public sealed class DecodedImage : IDecodedImage
{
    public DecodedImage(object platformImage, int pixelWidth, int pixelHeight, long estimatedBytes,
        bool downscaled = false, int orientation = 1, DecoderBackend actualBackend = DecoderBackend.WicDirect,
        int originalWidth = 0, int originalHeight = 0, PhotoReview.Imaging.Metadata.ExifSummary? exif = null,
        bool isDegradedFallback = false)
    {
        ArgumentNullException.ThrowIfNull(platformImage);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        if (platformImage is PixelBuffer pixels && (pixels.Width != pixelWidth || pixels.Height != pixelHeight))
        {
            throw new ArgumentException(
                $"Pixel size {pixelWidth}x{pixelHeight} does not match the {pixels.Width}x{pixels.Height} pixel buffer.", nameof(platformImage));
        }

        PlatformImage = platformImage;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        EstimatedBytes = estimatedBytes;
        Downscaled = downscaled;
        Orientation = orientation;
        ActualBackend = actualBackend;
        // Như WpfDecodedImage: giá trị <= 0 = "không biết", dùng kích thước pixel.
        OriginalWidth = originalWidth > 0 ? originalWidth : pixelWidth;
        OriginalHeight = originalHeight > 0 ? originalHeight : pixelHeight;
        Exif = exif;
        IsDegradedFallback = isDegradedFallback;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool Downscaled { get; }

    public int Orientation { get; }

    public long EstimatedBytes { get; }

    public object PlatformImage { get; }

    public DecoderBackend ActualBackend { get; }

    public int OriginalWidth { get; }

    public int OriginalHeight { get; }

    public PhotoReview.Imaging.Metadata.ExifSummary? Exif { get; }

    public bool IsDegradedFallback { get; }
}
