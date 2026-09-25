using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.TurboJpeg.Native;

namespace PhotoReview.Imaging.TurboJpeg;

/// <summary>
/// High-performance SIMD-accelerated JPEG decoder using libjpeg-turbo 3.x.
/// Supports DCT scaling factor IDCT decompression, EXIF orientation, and safe fallback on ICC profiles.
/// </summary>
public sealed class TurboJpegDecoder : IImageDecoder
{
    /// <summary>
    /// DCT scaling factors used for downscaled decodes, smallest first. All eighths are kept on
    /// purpose: restricting to the power-of-two factors (whose IDCTs are SIMD-accelerated) was
    /// measured slower (24 MP -> 2190 px: 1/2 decodes 3000 px wide at ~310-610 ms vs 3/8 at
    /// ~210-330 ms), because upsampling/color conversion scale with output pixel count.
    /// </summary>
    private static readonly TjScalingFactor[] SupportedScalingFactors =
    [
        new(1, 8),
        new(1, 4),
        new(3, 8),
        new(1, 2),
        new(5, 8),
        new(3, 4),
        new(7, 8),
        new(1, 1)
    ];

    public IDecodedImage Decode(DecodeRequest request)
    {
        ReadOnlyMemory<byte> bytesMemory = LoadBytes(request);
        try
        {
            return Decode(request, bytesMemory);
        }
        catch (Exception ex) when (!request.Bytes.HasValue)
        {
            // Read from disk here: hand the bytes to FallbackImageDecoder so it does not read the file again.
            DecodeFailureSourceBytes.Attach(ex, bytesMemory);
            throw;
        }
    }

    private static WpfDecodedImage Decode(DecodeRequest request, ReadOnlyMemory<byte> bytesMemory)
    {
        var bytes = bytesMemory.Span;

        // Magic byte verification: JPEG SOI marker (0xFF, 0xD8, 0xFF)
        if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new NotSupportedException($"File is not a valid JPEG: {request.Path ?? "memory buffer"}");
        }

        // ICC Profile check: if image contains embedded ICC profile, fallback to preserve color accuracy
        if (HasEmbeddedIccProfile(bytes))
        {
            throw new NotSupportedException("Embedded ICC profile detected in JPEG; falling back to WIC/WPF.");
        }

        int orientation = request.ApplyOrientation ? ReadExifOrientation(bytes) : 1;
        // Photo information line: parsed from the APP1 segment of the buffer this decode already holds (bounded,
        // never throws) -- no extra file read.
        var exif = ExifParser.TryParseJpeg(bytes);
        bool isTransposed = request.ApplyOrientation && orientation is >= 5 and <= 8;

        using var decompressor = TurboJpegNative.CreateDecompressor();
        ConfigureStrictDecoding(decompressor);

        unsafe
        {
            fixed (byte* pJpeg = bytes)
            {
                int headerRes = TurboJpegNative.tj3DecompressHeader(decompressor, pJpeg, (nuint)bytes.Length);
                if (headerRes != 0)
                {
                    string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Header parse error";
                    throw new InvalidDataException($"TurboJPEG failed to decompress header: {err}");
                }

                int origW = TurboJpegNative.tj3Get(decompressor, (int)TjParam.JpegWidth);
                int origH = TurboJpegNative.tj3Get(decompressor, (int)TjParam.JpegHeight);

                if (origW <= 0 || origH <= 0)
                {
                    throw new InvalidDataException($"Invalid image dimensions reported by TurboJPEG: {origW}x{origH}");
                }

                int targetW = origW;
                int targetH = origH;
                TjScalingFactor factor = TjScalingFactor.One;

                if (request.IsDownscaleRequested)
                {
                    // Box is in displayed (oriented) pixels; fit it on the stored grid, rotate later.
                    (targetW, targetH) = request.Box.FitStored(origW, origH, isTransposed);

                    if (targetW < origW || targetH < origH)
                    {
                        factor = SelectScalingFactor(origW, origH, targetW, targetH);
                    }
                }

                int setScaleRes = TurboJpegNative.tj3SetScalingFactor(decompressor, factor);
                if (setScaleRes != 0)
                {
                    factor = TjScalingFactor.One;
                }

                (int scaledW, int scaledH, int stride, int bufferLength) =
                    CalculateOutputBuffer(origW, origH, factor);

                // BGRX -> Bgr32: the opaque format WPF renders natively (JPEG has no alpha).
                // Decode into native scratch memory: BitmapSource.Create copies it anyway, so a
                // managed array would only add a large LOH allocation per decode.
                BitmapSource bitmap;
                IntPtr dstBuffer = Marshal.AllocHGlobal(bufferLength);
                try
                {
                    int decRes = TurboJpegNative.tj3Decompress8(
                        decompressor,
                        pJpeg,
                        (nuint)bytes.Length,
                        (byte*)dstBuffer,
                        stride,
                        (int)TjPixelFormat.Bgrx);

                    if (decRes != 0)
                    {
                        string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Decompression error";
                        throw new InvalidDataException($"TurboJPEG decompression failed: {err}");
                    }

                    bitmap = BitmapSource.Create(
                        scaledW, scaledH, 96, 96, PixelFormats.Bgr32, null, dstBuffer, bufferLength, stride);
                    bitmap.Freeze();
                }
                finally
                {
                    Marshal.FreeHGlobal(dstBuffer);
                }

                // Fine scale (DCT intermediate is >= target) and EXIF orientation in one WIC pass,
                // materialized here on the worker so the UI thread never runs the lazy pipeline.
                bool needsFineScale = request.IsDownscaleRequested && (scaledW > targetW || scaledH > targetH);
                bool needsOrientation = request.ApplyOrientation && orientation > 1;
                if (needsFineScale || needsOrientation)
                {
                    var transform = new TransformGroup();
                    if (needsFineScale)
                    {
                        transform.Children.Add(new ScaleTransform((double)targetW / scaledW, (double)targetH / scaledH));
                    }

                    if (needsOrientation)
                    {
                        transform.Children.Add(ExifOrientation.CreateTransform(orientation));
                    }

                    bitmap = WpfImageAdapter.Materialize(new TransformedBitmap(bitmap, transform));
                }

                // Perf: origW/origH (tj3Get JpegWidth/JpegHeight from the header already parsed
                // above) are free; a transposing orientation (5-8) swaps them, mirroring what
                // happened to the final bitmap's own pixel dimensions -- see
                // IDecodedImage.OriginalWidth/Height.
                int originalWidth = isTransposed ? origH : origW;
                int originalHeight = isTransposed ? origW : origH;

                return new WpfDecodedImage(
                    bitmap,
                    downscaled: request.IsDownscaleRequested && (scaledW < origW || targetW < origW || targetH < origH),
                    orientation: orientation,
                    actualBackend: DecoderBackend.TurboJpeg,
                    originalWidth: originalWidth,
                    originalHeight: originalHeight,
                    exif: exif);
            }
        }
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image file not found.", path);
        }

        byte[] headerBytes;
        // Read the first 64KB, which typically contains all header and EXIF markers
        using (var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan))
        {
            int toRead = (int)Math.Min(fs.Length, 64 * 1024);
            headerBytes = new byte[toRead];
            fs.ReadExactly(headerBytes, 0, toRead);
        }

        var bytes = headerBytes.AsSpan();
        if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new NotSupportedException($"File is not a valid JPEG: {path}");
        }

        int orientation = ReadExifOrientation(bytes);

        using var decompressor = TurboJpegNative.CreateDecompressor();
        ConfigureStrictDecoding(decompressor);
        unsafe
        {
            fixed (byte* pJpeg = bytes)
            {
                int headerRes = TurboJpegNative.tj3DecompressHeader(decompressor, pJpeg, (nuint)bytes.Length);
                if (headerRes != 0)
                {
                    // If 64KB wasn't enough, read full file
                    byte[] fullBytes = File.ReadAllBytes(path);
                    fixed (byte* pFull = fullBytes)
                    {
                        headerRes = TurboJpegNative.tj3DecompressHeader(decompressor, pFull, (nuint)fullBytes.Length);
                        if (headerRes != 0)
                        {
                            string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Header parse error";
                            throw new InvalidDataException($"TurboJPEG failed to read image info: {err}");
                        }
                    }
                }

                int width = TurboJpegNative.tj3Get(decompressor, (int)TjParam.JpegWidth);
                int height = TurboJpegNative.tj3Get(decompressor, (int)TjParam.JpegHeight);
                return new ImageInfo(width, height, orientation);
            }
        }
    }

    private static ReadOnlyMemory<byte> LoadBytes(DecodeRequest request)
    {
        if (request.Bytes.HasValue)
        {
            return request.Bytes.Value;
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new ArgumentException("DecodeRequest must provide either Path or Bytes.");
        }

        if (!File.Exists(request.Path))
        {
            throw new FileNotFoundException("Image file not found.", request.Path);
        }

        // Compliant with INV-8: File handle is closed immediately after reading byte buffer
        using var fs = new FileStream(
            request.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        byte[] buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Returns the smallest DCT scaling factor (in eighths) whose output is still at least
    /// <paramref name="targetW"/> x <paramref name="targetH"/>, so the final resample only ever
    /// shrinks (never upscales) the decoded image.
    /// </summary>
    public static TjScalingFactor SelectScalingFactor(int origW, int origH, int targetW, int targetH)
    {
        foreach (var factor in SupportedScalingFactors)
        {
            long scaledW = ((long)origW * factor.Num + factor.Denom - 1) / factor.Denom;
            long scaledH = ((long)origH * factor.Num + factor.Denom - 1) / factor.Denom;

            if (scaledW >= targetW && scaledH >= targetH)
            {
                return factor;
            }
        }

        return TjScalingFactor.One;
    }

    private static (int Width, int Height, int Stride, int BufferLength) CalculateOutputBuffer(
        int originalWidth, int originalHeight, TjScalingFactor factor)
    {
        long width = ((long)originalWidth * factor.Num + factor.Denom - 1) / factor.Denom;
        long height = ((long)originalHeight * factor.Num + factor.Denom - 1) / factor.Denom;
        long stride = width > long.MaxValue / 4 ? long.MaxValue : width * 4;
        long bufferLength = height > long.MaxValue / Math.Max(1, stride)
            ? long.MaxValue
            : stride * height;

        if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue ||
            stride > int.MaxValue || bufferLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"TurboJPEG output dimensions are too large: {width}x{height} ({bufferLength} bytes).");
        }

        return ((int)width, (int)height, (int)stride, (int)bufferLength);
    }

    private static void ConfigureStrictDecoding(Native.SafeTurboJpegHandle decompressor)
    {
        if (TurboJpegNative.tj3Set(decompressor, (int)TjParam.StopOnWarning, 1) != 0)
        {
            string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Unknown configuration error";
            throw new InvalidOperationException($"TurboJPEG could not enable strict decoding: {err}");
        }
    }

    /// <summary>
    /// Shared JPEG marker walker (IMG-07): yields the next marker segment's marker byte and payload
    /// (after the 2-byte length), skipping standalone markers. Returns false at SOS, on a malformed
    /// or truncated segment, or at the end of the header area. Start with <paramref name="offset"/> = 2
    /// (just past SOI).
    /// </summary>
    private static bool TryReadSegment(
        ReadOnlySpan<byte> jpeg, ref int offset, out byte marker, out ReadOnlySpan<byte> payload)
    {
        marker = 0;
        payload = default;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) return false;

            marker = jpeg[offset + 1];
            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            if (marker == 0xDA) return false; // SOS marker reached

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) return false;

            payload = jpeg.Slice(offset + 4, length - 2);
            offset += 2 + length;
            return true;
        }

        return false;
    }

    public static bool HasEmbeddedIccProfile(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;

        int offset = 2;
        while (TryReadSegment(jpeg, ref offset, out byte marker, out var payload))
        {
            // Marker APP2 (0xE2)
            if (marker == 0xE2 &&
                payload.Length >= 12 &&
                payload[0] == (byte)'I' && payload[1] == (byte)'C' && payload[2] == (byte)'C' &&
                payload[3] == (byte)'_' && payload[4] == (byte)'P' && payload[5] == (byte)'R' &&
                payload[6] == (byte)'O' && payload[7] == (byte)'F' && payload[8] == (byte)'I' &&
                payload[9] == (byte)'L' && payload[10] == (byte)'E' && payload[11] == 0)
            {
                return true;
            }
        }

        return false;
    }

    public static int ReadExifOrientation(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;

        int offset = 2;
        while (TryReadSegment(jpeg, ref offset, out byte marker, out var payload))
        {
            // Marker APP1 (0xE1)
            if (marker == 0xE1 &&
                payload.Length >= 14 &&
                payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i' &&
                payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
            {
                return ParseTiffOrientation(payload.Slice(6));
            }
        }

        return 1;
    }

    private static int ParseTiffOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;

        bool isLittleEndian = tiff[0] == 'I' && tiff[1] == 'I';
        bool isBigEndian = tiff[0] == 'M' && tiff[1] == 'M';
        if (!isLittleEndian && !isBigEndian) return 1;

        ushort magic = ReadUInt16(tiff, 2, isLittleEndian);
        if (magic != 42) return 1;

        uint ifd0Offset = ReadUInt32(tiff, 4, isLittleEndian);
        if (ifd0Offset >= (uint)tiff.Length) return 1;

        int ifdPos = (int)ifd0Offset;
        ushort numEntries = ReadUInt16(tiff, ifdPos, isLittleEndian);
        ifdPos += 2;

        for (int i = 0; i < numEntries; i++)
        {
            int entryPos = ifdPos + i * 12;
            if (entryPos + 12 > tiff.Length) break;

            ushort tag = ReadUInt16(tiff, entryPos, isLittleEndian);
            if (tag == 274) // EXIF Orientation
            {
                ushort val = ReadUInt16(tiff, entryPos + 8, isLittleEndian);
                if (val is >= 1 and <= 8) return val;
            }
        }

        return 1;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int pos, bool isLittleEndian)
    {
        if (pos + 2 > data.Length) return 0;
        return isLittleEndian
            ? (ushort)(data[pos] | (data[pos + 1] << 8))
            : (ushort)((data[pos] << 8) | data[pos + 1]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int pos, bool isLittleEndian)
    {
        if (pos + 4 > data.Length) return 0;
        return isLittleEndian
            ? (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24))
            : (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
    }
}
