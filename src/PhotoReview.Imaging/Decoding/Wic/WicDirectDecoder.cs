using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Core.Localization;

namespace PhotoReview.Imaging.Decoding.Wic;

/// <summary>
/// High-performance image decoder directly leveraging Windows Imaging Component (WIC) COM interfaces.
/// Bypasses WPF BitmapImage overhead, utilizes native DCT pre-scaling and WIC format converters,
/// and strictly preserves native sharing flags (ReadWrite | Delete, SequentialScan) conforming to INV-8.
/// </summary>
public sealed class WicDirectDecoder : IImageDecoder
{
    private readonly IPlatformImageCodec _codec;
    private readonly ISourceReader _sourceReader;

    /// <param name="codec">
    /// C-02 platform codec (no default, so a wrong codec is never picked up by accident): the WIC pixels are copied once into
    /// a <see cref="PixelBuffer"/> and handed to <see cref="IPlatformImageCodec.FromPixels"/> -- the WPF app passes
    /// <see cref="WpfBitmapSourceCodec.Instance"/> (a frozen BitmapSource, as before), the Win32 shell
    /// <see cref="PixelBufferImageCodec.Instance"/> (the buffer itself).
    /// </param>
    /// <param name="sourceReader">
    /// Q-R29 option C-2 seam: null (every existing call site) uses <see cref="PhysicalSourceReader"/>,
    /// byte-for-byte the direct <see cref="FileStream"/> this decoder opened before the seam existed.
    /// </param>
    public WicDirectDecoder(IPlatformImageCodec codec, ISourceReader? sourceReader = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
        _sourceReader = sourceReader ?? PhysicalSourceReader.Instance;
    }

    /// <summary>Memory seam for the output-size guard (total available, current load); tests inject a small machine.</summary>
    internal Func<(long TotalAvailable, long Load)> MemoryInfo { get; init; } = MemoryHeadroom.ReadGcMemoryInfo;

    public IDecodedImage Decode(DecodeRequest request)
    {
        // With pre-read bytes the path is only a label (TurboJpeg accepts any); a file is opened only without them.
        if (!request.Bytes.HasValue) ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        Stream stream = request.Bytes.HasValue
            ? ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value)
            : _sourceReader.OpenSource(request.Path, request.Priority, 1024 * 1024);

        using (stream)
        {
            try
            {
                return DecodeFromStream(stream, request, _codec, MemoryInfo);
            }
            catch (ArgumentException ex)
            {
                // WIC maps E_INVALIDARG (damaged metadata/header) to ArgumentException, which the fallback chain treats as a
                // caller bug and does not catch. The argument checks of this method ran before the try, so this is a data fault.
                throw AsInvalidData(ex);
            }
        }
    }

    /// <summary>WIC reports sizes as <c>uint</c>; everything downstream is <c>int</c>. A size beyond <see cref="int.MaxValue"/> is a damaged
    /// (or hostile) header, refused as data fault instead of wrapping to a negative size or an OverflowException.</summary>
    internal static (int Width, int Height) ToIntSize(uint width, uint height)
    {
        if (width > int.MaxValue || height > int.MaxValue)
            throw UserFacingError.Localized(
                new InvalidDataException($"WIC reported image dimensions beyond the supported range: {width}x{height}"),
                () => Tr.ErrDecoderInvalidDimensions(width, height));
        return ((int)width, (int)height);
    }

    internal static InvalidDataException AsInvalidData(ArgumentException ex, string decoder = "WicDirect")
    {
        var detail = ex.Message;
        return UserFacingError.Localized(new InvalidDataException(decoder + " rejected the image data: " + detail, ex),
            () => Tr.ErrDecoderDecompressFailed(detail));
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = _sourceReader.OpenSource(path, SourceReadPriority.Viewer, 64 * 1024);

        try
        {
            return ReadInfoCore(stream);
        }
        catch (ArgumentException ex)
        {
            throw AsInvalidData(ex);
        }
    }

    private static ImageInfo ReadInfoCore(Stream stream)
    {
        var factory = CreateFactory();
        using var managedStream = new ManagedIStream(stream);

        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        try
        {
            factory.CreateDecoderFromStream(
                managedStream,
                IntPtr.Zero,
                WICDecodeOptions.WICDecodeMetadataCacheOnDemand,
                out decoder);

            decoder.GetFrame(0, out frame);
            // Dimensions and orientation do not depend on the color profile, so profiled files
            // no longer need to fall back to WPF here.
            frame.GetSize(out uint origW, out uint origH);
            var (width, height) = ToIntSize(origW, origH);
            int orientation = WicExifReader.ReadOrientation(frame);

            return new ImageInfo(width, height, orientation);
        }
        finally
        {
            SafeReleaseCom(frame);
            SafeReleaseCom(decoder);
            SafeReleaseCom(factory);
        }
    }

    private static DecodedImage DecodeFromStream(Stream stream, DecodeRequest request, IPlatformImageCodec codec,
        Func<(long TotalAvailable, long Load)> memoryInfo)
    {
        var factory = CreateFactory();
        using var managedStream = new ManagedIStream(stream);

        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICBitmapSource? currentSource = null;
        IWICBitmapScaler? preScaler = null;
        IWICBitmapScaler? scaler = null;
        IWICFormatConverter? converter = null;
        IWICBitmapFlipRotator? rotator = null;
        IWICColorContext[]? sourceContexts = null;
        var colorChain = new ColorTransformChain();

        try
        {
            factory.CreateDecoderFromStream(
                managedStream,
                IntPtr.Zero,
                WICDecodeOptions.WICDecodeMetadataCacheOnDemand,
                out decoder);

            decoder.GetFrame(0, out frame);
            frame.GetSize(out uint origW, out uint origH);
            _ = ToIntSize(origW, origH); // every later computation is int-based: refuse a size the casts below would wrap

            // Orientation and the photo-information EXIF fields come from one query reader over the metadata
            // this decode parses anyway (no extra read of the stream).
            int orientation;
            ExifSummary? exif;
            if (request.ApplyOrientation && request.SourceOrientation.HasValue)
            {
                orientation = request.SourceOrientation.Value;
                WicExifReader.ReadFrameMetadata(frame, false, WicExifReader.ExifIfdRootOf(decoder), out exif);
            }
            else
            {
                orientation = WicExifReader.ReadFrameMetadata(frame, request.ApplyOrientation, WicExifReader.ExifIfdRootOf(decoder), out exif);
            }
            bool isTransposed = request.ApplyOrientation && ExifOrientation.IsTransposed(orientation);

            currentSource = (IWICBitmapSource)frame;
            bool downscaled = false;

            if (request.IsDownscaleRequested)
            {
                // The box applies to the displayed (oriented) image; FitStored maps it back onto
                // the stored pixel grid so we scale first and rotate afterwards.
                var (fitW, fitH) = request.Box.FitStored((int)origW, (int)origH, isTransposed);
                uint targetW = (uint)fitW, targetH = (uint)fitH;

                if (targetW < origW || targetH < origH)
                {
                    // Stage 1: let the codec reduce natively (JPEG DCT scaling 1/2, 1/4, 1/8) to the
                    // smallest size still >= target. A scaler sitting directly on the frame at exactly
                    // that size is served by IWICBitmapSourceTransform without resampling.
                    if (TryGetNativeReducedSize(frame, targetW, targetH, origW, origH, out uint nativeW, out uint nativeH))
                    {
                        factory.CreateBitmapScaler(out preScaler);
                        preScaler.Initialize(currentSource, nativeW, nativeH, WICBitmapInterpolationMode.Fant);
                        currentSource = (IWICBitmapSource)preScaler;
                    }

                    // Stage 2: high-quality resample of the (already reduced) image to the target.
                    factory.CreateBitmapScaler(out scaler);
                    try
                    {
                        scaler.Initialize(currentSource, targetW, targetH, WICBitmapInterpolationMode.HighQualityCubic);
                    }
                    catch (COMException)
                    {
                        scaler.Initialize(currentSource, targetW, targetH, WICBitmapInterpolationMode.Fant);
                    }
                    currentSource = (IWICBitmapSource)scaler;
                    downscaled = true;
                }
            }

            // Output the formats WPF renders natively so the UI thread never has to convert:
            // Bgr32 for opaque sources (JPEG), premultiplied Pbgra32 for anything that may carry alpha.
            currentSource.GetPixelFormat(out Guid sourceFormat);
            bool opaque = IsOpaqueFormat(sourceFormat);

            // Embedded ICC (or non-sRGB EXIF color space): transform to sRGB after downscaling so the
            // color math runs on the small image, not the full-resolution frame.
            sourceContexts = ReadColorContexts(factory, frame);
            IWICColorContext? sourceProfile = SelectSourceColorContext(sourceContexts);
            if (sourceProfile is not null)
            {
                currentSource = colorChain.Build(factory, currentSource, sourceProfile, opaque);
            }

            factory.CreateFormatConverter(out converter);
            var outputGuid = opaque ? WicGuids.GUID_WICPixelFormat32bppBGR : WicGuids.GUID_WICPixelFormat32bppPBGRA;
            converter.Initialize(
                currentSource,
                ref outputGuid,
                WICBitmapDitherType.None,
                IntPtr.Zero,
                0.0,
                WICBitmapPaletteType.Custom);
            currentSource = (IWICBitmapSource)converter;

            // Apply EXIF orientation via WICBitmapFlipRotator
            if (request.ApplyOrientation && orientation > 1)
            {
                var transformOptions = MapToTransformOptions(orientation);
                if (transformOptions != WICBitmapTransformOptions.Rotate0)
                {
                    factory.CreateBitmapFlipRotator(out rotator);
                    rotator.Initialize(currentSource, transformOptions);
                    currentSource = (IWICBitmapSource)rotator;
                }
            }

            currentSource.GetSize(out uint finalW, out uint finalH);
            _ = ToIntSize(finalW, finalH);
            // R16: admission first, on a saturating byte count. The checked int arithmetic below would otherwise throw an
            // OverflowException for a hostile size, which the fallback chain treats as fallbackable and hands to WPF.
            EnsureOutputFits((int)finalW, (int)finalH, OutputByteLength((int)finalW, (int)finalH), memoryInfo);
            var stride = checked((int)finalW * 4);
            var bufferSize = checked(stride * (int)finalH);

            // WP-03: WIC writes straight into the native PixelBuffer (one copy); the codec then owns it. With the WPF codec
            // that is the same single BitmapSource.Create copy into MIL as before (2 copies in all), with the pixel codec none.
            var pixels = PixelBuffer.Allocate((int)finalW, (int)finalH, opaque ? PixelLayout.Bgr32 : PixelLayout.Pbgra32);
            try
            {
                CopyPixelsGuarded(
                    () => currentSource.CopyPixels(IntPtr.Zero, (uint)stride, (uint)bufferSize, pixels.Address),
                    colorChain.IsActive);
            }
            catch
            {
                pixels.Dispose();
                throw;
            }

            var platformImage = codec.FromPixels(pixels);

            // Perf: origW/origH (frame.GetSize) are already read above at zero extra cost; a
            // transposing orientation (5-8) swaps them, exactly mirroring what happened to the
            // final bitmap's own pixel dimensions -- see IDecodedImage.OriginalWidth/Height.
            int originalWidth = isTransposed ? (int)origH : (int)origW;
            int originalHeight = isTransposed ? (int)origW : (int)origH;

            return new DecodedImage(platformImage, (int)finalW, (int)finalH, bufferSize, downscaled, orientation, DecoderBackend.WicDirect,
                originalWidth, originalHeight, exif);
        }
        finally
        {
            SafeReleaseCom(rotator);
            SafeReleaseCom(converter);
            colorChain.Release();
            ReleaseAll(sourceContexts);
            SafeReleaseCom(scaler);
            SafeReleaseCom(preScaler);
            SafeReleaseCom(frame);
            SafeReleaseCom(decoder);
            SafeReleaseCom(factory);
        }
    }

    /// <summary>Bytes of a 4-bytes-per-pixel output, saturating at <see cref="long.MaxValue"/> instead of wrapping: two positive int sides
    /// can multiply past a long, and a wrapped (negative) count would be mistaken for a small buffer by the admission check.</summary>
    internal static long OutputByteLength(int width, int height)
    {
        var pixels = (long)width * height; // two non-negative ints cannot overflow a long
        return pixels > long.MaxValue / 4 ? long.MaxValue : pixels * 4;
    }

    /// <summary>Refuses an output buffer the machine cannot hold with a clean error instead of an OutOfMemoryException.</summary>
    internal static void EnsureOutputFits(int width, int height, long bufferLength, Func<(long TotalAvailable, long Load)> memoryInfo)
    {
        // A negative length is an overflowed count, never a small buffer: it goes through the memory check like any huge one.
        if (bufferLength >= 0 && bufferLength < MemoryHeadroom.GuardThresholdBytes) return;
        var (total, load) = memoryInfo();
        if (MemoryHeadroom.OutputHasHeadroom(bufferLength, total, load)) return;
        throw UserFacingError.Localized(
            new DecoderMemoryAdmissionException($"WicDirect output dimensions are too large for the available memory: {width}x{height} ({bufferLength} bytes)."),
            () => Tr.ErrDecoderOutputTooLarge(width, height, bufferLength));
    }

    internal static IWICImagingFactory CreateFactory()
    {
        int hr = WicNativeMethods.WICCreateImagingFactory_Proxy(WicNativeMethods.WINCODEC_SDK_VERSION1, out IWICImagingFactory? factory);
        ThrowIfFactoryFailed(hr, factory is not null);
        return factory!;
    }

    private const int EFail = unchecked((int)0x80004005);

    /// <summary>
    /// Test seam for the factory-creation failure path (a real WIC failure cannot be provoked): an HRESULT failure throws its mapped
    /// exception, and S_OK without a factory throws the mapped E_FAIL <see cref="COMException"/> instead of leaving a null factory
    /// to surface later as a NullReferenceException (a COMException is a backend failure the WPF fallback can decode around).
    /// </summary>
    internal static void ThrowIfFactoryFailed(int hr, bool factoryCreated)
    {
        if (hr < 0)
        {
            // IntPtr(-1) = ignore the thread's IErrorInfo: ThrowExceptionForHR would otherwise pick up a stale error object left by an
            // earlier COM call on this thread, making the exception's message/shape depend on what ran before.
            var exception = Marshal.GetExceptionForHR(hr, new IntPtr(-1));
            if (exception is not null) throw exception;
        }
        if (!factoryCreated)
            throw Marshal.GetExceptionForHR(EFail, new IntPtr(-1))!; // E_FAIL always maps to a COMException (CA2201 forbids constructing one)
    }

    private static readonly HashSet<Guid> OpaquePixelFormats =
    [
        WicGuids.GUID_WICPixelFormatBlackWhite,
        WicGuids.GUID_WICPixelFormat2bppGray,
        WicGuids.GUID_WICPixelFormat4bppGray,
        WicGuids.GUID_WICPixelFormat8bppGray,
        WicGuids.GUID_WICPixelFormat16bppGray,
        WicGuids.GUID_WICPixelFormat16bppBGR555,
        WicGuids.GUID_WICPixelFormat16bppBGR565,
        WicGuids.GUID_WICPixelFormat24bppBGR,
        WicGuids.GUID_WICPixelFormat24bppRGB,
        WicGuids.GUID_WICPixelFormat32bppBGR,
        WicGuids.GUID_WICPixelFormat48bppRGB,
        WicGuids.GUID_WICPixelFormat48bppBGR,
        WicGuids.GUID_WICPixelFormat32bppCMYK,
        WicGuids.GUID_WICPixelFormat64bppCMYK
    ];

    /// <summary>
    /// The colour transform is evaluated lazily inside CopyPixels, so a COM failure there on an
    /// ICC-bearing image must still route the file to the colour-managed WPF fallback. Only this
    /// call is treated as an ICC failure (IMG-04): converter/rotator failures elsewhere propagate
    /// with their own accurate message instead of being reported as an ICC problem.
    /// </summary>
    internal static void CopyPixelsGuarded(Action copyPixels, bool colorTransformActive)
    {
        try
        {
            copyPixels();
        }
        catch (COMException ex) when (colorTransformActive)
        {
            var osMessage = ex.Message;
            throw UserFacingError.Localized(new NotSupportedException(
                "WicDirect could not transform the embedded ICC profile to sRGB: " + osMessage, ex),
                () => Tr.ErrDecoderIccTransformFailed(osMessage));
        }
    }

    /// <summary>
    /// True when the WIC pixel format cannot carry transparency. Unknown and indexed formats
    /// (palettes may contain transparent entries) are conservatively treated as having alpha.
    /// </summary>
    internal static bool IsOpaqueFormat(Guid pixelFormat) => OpaquePixelFormats.Contains(pixelFormat);

    private static bool TryGetNativeReducedSize(
        IWICBitmapFrameDecode frame, uint targetW, uint targetH, uint origW, uint origH,
        out uint nativeW, out uint nativeH)
    {
        nativeW = targetW;
        nativeH = targetH;
        if (frame is not IWICBitmapSourceTransform sourceTransform)
        {
            return false;
        }

        try
        {
            sourceTransform.GetClosestSize(ref nativeW, ref nativeH);
        }
        catch (COMException)
        {
            return false;
        }

        // Only useful when the codec actually reduces and does not undershoot the target.
        return nativeW >= targetW && nativeH >= targetH && (nativeW < origW || nativeH < origH);
    }

    private static IWICColorContext[]? ReadColorContexts(IWICImagingFactory factory, IWICBitmapFrameDecode frame)
    {
        uint count;
        try
        {
            frame.GetColorContexts(0, null, out count);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            // E_INVALIDARG / a COM failure: the colour metadata (e.g. a damaged EXIF TIFF header) cannot be read. The pixels are fine, so the
            // picture is treated as untagged sRGB instead of failing the decode.
            return null;
        }

        if (count == 0)
        {
            return null;
        }

        var contexts = new IWICColorContext[count];
        try
        {
            for (var i = 0; i < contexts.Length; i++)
            {
                factory.CreateColorContext(out contexts[i]);
            }

            frame.GetColorContexts(count, contexts, out _);
            return contexts;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            ReleaseAll(contexts);
            return null;
        }
        catch
        {
            ReleaseAll(contexts);
            throw;
        }
    }

    /// <summary>
    /// Picks the context describing the frame's pixels: an embedded ICC profile wins; otherwise a
    /// non-sRGB EXIF color space (e.g. Adobe RGB = 2). sRGB (EXIF 1) needs no transform.
    /// </summary>
    private static IWICColorContext? SelectSourceColorContext(IWICColorContext[]? contexts)
    {
        if (contexts is null)
        {
            return null;
        }

        IWICColorContext? exifCandidate = null;
        foreach (var context in contexts)
        {
            if (context is null)
            {
                continue;
            }

            context.GetContextType(out WICColorContextType type);
            if (type == WICColorContextType.Profile)
            {
                return context;
            }

            if (type == WICColorContextType.ExifColorSpace && exifCandidate is null)
            {
                context.GetExifColorSpace(out uint colorSpace);
                if (colorSpace != SrgbExifColorSpace)
                {
                    exifCandidate = context;
                }
            }
        }

        return exifCandidate;
    }

    private const uint SrgbExifColorSpace = 1;

    /// <summary>
    /// Owns the COM objects of the optional source-profile to sRGB stage of the decode pipeline.
    /// </summary>
    private sealed class ColorTransformChain
    {
        private IWICColorContext? _destination;
        private IWICColorTransform? _transform;
        private IWICFormatConverter? _normalizer;

        public bool IsActive => _transform is not null;

        public IWICBitmapSource Build(
            IWICImagingFactory factory, IWICBitmapSource source, IWICColorContext sourceProfile, bool opaque)
        {
            // Straight (non-premultiplied) alpha: color math on premultiplied values would be wrong.
            var transformFormat = opaque ? WicGuids.GUID_WICPixelFormat32bppBGR : WicGuids.GUID_WICPixelFormat32bppBGRA;
            try
            {
                factory.CreateColorContext(out _destination);
                _destination.InitializeFromExifColorSpace(SrgbExifColorSpace);

                // First let the transform consume the native format (keeps CMYK/gray profiles valid).
                factory.CreateColorTransformer(out _transform);
                try
                {
                    _transform.Initialize(source, sourceProfile, _destination, ref transformFormat);
                    return (IWICBitmapSource)_transform;
                }
                catch (COMException)
                {
                    // Source format not accepted directly: normalize to 32bpp BGR(A) and retry.
                    SafeReleaseCom(_transform);
                    _transform = null;
                }

                factory.CreateFormatConverter(out _normalizer);
                _normalizer.Initialize(
                    source, ref transformFormat, WICBitmapDitherType.None, IntPtr.Zero, 0.0, WICBitmapPaletteType.Custom);
                factory.CreateColorTransformer(out _transform);
                _transform.Initialize((IWICBitmapSource)_normalizer, sourceProfile, _destination, ref transformFormat);
                return (IWICBitmapSource)_transform;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                var osMessage = ex.Message;
                throw UserFacingError.Localized(new NotSupportedException(
                    "WicDirect could not transform the embedded ICC profile to sRGB: " + osMessage, ex),
                    () => Tr.ErrDecoderIccTransformFailed(osMessage));
            }
        }

        public void Release()
        {
            SafeReleaseCom(_transform);
            SafeReleaseCom(_normalizer);
            SafeReleaseCom(_destination);
            _transform = null;
            _normalizer = null;
            _destination = null;
        }
    }

    private static void ReleaseAll(IWICColorContext[]? contexts)
    {
        if (contexts is null)
        {
            return;
        }

        foreach (var context in contexts)
        {
            SafeReleaseCom(context);
        }
    }

    private static WICBitmapTransformOptions MapToTransformOptions(int orientation)
    {
        return orientation switch
        {
            2 => WICBitmapTransformOptions.FlipHorizontal,
            3 => WICBitmapTransformOptions.Rotate180,
            4 => WICBitmapTransformOptions.FlipVertical,
            5 => WICBitmapTransformOptions.FlipHorizontal | WICBitmapTransformOptions.Rotate270,
            6 => WICBitmapTransformOptions.Rotate90,
            7 => WICBitmapTransformOptions.FlipHorizontal | WICBitmapTransformOptions.Rotate90,
            8 => WICBitmapTransformOptions.Rotate270,
            _ => WICBitmapTransformOptions.Rotate0
        };
    }

    internal static void SafeReleaseCom(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            Marshal.ReleaseComObject(comObj);
        }
    }
}
