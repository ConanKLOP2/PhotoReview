using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02: codec của bản Win32 - "ảnh nền tảng" chính là <see cref="PixelBuffer"/>. FromPixels trả pixels; ToPixels trả
/// <c>new PixelLease((PixelBuffer)platformImage, owned: false)</c>. Thực thi ở WP-02. Không trạng thái nên thread-safe.
/// </summary>
public sealed class PixelBufferImageCodec : IPlatformImageCodec
{
    public static readonly PixelBufferImageCodec Instance = new();

    private PixelBufferImageCodec()
    {
    }

    public string Name => "pixels";

    /// <summary>Trả nguyên <paramref name="pixels"/> (quyền sở hữu chuyển sang ảnh nền tảng = chính buffer).</summary>
    public object FromPixels(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(pixels.IsDisposed, pixels);
        return pixels;
    }

    /// <summary>
    /// Mượn buffer (Owned=false: Dispose lease không giải phóng ảnh đang nằm trong cache). Ném ArgumentException khi
    /// <paramref name="platformImage"/> không phải <see cref="PixelBuffer"/>, ObjectDisposedException khi buffer đã giải phóng.
    /// </summary>
    public PixelLease ToPixels(object platformImage)
    {
        ArgumentNullException.ThrowIfNull(platformImage);
        if (platformImage is not PixelBuffer pixels)
            throw new ArgumentException($"Expected a {nameof(PixelBuffer)}, got {platformImage.GetType().FullName}.", nameof(platformImage));
        ObjectDisposedException.ThrowIf(pixels.IsDisposed, pixels);
        return new PixelLease(pixels, owned: false);
    }
}
