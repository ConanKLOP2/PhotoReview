using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02 (NO-WPF-EXEC-PLAN mục 5): chuyển giữa pixel và "ảnh nền tảng" mà <see cref="IDecodedImage.PlatformImage"/> mang
/// (object, giữ nguyên hợp đồng hiện tại). Bản WPF: WpfBitmapSourceCodec (PhotoReview.Imaging.Wpf) -&gt; BitmapSource đã Freeze.
/// Bản Win32: <see cref="PixelBufferImageCodec"/> -&gt; chính PixelBuffer. Thread-safe, gọi trên luồng decode/persist,
/// không bao giờ trên UI thread trong hot path. Nơi dùng nhận codec qua constructor, không có giá trị mặc định.
/// </summary>
public interface IPlatformImageCodec
{
    /// <summary>"wpf" | "pixels" (chỉ để log/metrics).</summary>
    string Name { get; }

    /// <summary>NHẬN quyền sở hữu <paramref name="pixels"/> (WPF: copy rồi Dispose; pixels: trả nguyên).</summary>
    object FromPixels(PixelBuffer pixels);

    /// <summary>Để encode cache; ném ArgumentException nếu kiểu lạ.</summary>
    PixelLease ToPixels(object platformImage);
}
