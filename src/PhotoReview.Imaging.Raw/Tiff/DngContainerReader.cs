using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Adobe DNG format.
/// Previews: IFD0 or SubIFDs with NewSubFileType=1 (0x00FE) and Compression=7 (JPEG) or 6.
/// Sensor size: Raw IFD (NewSubFileType=0) DefaultCropSize (0xC620) or ImageWidth/Length.
/// </summary>
public sealed class DngContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Dng;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".dng", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        return (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00) ||
               (first64Bytes[0] == 0x4D && first64Bytes[1] == 0x4D && first64Bytes[2] == 0x00 && first64Bytes[3] == 0x2A);
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for DNG container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid DNG TIFF header.");

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var allIfdOffsets = new List<long> { ifd0Offset };
        var visited = new HashSet<long>();

        // Step 1: Scan IFD chain and collect SubIFDs
        long currentIfdOffset = ifd0Offset;
        while (currentIfdOffset > 0 && allIfdOffsets.Count < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(currentIfdOffset)) break;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, currentIfdOffset, littleEndian, out uint nextIfdOffset);
            foreach (var entry in entries)
            {
                if (entry.Tag == 0x014A) // SubIFDs
                {
                    var subs = TiffHeaderNavigator.ReadTagUnsignedArray(source, entry, littleEndian);
                    foreach (var s in subs)
                    {
                        if (s > 0 && !allIfdOffsets.Contains(s))
                            allIfdOffsets.Add(s);
                    }
                }
            }

            if (nextIfdOffset > 0 && !allIfdOffsets.Contains(nextIfdOffset))
                allIfdOffsets.Add(nextIfdOffset);

            currentIfdOffset = nextIfdOffset;
        }

        // Step 2: Parse each IFD
        foreach (var ifdOffset in allIfdOffsets)
        {
            ct.ThrowIfCancellationRequested();
            var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdOffset, littleEndian, out _);

            uint subFileType = 0;
            ushort compression = 1;
            int width = 0;
            int height = 0;
            long? jpegOffset = null;
            long? jpegLength = null;
            long? stripOffset = null;
            long? stripByteCount = null;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case 0x00FE: // NewSubFileType: 0 = primary image, 1 = preview/thumbnail
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } sft)
                            subFileType = (uint)sft;
                        break;

                    case 0x0100: // ImageWidth
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                            width = (int)w;
                        break;

                    case 0x0101: // ImageLength
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                            height = (int)h;
                        break;

                    case 0x0103: // Compression: 1 = uncompressed, 6 = JPEG, 7 = JPEG (lossless/baseline)
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } comp)
                            compression = (ushort)comp;
                        break;

                    case 0x0112: // Orientation
                        if (ifdOffset == ifd0Offset)
                        {
                            var val = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                            if (val is >= 1 and <= 8) orientation = (int)val;
                        }
                        break;

                    case 0x0111: // StripOffsets
                        stripOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0117: // StripByteCounts
                        stripByteCount = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0201: // JPEGInterchangeFormat
                        jpegOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0202: // JPEGInterchangeFormatLength
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0xC620: // DefaultCropSize (2 rationals: width, height)
                        if (subFileType == 0 && entry.Count >= 2)
                        {
                            var cropSizes = TiffHeaderNavigator.ReadTagUnsignedArray(source, entry, littleEndian, 2);
                            if (cropSizes.Count >= 2 && cropSizes[0] > 0 && cropSizes[1] > 0)
                            {
                                sensorWidth = (int)cropSizes[0];
                                sensorHeight = (int)cropSizes[1];
                            }
                        }
                        break;
                }
            }

            if (subFileType == 0 && sensorWidth == 0 && width > 0 && height > 0)
            {
                sensorWidth = width;
                sensorHeight = height;
            }

            // Check if this IFD contains a JPEG preview
            long? finalOffset = jpegOffset ?? ((compression is 6 or 7) ? stripOffset : null);
            long? finalLength = jpegLength ?? ((compression is 6 or 7) ? stripByteCount : null);

            if (finalOffset is > 0 && finalLength is > 0 && finalOffset + finalLength <= source.Length)
            {
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: finalOffset.Value,
                    Length: finalLength.Value,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: width,
                    Height: height,
                    ColorSpace: PreviewColorSpace.Unknown));
            }
        }

        if (sensorWidth == 0 && previews.Count > 0)
        {
            var largest = previews.OrderByDescending(p => (long)p.Width * p.Height).First();
            sensorWidth = largest.Width;
            sensorHeight = largest.Height;
        }

        exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));

        return new RawContainerInfo(
            RawFormat.Dng,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }
}
