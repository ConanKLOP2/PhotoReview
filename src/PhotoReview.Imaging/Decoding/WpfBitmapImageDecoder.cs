using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// WPF WIC-based default image decoder.
/// Preserves native FileStream sharing flags (ReadWrite | Delete), sequential scan, 1MB buffer, OnLoad and Freeze.
/// </summary>
public sealed class WpfBitmapImageDecoder : IImageDecoder
{
    public (BitmapSource Bitmap, bool Downscaled) Decode(DecodeRequest request)
        => DecodeWithFallback(request);

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        // PixelWidth/Height only need the image header. DelayCreation prevents decoding pixel data.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return new ImageInfo(frame.PixelWidth, frame.PixelHeight);
    }

    public static (BitmapSource Bitmap, bool Downscaled) DecodeWithFallback(DecodeRequest request)
    {
        try
        {
            return (DecodeSource(request), request.TargetWidth > 0);
        }
        catch when (request.TargetWidth > 0)
        {
            var fallbackRequest = new DecodeRequest(request.Path, 0, request.ApplyOrientation, request.Bytes);
            return (DecodeSource(fallbackRequest), false);
        }
    }

    public static (BitmapSource Bitmap, bool Downscaled) DecodeWithFallback(string path, int targetWidth)
        => DecodeWithFallback(new DecodeRequest(path, targetWidth));

    public static BitmapSource DecodeSource(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = new MemoryStream(request.Bytes.Value.ToArray(), writable: false);
            var bitmapFromMemory = new BitmapImage();
            bitmapFromMemory.BeginInit();
            bitmapFromMemory.CacheOption = BitmapCacheOption.OnLoad;
            if (request.TargetWidth > 0) bitmapFromMemory.DecodePixelWidth = request.TargetWidth;
            bitmapFromMemory.StreamSource = memoryStream;
            bitmapFromMemory.EndInit();
            bitmapFromMemory.Freeze();
            return bitmapFromMemory;
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (request.TargetWidth > 0) bitmap.DecodePixelWidth = request.TargetWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource DecodeSource(string path, int targetWidth)
        => DecodeSource(new DecodeRequest(path, targetWidth));
}
