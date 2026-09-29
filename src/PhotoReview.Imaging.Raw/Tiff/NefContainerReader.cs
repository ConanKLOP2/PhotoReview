using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Nikon NEF format (TIFF container with SubIFDs 0x014A pointing to JpgFromRaw).
/// Orientation in IFD0.
/// </summary>
public sealed class NefContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Nef;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".nef", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        // Check standard TIFF magic
        if ((first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00) ||
            (first64Bytes[0] == 0x4D && first64Bytes[1] == 0x4D && first64Bytes[2] == 0x00 && first64Bytes[3] == 0x2A))
        {
            return true;
        }

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for NEF container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid NEF TIFF header.");

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var subIfdOffsets = new List<long>();
        var visited = new HashSet<long>();
        long currentIfdOffset = ifd0Offset;
        int ifdIndex = 0;

        while (currentIfdOffset > 0 && ifdIndex < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(currentIfdOffset)) break;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, currentIfdOffset, littleEndian, out uint nextIfdOffset);
            long? jpegOffset = null;
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

                    case 0x014A: // SubIFDs
                        var subs = TiffHeaderNavigator.ReadTagUnsignedArray(source, entry, littleEndian);
                        subIfdOffsets.AddRange(subs);
                        break;

                    case 0x0201: // JPEGInterchangeFormat
                        jpegOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0202: // JPEGInterchangeFormatLength
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;
                }
            }

            if (ifdIndex == 0 && ifdWidth > 0 && ifdHeight > 0)
            {
                sensorWidth = ifdWidth;
                sensorHeight = ifdHeight;
            }

            if (jpegOffset is > 0 && jpegLength is > 0 && jpegOffset + jpegLength <= source.Length)
            {
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: jpegOffset.Value,
                    Length: jpegLength.Value,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: ifdWidth,
                    Height: ifdHeight,
                    ColorSpace: PreviewColorSpace.Unknown));
            }

            currentIfdOffset = nextIfdOffset;
            ifdIndex++;
        }

        // Now walk SubIFDs to discover full-sized JpgFromRaw
        foreach (var subOffset in subIfdOffsets)
        {
            ct.ThrowIfCancellationRequested();
            if (subOffset <= 0 || !visited.Add(subOffset)) continue;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, subOffset, littleEndian, out _);
            long? jpegOffset = null;
            long? jpegLength = null;
            int subWidth = 0;
            int subHeight = 0;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case 0x0100:
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                            subWidth = (int)w;
                        break;
                    case 0x0101:
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                            subHeight = (int)h;
                        break;
                    case 0x0201:
                        jpegOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;
                    case 0x0202:
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;
                }
            }

            if (jpegOffset is > 0 && jpegLength is > 0 && jpegOffset + jpegLength <= source.Length)
            {
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: jpegOffset.Value,
                    Length: jpegLength.Value,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: subWidth,
                    Height: subHeight,
                    ColorSpace: PreviewColorSpace.Unknown));
            }
        }

        exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));

        return new RawContainerInfo(
            RawFormat.Nef,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }
}
