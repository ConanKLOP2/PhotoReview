using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PhotoReview.Imaging.Raw.Bmff;

/// <summary>
/// Parser and walker for ISO-BMFF (ISO base media file format) boxes.
/// Hostile-input safe: checked arithmetic, a cap on the children returned per box (<see cref="MaxChildBoxes"/>; the
/// CR3 reader walks a fixed, shallow moov/uuid/trak path rather than recursing) and bounds checking against container length.
/// </summary>
public static class BmffBoxNavigator
{
    public readonly record struct BmffBox(
        string Type,
        long Offset,
        long TotalSize,
        long PayloadOffset,
        long PayloadSize,
        byte[]? Uuid = null);

    /// <summary>Maximum number of immediate children returned by <see cref="ReadChildBoxes"/>; further children are ignored.</summary>
    public const int MaxChildBoxes = 256;

    /// <summary>
    /// Reads the next box header at <paramref name="offset"/>.
    /// Returns true if a valid box header was read; false on EOF or invalid structure
    /// (including sizes that would extend past the source, which are compared without overflow).
    /// A size of 0 means "to the end of the enclosing container": <paramref name="containerEnd"/> (default: the end of the
    /// source, i.e. a top-level box); <see cref="ReadChildBoxes"/> passes the parent's payload end.
    /// </summary>
    public static bool TryReadBox(IRawHeaderSource source, long offset, out BmffBox box, long containerEnd = -1)
    {
        box = default;
        long length = source.Length;
        if (offset < 0 || offset > length - 8) return false;

        var headerSpan = source.Read(offset, 8);
        if (headerSpan.Length < 8) return false;

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(headerSpan[..4]);
        string type = Encoding.ASCII.GetString(headerSpan.Slice(4, 4));

        long totalSize = size32;
        long headerSize = 8;
        byte[]? uuid = null;

        if (size32 == 1) // 64-bit largesize
        {
            if (offset > length - 16) return false;
            var largeSpan = source.Read(offset + 8, 8);
            if (largeSpan.Length < 8) return false;
            ulong largeSize = BinaryPrimitives.ReadUInt64BigEndian(largeSpan);
            if (largeSize > long.MaxValue) return false;
            totalSize = (long)largeSize;
            headerSize = 16;
        }
        else if (size32 == 0) // Extends to the end of the enclosing container (EOF for a top-level box)
        {
            long end = containerEnd >= 0 ? Math.Min(containerEnd, length) : length;
            totalSize = end - offset;
        }

        // totalSize > length - offset is the overflow-free form of offset + totalSize > length.
        if (totalSize < headerSize || totalSize > length - offset)
            return false;

        if (type == "uuid")
        {
            // A uuid box must hold its 16-byte extended type after the regular header.
            if (totalSize < headerSize + 16) return false;
            var uuidSpan = source.Read(offset + headerSize, 16);
            if (uuidSpan.Length < 16) return false;
            uuid = uuidSpan.ToArray();
            headerSize += 16;
        }

        long payloadOffset = offset + headerSize;
        long payloadSize = totalSize - headerSize;

        box = new BmffBox(type, offset, totalSize, payloadOffset, payloadSize, uuid);
        return true;
    }

    /// <summary>
    /// Enumerates immediate children boxes within a container box's payload (at most <see cref="MaxChildBoxes"/>).
    /// <paramref name="payloadSkip"/> skips a fixed-size prefix of the payload before the first child
    /// (e.g. the 8-byte header of Canon's preview uuid box). Never throws for malformed sizes; enumeration just stops.
    /// </summary>
    public static List<BmffBox> ReadChildBoxes(IRawHeaderSource source, in BmffBox parent, int payloadSkip = 0)
    {
        var list = new List<BmffBox>();
        long length = source.Length;
        long start = parent.PayloadOffset;
        if (start < 0 || start > length || parent.PayloadSize < 0 || payloadSkip < 0)
            return list;

        // Clamp to the source so the limit itself can never overflow or exceed the file.
        long limit = start + Math.Min(parent.PayloadSize, length - start);
        long current = start;
        if (payloadSkip > limit - current)
            return list;
        current += payloadSkip;

        while (limit - current >= 8 && list.Count < MaxChildBoxes)
        {
            if (!TryReadBox(source, current, out var child, limit))
                break;

            if (child.TotalSize <= 0 || child.TotalSize > limit - current)
                break; // Corrupt box size

            list.Add(child);
            current += child.TotalSize;
        }

        return list;
    }
}
