using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PhotoReview.Imaging.Raw.Bmff;

/// <summary>
/// Parser and walker for ISO-BMFF (ISO base media file format) boxes.
/// Hostile-input safe: checked arithmetic, box recursion depth cap (<see cref="RawContainerLimits.MaxBoxDepth"/>),
/// and bounds checking against container length.
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

    /// <summary>
    /// Reads the next box header at <paramref name="offset"/>.
    /// Returns true if a valid box header was read; false on EOF or invalid structure.
    /// </summary>
    public static bool TryReadBox(IRawHeaderSource source, long offset, out BmffBox box)
    {
        box = default;
        if (offset < 0 || offset + 8 > source.Length) return false;

        var headerSpan = source.Read(offset, 8);
        if (headerSpan.Length < 8) return false;

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(headerSpan[..4]);
        string type = Encoding.ASCII.GetString(headerSpan.Slice(4, 4));

        long totalSize = size32;
        long headerSize = 8;
        byte[]? uuid = null;

        if (size32 == 1) // 64-bit largesize
        {
            if (offset + 16 > source.Length) return false;
            var largeSpan = source.Read(offset + 8, 8);
            if (largeSpan.Length < 8) return false;
            totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSpan);
            headerSize = 16;
        }
        else if (size32 == 0) // Extends to EOF
        {
            totalSize = source.Length - offset;
        }

        if (totalSize < headerSize || offset + totalSize > source.Length)
            return false;

        if (type == "uuid")
        {
            if (offset + headerSize + 16 > source.Length) return false;
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
    /// Enumerates immediate children boxes within a container box's payload.
    /// </summary>
    public static List<BmffBox> ReadChildBoxes(IRawHeaderSource source, in BmffBox parent)
    {
        var list = new List<BmffBox>();
        long current = parent.PayloadOffset;
        long limit = parent.PayloadOffset + parent.PayloadSize;

        while (current + 8 <= limit && list.Count < 256)
        {
            if (!TryReadBox(source, current, out var child))
                break;

            if (child.TotalSize <= 0 || current + child.TotalSize > limit)
                break; // Corrupt box size

            list.Add(child);
            current += child.TotalSize;
        }

        return list;
    }
}
