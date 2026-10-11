using System.Buffers.Binary;
using System.IO.Compression;

namespace PhotoReview.Shell.Integration.Tests.Input;

/// <summary>
/// WP-22: PNG xám 8 bit kích thước tuỳ ý (gradient x ^ y) viết bằng BCL (ZLibStream), để test nhập liệu có ảnh LỚN hơn viewport
/// (zoom/cuộn) mà không cần WPF/System.Drawing và không nhúng tệp lớn. Giải mã bằng WIC như mọi PNG của người dùng.
/// </summary>
internal static class TestPng
{
    public static string WriteTo(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        WriteChunk(stream, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1]; // byte đầu = filter None
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    row[x + 1] = (byte)((x ^ y) >> 2);
                }

                zlib.Write(row);
            }
        }

        WriteChunk(stream, "IDAT", raw.ToArray());
        WriteChunk(stream, "IEND", []);
        return path;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes, uint crc)
    {
        foreach (var b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
