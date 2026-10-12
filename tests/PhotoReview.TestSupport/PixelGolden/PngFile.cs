using System.Buffers.Binary;
using System.IO.Compression;

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>
/// WP-28: minimal PNG reader/writer for G-PIX frames (8-bit RGB/RGBA, non-interlaced; every row filter type on read). No imaging
/// library on purpose: Shell.Tests has no WPF and the golden must decode the same way on every runner. The writer is
/// deterministic (fixed filter heuristic, fixed compression level) so re-recording on the same WPF build yields the same bytes.
/// </summary>
public static class PngFile
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Write(string path, PixelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, Encode(frame));
    }

    public static byte[] Encode(PixelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rowBytes = frame.Width * 3;
        var raw = new byte[(rowBytes + 1) * frame.Height];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var source = ((y * frame.Width) + x) * 4;
                current[x * 3] = frame.Bgra[source + 2];
                current[(x * 3) + 1] = frame.Bgra[source + 1];
                current[(x * 3) + 2] = frame.Bgra[source];
            }

            var bestFilter = 0;
            var bestCost = long.MaxValue;
            for (var filter = 0; filter <= 4; filter++)
            {
                ApplyFilter(filter, current, previous, candidate);
                long cost = 0;
                foreach (var value in candidate)
                {
                    cost += value < 128 ? value : 256 - value;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestFilter = filter;
                    Buffer.BlockCopy(candidate, 0, best, 0, rowBytes);
                }
            }

            var offset = y * (rowBytes + 1);
            raw[offset] = (byte)bestFilter;
            Buffer.BlockCopy(best, 0, raw, offset + 1, rowBytes);
            (previous, current) = (current, previous);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), frame.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type 2 = RGB (frames are opaque)
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static PixelFrame Read(string path) => Decode(File.ReadAllBytes(path));

    public static PixelFrame Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        var position = Signature.Length;
        int width = 0, height = 0, colourType = -1;
        using var idat = new MemoryStream();
        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            var body = position + 8;
            if (length < 0 || body + length + 4 > data.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk " + type);
            }

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body + 4, 4));
                    if (data[body + 8] != 8 || data[body + 12] != 0)
                    {
                        throw new InvalidDataException("Only 8-bit non-interlaced PNG is supported.");
                    }

                    colourType = data[body + 9];
                    if (colourType is not (2 or 6))
                    {
                        throw new InvalidDataException("Only RGB/RGBA PNG is supported.");
                    }

                    break;
                case "IDAT":
                    idat.Write(data, body, length);
                    break;
            }

            position = body + length + 4;
            if (type == "IEND")
            {
                break;
            }
        }

        if (width < 1 || height < 1 || colourType < 0)
        {
            throw new InvalidDataException("PNG has no IHDR.");
        }

        var channels = colourType == 6 ? 4 : 3;
        var rowBytes = width * channels;
        var raw = new byte[(rowBytes + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            z.ReadExactly(raw);
        }

        var bgra = new byte[width * height * 4];
        var previous = new byte[rowBytes];
        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (rowBytes + 1);
            Unfilter(raw[offset], raw.AsSpan(offset + 1, rowBytes), previous, row, channels);
            for (var x = 0; x < width; x++)
            {
                var target = ((y * width) + x) * 4;
                bgra[target] = row[(x * channels) + 2];
                bgra[target + 1] = row[(x * channels) + 1];
                bgra[target + 2] = row[x * channels];
                bgra[target + 3] = channels == 4 ? row[(x * channels) + 3] : (byte)255;
            }

            (previous, row) = (row, previous);
        }

        return new PixelFrame(width, height, bgra);
    }

    private static void ApplyFilter(int filter, byte[] current, byte[] previous, byte[] output)
    {
        const int Bpp = 3;
        for (var i = 0; i < current.Length; i++)
        {
            int left = i >= Bpp ? current[i - Bpp] : 0;
            int up = previous[i];
            int upLeft = i >= Bpp ? previous[i - Bpp] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                _ => Paeth(left, up, upLeft),
            };
            output[i] = (byte)(current[i] - predictor);
        }
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, byte[] previous, byte[] output, int bpp)
    {
        for (var i = 0; i < source.Length; i++)
        {
            int left = i >= bpp ? output[i - bpp] : 0;
            int up = previous[i];
            int upLeft = i >= bpp ? previous[i - bpp] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException("Unknown PNG filter " + filter),
            };
            output[i] = (byte)(source[i] + predictor);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        var crc = Crc32(typeBytes, 0xFFFFFFFFu);
        crc = Crc32(body, crc) ^ 0xFFFFFFFFu;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes, uint state)
    {
        foreach (var value in bytes)
        {
            state = CrcTable[(state ^ value) & 0xFF] ^ (state >> 8);
        }

        return state;
    }
}
