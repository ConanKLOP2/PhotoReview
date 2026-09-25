using System.Buffers.Binary;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>Small synthetic JPEG-header byte builders and deterministic mutators shared by the parser fuzz/oracle tests.</summary>
internal static class JpegBytes
{
    public static readonly byte[] IccTag = "ICC_PROFILE\0"u8.ToArray();
    public static readonly byte[] ExifTag = "Exif\0\0"u8.ToArray();

    public static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    /// <summary>TIFF block: header + IFD0 with the given (tag, type, count, 4 value bytes) entries; no out-of-line data.</summary>
    public static byte[] Tiff(bool little, params (ushort Tag, ushort Type, uint Count, byte[] Value)[] entries)
    {
        var bytes = new List<byte>();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v); bytes.AddRange(b.ToArray()); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); bytes.AddRange(b.ToArray()); }
        bytes.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(8);
        U16((ushort)entries.Length);
        foreach (var (tag, type, count, value) in entries)
        {
            U16(tag); U16(type); U32(count);
            var padded = new byte[4];
            value.AsSpan(0, Math.Min(4, value.Length)).CopyTo(padded);
            bytes.AddRange(padded);
        }
        U32(0);
        return [.. bytes];
    }

    /// <summary>A SHORT orientation value stored the way a real writer does (left-justified in the 4-byte field, file byte order).</summary>
    public static byte[] OrientationTiff(bool little, int orientation)
    {
        var value = new byte[4];
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)orientation); else BinaryPrimitives.WriteUInt16BigEndian(value, (ushort)orientation);
        return Tiff(little, (0x0112, 3, 1, value));
    }

    /// <summary>SOI, one Exif APP1 carrying <paramref name="tiff"/>, SOS.</summary>
    public static byte[] ExifApp1Jpeg(byte[] tiff) => [0xFF, 0xD8, .. ExifApp1(tiff), 0xFF, 0xDA, 0, 2];

    public static byte[] ExifApp1(byte[] tiff) => Segment(0xE1, [.. ExifTag, .. tiff]);

    public static byte[] Bytes(Random rng, int count)
    {
        var b = new byte[count];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Applies 1..3 random structural or bit-level mutations; never returns the input instance.</summary>
    public static byte[] Mutate(Random rng, byte[] input)
    {
        var data = new List<byte>(input);
        var rounds = rng.Next(1, 4);
        for (var r = 0; r < rounds; r++)
        {
            if (data.Count == 0) { data.Add((byte)rng.Next(256)); continue; }
            switch (rng.Next(9))
            {
                case 0: data[rng.Next(data.Count)] = (byte)rng.Next(256); break;
                case 1: data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8)); break;
                case 2: { var pos = rng.Next(data.Count); data.RemoveRange(pos, Math.Min(rng.Next(1, 9), data.Count - pos)); break; }
                case 3: data.InsertRange(rng.Next(data.Count + 1), Bytes(rng, rng.Next(1, 9))); break;
                case 4: { var keep = rng.Next(data.Count + 1); data.RemoveRange(keep, data.Count - keep); break; } // truncate
                case 5: data[rng.Next(data.Count)] = 0xFF; break;
                case 6: data[rng.Next(data.Count)] = 0x00; break;
                case 7: // duplicate a slice somewhere else (offsets/loops)
                {
                    var start = rng.Next(data.Count);
                    var len = Math.Min(rng.Next(1, 24), data.Count - start);
                    data.InsertRange(rng.Next(data.Count + 1), data.GetRange(start, len));
                    break;
                }
                default: // extreme 16/32-bit field
                {
                    var pos = rng.Next(data.Count);
                    byte[] extreme = rng.Next(3) switch { 0 => [0xFF, 0xFF], 1 => [0x7F, 0xFF], _ => [0x00, 0x00] };
                    for (var i = 0; i < extreme.Length && pos + i < data.Count; i++) data[pos + i] = extreme[i];
                    break;
                }
            }
        }
        return [.. data];
    }
}
