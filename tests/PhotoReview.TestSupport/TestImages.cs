using System.IO.Compression;

namespace PhotoReview.TestSupport;

public static class TestImages
{
    // Declared first: static initializers run in textual order and BuildPng below needs it.
    private static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    // A valid, tiny PNG keeps decode fixtures portable while exercising WPF's real decoder.
    // NOTE: this one is grayscale+alpha (color type 4), so WPF decodes it with an alpha format.
    public static readonly byte[] PreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    /// <summary>64x64 RGBA PNG: a half-transparent (alpha 128) red square on a fully transparent background.</summary>
    public static readonly byte[] TransparentPng = BuildPng(64, 64, hasAlpha: true);

    /// <summary>64x64 opaque RGB PNG (color type 2, no alpha channel, no tRNS).</summary>
    public static readonly byte[] OpaquePng = BuildPng(64, 64, hasAlpha: false);

    private static byte[] BuildPng(int width, int height, bool hasAlpha)
    {
        var bytesPerPixel = hasAlpha ? 4 : 3;
        var raw = new byte[(1 + width * bytesPerPixel) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * bytesPerPixel); // filter byte 0 (None) stays zero
            for (var x = 0; x < width; x++)
            {
                var inSquare = x >= 16 && x < 48 && y >= 16 && y < 48;
                var o = row + 1 + x * bytesPerPixel;
                raw[o] = inSquare ? (byte)255 : (byte)(x * 4);
                raw[o + 1] = inSquare ? (byte)0 : (byte)(y * 4);
                raw[o + 2] = 0;
                if (hasAlpha) raw[o + 3] = inSquare ? (byte)128 : (byte)0;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = hasAlpha ? (byte)6 : (byte)2; // color type

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var buffer = new byte[4];
        WriteBigEndian(buffer, 0, data.Length);
        stream.Write(buffer);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data)) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        WriteBigEndian(buffer, 0, unchecked((int)(crc ^ 0xFFFFFFFFu)));
        stream.Write(buffer);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
