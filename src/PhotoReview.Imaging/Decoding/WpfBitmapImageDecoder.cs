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
            var bitmap = DecodeSource(request, out int orientation, out int originalWidth, out int originalHeight);
            return new WpfDecodedImage(bitmap, downscaled: request.TargetWidth > 0, orientation: orientation,
                originalWidth: originalWidth, originalHeight: originalHeight);
        }
        catch (Exception ex) when (request.TargetWidth > 0 && IsDownscaleFallbackException(ex))
        {
            var fallbackRequest = new DecodeRequest(request.Path, 0, request.ApplyOrientation, request.Bytes);
            var bitmap = DecodeSource(fallbackRequest, out int orientation, out int originalWidth, out int originalHeight);
            return new WpfDecodedImage(bitmap, downscaled: false, orientation: orientation,
                originalWidth: originalWidth, originalHeight: originalHeight);
        }
    }

    public static IDecodedImage DecodeWithFallback(string path, int targetWidth)
        => DecodeWithFallback(new DecodeRequest(path, targetWidth));

    public static BitmapSource DecodeSource(DecodeRequest request)
        => DecodeSource(request, out _, out _, out _);

    private static BitmapSource DecodeSource(DecodeRequest request, out int orientation, out int originalWidth, out int originalHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value);
            return DecodeStream(memoryStream, request, out orientation, out originalWidth, out originalHeight);
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        return DecodeStream(stream, request, out orientation, out originalWidth, out originalHeight);
    }

    private static BitmapSource DecodeStream(Stream stream, DecodeRequest request, out int orientation, out int originalWidth, out int originalHeight)
    {
        orientation = 1;
        originalWidth = 0;
        originalHeight = 0;
        if (request.ApplyOrientation && stream.CanSeek)
        {
            try
            {
                // DelayCreation only parses the header (no pixel decode), so PixelWidth/Height
                // here -- read for free alongside the orientation tag -- are the full source's
                // own dimensions, pre-orientation-swap (matches WpfBitmapImageDecoder.ReadInfo).
                var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (headerDecoder.Frames.Count > 0)
                {
                    var headerFrame = headerDecoder.Frames[0];
                    orientation = ExifOrientation.Read(headerFrame.Metadata as BitmapMetadata);
                    originalWidth = headerFrame.PixelWidth;
                    originalHeight = headerFrame.PixelHeight;
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
            {
                orientation = 1;
                originalWidth = 0;
                originalHeight = 0;
            }
            stream.Position = 0;
        }

        // A transposing orientation (5-8) swaps width/height, exactly mirroring what
        // ExifOrientation.Apply below does to the final bitmap's own pixel dimensions.
        if (orientation is >= 5 and <= 8 && originalWidth > 0)
        {
            (originalWidth, originalHeight) = (originalHeight, originalWidth);
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

    private static bool IsDownscaleFallbackException(Exception ex)
        => ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException;
}
