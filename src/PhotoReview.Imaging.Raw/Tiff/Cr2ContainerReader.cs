using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Canon CR2 format (TIFF container with 'CR' at offset 8).
/// IFD0: StripOffsets (0x0111) / StripByteCounts (0x0117) contain full JPEG preview.
/// IFD1: Small JPEG preview / thumbnail (0x0201 / 0x0202).
/// Orientation in IFD0 (0x0112).
/// </summary>
public sealed class Cr2ContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Cr2;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".cr2", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 10) return false;

        // TIFF header: II (0x49 0x49), magic 42, CR at offset 8, 9
        if (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 &&
            first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00 &&
            first64Bytes[8] == (byte)'C' && first64Bytes[9] == (byte)'R')
        {
            return true;
        }

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for CR2 container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid CR2 TIFF header.");

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        // Visited IFD offsets to guard against cycles
        var visited = new HashSet<long>();
        long currentIfdOffset = ifd0Offset;
        int ifdIndex = 0;

        while (currentIfdOffset > 0 && ifdIndex < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();

            if (!visited.Add(currentIfdOffset))
                break; // Cycle detected

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, currentIfdOffset, littleEndian, out uint nextIfdOffset);

            long? stripOffset = null;
            long? stripByteCount = null;
            long? jpegInterchange = null;
            long? jpegLength = null;
            int ifdWidth = 0;
            int ifdHeight = 0;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case 0x0112: // Orientation
                        if (ifdIndex == 0)
                        {
                            var val = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                            if (val is >= 1 and <= 8) orientation = (int)val;
                        }
                        break;

                    case 0x0100: // ImageWidth
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                            ifdWidth = (int)w;
                        break;

                    case 0x0101: // ImageLength
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                            ifdHeight = (int)h;
                        break;

                    case 0x0111: // StripOffsets
                        stripOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0117: // StripByteCounts
                        stripByteCount = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0201: // JPEGInterchangeFormat
                        jpegInterchange = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0202: // JPEGInterchangeFormatLength
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x8769: // ExifIFD pointer
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } exifOffset && exifOffset > 0)
                        {
                            exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));
                        }
                        break;
                }
            }

            if (ifdIndex == 0)
            {
                // Full size preview in IFD0 StripOffsets/ByteCounts
                if (stripOffset is > 0 && stripByteCount is > 0 && stripOffset + stripByteCount <= source.Length)
                {
                    previews.Add(new EmbeddedPreview(
                        Index: previews.Count,
                        Offset: stripOffset.Value,
                        Length: stripByteCount.Value,
                        Kind: EmbeddedPreviewKind.Jpeg,
                        Width: ifdWidth,
                        Height: ifdHeight,
                        ColorSpace: PreviewColorSpace.Unknown));
                }

                if (ifdWidth > 0 && ifdHeight > 0)
                {
                    sensorWidth = ifdWidth;
                    sensorHeight = ifdHeight;
                }
            }
            else
            {
                // Secondary preview / thumbnail (IFD1, etc.)
                if (jpegInterchange is > 0 && jpegLength is > 0 && jpegInterchange + jpegLength <= source.Length)
                {
                    previews.Add(new EmbeddedPreview(
                        Index: previews.Count,
                        Offset: jpegInterchange.Value,
                        Length: jpegLength.Value,
                        Kind: EmbeddedPreviewKind.Jpeg,
                        Width: ifdWidth,
                        Height: ifdHeight,
                        ColorSpace: PreviewColorSpace.Unknown));
                }
            }

            currentIfdOffset = nextIfdOffset;
            ifdIndex++;
        }

        if (exifBlocks.Count == 0 && source.Length > 0)
        {
            exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));
        }

        return new RawContainerInfo(
            RawFormat.Cr2,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }
}
