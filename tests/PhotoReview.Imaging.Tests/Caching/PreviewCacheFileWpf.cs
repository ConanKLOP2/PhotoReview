using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// WP-04: cầu nối test giữ hình dạng API cũ (BitmapSource) cho các test cache viết trước WP-04. Chỉ chuyển kiểu qua codec WPF
/// (<see cref="WpfBitmapSourceCodec"/>, đúng đường app WPF đi) rồi gọi thẳng API PixelBuffer của <see cref="PreviewCacheFile"/>:
/// mọi kiểm tra (alpha Q-R7, orientation, header) vẫn là của mã sản phẩm.
/// </summary>
internal static class PreviewCacheFileWpf
{
    public readonly record struct ReadResult(BitmapSource Bitmap, DecoderBackend ActualBackend, int Orientation, long FileBytes, int OriginalWidth,
        int OriginalHeight, ExifSummary? Exif = null);

    public static async Task WriteAtomicallyAsync(BitmapSource bitmap, DecoderBackend actualBackend, int orientation, int originalWidth,
        int originalHeight, string cachePath, bool opacityVerified = false, ExifSummary? exif = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var lease = WpfBitmapSourceCodec.Instance.ToPixels(bitmap);
        await PreviewCacheFile.WriteAtomicallyAsync(lease.Pixels, actualBackend, orientation, originalWidth, originalHeight, cachePath,
            opacityVerified, exif, cancellationToken).ConfigureAwait(false);
    }

    public static ReadResult Read(string cachePath)
    {
        var entry = PreviewCacheFile.Read(cachePath);
        var bitmap = (BitmapSource)WpfBitmapSourceCodec.Instance.FromPixels(entry.Pixels);
        return new ReadResult(bitmap, entry.ActualBackend, entry.Orientation, entry.FileBytes, entry.OriginalWidth, entry.OriginalHeight, entry.Exif);
    }
}
