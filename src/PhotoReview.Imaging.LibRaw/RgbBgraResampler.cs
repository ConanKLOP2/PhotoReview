using System.IO;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Pure pixel helpers for converting LibRaw packed 8-bit RGB output into the BGRA (Bgr32) buffer WPF consumes.</summary>
internal static class RgbBgraResampler
{
    /// <summary>Above this per-axis reduction ratio bilinear sampling skips source pixels and aliases; area averaging is used instead.</summary>
    internal const double BoxAverageRatioThreshold = 2d;

    /// <summary>True for the processed-image channel counts the resampler understands: 3 (RGB) and 1 (monochrome sensors, expanded to gray).</summary>
    internal static bool IsSupportedChannelCount(int channels) => channels is 1 or 3;

    /// <summary>
    /// Validates the processed-image header and returns the source byte length (<paramref name="channels"/> bytes per pixel). The multiplications are done
    /// in <see cref="long"/> because ushort * ushort can exceed <see cref="int.MaxValue"/> for absurd dimensions; every
    /// failure is an <see cref="InvalidDataException"/> (the decoder contract), never an <see cref="OverflowException"/>.
    /// </summary>
    internal static int ValidateSourceLength(int width, int height, long dataSize, int channels = 3)
    {
        if (!IsSupportedChannelCount(channels)) throw new InvalidDataException("LibRaw returned an unsupported processed image format.");
        var rgbLength = (long)width * height * channels;
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
        Span<byte> bgra, int targetWidth, int targetHeight, CancellationToken cancellationToken, int channels = 3)
    {
        if (!IsSupportedChannelCount(channels)) throw new InvalidDataException("LibRaw returned an unsupported processed image format.");
        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            Convert(rgb, bgra, channels, cancellationToken);
        else if ((double)sourceWidth / targetWidth > BoxAverageRatioThreshold || (double)sourceHeight / targetHeight > BoxAverageRatioThreshold)
            BoxAverage(rgb, sourceWidth, sourceHeight, bgra, targetWidth, targetHeight, channels, cancellationToken);
        else
            Bilinear(rgb, sourceWidth, sourceHeight, bgra, targetWidth, targetHeight, channels, cancellationToken);
    }

    /// <summary>
    /// Resamples the decoder's packed source (<paramref name="channels"/> = 3 RGB or 1 gray) straight into a native BGRA <see cref="PixelBuffer"/>. No managed
    /// array: a full-size 100 MP target would be a 400 MB LOH object that lingers until the next gen-2 GC. The caller frees the source
    /// buffer, then either takes the pixels (<see cref="BgraBuffer.TakePixels"/>, pixel path: no copy) or calls <see cref="ToBitmap"/>
    /// (WPF path: BitmapSource.Create copies; a WriteableBitmap holds two native buffers too, measured) and disposes the buffer.
    /// Peak of the bitmap step: target + WPF copy, never source + target + copy.
    /// </summary>
    internal static unsafe BgraBuffer ResizeToBuffer(ReadOnlySpan<byte> rgb, int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight, int channels, CancellationToken cancellationToken)
    {
        // Cheap guard outside the inner loops: a non-positive size would otherwise reach the native allocation / divide by zero.
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            throw new InvalidDataException($"Invalid RAW resize dimensions {sourceWidth}x{sourceHeight} -> {targetWidth}x{targetHeight}.");
        var buffer = new BgraBuffer(targetWidth, targetHeight);
        try
        {
            Resize(rgb, sourceWidth, sourceHeight, buffer.AsSpan(), targetWidth, targetHeight, cancellationToken, channels);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// BGRA (Bgr32) target of a resample: a thin owner over a <see cref="PixelBuffer"/> (C-01, native, 64-byte aligned,
    /// stride = width x 4), so the pixels are written once, straight into the memory the codec / WPF adapter later takes.
    /// Dispose frees it unless <see cref="TakePixels"/> handed it over.
    /// </summary>
    internal sealed class BgraBuffer : IDisposable
    {
        private PixelBuffer? _pixels;

        internal BgraBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Stride = checked(width * 4);
            Length = checked(Stride * height);
            _pixels = PixelBuffer.Allocate(width, height, PixelLayout.Bgr32);
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int Stride { get; }
        internal int Length { get; }
        internal IntPtr Pointer => _pixels is { IsDisposed: false } pixels ? pixels.Address : IntPtr.Zero;

        internal Span<byte> AsSpan()
        {
            var pixels = _pixels ?? throw new ObjectDisposedException(nameof(BgraBuffer));
            return pixels.TryGetSpan(out var span) ? span : throw new InvalidDataException("The decoded RAW image is too large to hold in memory.");
        }

        /// <summary>Hands the pixels (and their ownership) to the caller; the buffer is empty afterwards and Dispose frees nothing.</summary>
        internal PixelBuffer TakePixels()
        {
            var pixels = _pixels ?? throw new ObjectDisposedException(nameof(BgraBuffer));
            _pixels = null;
            return pixels;
        }

        public void Dispose()
        {
            var pixels = _pixels;
            _pixels = null;
            pixels?.Dispose();
        }
    }
    // Source layout is <channels> bytes per pixel: R,G,B for 3; a single gray value for 1 (green/blue offsets collapse to 0).
    private static void Convert(ReadOnlySpan<byte> rgb, Span<byte> bgra, int channels, CancellationToken cancellationToken)
    {
        var greenOffset = channels == 3 ? 1 : 0;
        var blueOffset = channels == 3 ? 2 : 0;
        var pixelCount = rgb.Length / channels;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var source = pixelIndex * channels;
            var target = pixelIndex * 4;
            bgra[target] = rgb[source + blueOffset];
            bgra[target + 1] = rgb[source + greenOffset];
            bgra[target + 2] = rgb[source];
            bgra[target + 3] = byte.MaxValue;
            if ((pixelIndex & 0x3FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Each target pixel is the mean of the source pixels it covers (source footprint [x*sw/tw, (x+1)*sw/tw)).</summary>
    private static void BoxAverage(ReadOnlySpan<byte> rgb, int sourceWidth, int sourceHeight,
        Span<byte> bgra, int targetWidth, int targetHeight, int channels, CancellationToken cancellationToken)
    {
        var greenOffset = channels == 3 ? 1 : 0;
        var blueOffset = channels == 3 ? 2 : 0;
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
                    var row = rgb.Slice((sy * sourceWidth + x0) * channels, (x1 - x0) * channels);
                    for (var i = 0; i < row.Length; i += channels)
                    {
                        red += row[i];
                        green += row[i + greenOffset];
                        blue += row[i + blueOffset];
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
        Span<byte> bgra, int targetWidth, int targetHeight, int channels, CancellationToken cancellationToken)
    {
        var greenChannel = channels == 3 ? 1 : 0;
        var blueChannel = channels == 3 ? 2 : 0;
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
                bgra[target] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, blueChannel, channels);
                bgra[target + 1] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, greenChannel, channels);
                bgra[target + 2] = Interpolate(rgb, sourceWidth, x0, x1, y0, y1, fx, fy, 0, channels);
                bgra[target + 3] = byte.MaxValue;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static byte Interpolate(ReadOnlySpan<byte> source, int width, int x0, int x1, int y0, int y1, double fx, double fy, int channel, int channels)
    {
        var topLeft = source[(y0 * width + x0) * channels + channel];
        var topRight = source[(y0 * width + x1) * channels + channel];
        var bottomLeft = source[(y1 * width + x0) * channels + channel];
        var bottomRight = source[(y1 * width + x1) * channels + channel];
        var top = topLeft + (topRight - topLeft) * fx;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * fx;
        return (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), byte.MinValue, byte.MaxValue);
    }
}
