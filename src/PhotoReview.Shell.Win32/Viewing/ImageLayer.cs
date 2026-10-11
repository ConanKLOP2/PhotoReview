using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-21: lớp ảnh - vẽ một <see cref="PixelBuffer"/> (qua <see cref="IGpuImageCache"/>, khoá theo <c>PixelBuffer.Id</c>) vào một
/// hình chữ nhật đích (DIP). Dùng chung cho ảnh chính, lớp ảnh cũ của crossfade và hai ô của compare. Không giữ trạng thái:
/// thời gian sống của GPU bitmap là việc của cache (LRU + <c>Retain</c>), thời gian sống của buffer là của chủ sở hữu CPU
/// (cache preview) nên buffer có thể bị giải phóng giữa hai khung - khi đó trả false thay vì ném.
/// </summary>
internal static class ImageLayer
{
    /// <summary>
    /// Vẽ <paramref name="pixels"/> vào <paramref name="destination"/>. <paramref name="exifOrientation"/> 2..8 chỉ cho buffer
    /// THÔ chưa xoay (mọi decoder của app đã áp orientation nên shell dùng 1); <paramref name="destination"/> luôn ở kích thước
    /// đã xoay. Trả false khi buffer đã bị giải phóng hoặc đích rỗng (không vẽ gì).
    /// </summary>
    internal static bool TryDraw(IGpuImageCache cache, IDrawContext context, PixelBuffer pixels, RectD destination,
        ImageInterpolation interpolation, float opacity = 1f, int exifOrientation = 1)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.IsDisposed || destination.Width <= 0 || destination.Height <= 0
            || double.IsNaN(destination.X) || double.IsNaN(destination.Y)
            || double.IsNaN(destination.Width) || double.IsNaN(destination.Height))
        {
            return false;
        }

        IGpuImage gpu;
        try
        {
            gpu = cache.GetOrUpload(pixels);
        }
        catch (ObjectDisposedException)
        {
            return false; // bộ đệm CPU vừa giải phóng buffer này: ảnh thay thế sắp tới
        }

        if (exifOrientation is >= 2 and <= 8 && context is D2DDrawContext oriented)
        {
            oriented.DrawImageOriented(gpu, destination, exifOrientation, null, interpolation, opacity);
        }
        else
        {
            context.DrawImage(gpu, destination, null, interpolation, opacity);
        }

        return true;
    }
}
