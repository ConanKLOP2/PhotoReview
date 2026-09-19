using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// Result of comparing two decoded images.
/// </summary>
public sealed record ImageCompareResult(double Psnr, double MeanDeltaE, int MaxChannelDiff);

/// <summary>
/// Utility for extracting normalized BGRA32 pixel buffers from BitmapSource / IDecodedImage and comparing them.
/// </summary>
public static class ImageCompare
{
    /// <summary>
    /// Converts a <see cref="BitmapSource"/> into a contiguous BGRA32 byte array.
    /// </summary>
    public static byte[] ToBgra32(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// Extracts BGRA32 pixel bytes from an <see cref="IDecodedImage"/>.
    /// </summary>
    public static byte[] ToBgra32(IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.PlatformImage is BitmapSource bitmap)
        {
            return ToBgra32(bitmap);
        }

        throw new NotSupportedException($"Unsupported PlatformImage type: {image.PlatformImage?.GetType().FullName ?? "null"}");
    }

    /// <summary>
    /// Compares two <see cref="BitmapSource"/> instances of identical pixel dimensions.
    /// </summary>
    public static ImageCompareResult Compare(BitmapSource a, BitmapSource b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            throw new ArgumentException(
                $"Image dimensions do not match: {a.PixelWidth}x{a.PixelHeight} vs {b.PixelWidth}x{b.PixelHeight}");
        }

        var pixelsA = ToBgra32(a);
        var pixelsB = ToBgra32(b);

        var psnr = PixelMetrics.Psnr(pixelsA, pixelsB);
        var deltaE = PixelMetrics.MeanDeltaE(pixelsA, pixelsB);
        var maxDiff = PixelMetrics.MaxChannelDiff(pixelsA, pixelsB);

        return new ImageCompareResult(psnr, deltaE, maxDiff);
    }

    /// <summary>
    /// Compares two <see cref="IDecodedImage"/> instances of identical pixel dimensions.
    /// </summary>
    public static ImageCompareResult Compare(IDecodedImage a, IDecodedImage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            throw new ArgumentException(
                $"Image dimensions do not match: {a.PixelWidth}x{a.PixelHeight} vs {b.PixelWidth}x{b.PixelHeight}");
        }

        var pixelsA = ToBgra32(a);
        var pixelsB = ToBgra32(b);

        var psnr = PixelMetrics.Psnr(pixelsA, pixelsB);
        var deltaE = PixelMetrics.MeanDeltaE(pixelsA, pixelsB);
        var maxDiff = PixelMetrics.MaxChannelDiff(pixelsA, pixelsB);

        return new ImageCompareResult(psnr, deltaE, maxDiff);
    }
}
