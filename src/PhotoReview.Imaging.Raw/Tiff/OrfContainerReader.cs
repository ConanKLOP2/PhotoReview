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
                                // MakerNote is UNDEFINED data: it is only an offset when it does not fit inline.
                                if (ee.Tag == 0x927C && ee.Count > 4)
                                {
                                    makerNoteOffset = ee.ValueOrOffset;
                                }
                            }
                        }
                        break;

                    case 0x927C: // MakerNote in IFD0
                        if (entry.Count > 4)
                            makerNoteOffset = entry.ValueOrOffset;
                        break;
                }
            }

            if (ifdIndex == 0 && ifdWidth > 0 && ifdHeight > 0)
            {
                sensorWidth = ifdWidth;
                sensorHeight = ifdHeight;
            }

            if (jpegOffset is > 0 && jpegLength is > 0 && TiffHeaderNavigator.StartsWithSoi(source, jpegOffset.Value, jpegLength.Value))
            {
                int previewWidth = ifdWidth;
                int previewHeight = ifdHeight;
                TiffHeaderNavigator.ReconcileJpegSize(source, jpegOffset.Value, jpegLength.Value, ref previewWidth, ref previewHeight);
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: jpegOffset.Value,
                    Length: jpegLength.Value,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: previewWidth,
                    Height: previewHeight,
                    ColorSpace: PreviewColorSpace.Unknown));
            }

            currentIfdOffset = nextIfdOffset;
            ifdIndex++;
        }

        // Search MakerNote for PreviewImage (Olympus MakerNote CameraSettings 0x2020)
        if (makerNoteOffset is > 0 && makerNoteOffset < source.Length - 16)
        {
            ParseOlympusMakerNote(source, makerNoteOffset.Value, littleEndian, previews);
        }

        exifBlocks.Add(TiffHeaderNavigator.ComputeExifBlock(source, littleEndian, ifd0Offset));

        return new RawContainerInfo(
            RawFormat.Orf,
            DecodedWidth(sensorWidth),
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }

    /// <summary>Raw frame width (IFD0 ImageWidth) of the E-PM1 / E-PL3 / E-P3 sensor: 24 of its 4080 columns are not image.</summary>
    internal const int RawFrameWidthWithMaskedColumns = 4080;

    /// <summary>Width LibRaw decodes for <see cref="RawFrameWidthWithMaskedColumns"/> (its identify.cpp trims the 24 columns).</summary>
    internal const int DecodedWidthWithoutMaskedColumns = 4056;

    /// <summary>
    /// The size the zoom (LibRaw) decode returns, which is what the viewer swaps in at 100 %. IFD0 ImageWidth/ImageLength is the raw
    /// frame and LibRaw decodes it unchanged (E-M1 4640x3472, OM-1 5220x3912) except for one hard-coded per-family rule the file
    /// itself does not carry: a 4080-wide frame (E-PM1, E-PL3, E-P3) loses 24 columns. Reporting 4080 made the zoom decode (4056)
    /// change the image size under the user. The camera image proper is smaller still (MakerNote ImageProcessing 0x0614/0x0615:
    /// 4032x3024 for the E-P3), as for every other format the decode is a superset of it. Pinned by OrfDisplayedSizeTests.
    /// </summary>
    private static int DecodedWidth(int ifd0Width) =>
        ifd0Width == RawFrameWidthWithMaskedColumns ? DecodedWidthWithoutMaskedColumns : ifd0Width;

    /// <summary>
    /// Olympus/OM System MakerNote. Offsets stored inside the note (CameraSettings pointer 0x2020 and the
    /// PreviewImageStart/ThumbnailImage values) are relative to the start of the MakerNote ("OLYMPUS\0" or
    /// "OM SYSTEM\0"), not to the file. Legacy "OLYMP\0" notes (IFD at +8) use file-absolute offsets. Notes without a known signature are treated as plain TIFF-style
    /// directories in the file's byte order with file-absolute offsets.
    /// </summary>
    private static void ParseOlympusMakerNote(IRawHeaderSource source, long noteOffset, bool fileLittleEndian, List<EmbeddedPreview> previews)
    {
        // Short reads (note near the end of a truncated file) skip the MakerNote instead of throwing.
        int available = (int)Math.Min(32L, source.Length - noteOffset);
        if (available < 16) return;

        var header = source.Read(noteOffset, available);

        bool noteLittleEndian = fileLittleEndian;
        long ifdStart = noteOffset;
        long offsetBase = 0; // file-absolute unless the signature says the note is self-relative

        if (header[..10].SequenceEqual("OM SYSTEM\0"u8))
        {
            noteLittleEndian = header[12] == (byte)'I' && header[13] == (byte)'I';
            ifdStart = noteOffset + 16;
            offsetBase = noteOffset;
        }
        else if (header[..8].SequenceEqual("OLYMPUS\0"u8))
        {
            noteLittleEndian = header[8] == (byte)'I' && header[9] == (byte)'I';
            ifdStart = noteOffset + 12;
            offsetBase = noteOffset;
        }

        else if (header[..6].SequenceEqual("OLYMP\0"u8))
        {
            // Legacy (pre-2010) note: "OLYMP\0" + 2 version bytes, IFD at +8, file byte order and file-absolute offsets.
            ifdStart = noteOffset + 8;
        }

        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdStart, noteLittleEndian, out _);

        foreach (var entry in entries)
        {
            // 0x0100 ThumbnailImage: UNDEFINED bytes whose offset is relative to offsetBase.
            if (entry.Tag == 0x0100 && entry.Count > 4)
                AddPreview(source, offsetBase + entry.ValueOrOffset, entry.Count, previews);
        }

        if (!TiffHeaderNavigator.TryGetEntry(entries, 0x2020, out var cameraSettings))
            return;

        // CameraSettings is an IFD (type 13): the four value bytes are the IFD offset.
        var settingsEntries = TiffHeaderNavigator.ReadIfdEntries(source, offsetBase + cameraSettings.ValueOrOffset, noteLittleEndian, out _);
        if (TiffHeaderNavigator.ReadTagValue(source, settingsEntries, 0x0101, noteLittleEndian) is { } previewStart &&
            TiffHeaderNavigator.ReadTagValue(source, settingsEntries, 0x0102, noteLittleEndian) is { } previewLength)
        {
            AddPreview(source, offsetBase + previewStart, previewLength, previews);
        }
    }

    private static void AddPreview(IRawHeaderSource source, long offset, long length, List<EmbeddedPreview> previews)
    {
        if (!TiffHeaderNavigator.IsRangeInFile(offset, length, source.Length) ||
            length < 4 ||
            source.Read(offset, 2) is not [0xFF, 0xD8] ||
            previews.Any(p => p.Offset == offset))
            return;

        previews.Add(new EmbeddedPreview(
            Index: previews.Count,
            Offset: offset,
            Length: length,
            Kind: EmbeddedPreviewKind.Jpeg,
            Width: 0,
            Height: 0,
            ColorSpace: PreviewColorSpace.Unknown));
    }
}
