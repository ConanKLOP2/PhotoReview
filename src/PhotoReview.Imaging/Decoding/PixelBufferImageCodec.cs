using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02: codec của bản Win32 - "ảnh nền tảng" chính là <see cref="PixelBuffer"/>. FromPixels trả pixels; ToPixels trả
/// <c>new PixelLease((PixelBuffer)platformImage, owned: false)</c>. Thực thi ở WP-02.
/// </summary>
public sealed class PixelBufferImageCodec : IPlatformImageCodec
{
    public static readonly PixelBufferImageCodec Instance = new();

    private PixelBufferImageCodec()
    {
    }

    public string Name => throw new NotImplementedException();

    public object FromPixels(PixelBuffer pixels) => throw new NotImplementedException();

    public PixelLease ToPixels(object platformImage) => throw new NotImplementedException();
}
