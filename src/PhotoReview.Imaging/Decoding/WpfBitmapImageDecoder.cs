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
            var bitmap = DecodeSource(request, out int orientation, out bool downscaled);
            return new WpfDecodedImage(bitmap, downscaled, orientation: orientation);
        }
        catch (Exception ex) when (request.IsDownscaleRequested && IsDownscaleFallbackException(ex))
        {
            var fallbackRequest = new DecodeRequest(request.Path, 0, request.ApplyOrientation, request.Bytes);
            var bitmap = DecodeSource(fallbackRequest, out int orientation, out _);
            return new WpfDecodedImage(bitmap, downscaled: false, orientation: orientation);
        }
    }

    public static IDecodedImage DecodeWithFallback(string path, int targetWidth)
        => DecodeWithFallback(new DecodeRequest(path, targetWidth));

    public static BitmapSource DecodeSource(DecodeRequest request)
        => DecodeSource(request, out _, out _);

    private static BitmapSource DecodeSource(DecodeRequest request, out int orientation, out bool downscaled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value);
            return DecodeStream(memoryStream, request, out orientation, out downscaled);
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        return DecodeStream(stream, request, out orientation, out downscaled);
    }

    private static BitmapSource DecodeStream(Stream stream, DecodeRequest request, out int orientation, out bool downscaled)
    {
        orientation = 1;
        // A box (both axes, e.g. previews) needs the source size to pick the constraining side and
        // to avoid upscaling; width-only requests (thumbnails, legacy callers) keep the old path.
        bool isBox = request.TargetHeight > 0;
        int rawWidth = 0, rawHeight = 0;
        if ((request.ApplyOrientation || isBox) && stream.CanSeek)
        {
            try
            {
                var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (headerDecoder.Frames.Count > 0)
                {
                    var frame = headerDecoder.Frames[0];
                    if (request.ApplyOrientation) orientation = ExifOrientation.Read(frame.Metadata as BitmapMetadata);
                    if (isBox)
                    {
                        rawWidth = frame.PixelWidth;
                        rawHeight = frame.PixelHeight;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
            {
                orientation = 1;
                rawWidth = rawHeight = 0;
            }
            stream.Position = 0;
        }

        // For transposed orientations (5 to 8), decoded height becomes the final visual width after rotation.
        bool isTransposed = request.ApplyOrientation && orientation is >= 5 and <= 8;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        downscaled = false;
        if (isBox && rawWidth > 0 && rawHeight > 0)
        {
            // Fit the box against the displayed (oriented) size on the stored pixel grid (rotation is
            // applied afterwards). Setting both sides yields exactly the size the other decoders
            // produce (DecodeBox.Fit already preserved the aspect ratio).
            var (targetW, targetH) = request.Box.FitStored(rawWidth, rawHeight, isTransposed);
            if (targetW < rawWidth || targetH < rawHeight)
            {
                bitmap.DecodePixelWidth = targetW;
                bitmap.DecodePixelHeight = targetH;
                downscaled = true;
            }
        }
        else if (request.IsDownscaleRequested)
        {
            // Width-only request (or a box whose source size could not be read): legacy behaviour.
            if (request.TargetWidth > 0)
            {
                if (isTransposed) bitmap.DecodePixelHeight = request.TargetWidth;
                else bitmap.DecodePixelWidth = request.TargetWidth;
            }
            else
            {
                if (isTransposed) bitmap.DecodePixelWidth = request.TargetHeight;
                else bitmap.DecodePixelHeight = request.TargetHeight;
            }
            downscaled = true;
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
