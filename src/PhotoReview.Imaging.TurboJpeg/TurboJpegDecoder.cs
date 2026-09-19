using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.TurboJpeg.Native;

namespace PhotoReview.Imaging.TurboJpeg;

/// <summary>
/// High-performance SIMD-accelerated JPEG decoder using libjpeg-turbo 3.x.
/// Supports DCT scaling factor IDCT decompression, EXIF orientation, and safe fallback on ICC profiles.
/// </summary>
public sealed class TurboJpegDecoder : IImageDecoder
{
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

                if (request.TargetWidth > 0)
                {
                    if (isTransposed)
                    {
                        targetH = request.TargetWidth;
                        targetW = Math.Max(1, (int)((long)origW * request.TargetWidth / Math.Max(1, origH)));
                    }
                    else
                    {
                        targetW = request.TargetWidth;
                        targetH = Math.Max(1, (int)((long)origH * request.TargetWidth / Math.Max(1, origW)));
                    }

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

                int scaledW = (origW * factor.Num + factor.Denom - 1) / factor.Denom;
                int scaledH = (origH * factor.Num + factor.Denom - 1) / factor.Denom;

                int stride = scaledW * 4;
                byte[] dstBuffer = new byte[stride * scaledH];

                fixed (byte* pDst = dstBuffer)
                {
                    int decRes = TurboJpegNative.tj3Decompress8(
                        decompressor,
                        pJpeg,
                        (nuint)bytes.Length,
                        pDst,
                        stride,
                        (int)TjPixelFormat.Bgra);

                    if (decRes != 0)
                    {
                        string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Decompression error";
                        throw new InvalidDataException($"TurboJPEG decompression failed: {err}");
                    }
                }

                BitmapSource bitmap = WpfImageAdapter.FromBgra32(dstBuffer, scaledW, scaledH, stride);

                // Fine scale if DCT intermediate size is larger than requested target
                if (request.TargetWidth > 0 && (scaledW > targetW || scaledH > targetH))
                {
                    double scaleX = (double)targetW / scaledW;
                    double scaleY = (double)targetH / scaledH;
                    var fineScaled = new TransformedBitmap(bitmap, new ScaleTransform(scaleX, scaleY));
                    fineScaled.Freeze();
                    bitmap = fineScaled;
                }

                // Apply EXIF orientation
                if (request.ApplyOrientation && orientation > 1)
                {
                    bitmap = ExifOrientation.Apply(bitmap, orientation);
                }

                return new WpfDecodedImage(
                    bitmap,
                    downscaled: request.TargetWidth > 0 && (scaledW < origW || targetW < origW),
                    orientation: orientation,
                    actualBackend: DecoderBackend.TurboJpeg);
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

    private static TjScalingFactor SelectScalingFactor(int origW, int origH, int targetW, int targetH)
    {
        foreach (var factor in SupportedScalingFactors)
        {
            int scaledW = (origW * factor.Num + factor.Denom - 1) / factor.Denom;
            int scaledH = (origH * factor.Num + factor.Denom - 1) / factor.Denom;

            if (scaledW >= targetW && scaledH >= targetH)
            {
                return factor;
            }
        }

        return TjScalingFactor.One;
    }

    private static void ConfigureStrictDecoding(Native.SafeTurboJpegHandle decompressor)
    {
        if (TurboJpegNative.tj3Set(decompressor, (int)TjParam.StopOnWarning, 1) != 0)
        {
            string err = TurboJpegNative.GetErrorMessage(decompressor) ?? "Unknown configuration error";
            throw new InvalidOperationException($"TurboJPEG could not enable strict decoding: {err}");
        }
    }

    public static bool HasEmbeddedIccProfile(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;

        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) break;

            byte marker = jpeg[offset + 1];
            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            if (marker == 0xDA) break; // SOS marker reached

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) break;

            // Marker APP2 (0xE2)
            if (marker == 0xE2)
            {
                var payload = jpeg.Slice(offset + 4, length - 2);
                if (payload.Length >= 12 &&
                    payload[0] == (byte)'I' && payload[1] == (byte)'C' && payload[2] == (byte)'C' &&
                    payload[3] == (byte)'_' && payload[4] == (byte)'P' && payload[5] == (byte)'R' &&
                    payload[6] == (byte)'O' && payload[7] == (byte)'F' && payload[8] == (byte)'I' &&
                    payload[9] == (byte)'L' && payload[10] == (byte)'E' && payload[11] == 0)
                {
                    return true;
                }
            }

            offset += 2 + length;
        }

        return false;
    }

    public static int ReadExifOrientation(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;

        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) break;

            byte marker = jpeg[offset + 1];
            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            if (marker == 0xDA) break;

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) break;

            // Marker APP1 (0xE1)
            if (marker == 0xE1)
            {
                var payload = jpeg.Slice(offset + 4, length - 2);
                if (payload.Length >= 14 &&
                    payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i' &&
                    payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
                {
                    var tiff = payload.Slice(6);
                    return ParseTiffOrientation(tiff);
                }
            }

            offset += 2 + length;
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
