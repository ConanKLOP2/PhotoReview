using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Metadata;

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
            return DecodeWithoutProfileFallback(request);
        }
        catch (Exception ex) when (request.IsDownscaleRequested && IsDownscaleFallbackException(ex))
        {
            // Full-resolution retry: same path (and EXIF extraction) as a normal decode, just unconstrained.
            return DecodeWithoutProfileFallback(new DecodeRequest(request.Path, 0, request.ApplyOrientation, request.Bytes));
        }
    }

    /// <summary>
    /// WPF reads the colour contexts (embedded ICC profile, EXIF colour space) while finishing the bitmap, and a damaged
    /// EXIF/ICC block there fails the whole decode with <see cref="ArgumentException"/> although the pixel data is fine.
    /// The retry ignores the colour profile: an untagged (sRGB) picture beats an error for a photo whose metadata is bad.
    /// </summary>
    private static WpfDecodedImage DecodeWithoutProfileFallback(DecodeRequest request)
    {
        try
        {
            return DecodeImage(request, ignoreColorProfile: false);
        }
        catch (ArgumentException)
        {
            return DecodeImage(request, ignoreColorProfile: true);
        }
    }

    private static WpfDecodedImage DecodeImage(DecodeRequest request, bool ignoreColorProfile)
    {
        var bitmap = DecodeSource(request, ignoreColorProfile, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out var exif);
        return new WpfDecodedImage(bitmap, downscaled, orientation: orientation,
            originalWidth: originalWidth, originalHeight: originalHeight, exif: exif);
    }

    public static BitmapSource DecodeSource(DecodeRequest request)
        => DecodeSource(request, ignoreColorProfile: false, out _, out _, out _, out _, out _);

    private static BitmapSource DecodeSource(DecodeRequest request, bool ignoreColorProfile, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out ExifSummary? exif)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value);
            return DecodeStream(memoryStream, request, ignoreColorProfile, out orientation, out downscaled, out originalWidth, out originalHeight, out exif);
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        return DecodeStream(stream, request, ignoreColorProfile, out orientation, out downscaled, out originalWidth, out originalHeight, out exif);
    }

    private static BitmapSource DecodeStream(Stream stream, DecodeRequest request, bool ignoreColorProfile, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out ExifSummary? exif)
    {
        orientation = 1;
        exif = null;
        originalWidth = 0;
        originalHeight = 0;
        // Any downscale request (a box, or one axis only: thumbnails, legacy callers) needs the source size to pick the
        // constraining side and, above all, to never upscale a source that already fits -- DecodePixelWidth alone stretches
        // a small image up to the requested width, unlike the other backends' DecodeBox.Fit.
        bool isBox = request.IsDownscaleRequested;
        int rawWidth = 0, rawHeight = 0;
        if ((request.ApplyOrientation || isBox) && stream.CanSeek)
        {
            try
            {
                // DelayCreation only parses the header (no pixel decode), so PixelWidth/Height
                // here -- read for free alongside the orientation tag -- are the full source's
                // own dimensions, pre-orientation-swap (matches WpfBitmapImageDecoder.ReadInfo).
                var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (headerDecoder.Frames.Count > 0)
                {
                    var frame = headerDecoder.Frames[0];
                    var metadata = frame.Metadata as BitmapMetadata;
                    if (request.ApplyOrientation) orientation = ExifOrientation.Read(metadata);
                    // Photo information line: same header frame, same metadata block -- no extra read.
                    exif = WpfExifReader.Read(metadata);
                    rawWidth = frame.PixelWidth;
                    rawHeight = frame.PixelHeight;
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException
                or ArgumentException or OverflowException or InvalidCastException or System.Runtime.InteropServices.COMException)
            {
                // Header/metadata pre-read only: whatever WIC says about corrupt metadata, the pixel decode below decides.
                orientation = 1;
                exif = null;
                rawWidth = rawHeight = 0;
            }
            stream.Position = 0;
        }

        // For transposed orientations (5 to 8), decoded height becomes the final visual width after rotation.
        bool isTransposed = request.ApplyOrientation && orientation is >= 5 and <= 8;

        // Original (post-orientation) dimensions reported via IDecodedImage -- mirrors what
        // ExifOrientation.Apply below does to the final bitmap's own pixel dimensions.
        if (rawWidth > 0)
        {
            (originalWidth, originalHeight) = isTransposed ? (rawHeight, rawWidth) : (rawWidth, rawHeight);
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (ignoreColorProfile) bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

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
            else if (request.TargetWidth <= 0 || request.TargetHeight <= 0)
            {
                // Compatibility: a one-axis (width-only) request has always reported Downscaled, also when the source already
                // fit (it used to be stretched instead). Callers and tests key the disk-cache persistence on this flag.
                downscaled = true;
            }
        }
        else if (request.IsDownscaleRequested)
        {
            // The source size could not be read (unseekable stream or a failed header pre-read): legacy behaviour.
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

    private static bool IsDownscaleFallbackException(Exception ex)
        => ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException;
}
