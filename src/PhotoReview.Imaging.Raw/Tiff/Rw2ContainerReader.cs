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
        bool littleEndian = headerSpan[0] == 0x49 && headerSpan[1] == 0x49;
        uint ifd0Offset = TiffStructure.ReadU32(headerSpan, 4, littleEndian);

        int? ifd0Orientation = null;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifd0Offset, littleEndian, out _);

        long? jpgFromRawOffset = null;
        long? jpgFromRawLength = null;
        int? embeddedJpegOrientation = null;

        foreach (var entry in entries)
        {
            switch (entry.Tag)
            {
                case 0x0002: // Sensor width / ImageWidth
                    if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                        sensorWidth = (int)w;
                    break;

                case 0x0003: // Sensor height / ImageHeight
                    if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                        sensorHeight = (int)h;
                    break;

                case 0x0112: // Orientation
                    if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } orient and >= 1 and <= 8)
                        ifd0Orientation = (int)orient;
                    break;

                case 0x002E: // JpgFromRaw
                    // In RW2 tag 0x002E can be offset to the JPEG, or offset to an IFD with offset/length
                    jpgFromRawOffset = entry.ValueOrOffset;
                    // Tag 0x002E count gives the length in bytes (or check value)
                    if (entry.Count > 4)
                        jpgFromRawLength = entry.Count;
                    break;
            }
        }

        // If length is not directly from tag count, probe JPEG SOI -> EOI or remaining file length
        if (jpgFromRawOffset is > 0 && jpgFromRawOffset < source.Length)
        {
            long offset = jpgFromRawOffset.Value;
            var probeSpan = source.Read(offset, 4);
            if (probeSpan.Length >= 2 && probeSpan[0] == 0xFF && probeSpan[1] == 0xD8) // Valid JPEG SOI
            {
                long length = jpgFromRawLength ?? (source.Length - offset);
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
