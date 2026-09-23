using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Decoding.Wic;

/// <summary>
/// High-performance image decoder directly leveraging Windows Imaging Component (WIC) COM interfaces.
/// Bypasses WPF BitmapImage overhead, utilizes native DCT pre-scaling and WIC format converters,
/// and strictly preserves native sharing flags (ReadWrite | Delete, SequentialScan) conforming to INV-8.
/// </summary>
public sealed class WicDirectDecoder : IImageDecoder
{
    private const string ExifOrientationQuery = "/app1/ifd/{ushort=274}";
    private const string WindowsOrientationQuery = "System.Photo.Orientation";

    public IDecodedImage Decode(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        Stream stream = request.Bytes.HasValue
            ? ReadOnlyMemoryStreamFactory.Create(request.Bytes.Value)
            : new FileStream(request.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);

        using (stream)
        {
            return DecodeFromStream(stream, request);
        }
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);

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
            int orientation = ReadExifOrientation(frame);

            return new ImageInfo((int)origW, (int)origH, orientation);
        }
        finally
        {
            SafeReleaseCom(frame);
            SafeReleaseCom(decoder);
            SafeReleaseCom(factory);
        }
    }

    private static WpfDecodedImage DecodeFromStream(Stream stream, DecodeRequest request)
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

            int orientation = request.ApplyOrientation ? ReadExifOrientation(frame) : 1;
            bool isTransposed = request.ApplyOrientation && orientation is >= 5 and <= 8;

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
            var stride = checked((int)finalW * 4);
            var bufferSize = checked(stride * (int)finalH);

            // Native scratch buffer: BitmapSource.Create copies it into its own WIC bitmap, so a
            // managed array here would only add a large LOH allocation (GC pressure) per decode.
            BitmapSource bitmap;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                currentSource.CopyPixels(IntPtr.Zero, (uint)stride, (uint)bufferSize, buffer);
                bitmap = BitmapSource.Create(
                    (int)finalW,
                    (int)finalH,
                    96,
                    96,
                    opaque ? PixelFormats.Bgr32 : PixelFormats.Pbgra32,
                    null,
                    buffer,
                    bufferSize,
                    stride);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            bitmap.Freeze();

            return new WpfDecodedImage(bitmap, downscaled, orientation, DecoderBackend.WicDirect);
        }
        catch (COMException ex) when (colorChain.IsActive)
        {
            // The transform is evaluated lazily inside CopyPixels; a failure there must still route
            // the file to the color-managed WPF fallback instead of surfacing as a hard error.
            throw new NotSupportedException(
                "WicDirect could not transform the embedded ICC profile to sRGB: " + ex.Message, ex);
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

    private static IWICImagingFactory CreateFactory()
    {
        int hr = WicNativeMethods.WICCreateImagingFactory_Proxy(WicNativeMethods.WINCODEC_SDK_VERSION1, out IWICImagingFactory? factory);
        if (hr < 0 || factory is null)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        return factory!;
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
        frame.GetColorContexts(0, null, out uint count);
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
                throw new NotSupportedException(
                    "WicDirect could not transform the embedded ICC profile to sRGB: " + ex.Message, ex);
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

    private static int ReadExifOrientation(IWICBitmapFrameDecode frame)
    {
        IWICMetadataQueryReader? reader = null;
        IntPtr pvar = IntPtr.Zero;
        try
        {
            frame.GetMetadataQueryReader(out reader);
            pvar = Marshal.AllocHGlobal(24);

            // Clear buffer
            for (var i = 0; i < 24; i++) Marshal.WriteByte(pvar, i, 0);

            try
            {
                reader.GetMetadataByName(ExifOrientationQuery, pvar);
                int val = ReadVariantInt(pvar);
                if (val is >= 1 and <= 8) return val;
            }
            catch
            {
                // Fall back to secondary query
            }

            _ = PropVariantClear(pvar);
            for (var i = 0; i < 24; i++) Marshal.WriteByte(pvar, i, 0);

            try
            {
                reader.GetMetadataByName(WindowsOrientationQuery, pvar);
                int val = ReadVariantInt(pvar);
                if (val is >= 1 and <= 8) return val;
            }
            catch
            {
                // Ignored
            }

            return 1;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (pvar != IntPtr.Zero)
            {
                _ = PropVariantClear(pvar);
                Marshal.FreeHGlobal(pvar);
            }
            SafeReleaseCom(reader);
        }
    }

    private static int ReadVariantInt(IntPtr pvar)
    {
        ushort vt = (ushort)Marshal.ReadInt16(pvar);
        // VT_UI2 = 18, VT_I2 = 2, VT_UI4 = 19, VT_I4 = 3
        return vt switch
        {
            2 => Marshal.ReadInt16(pvar, 8),
            18 => (ushort)Marshal.ReadInt16(pvar, 8),
            3 => Marshal.ReadInt32(pvar, 8),
            19 => (int)(uint)Marshal.ReadInt32(pvar, 8),
            _ => 1
        };
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

    private static void SafeReleaseCom(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            Marshal.ReleaseComObject(comObj);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr pvar);
}
