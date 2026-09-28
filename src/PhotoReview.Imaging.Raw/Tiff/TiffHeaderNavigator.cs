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

        // Read next IFD offset (4 bytes after all entries)
        long nextOffsetPos = ifdOffset + 2 + (entryCount * 12L);
        if (nextOffsetPos + 4 <= source.Length)
        {
            var nextSpan = source.Read(nextOffsetPos, 4);
            if (nextSpan.Length == 4)
            {
                nextIfdOffset = TiffStructure.ReadU32(nextSpan, 0, littleEndian);
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

        int count = Math.Min((int)entry.Count, maxItems);
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
                if (TiffStructure.ReadUnsigned(slice, entry.Type, littleEndian) is { } val)
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
            if (TiffStructure.ReadUnsigned(slice, entry.Type, littleEndian) is { } val)
                result.Add(val);
        }

        return result;
    }
}
