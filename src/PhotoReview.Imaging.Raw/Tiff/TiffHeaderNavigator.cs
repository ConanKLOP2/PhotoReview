using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Helper for walking TIFF structures safely across streams or random-access header sources.
/// </summary>
public static class TiffHeaderNavigator
{
    public readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset);

    /// <summary>
    /// Reads directory entries from an IFD offset. Enforces <see cref="RawContainerLimits.MaxEntriesPerIfd"/>.
    /// </summary>
    public static List<TiffEntry> ReadIfdEntries(IRawHeaderSource source, long ifdOffset, bool littleEndian, out uint nextIfdOffset)
    {
        nextIfdOffset = 0;
        var list = new List<TiffEntry>();
        if (ifdOffset < 8 || ifdOffset > source.Length - 2) return list;

        var countSpan = source.Read(ifdOffset, 2);
        if (countSpan.Length < 2) return list;

        ushort entryCount = TiffStructure.ReadU16(countSpan, 0, littleEndian);
        int clampedCount = Math.Min((int)entryCount, RawContainerLimits.MaxEntriesPerIfd);

        int bytesNeeded = clampedCount * 12;
        if (ifdOffset + 2 + bytesNeeded > source.Length)
        {
            clampedCount = (int)((source.Length - ifdOffset - 2) / 12);
            if (clampedCount <= 0) return list;
            bytesNeeded = clampedCount * 12;
        }

        var entriesSpan = source.Read(ifdOffset + 2, bytesNeeded);
        for (int i = 0; i < clampedCount; i++)
        {
            int offset = i * 12;
            if (offset + 12 > entriesSpan.Length) break;
            ushort tag = TiffStructure.ReadU16(entriesSpan, offset, littleEndian);
            ushort type = TiffStructure.ReadU16(entriesSpan, offset + 2, littleEndian);
            uint count = TiffStructure.ReadU32(entriesSpan, offset + 4, littleEndian);
            uint valOrOffset = TiffStructure.ReadU32(entriesSpan, offset + 8, littleEndian);
            list.Add(new TiffEntry(tag, type, count, valOrOffset));
        }

        // Read next IFD offset (4 bytes after all entries). An IFD claiming more entries than we accept is
        // malformed, so its trailing pointer cannot be located and the chain ends here.
        if (entryCount <= RawContainerLimits.MaxEntriesPerIfd)
        {
            long nextOffsetPos = ifdOffset + 2 + (entryCount * 12L);
            if (nextOffsetPos + 4 <= source.Length)
            {
                var nextSpan = source.Read(nextOffsetPos, 4);
                if (nextSpan.Length == 4)
                {
                    uint next = TiffStructure.ReadU32(nextSpan, 0, littleEndian);
                    // A next-IFD pointer must land after the TIFF header with room for at least an empty IFD
                    // (count + next pointer = 6 bytes); anything else ends the chain.
                    nextIfdOffset = next >= 8 && next <= source.Length - 6 ? next : 0;
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Reads tag value as unsigned integer.
    /// </summary>
    public static long? ReadTagUnsigned(IRawHeaderSource source, in TiffEntry entry, bool littleEndian)
    {
        int typeSize = TiffStructure.TypeSize(entry.Type);
        if (typeSize == 0 || entry.Count == 0) return null;

        if (typeSize * entry.Count <= 4)
        {
            // Inline value
            Span<byte> buf = stackalloc byte[4];
            if (littleEndian)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, entry.ValueOrOffset);
            else
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, entry.ValueOrOffset);

            return TiffStructure.ReadUnsigned(buf, entry.Type, littleEndian);
        }

        // Value stored at offset
        long valOffset = entry.ValueOrOffset;
        if (valOffset < 0 || valOffset + typeSize > source.Length) return null;

        var valSpan = source.Read(valOffset, typeSize);
        return TiffStructure.ReadUnsigned(valSpan, entry.Type, littleEndian);
    }

    /// <summary>
    /// Reads an array of unsigned integers from a tag.
    /// </summary>
    public static List<long> ReadTagUnsignedArray(IRawHeaderSource source, in TiffEntry entry, bool littleEndian, int maxItems = 64)
    {
        var result = new List<long>();
        int typeSize = TiffStructure.TypeSize(entry.Type);
        if (typeSize == 0 || entry.Count == 0) return result;

        if (maxItems <= 0) return result;

        // entry.Count is untrusted (up to uint.MaxValue): clamp in unsigned space so the narrowing cannot go negative.
        int count = (int)Math.Min(entry.Count, (uint)maxItems);
        long totalBytes = (long)typeSize * entry.Count;

        if (totalBytes <= 4)
        {
            Span<byte> buf = stackalloc byte[4];
            if (littleEndian)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, entry.ValueOrOffset);
            else
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, entry.ValueOrOffset);

            for (int i = 0; i < count; i++)
            {
                var slice = buf.Slice(i * typeSize, typeSize);
                if (ReadArrayItem(slice, entry.Type, littleEndian) is { } val)
                    result.Add(val);
            }
            return result;
        }

        long valOffset = entry.ValueOrOffset;
        if (valOffset < 0 || valOffset + (count * typeSize) > source.Length) return result;

        var span = source.Read(valOffset, count * typeSize);
        for (int i = 0; i < count; i++)
        {
            if ((i + 1) * typeSize > span.Length) break;
            var slice = span.Slice(i * typeSize, typeSize);
            if (ReadArrayItem(slice, entry.Type, littleEndian) is { } val)
                result.Add(val);
        }

        return result;
    }

    // Tags ExifParser.TryParseTiff actually reads: IFD0 Make/Model/DateTime/ExifIFD pointer, then the Exif IFD values below.
    private static readonly ushort[] ExifSummaryIfd0Tags = [0x010F, 0x0110, 0x0132, 0x8769];
    private static readonly ushort[] ExifSummaryExifTags = [0x829A, 0x829D, 0x8827, 0x9003, 0x9004, 0x920A, 0xA434];

    /// <summary>
    /// Sizes the TIFF-header EXIF block. It always starts at 0 (ExifParser offsets are relative to the TIFF header) and is at
    /// least min(file, <see cref="RawContainerLimits.DefaultExifBlockBytes"/>); it grows to cover IFD0, the Exif IFD and the
    /// out-of-line values of the tags the summary reads when a writer placed them after the pixel data, bounded by
    /// <see cref="RawContainerLimits.MaxExifBlockBytes"/> and the file length.
    /// </summary>
    public static ExifBlock ComputeExifBlock(IRawHeaderSource source, bool littleEndian, long ifd0Offset)
    {
        long length = Math.Min(source.Length, RawContainerLimits.DefaultExifBlockBytes);
        try
        {
            long needed = ExifSummaryExtent(source, ifd0Offset, littleEndian, ExifSummaryIfd0Tags, out uint exifPointer);
            if (exifPointer > 0)
                needed = Math.Max(needed, ExifSummaryExtent(source, exifPointer, littleEndian, ExifSummaryExifTags, out _));
            // Grow only when the whole extent fits: a block clamped below `needed` still cannot hold the structure (offsets are
            // relative to 0), it would just make RawExif read and charge up to 4 MiB of header budget for no EXIF.
            if (needed <= Math.Min(source.Length, RawContainerLimits.MaxExifBlockBytes)) length = Math.Max(length, needed);
        }
        catch (InvalidDataException)
        {
            // Exhausted header budget or hostile structure: keep the default block.
        }

        return new ExifBlock(0, length, IsTiffHeader: true);
    }

    private static long ExifSummaryExtent(IRawHeaderSource source, long ifdOffset, bool littleEndian, ushort[] wantedTags, out uint exifPointer)
    {
        exifPointer = 0;
        var entries = ReadIfdEntries(source, ifdOffset, littleEndian, out _);
        if (entries.Count == 0) return 0;

        long end = ifdOffset + 2 + (entries.Count * 12L) + 4;
        foreach (var entry in entries)
        {
            if (entry.Tag == 0x8769) exifPointer = entry.ValueOrOffset;
            if (Array.IndexOf(wantedTags, entry.Tag) < 0) continue;

            long bytes = (long)TiffStructure.TypeSize(entry.Type) * entry.Count;
            if (bytes > 4) end = Math.Max(end, entry.ValueOrOffset + bytes);
        }

        return end;
    }

    /// <summary>
    /// Finds the first entry with <paramref name="tag"/>.
    /// </summary>
    public static bool TryGetEntry(IReadOnlyList<TiffEntry> entries, ushort tag, out TiffEntry entry)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Tag == tag)
            {
                entry = entries[i];
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// True when [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) lies inside a file of
    /// <paramref name="fileLength"/> bytes (overflow-safe, offset 0 is never a valid payload start).
    /// </summary>
    public static bool IsRangeInFile(long offset, long length, long fileLength) =>
        offset > 0 && length > 0 && offset <= fileLength - length;

    /// <summary>
    /// Reads StripOffsets (0x0111) / StripByteCounts (0x0117) when the image is stored as exactly one strip.
    /// A JPEG payload split over several strips cannot be exposed as a single byte range, so multi-strip
    /// images are rejected instead of silently using only the first strip.
    /// </summary>
    public static bool TryReadSingleStrip(
        IRawHeaderSource source,
        IReadOnlyList<TiffEntry> entries,
        bool littleEndian,
        out long offset,
        out long length)
    {
        offset = 0;
        length = 0;
        if (!TryGetEntry(entries, 0x0111, out var offsetEntry) || !TryGetEntry(entries, 0x0117, out var lengthEntry))
            return false;
        if (offsetEntry.Count != 1 || lengthEntry.Count != 1)
            return false;

        if (ReadTagUnsigned(source, offsetEntry, littleEndian) is not { } strip ||
            ReadTagUnsigned(source, lengthEntry, littleEndian) is not { } bytes)
            return false;

        if (!IsRangeInFile(strip, bytes, source.Length))
            return false;

        offset = strip;
        length = bytes;
        return true;
    }

    /// <summary>
    /// Reads the pixel size stored in an Exif IFD (PixelXDimension 0xA002 / PixelYDimension 0xA003). Both outputs are 0 unless
    /// the method returns true.
    /// </summary>
    public static bool TryReadExifPixelDimensions(
        IRawHeaderSource source,
        long exifIfdOffset,
        bool littleEndian,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        int foundWidth = 0;
        int foundHeight = 0;
        var entries = ReadIfdEntries(source, exifIfdOffset, littleEndian, out _);
        foreach (var entry in entries)
        {
            if (entry.Tag == 0xA002 && ReadTagUnsigned(source, entry, littleEndian) is { } w and > 0 and <= int.MaxValue)
                foundWidth = (int)w;
            else if (entry.Tag == 0xA003 && ReadTagUnsigned(source, entry, littleEndian) is { } h and > 0 and <= int.MaxValue)
                foundHeight = (int)h;
        }

        // Out-params are only assigned on success: a width-only Exif IFD must not leave a half-set size behind.
        if (foundWidth <= 0 || foundHeight <= 0) return false;
        width = foundWidth;
        height = foundHeight;
        return true;
    }

    /// <summary>
    /// Reads DNG-style DefaultCropSize (0xC620: width, height as SHORT/LONG/RATIONAL) from an IFD; the crop size is the
    /// active image area without the masked sensor margins that ImageWidth/ImageLength include.
    /// </summary>
    public static bool TryReadDefaultCropSize(
        IRawHeaderSource source,
        IReadOnlyList<TiffEntry> entries,
        bool littleEndian,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (!TryGetEntry(entries, 0xC620, out var entry)) return false;

        var crop = ReadTagUnsignedArray(source, entry, littleEndian, 2);
        if (crop.Count < 2 || crop[0] is <= 0 or > int.MaxValue || crop[1] is <= 0 or > int.MaxValue) return false;

        width = (int)crop[0];
        height = (int)crop[1];
        return true;
    }

    /// <summary>
    /// False when the IFD carries a DNG DefaultScale (0xC61E: two RATIONALs, horizontal and vertical pixel scale) that is
    /// not 1:1 (non-square pixels, or an unreadable/zero-denominator value). Absent means square.
    /// </summary>
    public static bool HasSquareDefaultScale(IRawHeaderSource source, IReadOnlyList<TiffEntry> entries, bool littleEndian)
    {
        if (!TryGetEntry(entries, 0xC61E, out var entry)) return true;
        if (entry.Type != 5 || entry.Count < 2 || entry.ValueOrOffset < 8 || entry.ValueOrOffset > source.Length - 16) return false;

        var span = source.Read(entry.ValueOrOffset, 16);
        if (span.Length < 16) return false;
        uint horizontalNumerator = TiffStructure.ReadU32(span, 0, littleEndian);
        uint horizontalDenominator = TiffStructure.ReadU32(span, 4, littleEndian);
        uint verticalNumerator = TiffStructure.ReadU32(span, 8, littleEndian);
        uint verticalDenominator = TiffStructure.ReadU32(span, 12, littleEndian);
        if (horizontalDenominator == 0 || verticalDenominator == 0 || horizontalNumerator == 0 || verticalNumerator == 0) return false;

        // Equal ratios (h/hd == v/vd), cross-multiplied in 64 bits.
        return (ulong)horizontalNumerator * verticalDenominator == (ulong)verticalNumerator * horizontalDenominator;
    }

    /// <summary>
    /// Picks the active sensor size: DefaultCropSize, else the Exif pixel size, else the raw IFD size. A candidate
    /// larger than the raw IFD in either direction is ignored (masked margins can only shrink the image).
    /// </summary>
    public static (int Width, int Height) ChooseActiveSensorSize(
        int rawWidth, int rawHeight, int cropWidth, int cropHeight, int exifWidth, int exifHeight)
    {
        if (Fits(cropWidth, cropHeight)) return (cropWidth, cropHeight);
        if (Fits(exifWidth, exifHeight)) return (exifWidth, exifHeight);
        return (rawWidth, rawHeight);

        bool Fits(int w, int h) => w > 0 && h > 0 && (rawWidth <= 0 || rawHeight <= 0 || (w <= rawWidth && h <= rawHeight));
    }

    /// <summary>
    /// Reads a single unsigned value for <paramref name="tag"/> from <paramref name="entries"/>.
    /// </summary>
    public static long? ReadTagValue(IRawHeaderSource source, IReadOnlyList<TiffEntry> entries, ushort tag, bool littleEndian) =>
        TryGetEntry(entries, tag, out var entry) ? ReadTagUnsigned(source, entry, littleEndian) : null;

    /// <summary>
    /// Reads ImageWidth (0x0100) / ImageLength (0x0101); missing or absurd values yield 0.
    /// </summary>
    public static void ReadImageSize(IRawHeaderSource source, IReadOnlyList<TiffEntry> entries, bool littleEndian, out int width, out int height)
    {
        width = ClampToInt(ReadTagValue(source, entries, 0x0100, littleEndian));
        height = ClampToInt(ReadTagValue(source, entries, 0x0101, littleEndian));
    }

    /// <summary>
    /// Reads JPEGInterchangeFormat (0x0201) / JPEGInterchangeFormatLength (0x0202) and requires the byte range to fit the file.
    /// </summary>
    public static bool TryReadJpegInterchange(
        IRawHeaderSource source,
        IReadOnlyList<TiffEntry> entries,
        bool littleEndian,
        out long offset,
        out long length)
    {
        offset = 0;
        length = 0;
        if (ReadTagValue(source, entries, 0x0201, littleEndian) is not { } start ||
            ReadTagValue(source, entries, 0x0202, littleEndian) is not { } bytes)
            return false;

        if (!IsRangeInFile(start, bytes, source.Length) || !StartsWithSoi(source, start, bytes)) return false;

        offset = start;
        length = bytes;
        return true;
    }

    /// <summary>True when the byte range starts with the JPEG SOI marker (FFD8); a zero-padded or garbage pointer is not a preview.</summary>
    public static bool StartsWithSoi(IRawHeaderSource source, long offset, long length) =>
        length >= 4 && IsRangeInFile(offset, length, source.Length) && source.Read(offset, 2) is [0xFF, 0xD8];

    /// <summary>
    /// A declared IFD ImageWidth/ImageLength is only a hint for an embedded JPEG. When both are declared, the JPEG's own
    /// frame header is consulted and, if it is readable and disagrees, its size wins (a bogus IFD size must not win the
    /// "largest preview" choice). Undeclared sizes stay 0 so <see cref="PreviewSelector"/> resolves them lazily.
    /// </summary>
    public static void ReconcileJpegSize(IRawHeaderSource source, long offset, long length, ref int width, ref int height)
    {
        if (width <= 0 || height <= 0) return;
        if (PreviewSelector.TryReadJpegFrame(source, offset, length, out int frameWidth, out int frameHeight, out _))
        {
            width = frameWidth;
            height = frameHeight;
        }
    }

    /// <summary>Positive values clamped to <see cref="int.MaxValue"/>; null, zero and negative yield 0 (never wraps to a negative int).</summary>
    public static int ClampToInt(long? value) => value is > 0 ? (int)Math.Min(value.Value, int.MaxValue) : 0;

    private static long? ReadArrayItem(ReadOnlySpan<byte> item, ushort type, bool littleEndian)
    {
        // RATIONAL (e.g. DNG DefaultCropSize): the caller wants whole pixels, so round num/den.
        if (type == 5)
        {
            if (item.Length < 8) return null;
            uint numerator = TiffStructure.ReadU32(item, 0, littleEndian);
            uint denominator = TiffStructure.ReadU32(item, 4, littleEndian);
            if (denominator == 0) return null;
            return (long)Math.Round((double)numerator / denominator, MidpointRounding.AwayFromZero);
        }

        return TiffStructure.ReadUnsigned(item, type, littleEndian);
    }
}
