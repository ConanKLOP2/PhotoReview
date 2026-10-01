using System.Buffers.Binary;
using System.Text;

namespace PhotoReview.Imaging.Metadata;

/// <summary>
/// Low-level TIFF reader and IFD structure parser.
/// Shared across ExifParser and Camera RAW format readers (CR2, NEF, ARW, DNG, ORF, RW2).
/// Hostile-input safe: all offsets and lengths are bounds checked.
/// </summary>
public static class TiffStructure
{
    public const int MaxIfdEntries = 4096;
    public const int MaxAsciiBytes = 256;

    /// <summary>Longest out-of-line non-ASCII value (array or UNDEFINED blob) returned by <see cref="TryGetValueSpan"/>, in bytes.</summary>
    public const int MaxValueBytes = 4096;

    /// <summary>
    /// Reads TIFF header (byte order and first IFD offset).
    /// Returns true if header is valid; otherwise false.
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> tiff, out bool littleEndian, out ushort magic, out uint ifd0Offset)
    {
        littleEndian = true;
        magic = 0;
        ifd0Offset = 0;

        if (tiff.Length < 8) return false;

        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
            littleEndian = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
            littleEndian = false;
        else
            return false;

        magic = ReadU16(tiff, 2, littleEndian);
        ifd0Offset = ReadU32(tiff, 4, littleEndian);
        return true;
    }

    /// <summary>
    /// Computes byte size of a TIFF field type. Returns 0 for unknown types.
    /// </summary>
    public static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1, // BYTE, ASCII, SBYTE, UNDEFINED
        3 or 8 => 2,           // SHORT, SSHORT
        4 or 9 or 11 or 13 => 4, // LONG, SLONG, FLOAT, IFD (TIFF Tech Note 1: SubIFD pointers may be type IFD)
        5 or 10 or 12 => 8,    // RATIONAL, SRATIONAL, DOUBLE
        _ => 0,
    };

    /// <summary>
    /// Reads unsigned 16-bit integer with specified byte order.
    /// </summary>
    public static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset < 0 || offset > data.Length - 2) return 0;
        var slice = data.Slice(offset, 2);
        return littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(slice) : BinaryPrimitives.ReadUInt16BigEndian(slice);
    }

    /// <summary>
    /// Reads unsigned 32-bit integer with specified byte order.
    /// </summary>
    public static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset < 0 || offset > data.Length - 4) return 0;
        var slice = data.Slice(offset, 4);
        return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(slice) : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    /// <summary>
    /// Reads entry's value span (inline or from offset) within a TIFF byte span. The span covers the whole value:
    /// every element of an array, and all bytes of an ASCII (up to <see cref="MaxAsciiBytes"/>) or UNDEFINED
    /// (up to <see cref="MaxValueBytes"/>) value. A text or UNDEFINED value that runs past the end of the block is rejected; an
    /// array is cut to the whole elements that exist. False when not even one element / byte is
    /// readable. Callers that want a scalar read the first element of the span.
    /// </summary>
    public static bool TryGetValueSpan(ReadOnlySpan<byte> tiff, int entryOffset, ushort type, uint count, bool littleEndian, out ReadOnlySpan<byte> value)
    {
        value = default;
        var size = TypeSize(type);
        if (size == 0 || count == 0) return false;
        var total = (long)size * count;
        if (total <= 4)
        {
            if (entryOffset + 8 + (int)total > tiff.Length) return false;
            value = tiff.Slice(entryOffset + 8, (int)total);
            return true;
        }
        var offset = ReadU32(tiff, entryOffset + 8, littleEndian);
        if (offset >= (uint)tiff.Length) return false;

        long available = tiff.Length - offset;
        total = Math.Min(total, type == 2 ? MaxAsciiBytes : MaxValueBytes);
        // Text/blobs must lie fully inside the block (a value running past its end is skipped, as before); a numeric array is
        // cut to the whole elements that exist so its first element stays readable.
        if (type is 2 or 7)
        {
            if (total > available) return false;
        }
        else
        {
            total = Math.Min(total, available / size * size);
        }

        if (total < size) return false;

        value = tiff.Slice((int)offset, (int)total);
        return true;
    }

    /// <summary>
    /// Reads an ASCII string value from an entry.
    /// </summary>
    public static string? ReadAscii(ReadOnlySpan<byte> value, ushort type)
    {
        if (type is not (2 or 7)) return null;
        if (value.Length > MaxAsciiBytes) value = value[..MaxAsciiBytes]; // UNDEFINED values may carry up to MaxValueBytes: keep the text cap of type 2
        var end = value.IndexOf((byte)0);
        if (end >= 0) value = value[..end];
        return value.IsEmpty ? null : Encoding.UTF8.GetString(value);
    }

    /// <summary>
    /// Reads an unsigned integer up to 64-bit from an entry.
    /// </summary>
    public static long? ReadUnsigned(ReadOnlySpan<byte> value, ushort type, bool littleEndian) => type switch
    {
        1 when value.Length >= 1 => value[0],
        3 when value.Length >= 2 => ReadU16(value, 0, littleEndian),
        4 or 13 when value.Length >= 4 => ReadU32(value, 0, littleEndian),
        8 when value.Length >= 2 => (short)ReadU16(value, 0, littleEndian),
        9 when value.Length >= 4 => (int)ReadU32(value, 0, littleEndian),
        _ => null,
    };

    /// <summary>
    /// Reads a rational (numerator / denominator) from an entry.
    /// </summary>
    public static ExifRational? ReadRational(ReadOnlySpan<byte> value, ushort type, bool littleEndian)
    {
        if (value.Length < 8) return null;
        var numerator = ReadU32(value, 0, littleEndian);
        var denominator = ReadU32(value, 4, littleEndian);
        if (type == 10)
        {
            int n = (int)numerator, d = (int)denominator;
            if (n <= 0 || d <= 0) return null;
            return new ExifRational((uint)n, (uint)d);
        }
        return type == 5 ? new ExifRational(numerator, denominator) : null;
    }
}
