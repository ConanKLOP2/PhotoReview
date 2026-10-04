using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// WPF WIC-based default image decoder.
/// Preserves native FileStream sharing flags (ReadWrite | Delete), sequential scan, 1MB buffer, OnLoad and Freeze.
/// </summary>
public sealed class WpfBitmapImageDecoder : IImageDecoder
{
    private readonly ISourceReader _sourceReader;

    /// <param name="sourceReader">
    /// Q-R29 option C-2 seam: null (every existing call site) uses <see cref="PhysicalSourceReader"/>,
    /// byte-for-byte the direct <see cref="FileStream"/> this decoder opened before the seam existed.
    /// </param>
    public WpfBitmapImageDecoder(ISourceReader? sourceReader = null) => _sourceReader = sourceReader ?? PhysicalSourceReader.Instance;

    /// <summary>Memory reading for the upfront output admission (test seam; production reads the GC).</summary>
    internal Func<(long TotalAvailable, long Load)> MemoryInfo { get; init; } = MemoryHeadroom.ReadGcMemoryInfo;

    public IDecodedImage Decode(DecodeRequest request)
        => DecodeWithFallback(request, _sourceReader, MemoryInfo);

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = _sourceReader.OpenSource(path, SourceReadPriority.Viewer, 1024 * 1024);
        BitmapFrame frame;
        try
        {
            // PixelWidth/Height and orientation only need the image header. DelayCreation prevents decoding pixel data.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException($"Image has no frames: {path}");
            }
            frame = decoder.Frames[0];
        }
        catch (ArgumentException ex)
        {
            // WIC maps E_INVALIDARG (damaged header) to ArgumentException, which callers treat as a caller bug. The argument
            // checks of this method ran before the try, so this is a data fault.
            throw WicDirectDecoder.AsInvalidData(ex, "WPF");
        }
        var orientation = ReadOrientationOrDefault(() => ExifOrientation.Read(frame.Metadata as BitmapMetadata));
        return new ImageInfo(frame.PixelWidth, frame.PixelHeight, orientation);
    }

    /// <summary>Damaged metadata must not fail a header-only read: the pixel dimensions are still valid, so a metadata fault reads as orientation 1.</summary>
    internal static int ReadOrientationOrDefault(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
            or OverflowException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            return 1;
        }
    }

    /// <param name="sourceReader">
    /// Q-R29 option C-2 seam: null (every existing static call site, e.g. <c>ThumbnailCache</c> and
    /// tests) uses <see cref="PhysicalSourceReader"/>, identical to this method's behavior before the
    /// seam existed. The instance <see cref="Decode"/> above passes this decoder's own injected reader.
    /// </param>
    public static IDecodedImage DecodeWithFallback(DecodeRequest request, ISourceReader? sourceReader = null)
        => DecodeWithFallback(request, sourceReader, MemoryHeadroom.ReadGcMemoryInfo);

    internal static IDecodedImage DecodeWithFallback(DecodeRequest request, ISourceReader? sourceReader, Func<(long TotalAvailable, long Load)> memoryInfo)
    {
        try
        {
            return DecodeWithoutProfileFallback(request, sourceReader, memoryInfo);
        }
        catch (Exception ex) when (request.IsDownscaleRequested && IsDownscaleFallbackException(ex))
        {
            // Full-resolution retry: same path (and EXIF extraction) as a normal decode, just unconstrained.
            return DecodeWithoutProfileFallback(request with { TargetWidth = 0, TargetHeight = 0 }, sourceReader, memoryInfo);
        }
    }

    /// <summary>
    /// WPF reads the colour contexts (embedded ICC profile, EXIF colour space) while finishing the bitmap, and a damaged
    /// EXIF/ICC block there fails the whole decode with <see cref="ArgumentException"/> although the pixel data is fine.
    /// The retry ignores the colour profile: an untagged (sRGB) picture beats an error for a photo whose metadata is bad.
    /// </summary>
    private static WpfDecodedImage DecodeWithoutProfileFallback(DecodeRequest request, ISourceReader? sourceReader, Func<(long TotalAvailable, long Load)> memoryInfo)
    {
        // A caller bug (no path and no bytes) stays an ArgumentException: only WIC's own rejection is converted to a data fault below.
        if (!request.Bytes.HasValue) ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        try
        {
            return DecodeImage(request, ignoreColorProfile: false, sourceReader, memoryInfo);
        }
        catch (ArgumentException)
        {
            try
            {
                return DecodeImage(request, ignoreColorProfile: true, sourceReader, memoryInfo);
            }
            catch (ArgumentException ex)
            {
                // Still rejected without the colour profile: the data itself is damaged, not the caller's arguments.
                throw WicDirectDecoder.AsInvalidData(ex, "WPF");
            }
        }
    }

    private static WpfDecodedImage DecodeImage(DecodeRequest request, bool ignoreColorProfile, ISourceReader? sourceReader, Func<(long TotalAvailable, long Load)> memoryInfo)
    {
        var bitmap = DecodeSource(request, ignoreColorProfile, sourceReader, memoryInfo, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out var exif);
        return new WpfDecodedImage(bitmap, downscaled, orientation: orientation,
            originalWidth: originalWidth, originalHeight: originalHeight, exif: exif);
    }

    private static BitmapSource DecodeSource(DecodeRequest request, bool ignoreColorProfile, ISourceReader? sourceReader, Func<(long TotalAvailable, long Load)> memoryInfo, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out ExifSummary? exif)
    {
        // With pre-read bytes the path is only a label (TurboJpeg accepts any); a file is opened only without them.
        if (!request.Bytes.HasValue) ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        if (request.Bytes.HasValue)
        {
            using var memoryStream = ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value);
            return DecodeStream(memoryStream, request, ignoreColorProfile, memoryInfo, out orientation, out downscaled, out originalWidth, out originalHeight, out exif);
        }

        using var stream = (sourceReader ?? PhysicalSourceReader.Instance).OpenSource(request.Path, request.Priority, 1024 * 1024);
        return DecodeStream(stream, request, ignoreColorProfile, memoryInfo, out orientation, out downscaled, out originalWidth, out originalHeight, out exif);
    }

    private static BitmapSource DecodeStream(Stream stream, DecodeRequest request, bool ignoreColorProfile, Func<(long TotalAvailable, long Load)> memoryInfo, out int orientation, out bool downscaled, out int originalWidth, out int originalHeight, out ExifSummary? exif)
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
                    if (request.ApplyOrientation)
                    {
                        orientation = request.SourceOrientation ?? ExifOrientation.Read(metadata);
                    }
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
                orientation = request.ApplyOrientation && request.SourceOrientation.HasValue ? request.SourceOrientation.Value : 1;
                exif = null;
                rawWidth = rawHeight = 0;
            }
            stream.Position = 0;
        }
        else if (request.ApplyOrientation && request.SourceOrientation.HasValue)
        {
            orientation = request.SourceOrientation.Value;
        }

        // For transposed orientations (5 to 8), decoded height becomes the final visual width after rotation.
        bool isTransposed = request.ApplyOrientation && ExifOrientation.IsTransposed(orientation);

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

        // D-08: the same upfront admission the other backends have (the WPF fallback must not allocate the surface they refused).
        // Only when the source size is known (header pre-read ran); the size is the one the decode will produce (BGRA, 4 bytes/pixel).
        if (rawWidth > 0 && rawHeight > 0)
        {
            var (outW, outH) = bitmap.DecodePixelWidth > 0 && bitmap.DecodePixelHeight > 0 ? (bitmap.DecodePixelWidth, bitmap.DecodePixelHeight) : (rawWidth, rawHeight);
            EnsureDecodeAdmitted(outW, outH, memoryInfo);
        }

        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        return (request.ApplyOrientation && orientation > 1)
            ? ExifOrientation.Apply(bitmap, orientation)
            : bitmap;
    }

    /// <summary>R16: admission on the saturating byte count (a plain <c>(long)w * h * 4</c> wraps negative for hostile sides and would "fit").</summary>
    internal static void EnsureDecodeAdmitted(int outW, int outH, Func<(long TotalAvailable, long Load)> memoryInfo)
        => WicDirectDecoder.EnsureOutputFits(outW, outH, WicDirectDecoder.OutputByteLength(outW, outH), memoryInfo);

    private static bool IsDownscaleFallbackException(Exception ex)
        => ex is not DecoderMemoryAdmissionException // refused for memory: the full-size retry needs even more
           && ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException;
}
