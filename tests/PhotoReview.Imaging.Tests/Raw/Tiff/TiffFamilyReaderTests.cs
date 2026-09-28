using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

public sealed class TiffFamilyReaderTests
{
    [Fact]
    public void Cr2Reader_Synthetic_CanReadAndParsePreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        // Build CR2 with 'CR' at offset 8
        using var ms = new MemoryStream();
        ms.Write([0x49, 0x49, 0x2A, 0x00]); // II, 42
        ms.Write([0x10, 0x00, 0x00, 0x00]); // IFD0 at offset 16
        ms.Write([(byte)'C', (byte)'R', 0x02, 0x00, 0x00, 0x00, 0x00, 0x00]); // 8 bytes CR2 magic

        // IFD0 at offset 16
        // 3 entries: StripOffsets (0x0111), StripByteCounts (0x0117), Orientation (0x0112)
        ms.Write([0x03, 0x00]); // 3 entries
        long jpegOffset = 16 + 2 + (3 * 12) + 4;

        // 0x0111 (StripOffsets)
        WriteEntry(ms, 0x0111, 4, 1, (uint)jpegOffset);
        // 0x0117 (StripByteCounts)
        WriteEntry(ms, 0x0117, 4, 1, (uint)jpeg.Length);
        // 0x0112 (Orientation = 6)
        WriteEntry(ms, 0x0112, 3, 1, 6);
        // Next IFD = 0
        ms.Write([0x00, 0x00, 0x00, 0x00]);
        // Jpeg payload
        ms.Write(jpeg);

        var data = ms.ToArray();
        var reader = new Cr2ContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".cr2"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Cr2, info.Format);
        Assert.Equal(6, info.Orientation);
        Assert.Single(info.Previews);
        Assert.Equal(jpegOffset, info.Previews[0].Offset);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }

    [Fact]
    public void NefReader_Synthetic_CanReadSubIfdPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(800, 600);
        using var ms = new MemoryStream();
        ms.Write([0x49, 0x49, 0x2A, 0x00]); // II, 42
        ms.Write([0x08, 0x00, 0x00, 0x00]); // IFD0 at 8

        // IFD0 at 8: SubIFD tag 0x014A pointing to offset 40
        ms.Write([0x02, 0x00]); // 2 entries
        uint subIfdOffset = 40;
        WriteEntry(ms, 0x0112, 3, 1, 1); // Orientation 1
        WriteEntry(ms, 0x014A, 4, 1, subIfdOffset); // SubIFD
        ms.Write([0x00, 0x00, 0x00, 0x00]); // Next IFD

        while (ms.Position < subIfdOffset) ms.WriteByte(0);

        // SubIFD at 40: JPEGInterchangeFormat (0x0201) and Length (0x0202)
        ms.Write([0x02, 0x00]); // 2 entries
        uint jpegPos = 80;
        WriteEntry(ms, 0x0201, 4, 1, jpegPos);
        WriteEntry(ms, 0x0202, 4, 1, (uint)jpeg.Length);
        ms.Write([0x00, 0x00, 0x00, 0x00]);

        while (ms.Position < jpegPos) ms.WriteByte(0);
        ms.Write(jpeg);

        var data = ms.ToArray();
        var reader = new NefContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".nef"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Nef, info.Format);
        Assert.Single(info.Previews);
        Assert.Equal(jpegPos, info.Previews[0].Offset);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }

    [Fact]
    public void ArwReader_Synthetic_CanReadPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var data = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpeg, orientation: 3);

        var reader = new ArwContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".arw"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Arw, info.Format);
        Assert.Equal(3, info.Orientation);
        Assert.Single(info.Previews);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }

    [Fact]
    public void DngReader_Synthetic_CanReadPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var data = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpeg, orientation: 1);

        var reader = new DngContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".dng"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Dng, info.Format);
        Assert.Single(info.Previews);
    }

    [Fact]
    public void OrfReader_Synthetic_CanReadHeader()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        // ORF with 'IIRO'
        using var ms = new MemoryStream();
        ms.Write([(byte)'I', (byte)'I', (byte)'R', (byte)'O']);
        ms.Write([0x08, 0x00, 0x00, 0x00]); // IFD0 at 8
        ms.Write([0x02, 0x00]); // 2 entries
        uint jpegPos = 40;
        WriteEntry(ms, 0x0201, 4, 1, jpegPos);
        WriteEntry(ms, 0x0202, 4, 1, (uint)jpeg.Length);
        ms.Write([0x00, 0x00, 0x00, 0x00]);
        while (ms.Position < jpegPos) ms.WriteByte(0);
        ms.Write(jpeg);

        var data = ms.ToArray();
        var reader = new OrfContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".orf"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Orf, info.Format);
        Assert.Single(info.Previews);
    }

    [Fact]
    public void Rw2Reader_Synthetic_CanReadPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        // RW2 with 'IIU\0'
        using var ms = new MemoryStream();
        ms.Write([(byte)'I', (byte)'I', 0x55, 0x00]);
        ms.Write([0x08, 0x00, 0x00, 0x00]); // IFD0 at 8
        ms.Write([0x01, 0x00]); // 1 entry
        uint jpegPos = 32;
        WriteEntry(ms, 0x002E, 4, (uint)jpeg.Length, jpegPos); // Tag 0x002E JpgFromRaw
        ms.Write([0x00, 0x00, 0x00, 0x00]);
        while (ms.Position < jpegPos) ms.WriteByte(0);
        ms.Write(jpeg);

        var data = ms.ToArray();
        var reader = new Rw2ContainerReader();
        Assert.True(reader.CanRead(data.AsSpan(0, Math.Min(64, data.Length)), ".rw2"));

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Rw2, info.Format);
        Assert.Single(info.Previews);
        Assert.Equal(jpegPos, info.Previews[0].Offset);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }

    private static void WriteEntry(Stream s, ushort tag, ushort type, uint count, uint valOrOffset)
    {
        s.WriteByte((byte)tag);
        s.WriteByte((byte)(tag >> 8));
        s.WriteByte((byte)type);
        s.WriteByte((byte)(type >> 8));
        s.WriteByte((byte)count);
        s.WriteByte((byte)(count >> 8));
        s.WriteByte((byte)(count >> 16));
        s.WriteByte((byte)(count >> 24));
        if (type == 3) // SHORT
        {
            s.WriteByte((byte)valOrOffset);
            s.WriteByte((byte)(valOrOffset >> 8));
            s.WriteByte(0);
            s.WriteByte(0);
        }
        else
        {
            s.WriteByte((byte)valOrOffset);
            s.WriteByte((byte)(valOrOffset >> 8));
            s.WriteByte((byte)(valOrOffset >> 16));
            s.WriteByte((byte)(valOrOffset >> 24));
        }
    }
}
