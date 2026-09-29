using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Sony ARW format (TIFF container with IFD0 JPEG preview in 0x0201/0x0202 and IFD1 thumbnail;
/// SubIFD JPEGs and the MakerNote PreviewImage (0x2001) are picked up too).
/// </summary>
public sealed class ArwContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Arw;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".arw", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        // Sony ARW is little-endian TIFF (II*\0)
        return first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 &&
               first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for ARW container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid ARW TIFF header.");

        int orientation = 1;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var pendingIfds = new Queue<long>();
        pendingIfds.Enqueue(ifd0Offset);
        var visited = new HashSet<long>();
        long? makerNoteOffset = null;
        int ifdCount = 0;
        long largestArea = 0;
        int sensorWidth = 0;
        int sensorHeight = 0;

        // IFD0 chain (IFD0 = large preview, IFD1 = thumbnail) then SubIFDs (raw image, sometimes a JPEG).
        while (pendingIfds.Count > 0 && ifdCount < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();
            long ifdOffset = pendingIfds.Dequeue();
            if (ifdOffset <= 0 || !visited.Add(ifdOffset)) continue;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdOffset, littleEndian, out uint nextIfdOffset);
            bool isIfd0 = ifdCount == 0;
            ifdCount++;

            TiffHeaderNavigator.ReadImageSize(source, entries, littleEndian, out int width, out int height);

            if (isIfd0 && TiffHeaderNavigator.ReadTagValue(source, entries, 0x0112, littleEndian) is >= 1 and <= 8 and var o)
                orientation = (int)o;

            if (TiffHeaderNavigator.TryGetEntry(entries, 0x014A, out var subIfdEntry))
            {
                foreach (long sub in TiffHeaderNavigator.ReadTagUnsignedArray(source, subIfdEntry, littleEndian))
                    pendingIfds.Enqueue(sub);
            }

            if (TiffHeaderNavigator.TryGetEntry(entries, 0x8769, out var exifEntry) &&
                TiffHeaderNavigator.ReadTagUnsigned(source, exifEntry, littleEndian) is { } exifOffset && exifOffset > 0)
            {
                foreach (var exifTag in TiffHeaderNavigator.ReadIfdEntries(source, exifOffset, littleEndian, out _))
                {
                    if (exifTag.Tag == 0x927C && exifTag.Count > 4)
                        makerNoteOffset ??= exifTag.ValueOrOffset;
                }
            }

            bool isPreview = TiffHeaderNavigator.TryReadJpegInterchange(source, entries, littleEndian, out long jpegOffset, out long jpegLength);
            if (isPreview)
                AddPreview(previews, jpegOffset, jpegLength, width, height);

            // The sensor size is the largest IFD that is not a JPEG preview (Sony raw SubIFD).
            long area = (long)width * height;
            if (!isPreview && area > largestArea)
            {
                largestArea = area;
                sensorWidth = width;
                sensorHeight = height;
            }

            if (nextIfdOffset > 0)
                pendingIfds.Enqueue(nextIfdOffset);
        }

        if (makerNoteOffset is > 0)
            ParseSonyMakerNotePreview(source, makerNoteOffset.Value, littleEndian, previews);

        exifBlocks.Add(new ExifBlock(0, Math.Min(source.Length, 128 * 1024), IsTiffHeader: true));

        return new RawContainerInfo(
            RawFormat.Arw,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }

    private static void AddPreview(List<EmbeddedPreview> previews, long offset, long length, int width, int height)
    {
        if (previews.Any(p => p.Offset == offset)) return;
        previews.Add(new EmbeddedPreview(
            Index: previews.Count,
            Offset: offset,
            Length: length,
            Kind: EmbeddedPreviewKind.Jpeg,
            Width: width,
            Height: height,
            ColorSpace: PreviewColorSpace.Unknown));
    }

    /// <summary>
    /// Sony MakerNote tag 0x2001 (PreviewImage): UNDEFINED bytes, Count = length, offset relative to the TIFF header.
    /// Some bodies prefix the note with "SONY DSC \0\0\0" (IFD at +12); offsets stay file-absolute either way.
    /// </summary>
    private static void ParseSonyMakerNotePreview(IRawHeaderSource source, long noteOffset, bool littleEndian, List<EmbeddedPreview> previews)
    {
        if (noteOffset < 8 || noteOffset > source.Length - 14) return;

        long ifdStart = noteOffset;
        if (source.Read(noteOffset, 9).SequenceEqual("SONY DSC "u8))
            ifdStart = noteOffset + 12;

        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdStart, littleEndian, out _);
        if (!TiffHeaderNavigator.TryGetEntry(entries, 0x2001, out var previewEntry) || previewEntry.Count <= 4)
            return;

        long start = previewEntry.ValueOrOffset;
        long length = previewEntry.Count;
        if (!TiffHeaderNavigator.IsRangeInFile(start, length, source.Length) ||
            length < 4 ||
            source.Read(start, 2) is not [0xFF, 0xD8])
            return;

        AddPreview(previews, start, length, 0, 0);
    }
}
