using System.IO;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Pure pixel helpers for converting LibRaw packed 8-bit RGB output into the BGRA (Bgr32) buffer WPF consumes.</summary>
internal static class RgbBgraResampler
{
    /// <summary>Above this per-axis reduction ratio bilinear sampling skips source pixels and aliases; area averaging is used instead.</summary>
    internal const double BoxAverageRatioThreshold = 2d;

    /// <summary>
    /// Validates the processed-image header and returns the source RGB byte length. The multiplications are done
    /// in <see cref="long"/> because ushort * ushort can exceed <see cref="int.MaxValue"/> for absurd dimensions; every
    /// failure is an <see cref="InvalidDataException"/> (the decoder contract), never an <see cref="OverflowException"/>.
    /// </summary>
    internal static int ValidateSourceLength(int width, int height, long dataSize)
    {
        var rgbLength = (long)width * height * 3;
        if (width <= 0 || height <= 0 || dataSize < rgbLength || rgbLength > int.MaxValue)
            throw new InvalidDataException("LibRaw returned an invalid processed image buffer length.");
        return (int)rgbLength;
    }

    /// <summary>Returns the BGRA byte length for the target size, or throws <see cref="InvalidDataException"/> when a managed array cannot hold it.</summary>
    internal static int ValidateTargetLength(int width, int height)
    {
        var length = (long)width * height * 4;
        if (width <= 0 || height <= 0 || length > Array.MaxLength)
            throw new InvalidDataException($"The decoded RAW image is too large to hold in memory at {width}x{height}.");
        return (int)length;
    }

    internal static void Resize(ReadOnlySpan<byte> rgb, int sourceWidth, int sourceHeight,
        Span<byte> bgra, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            Convert(rgb, bgra, cancellationToken);
        else if ((double)sourceWidth / targetWidth > BoxAverageRatioThreshold || (double)sourceHeight / targetHeight > BoxAverageRatioThreshold)
            BoxAverage(rgb, sourceWidth, sourceHeight, bgra, targetWidth, targetHeight, cancellationToken);
        else
            Bilinear(rgb, sourceWidth, sourceHeight, bgra, targetWidth, targetHeight, cancellationToken);
    }

    private static void Convert(ReadOnlySpan<byte> rgb, Span<byte> bgra, CancellationToken cancellationToken)
    {
        var pixelCount = rgb.Length / 3;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var source = pixelIndex * 3;
            var target = pixelIndex * 4;
            bgra[target] = rgb[source + 2];
            bgra[target + 1] = rgb[source + 1];
            bgra[target + 2] = rgb[source];
            bgra[target + 3] = byte.MaxValue;
            if ((pixelIndex & 0x3FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Each target pixel is the mean of the source pixels it covers (source footprint [x*sw/tw, (x+1)*sw/tw)).</summary>
    private static void BoxAverage(ReadOnlySpan<byte> rgb, int sourceWidth, int sourceHeight,
        Span<byte> bgra, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        for (var y = 0; y < targetHeight; y++)
        {
            var y0 = (int)((long)y * sourceHeight / targetHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / targetHeight));
            for (var x = 0; x < targetWidth; x++)
            {
                var x0 = (int)((long)x * sourceWidth / targetWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / targetWidth));
                long red = 0, green = 0, blue = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = rgb.Slice((sy * sourceWidth + x0) * 3, (x1 - x0) * 3);
                    for (var i = 0; i < row.Length; i += 3)
                    {
                        red += row[i];
                        green += row[i + 1];
                        blue += row[i + 2];
                    }
                }

                long count = (long)(y1 - y0) * (x1 - x0);
                var target = (y * targetWidth + x) * 4;
                bgra[target] = (byte)((blue + count / 2) / count);
                bgra[target + 1] = (byte)((green + count / 2) / count);
                bgra[target + 2] = (byte)((red + count / 2) / count);
                bgra[target + 3] = byte.MaxValue;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void Bilinear(ReadOnlySpan<byte> rgb, int sourceWidth, int sourceHeight,
        Span<byte> bgra, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Clamp((y + 0.5d) * sourceHeight / targetHeight - 0.5d, 0, sourceHeight - 1d);
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var fy = sourceY - y0;
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Clamp((x + 0.5d) * sourceWidth / targetWidth - 0.5d, 0, sourceWidth - 1d);
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var fx = sourceX - x0;
                var target = (y * targetWidth + x) * 4;
                bgra[target] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, 2);
                bgra[target + 1] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, 1);
                bgra[target + 2] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, 0);
                bgra[target + 3] = byte.MaxValue;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static byte Interpolate(ReadOnlySpan<byte> source, int width, int x0, int x1, int y0, int y1, double fx, double fy, int channel)
    {
        var topLeft = source[(y0 * width + x0) * 3 + channel];
        var topRight = source[(y0 * width + x1) * 3 + channel];
        var bottomLeft = source[(y1 * width + x0) * 3 + channel];
        var bottomRight = source[(y1 * width + x1) * 3 + channel];
        var top = topLeft + (topRight - topLeft) * fx;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * fx;
        return (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), byte.MinValue, byte.MaxValue);
    }
}
