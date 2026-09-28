using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Olympus/OM System ORF format.
/// Magic: 'IIRO' (0x49 0x49 0x52 0x4F) or 'IIRS' (0x49 0x49 0x52 0x53) or 'MMOR'.
/// Previews: IFD0 / IFD1 thumbnail, or MakerNote CameraSettings (tag 0x2020) PreviewImageStart / Length.
/// </summary>
public sealed class OrfContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Orf;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".orf", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        // IIRO
        if (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x52 && first64Bytes[3] == 0x4F)
            return true;

        // IIRS
        if (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x52 && first64Bytes[3] == 0x53)
            return true;

        // MMOR
        if (first64Bytes[0] == 0x4D && first64Bytes[1] == 0x4D && first64Bytes[2] == 0x4F && first64Bytes[3] == 0x52)
            return true;

        // Standard TIFF magic fallback for some ORF bodies
        if (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00)
            return true;

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for ORF container.");

        var headerSpan = source.Read(0, 16);
        bool littleEndian = headerSpan[0] == 0x49 && headerSpan[1] == 0x49;
        uint ifd0Offset = TiffStructure.ReadU32(headerSpan, 4, littleEndian);

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var visited = new HashSet<long>();
        long currentIfdOffset = ifd0Offset;
        int ifdIndex = 0;
        long? makerNoteOffset = null;

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
                    case 0x0112:
                        if (ifdIndex == 0)
                        {
                            var val = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                            if (val is >= 1 and <= 8) orientation = (int)val;
                        }
                        break;

                    case 0x0100:
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                            ifdWidth = (int)w;
                        break;

                    case 0x0101:
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                            ifdHeight = (int)h;
                        break;

                    case 0x0201:
                        jpegOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0202:
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x8769: // ExifIFD pointer
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } exifPtr && exifPtr > 0)
                        {
                            var exifEntries = TiffHeaderNavigator.ReadIfdEntries(source, exifPtr, littleEndian, out _);
                            foreach (var ee in exifEntries)
                            {
                                if (ee.Tag == 0x927C) // MakerNote
                                {
                                    makerNoteOffset = ee.ValueOrOffset;
                                }
                            }
                        }
                        break;

                    case 0x927C: // MakerNote in IFD0
                        makerNoteOffset = entry.ValueOrOffset;
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

        // Search MakerNote for PreviewImage (Olympus MakerNote CameraSettings 0x2020)
        if (makerNoteOffset is > 0 && makerNoteOffset < source.Length - 16)
        {
            ParseOlympusMakerNote(source, makerNoteOffset.Value, previews);
        }

        exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));

        return new RawContainerInfo(
            RawFormat.Orf,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }

    private static void ParseOlympusMakerNote(IRawHeaderSource source, long baseOffset, List<EmbeddedPreview> previews)
    {
        // Olympus MakerNote often begins with "OLYMPUS\0II\x03\0" (12 bytes) or similar
        var header = source.Read(baseOffset, 32);
        if (header.Length < 16) return;

        bool mnLittle = true;
        long ifdStart = baseOffset;

        if (header.Length >= 16 &&
            header[0] == (byte)'O' && header[1] == (byte)'M' && header[2] == (byte)' ' &&
            header[3] == (byte)'S' && header[4] == (byte)'Y' && header[5] == (byte)'S' &&
            header[6] == (byte)'T' && header[7] == (byte)'E' && header[8] == (byte)'M' && header[9] == 0)
        {
            mnLittle = header[12] == (byte)'I' && header[13] == (byte)'I';
            ifdStart = baseOffset + 16;
        }
        else if (header.Length >= 12 &&
            header[0] == (byte)'O' && header[1] == (byte)'L' && header[2] == (byte)'Y' &&
            header[3] == (byte)'M' && header[4] == (byte)'P' && header[5] == (byte)'U' &&
            header[6] == (byte)'S' && header[7] == 0)
        {
            mnLittle = header[8] == (byte)'I' && header[9] == (byte)'I';
            ifdStart = baseOffset + 12;
        }

        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdStart, mnLittle, out _);
        long? previewStart = null;
        long? previewLength = null;

        foreach (var entry in entries)
        {
            // Tag 0x0101 / 0x0102 or CameraSettings 0x2020
            if (entry.Tag == 0x0101)
                previewStart = TiffHeaderNavigator.ReadTagUnsigned(source, entry, mnLittle);
            else if (entry.Tag == 0x0102)
                previewLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, mnLittle);
            else if (entry.Tag == 0x2020) // CameraSettings IFD
            {
                long subOffset = (entry.ValueOrOffset > baseOffset) ? entry.ValueOrOffset : (baseOffset + entry.ValueOrOffset);
                var subEntries = TiffHeaderNavigator.ReadIfdEntries(source, subOffset, mnLittle, out _);
                foreach (var se in subEntries)
                {
                    if (se.Tag == 0x0101)
                    {
                        var rawOffset = TiffHeaderNavigator.ReadTagUnsigned(source, se, mnLittle);
                        if (rawOffset is > 0)
                        {
                            // Could be absolute or relative to baseOffset
                            previewStart = (rawOffset.Value > baseOffset) ? rawOffset.Value : (baseOffset + rawOffset.Value);
                        }
                    }
                    else if (se.Tag == 0x0102)
                    {
                        previewLength = TiffHeaderNavigator.ReadTagUnsigned(source, se, mnLittle);
                    }
                }
            }
        }

        if (previewStart is > 0 && previewLength is > 0 && previewStart + previewLength <= source.Length)
        {
            if (!previews.Any(p => p.Offset == previewStart.Value))
            {
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: previewStart.Value,
                    Length: previewLength.Value,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: 0,
                    Height: 0,
                    ColorSpace: PreviewColorSpace.Unknown));
            }
        }
    }
}
