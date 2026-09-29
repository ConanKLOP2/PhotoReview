using System.Buffers.Binary;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Test helper that lays out a TIFF-family file byte by byte at explicit offsets, so hostile or unusual
/// structures (huge counts, dangling pointers, relative MakerNote offsets) can be written exactly.
/// </summary>
internal sealed class TiffBytes
{
    internal readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint Value);

    private readonly byte[] _buffer;

    public TiffBytes(bool littleEndian, int size)
    {
        LittleEndian = littleEndian;
        _buffer = new byte[size];
    }

    public bool LittleEndian { get; }

    public byte[] ToArray() => _buffer;

    public TiffBytes Header(uint ifd0Offset, ReadOnlySpan<byte> signature = default)
    {
        ReadOnlySpan<byte> magic = signature.IsEmpty ? (LittleEndian ? "II*\0"u8 : "MM\0*"u8) : signature;
        magic.CopyTo(_buffer);
        U32(4, ifd0Offset);
        return this;
    }

    public TiffBytes Put(int position, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer.AsSpan(position));
        return this;
    }

    public TiffBytes U16(int position, ushort value)
    {
        if (LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(position), value);
        else BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(position), value);
        return this;
    }

    public TiffBytes U32(int position, uint value)
    {
        if (LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position), value);
        else BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(position), value);
        return this;
    }

    /// <summary>Writes an IFD of <paramref name="entries"/> at <paramref name="position"/> followed by the next-IFD pointer.</summary>
    public TiffBytes Ifd(int position, uint nextIfd, params Entry[] entries)
    {
        U16(position, (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            int at = position + 2 + (i * 12);
            var e = entries[i];
            U16(at, e.Tag);
            U16(at + 2, e.Type);
            U32(at + 4, e.Count);
            if (e.Type == 3 && e.Count == 1) U16(at + 8, (ushort)e.Value); // SHORT is left-justified in the value field
            else U32(at + 8, e.Value);
        }

        U32(position + 2 + (entries.Length * 12), nextIfd);
        return this;
    }

    public static Entry Short(ushort tag, uint value) => new(tag, 3, 1, value);

    public static Entry Long(ushort tag, uint value) => new(tag, 4, 1, value);

    /// <summary>Out-of-line entry (array, rational, undefined blob) whose data lives at <paramref name="offset"/>.</summary>
    public static Entry At(ushort tag, ushort type, uint count, uint offset) => new(tag, type, count, offset);

    /// <summary>Copy of <paramref name="baselineJpeg"/> whose first SOF0 marker is rewritten to SOF3 (lossless JPEG).</summary>
    public static byte[] AsLosslessJpeg(byte[] baselineJpeg)
    {
        var copy = (byte[])baselineJpeg.Clone();
        for (int i = 0; i + 1 < copy.Length; i++)
        {
            if (copy[i] == 0xFF && copy[i + 1] == 0xC0)
            {
                copy[i + 1] = 0xC3;
                return copy;
            }
        }

        throw new InvalidOperationException("No SOF0 marker found.");
    }
}
