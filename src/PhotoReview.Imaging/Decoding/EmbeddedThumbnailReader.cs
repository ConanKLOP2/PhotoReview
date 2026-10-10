using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Reads a JPEG's embedded EXIF thumbnail (APP1) without decoding the full-size image.
/// Used by <see cref="Caching.ThumbnailCache"/> on a cache miss so an open-folder thumbnail
/// placeholder never forces a second full decode of the same source the preview is already
/// decoding concurrently (see ImagePresenter's preview/thumbnail race). Not a general-purpose
/// decoder: non-JPEG sources and JPEGs without an embedded thumbnail simply yield no
/// thumbnail -- the caller is expected to fall back to the in-flight preview decode instead.
/// <para>WP-03: WIC COM directly (<c>IWICBitmapFrameDecode::GetThumbnail</c> + a 32bppPBGRA format converter + the frame's
/// metadata query reader), orientation by <see cref="PixelOps.ApplyOrientation"/>, result through the
/// <see cref="IPlatformImageCodec"/>; no WPF type. Only the header, the APP1 metadata and the thumbnail are read -- the main
/// frame's pixels are never decoded.</para>
/// </summary>
public static class EmbeddedThumbnailReader
{
    /// <summary>EXIF thumbnails are about 160x120; anything above this many pixels is treated as corrupt (4 MP = 16 MB BGRA).</summary>
    internal const long MaxThumbnailPixels = 4L * 1024 * 1024;

    /// <summary>True when a thumbnail of this size is positive and within <see cref="MaxThumbnailPixels"/>.</summary>
    internal static bool IsAcceptableSize(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxThumbnailPixels;

    /// <summary>
    /// WP-03 bridge for the one caller outside this package's files (<see cref="Caching.ThumbnailCache"/>, WP-04's zone) and the
    /// existing tests: the WPF app's codec (<see cref="WpfBitmapSourceCodec"/>), i.e. a frozen BitmapSource as before. WP-04
    /// injects the thumbnail decoder/codec and removes this overload.
    /// </summary>
    public static IDecodedImage? TryRead(string path) => TryRead(path, WpfBitmapSourceCodec.Instance);

    /// <summary>
    /// Returns the source's embedded EXIF thumbnail with orientation applied, or null if the
    /// source has none (wrong format, missing APP1 thumbnail, or any read/decode failure --
    /// all treated the same: nothing to show yet, the full preview decode is already in flight).
    /// The pixels are premultiplied BGRA (<see cref="PixelLayout.Pbgra32"/>, A = 255 for a JPEG thumbnail), as alpha-capable
    /// as the straight-BGRA bitmap the WPF reader produced, so the thumbnail disk cache keeps writing the same PNG format.
    /// </summary>
    public static IDecodedImage? TryRead(string path, IPlatformImageCodec codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(codec);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return ReadFromStream(stream, codec);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
            or UnauthorizedAccessException
            // Damaged EXIF/thumbnail metadata surfaces from WIC as ArgumentException ("corrupted metadata header"), OverflowException
            // or COMException; ThumbnailCache does not catch around this reader, so a throw would fail the whole thumbnail load.
            or ArgumentException or OverflowException or InvalidCastException or FormatException
            or COMException)
        {
            return null;
        }
    }

    private static DecodedImage? ReadFromStream(Stream stream, IPlatformImageCodec codec)
    {
        var factory = WicDirectDecoder.CreateFactory();
        using var managedStream = new ManagedIStream(stream);

        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICBitmapSource? thumbnail = null;
        IWICFormatConverter? converter = null;
        try
        {
            factory.CreateDecoderFromStream(managedStream, IntPtr.Zero, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, out decoder);
            decoder.GetFrameCount(out uint frameCount);
            if (frameCount == 0) return null;
            decoder.GetFrame(0, out frame);

            // Perf: the *main* frame's own header dimensions (not the embedded thumbnail's) come for free with the header
            // just parsed -- no extra open/read needed to learn the source's real size.
            frame.GetSize(out uint frameW, out uint frameH);
            if (frameW > int.MaxValue || frameH > int.MaxValue) return null;
            int mainW = (int)frameW, mainH = (int)frameH;

            if (!TryGetThumbnail(frame, out thumbnail)) return null;

            thumbnail.GetSize(out uint thumbW, out uint thumbH);
            // A hostile/corrupt EXIF thumbnail may claim huge dimensions: refuse before any width*height*4 allocation.
            if (thumbW > int.MaxValue || thumbH > int.MaxValue || !IsAcceptableSize((int)thumbW, (int)thumbH)) return null;
            int width = (int)thumbW, height = (int)thumbH;

            factory.CreateFormatConverter(out converter);
            var pbgra = WicGuids.GUID_WICPixelFormat32bppPBGRA;
            converter.Initialize(thumbnail, ref pbgra, WICBitmapDitherType.None, IntPtr.Zero, 0.0, WICBitmapPaletteType.Custom);

            // Eager copy while the stream is still open: the returned image never depends on the source stream.
            var pixels = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
            try
            {
                converter.CopyPixels(IntPtr.Zero, (uint)pixels.Stride, checked((uint)pixels.ByteCount), pixels.Address);
            }
            catch
            {
                pixels.Dispose();
                throw;
            }

            var orientation = WicExifReader.ReadOrientation(frame);
            // ApplyOrientation owns pixels: it returns them (orientation 1) or a rotated copy and disposes the input.
            var oriented = PixelOps.ApplyOrientation(pixels, orientation);
            var (orientedW, orientedH, orientedBytes) = (oriented.Width, oriented.Height, oriented.ByteCount);

            // A transposing orientation (5-8) swaps the main size, mirroring what ApplyOrientation did to the thumbnail.
            bool transposed = ExifOrientation.IsTransposed(orientation);

            var platformImage = codec.FromPixels(oriented);
            return new DecodedImage(platformImage, orientedW, orientedH, orientedBytes, downscaled: true, orientation: orientation,
                // Wpf, as before WP-03: the backend label is part of the cache identity, so it must not change.
                actualBackend: DecoderBackend.Wpf,
                originalWidth: transposed ? mainH : mainW, originalHeight: transposed ? mainW : mainH);
        }
        finally
        {
            WicDirectDecoder.SafeReleaseCom(converter);
            WicDirectDecoder.SafeReleaseCom(thumbnail);
            WicDirectDecoder.SafeReleaseCom(frame);
            WicDirectDecoder.SafeReleaseCom(decoder);
            WicDirectDecoder.SafeReleaseCom(factory);
        }
    }

    /// <summary>The frame's embedded thumbnail; false when the codec has none (WINCODEC_ERR_CODECNOTHUMBNAIL and kin).</summary>
    private static bool TryGetThumbnail(IWICBitmapFrameDecode frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWICBitmapSource? thumbnail)
    {
        try
        {
            frame.GetThumbnail(out thumbnail);
            return thumbnail is not null;
        }
        catch (COMException)
        {
            thumbnail = null;
            return false;
        }
    }
}
