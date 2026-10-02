using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Panasonic RW2 format.
/// Magic: 'IIU\0' (0x49 0x49 0x55 0x00).
/// Embedded JPEG preview: tag 0x002E (JpgFromRaw) in IFD0.
/// EXIF lives inside the embedded JPEG APP1 marker; Orientation is IFD0 tag 0x0112 when present, else that JPEG's EXIF.
/// </summary>
public sealed class Rw2ContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Rw2;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".rw2", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        // Magic IIU\0 (0x49 0x49 0x55 0x00)
        return first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 &&
               first64Bytes[2] == 0x55 && first64Bytes[3] == 0x00;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for RW2 container.");

        var headerSpan = source.Read(0, 16);
        // 'IIU\0': little-endian byte order with the Panasonic magic 0x0055 instead of 42.
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) ||
            !littleEndian || magic != 0x55)
            throw new InvalidDataException("Invalid RW2 header.");

        int? ifd0Orientation = null;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifd0Offset, littleEndian, out _);

        long? sensorWidthTag = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0002, littleEndian);
        long? sensorHeightTag = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0003, littleEndian);
        long? topBorder = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0004, littleEndian);
        long? leftBorder = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0005, littleEndian);
        long? bottomBorder = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0006, littleEndian);
        long? rightBorder = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0007, littleEndian);

        // Orientation is normally in IFD0; some files only carry it in the embedded JPEG's EXIF.
        if (TiffHeaderNavigator.ReadTagValue(source, entries, 0x0112, littleEndian) is { } orient and >= 1 and <= 8)
            ifd0Orientation = (int)orient;
        int? embeddedJpegOrientation = null;

        // Tags 2/3 describe the full sensor readout including masked margins; the borders (4..7) delimit the image.
        if (leftBorder is >= 0 and var left && rightBorder is { } right && right > left && right <= int.MaxValue)
            sensorWidth = checked((int)(right - left));
        else if (sensorWidthTag is > 0 and <= int.MaxValue)
            sensorWidth = (int)sensorWidthTag.Value;

        if (topBorder is >= 0 and var top && bottomBorder is { } bottom && bottom > top && bottom <= int.MaxValue)
            sensorHeight = checked((int)(bottom - top));
        else if (sensorHeightTag is > 0 and <= int.MaxValue)
            sensorHeight = (int)sensorHeightTag.Value;

        // JpgFromRaw (0x002E): the entry offset points at the JPEG and the entry count is its length in bytes.
        if (TiffHeaderNavigator.TryGetEntry(entries, 0x002E, out var jpgEntry) && jpgEntry.Count > 4)
        {
            long offset = jpgEntry.ValueOrOffset;
            long length = jpgEntry.Count;
            if (TiffHeaderNavigator.IsRangeInFile(offset, length, source.Length) && source.Read(offset, 2) is [0xFF, 0xD8])
            {
                previews.Add(new EmbeddedPreview(
                    Index: 0,
                    Offset: offset,
                    Length: length,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: 0,
                    Height: 0,
                    ColorSpace: PreviewColorSpace.Unknown));

                // RW2 EXIF is inside the preview JPEG
                exifBlocks.Add(new ExifBlock(offset, Math.Min(length, 128 * 1024), IsTiffHeader: false));

                // Orientation is normally in IFD0; some files only carry it in the embedded JPEG's EXIF.
                if (ifd0Orientation is null)
                {
                    int exifLength = (int)Math.Min(Math.Min(length, 128 * 1024), source.Length - offset);
                    if (exifLength > 0)
                        embeddedJpegOrientation = ExifParser.TryReadOrientationFromJpeg(source.Read(offset, exifLength));
                }
            }
        }

        return new RawContainerInfo(
            RawFormat.Rw2,
            sensorWidth,
            sensorHeight,
            ifd0Orientation ?? embeddedJpegOrientation ?? 1,
            previews,
            exifBlocks);
    }
}
