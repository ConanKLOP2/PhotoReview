using System.Buffers.Binary;
using System.Text;

namespace PhotoReview.TestSupport;

/// <summary>
/// Builds tiny, valid WebP files in memory (no encoder needed: Windows ships only a WebP decoder). The pixels are coded
/// as lossless VP8L with "simple" prefix codes, which limits every channel (A, R, G, B) to at most two distinct values
/// per image -- enough for orientation markers, transparency and per-frame colours. Files: plain (RIFF + VP8L),
/// extended (VP8X + VP8L + optional EXIF orientation) and animated (VP8X + ANIM + ANMF frames).
/// </summary>
public static class WebPTestImage
{
    /// <summary>ARGB pixel (0xAARRGGBB).</summary>
    public delegate uint PixelAt(int x, int y);

    /// <summary>Plain lossless WebP (simple format: RIFF + VP8L).</summary>
    public static byte[] Lossless(int width, int height, PixelAt pixel)
    {
        var vp8l = EncodeVp8L(width, height, pixel, out _);
        return Riff(Chunk("VP8L", vp8l));
    }

    /// <summary>Extended WebP (VP8X) with an EXIF chunk carrying <paramref name="exifOrientation"/> (1-8).</summary>
    public static byte[] WithExifOrientation(int width, int height, PixelAt pixel, ushort exifOrientation)
    {
        var vp8l = EncodeVp8L(width, height, pixel, out var hasAlpha);
        var flags = (byte)(0x08 | (hasAlpha ? 0x10 : 0)); // EXIF (+ ALPHA)
        return Riff(Vp8X(flags, width, height), Chunk("VP8L", vp8l), Chunk("EXIF", TiffWithOrientation(exifOrientation)));
    }

    /// <summary>Animated WebP: every frame fills the whole canvas with its own ARGB colour.</summary>
    public static byte[] Animated(int width, int height, params uint[] frameColours)
    {
        ArgumentNullException.ThrowIfNull(frameColours);
        var chunks = new List<byte[]>
        {
            Vp8X(0x02 | 0x10, width, height), // ANIMATION | ALPHA
            Chunk("ANIM", [0, 0, 0, 0, 0, 0]), // background colour, loop count 0 (infinite)
        };
        foreach (var colour in frameColours)
        {
            var vp8l = EncodeVp8L(width, height, (_, _) => colour, out _);
            var anmf = new List<byte>();
            anmf.AddRange(UInt24(0)); // X / 2
            anmf.AddRange(UInt24(0)); // Y / 2
            anmf.AddRange(UInt24(width - 1));
            anmf.AddRange(UInt24(height - 1));
            anmf.AddRange(UInt24(100)); // duration ms
            anmf.Add(0x02); // do not blend, no dispose
            anmf.AddRange(Chunk("VP8L", vp8l));
            chunks.Add(Chunk("ANMF", [.. anmf]));
        }

        return Riff([.. chunks]);
    }

    private static byte[] Vp8X(byte flags, int width, int height)
    {
        var payload = new List<byte> { flags, 0, 0, 0 };
        payload.AddRange(UInt24(width - 1));
        payload.AddRange(UInt24(height - 1));
        return Chunk("VP8X", [.. payload]);
    }

    private static byte[] TiffWithOrientation(ushort orientation)
    {
        var tiff = new byte[26];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8); // IFD0 offset
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1); // one entry
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3); // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1); // count
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        // bytes 22..25: next IFD offset 0
        return tiff;
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var body = chunks.SelectMany(c => c).ToArray();
        var file = new byte[12 + body.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(file, 8);
        body.CopyTo(file, 12);
        return file;
    }

    private static byte[] Chunk(string fourCc, byte[] payload)
    {
        var padded = payload.Length + (payload.Length & 1);
        var chunk = new byte[8 + padded];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] UInt24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];

    /// <summary>VP8L bitstream: no transforms, no colour cache, one prefix-code group of simple (1- or 2-symbol) codes.</summary>
    private static byte[] EncodeVp8L(int width, int height, PixelAt pixel, out bool hasAlpha)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = pixel(x, y);

        // Channel order of the VP8L prefix-code group: green, red, blue, alpha (then distance).
        int[] shifts = [8, 16, 0, 24];
        var symbols = shifts.Select(shift => pixels.Select(p => (byte)(p >> shift)).Distinct().Order().ToArray()).ToArray();
        if (symbols.Any(s => s.Length > 2))
            throw new ArgumentException("Each channel may use at most two distinct values (simple prefix codes).", nameof(pixel));
        hasAlpha = symbols[3].Any(a => a != 255);

        var bits = new BitWriter();
        bits.Write(0x2F, 8); // signature
        bits.Write((uint)(width - 1), 14);
        bits.Write((uint)(height - 1), 14);
        bits.Write(hasAlpha ? 1u : 0u, 1);
        bits.Write(0, 3); // version
        bits.Write(0, 1); // no transform
        bits.Write(0, 1); // no colour cache
        bits.Write(0, 1); // no meta prefix codes
        foreach (var channel in symbols) WriteSimpleCode(bits, channel);
        WriteSimpleCode(bits, [0]); // distance code (unused)

        foreach (var p in pixels)
        {
            for (var c = 0; c < 4; c++)
            {
                if (symbols[c].Length == 2) bits.Write((byte)(p >> shifts[c]) == symbols[c][1] ? 1u : 0u, 1);
            }
        }

        return bits.ToArray();
    }

    private static void WriteSimpleCode(BitWriter bits, byte[] sortedSymbols)
    {
        bits.Write(1, 1); // simple code
        bits.Write((uint)(sortedSymbols.Length - 1), 1);
        var first = sortedSymbols[0];
        if (first <= 1)
        {
            bits.Write(0, 1);
            bits.Write(first, 1);
        }
        else
        {
            bits.Write(1, 1);
            bits.Write(first, 8);
        }

        if (sortedSymbols.Length == 2) bits.Write(sortedSymbols[1], 8);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _used;

        public void Write(uint value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (((value >> i) & 1) != 0) _current |= 1 << _used;
                if (++_used == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = 0;
                    _used = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            var result = new List<byte>(_bytes);
            if (_used > 0) result.Add((byte)_current);
            result.AddRange(new byte[4]); // slack: decoders may prefetch past the last code
            return [.. result];
        }
    }
}
