using System.IO;
using System.Text;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Test helper that builds minimal, valid in-memory TIFF (both byte orders), ISO-BMFF (CR3),
/// and RAF container files embedding a JPEG payload.
/// </summary>
public static class SyntheticRawBuilder
{
    /// <summary>
    /// Creates a minimal valid synthetic JPEG in memory (SOI + APP0 + SOF0 + SOS + data + EOI).
    /// </summary>
    public static byte[] CreateMinimalJpeg(int width = 320, int height = 240)
    {
        using var ms = new MemoryStream();
        // SOI
        ms.Write([0xFF, 0xD8]);

        // APP0 (JFIF)
        ms.Write([0xFF, 0xE0, 0x00, 0x10]);
        ms.Write(Encoding.ASCII.GetBytes("JFIF\0"));
        ms.Write([0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // DQT (Quantization Table)
        ms.Write([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        for (int i = 0; i < 64; i++) ms.WriteByte(16);

        // SOF0 (Baseline DCT)
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]); // length 17, precision 8
        ms.WriteByte((byte)(height >> 8));
        ms.WriteByte((byte)height);
        ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)width);
        ms.WriteByte(3); // 3 components (Y, Cb, Cr)
        ms.Write([0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);

        // DHT (Huffman Table minimal)
        ms.Write([0xFF, 0xC4, 0x00, 0x1F, 0x00]);
        ms.Write([0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        ms.Write([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B]);

        // SOS (Start of Scan)
        ms.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);

        // Dummy compressed image bytes (must avoid bare FF unless stuffed with 00)
        ms.Write([0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55]);

        // EOI
        ms.Write([0xFF, 0xD9]);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal TIFF container (little-endian or big-endian) embedding a JPEG preview.
    /// Tag 0x0201 = JPEGInterchangeFormat (offset), Tag 0x0202 = JPEGInterchangeFormatLength.
    /// Tag 0x0112 = Orientation.
    /// </summary>
    public static byte[] BuildTiff(bool littleEndian, byte[]? jpegBytes = null, ushort orientation = 1, ushort magic = 42)
    {
        jpegBytes ??= CreateMinimalJpeg(640, 480);
        using var ms = new MemoryStream();

        // 1. Header (8 bytes)
        // Byte order
        ms.Write(littleEndian ? [0x49, 0x49] : [0x4D, 0x4D]);
        WriteUInt16(ms, magic, littleEndian);
        WriteUInt32(ms, 8, littleEndian); // IFD0 offset = 8

        // 2. IFD0 (starts at offset 8)
        // 3 entries: Orientation (0x0112), JpegOffset (0x0201), JpegLength (0x0202)
        ushort entryCount = 3;
        WriteUInt16(ms, entryCount, littleEndian);

        long ifdStart = 8;
        long nextIfdOffsetPos = ifdStart + 2 + (entryCount * 12);
        long jpegOffset = nextIfdOffsetPos + 4; // place JPEG right after IFD0 next-pointer

        // Tag 0x0112 (Orientation, SHORT, count 1)
        WriteTiffEntry(ms, 0x0112, 3, 1, orientation, littleEndian);

        // Tag 0x0201 (JPEGInterchangeFormat, LONG, count 1)
        WriteTiffEntry(ms, 0x0201, 4, 1, (uint)jpegOffset, littleEndian);

        // Tag 0x0202 (JPEGInterchangeFormatLength, LONG, count 1)
        WriteTiffEntry(ms, 0x0202, 4, 1, (uint)jpegBytes.Length, littleEndian);

        // Next IFD offset = 0
        WriteUInt32(ms, 0, littleEndian);

        // 3. JPEG payload
        ms.Write(jpegBytes);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal ISO-BMFF container with an 'ftyp' box (crx) and a preview box containing JPEG.
    /// </summary>
    public static byte[] BuildIsoBmff(byte[]? jpegBytes = null)
    {
        jpegBytes ??= CreateMinimalJpeg(800, 600);
        using var ms = new MemoryStream();

        // ftyp box: length = 16, type = 'ftyp', major_brand = 'crx ', minor_version = 1
        WriteUInt32BigEndian(ms, 16);
        ms.Write(Encoding.ASCII.GetBytes("ftyp"));
        ms.Write(Encoding.ASCII.GetBytes("crx "));
        WriteUInt32BigEndian(ms, 1);

        // prvw box: length = 8 + jpegBytes.Length, type = 'prvw'
        uint prvwLen = (uint)(8 + jpegBytes.Length);
        WriteUInt32BigEndian(ms, prvwLen);
        ms.Write(Encoding.ASCII.GetBytes("prvw"));
        ms.Write(jpegBytes);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal RAF container: 16-byte magic "FUJIFILMCCD-RAW ", header offsets, and embedded JPEG.
    /// </summary>
    public static byte[] BuildRaf(byte[]? jpegBytes = null)
    {
        jpegBytes ??= CreateMinimalJpeg(1024, 768);
        using var ms = new MemoryStream();

        // 16 bytes magic
        ms.Write(Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW "));
        // 4 bytes format version "0201"
        ms.Write(Encoding.ASCII.GetBytes("0201"));
        // 8 bytes camera ID
        ms.Write(Encoding.ASCII.GetBytes("FF-TEST\0"));

        // Fixed header fields up to offset 84 (where JPEG offset & length live)
        while (ms.Position < 84) ms.WriteByte(0);

        // JPEG offset (offset 84) and length (offset 88) - big endian
        uint jpegOffset = 160;
        WriteUInt32BigEndian(ms, jpegOffset);
        WriteUInt32BigEndian(ms, (uint)jpegBytes.Length);

        // Pad to jpegOffset
        while (ms.Position < jpegOffset) ms.WriteByte(0);

        // Write JPEG payload
        ms.Write(jpegBytes);

        return ms.ToArray();
    }

    private static void WriteTiffEntry(Stream s, ushort tag, ushort type, uint count, uint valOrOffset, bool littleEndian)
    {
        WriteUInt16(s, tag, littleEndian);
        WriteUInt16(s, type, littleEndian);
        WriteUInt32(s, count, littleEndian);
        if (type == 3) // SHORT
        {
            WriteUInt16(s, (ushort)valOrOffset, littleEndian);
            WriteUInt16(s, 0, littleEndian); // pad to 4 bytes
        }
        else
        {
            WriteUInt32(s, valOrOffset, littleEndian);
        }
    }

    private static void WriteUInt16(Stream s, ushort val, bool littleEndian)
    {
        if (littleEndian)
        {
            s.WriteByte((byte)val);
            s.WriteByte((byte)(val >> 8));
        }
        else
        {
            s.WriteByte((byte)(val >> 8));
            s.WriteByte((byte)val);
        }
    }

    private static void WriteUInt32(Stream s, uint val, bool littleEndian)
    {
        if (littleEndian)
        {
            s.WriteByte((byte)val);
            s.WriteByte((byte)(val >> 8));
            s.WriteByte((byte)(val >> 16));
            s.WriteByte((byte)(val >> 24));
        }
        else
        {
            WriteUInt32BigEndian(s, val);
        }
    }

    private static void WriteUInt32BigEndian(Stream s, uint val)
    {
        s.WriteByte((byte)(val >> 24));
        s.WriteByte((byte)(val >> 16));
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)val);
    }
}
