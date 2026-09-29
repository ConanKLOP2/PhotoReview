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
    /// Builds a CR3 laid out like a real Canon file: ftyp, moov (Canon uuid with CMT1 + THMB, one trak with stsz/co64),
    /// a preview uuid (8-byte prefix + PRVW with 16-byte header) and the track sample JPEG after it.
    /// </summary>
    public static byte[] BuildCanonCr3(byte[]? prvwJpeg = null, byte[]? thumbJpeg = null, byte[]? trackJpeg = null)
    {
        prvwJpeg ??= CreateMinimalJpeg(1620, 1080);
        thumbJpeg ??= CreateMinimalJpeg(160, 120);
        trackJpeg ??= CreateMinimalJpeg(6000, 4000);

        byte[] U16(int v) => [(byte)(v >> 8), (byte)v];
        byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        byte[] U64(ulong v) => [.. U32((uint)(v >> 32)), .. U32((uint)v)];
        byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = [.. parts.SelectMany(x => x)];
            return [.. U32((uint)(8 + payload.Length)), .. Encoding.ASCII.GetBytes(type), .. payload];
        }

        byte[] canonUuid = [0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48];
        byte[] previewUuid = [0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16];

        // CMT1: little-endian TIFF with Orientation=6, ImageWidth=6000, ImageLength=4000.
        byte[] Le16(int v) => [(byte)v, (byte)(v >> 8)];
        byte[] Le32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        byte[] Entry(int tag, int type, uint value) => [.. Le16(tag), .. Le16(type), .. Le32(1), .. Le32(value)];
        byte[] cmt1 = [0x49, 0x49, 0x2A, 0x00, .. Le32(8), .. Le16(3), .. Entry(0x0100, 4, 6000), .. Entry(0x0101, 4, 4000), .. Entry(0x0112, 3, 6), .. Le32(0)];

        byte[] thmb = Box("THMB", U32(0), U16(160), U16(120), U32((uint)thumbJpeg.Length), U16(1), U16(0), thumbJpeg);
        byte[] prvw = Box("PRVW", U32(0), U16(1), U16(1620), U16(1080), U16(1), U32((uint)prvwJpeg.Length), prvwJpeg);
        byte[] previewBox = Box("uuid", previewUuid, U32(0), U32(1), prvw);
        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("crx "), U32(1), Encoding.ASCII.GetBytes("crx "));

        byte[] Moov(ulong chunkOffset) => Box("moov",
            Box("uuid", canonUuid, Box("CMT1", cmt1), thmb),
            Box("trak", Box("mdia", Box("minf", Box("stbl",
                Box("stsz", U32(0), U32((uint)trackJpeg.Length), U32(1)),
                Box("co64", U32(0), U32(1), U64(chunkOffset)))))));

        // The chunk offset does not change the moov size, so size it once and then place the track JPEG after the preview box.
        long trackOffset = ftyp.Length + Moov(0).Length + previewBox.Length;
        return [.. ftyp, .. Moov((ulong)trackOffset), .. previewBox, .. trackJpeg];
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
