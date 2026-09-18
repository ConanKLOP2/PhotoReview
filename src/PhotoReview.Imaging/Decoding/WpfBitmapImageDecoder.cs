using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// WPF WIC-based default image decoder.
/// Preserves native FileStream sharing flags (ReadWrite | Delete), sequential scan, 1MB buffer, OnLoad and Freeze.
/// </summary>
public sealed class WpfBitmapImageDecoder : IImageDecoder
{
    public IDecodedImage Decode(DecodeRequest request)
        => DecodeWithFallback(request);

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        // PixelWidth/Height and orientation only need the image header. DelayCreation prevents decoding pixel data.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var orientation = ExifOrientation.Read(frame.Metadata as BitmapMetadata);
        return new ImageInfo(frame.PixelWidth, frame.PixelHeight, orientation);
    }

    public static IDecodedImage DecodeWithFallback(DecodeRequest request)
    {
        try
        {
            var bitmap = DecodeSource(request);
            return new WpfDecodedImage(bitmap, downscaled: request.TargetWidth > 0);
        }
        catch when (request.TargetWidth > 0)
        {
            var fallbackRequest = new DecodeRequest(request.Path, 0, request.ApplyOrientation, request.Bytes);
            var bitmap = DecodeSource(fallbackRequest);
            return new WpfDecodedImage(bitmap, downscaled: false);
        }
    }

    public static IDecodedImage DecodeWithFallback(string path, int targetWidth)
        => DecodeWithFallback(new DecodeRequest(path, targetWidth));

    public static BitmapSource DecodeSource(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = new MemoryStream(request.Bytes.Value.ToArray(), writable: false);
            return DecodeStream(memoryStream, request);
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        return DecodeStream(stream, request);
    }

    private static BitmapSource DecodeStream(Stream stream, DecodeRequest request)
    {
        int orientation = 1;
        if (request.ApplyOrientation && stream.CanSeek)
        {
            try
            {
                var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (headerDecoder.Frames.Count > 0)
                {
                    orientation = ExifOrientation.Read(headerDecoder.Frames[0].Metadata as BitmapMetadata);
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
            {
                orientation = 1;
            }
            stream.Position = 0;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        if (request.TargetWidth > 0)
        {
            // For transposed orientations (5 to 8), decoded height becomes the final visual width after rotation.
            if (request.ApplyOrientation && orientation is >= 5 and <= 8)
            {
                bitmap.DecodePixelHeight = request.TargetWidth;
            }
            else
            {
                bitmap.DecodePixelWidth = request.TargetWidth;
            }
        }

        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        return (request.ApplyOrientation && orientation > 1)
            ? ExifOrientation.Apply(bitmap, orientation)
            : bitmap;
    }

    public static BitmapSource DecodeSource(string path, int targetWidth)
        => DecodeSource(new DecodeRequest(path, targetWidth));
}
