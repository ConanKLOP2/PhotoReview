using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Imaging.Raw.Raf;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Exact-value tests for <see cref="Cr2ContainerReader"/>, <see cref="ArwContainerReader"/> and <see cref="RafContainerReader"/>:
/// signature sniffing, IFD caps, size ties, strip preview validation, MakerNote bounds and the RAF CFA record walk.
/// </summary>
public sealed class Cr2ArwRafMutationGapTests
{
    private static RawContainerInfo ReadCr2(byte[] data) =>
        new Cr2ContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static RawContainerInfo ReadArw(byte[] data) =>
        new ArwContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static RawContainerInfo ReadRaf(byte[] data) =>
        new RafContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static byte[] Jpeg(int width, int height) => SyntheticRawBuilder.CreateMinimalJpeg(width, height);

    private static ushort Us(int value) => (ushort)value;

    // ---------------------------------------------------------------- CR2 / ARW signatures

    [Fact]
    public void Cr2CanRead_ExactlyTenBytesWithEachSignatureByteChecked()
    {
        byte[] head = [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, (byte)'C', (byte)'R'];
        var reader = new Cr2ContainerReader();

        Assert.True(reader.CanRead(head, ".cr2"));
        Assert.False(reader.CanRead(head.AsSpan(0, 9), ".cr2"));
        foreach (var position in new[] { 0, 1, 2, 3, 8, 9 })
        {
            var broken = (byte[])head.Clone();
            broken[position] ^= 0x40;
            Assert.False(reader.CanRead(broken, ".cr2"));
        }
    }

    [Fact]
    public void ArwCanRead_ExactlyFourBytesWithEachSignatureByteChecked()
    {
        byte[] head = [0x49, 0x49, 0x2A, 0x00];
        var reader = new ArwContainerReader();

        Assert.True(reader.CanRead(head, ".arw"));
        Assert.False(reader.CanRead(head.AsSpan(0, 3), ".arw"));
        foreach (var position in new[] { 0, 1, 2, 3 })
        {
            var broken = (byte[])head.Clone();
            broken[position] ^= 0x40;
            Assert.False(reader.CanRead(broken, ".arw"));
        }
    }

    [Fact]
    public void Read_SixteenByteFileWithEmptyIfd_ReturnsEmptyInfoForCr2AndArw()
    {
        foreach (var info in new[]
        {
            ReadCr2(new TiffBytes(true, 16).Header(8).Ifd(8, 0).ToArray()),
            ReadArw(new TiffBytes(true, 16).Header(8).Ifd(8, 0).ToArray()),
        })
        {
            Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
            Assert.Empty(info.Previews);
            Assert.Single(info.ExifBlocks);
        }
    }

    [Fact]
    public void ReadArw_ValidByteOrderButMagicFortyThree_Throws()
    {
        var data = new TiffBytes(true, 64).Header(8, "II+\0"u8).Ifd(8, 0, Short(0x0100, 100)).ToArray();

        Assert.Throws<InvalidDataException>(() => ReadArw(data));
    }

    // ---------------------------------------------------------------- CR2

    [Fact]
    public void ReadCr2_ChainOfSixtySixIfds_ReadsOnlyTheFirstSixtyFour()
    {
        const int ifdSize = 30;
        var file = new TiffBytes(true, 8 + (66 * ifdSize)).Header(8);
        for (int i = 0; i < 66; i++)
        {
            int at = 8 + (i * ifdSize);
            file.Ifd(at, i < 65 ? (uint)(at + ifdSize) : 0, Short(0x0100, Us(100 + i)), Short(0x0101, 100));
        }

        var info = ReadCr2(file.ToArray());

        Assert.Equal((163, 100), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadCr2_TwoIfdsOfEqualArea_KeepsTheFirst()
    {
        var data = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 60, Short(0x0100, 4000), Short(0x0101, 3000))
            .Ifd(60, 0, Short(0x0100, 3000), Short(0x0101, 4000))
            .ToArray();

        var info = ReadCr2(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadCr2_Ifd0StripThatDoesNotStartWithSoi_IsNotAPreview()
    {
        var data = new TiffBytes(true, 512).Header(8)
            .Ifd(8, 0, Short(0x0100, 100), Short(0x0101, 100), Long(0x0111, 300), Long(0x0117, 100))
            .ToArray();

        Assert.Empty(ReadCr2(data).Previews);
    }

    [Fact]
    public void ReadCr2_Ifd0StripWithSoi_IsAPreview()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 300 + jpeg.Length).Header(8)
            .Ifd(8, 0, Short(0x0100, 100), Short(0x0101, 100), Long(0x0111, 300), Long(0x0117, (uint)jpeg.Length))
            .Put(300, jpeg)
            .ToArray();

        var preview = Assert.Single(ReadCr2(data).Previews);

        Assert.Equal((300L, (long)jpeg.Length, 640, 480), (preview.Offset, preview.Length, preview.Width, preview.Height));
    }

    [Fact]
    public void ReadCr2_WithoutAnExifPointer_StillReportsOneExifBlock()
    {
        var data = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Short(0x0100, 100), Short(0x0101, 100)).ToArray();

        var block = Assert.Single(ReadCr2(data).ExifBlocks);

        Assert.Equal((0L, true), (block.Offset, block.IsTiffHeader));
    }

    // ---------------------------------------------------------------- ARW

    [Fact]
    public void ReadArw_ChainOfSixtySixIfds_ReadsOnlyTheFirstSixtyFour()
    {
        const int ifdSize = 30;
        var file = new TiffBytes(true, 8 + (66 * ifdSize)).Header(8);
        for (int i = 0; i < 66; i++)
        {
            int at = 8 + (i * ifdSize);
            file.Ifd(at, i < 65 ? (uint)(at + ifdSize) : 0, Short(0x0100, Us(100 + i)), Short(0x0101, 100));
        }

        var info = ReadArw(file.ToArray());

        Assert.Equal((163, 100), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadArw_TwoRawIfdsOfEqualArea_KeepsTheFirst()
    {
        var data = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 60, Short(0x0100, 4000), Short(0x0101, 3000))
            .Ifd(60, 0, Short(0x0100, 3000), Short(0x0101, 4000))
            .ToArray();

        var info = ReadArw(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadArw_SameJpegReferencedByTwoIfds_IsListedOnce()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 400 + jpeg.Length).Header(8)
            .Ifd(8, 60, Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length))
            .Ifd(60, 0, Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length))
            .Put(400, jpeg)
            .ToArray();

        Assert.Single(ReadArw(data).Previews);
    }

    private static byte[] ArwWithNote(ushort exifTag, uint exifCount, ushort previewCount4Tag, uint previewCount, int noteOffset, int size, byte[]? jpegBytes = null)
    {
        var jpeg = jpegBytes ?? Jpeg(640, 480);
        return new TiffBytes(true, size).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(exifTag, 7, exifCount, (uint)noteOffset))
            .Ifd(noteOffset, 0, At(previewCount4Tag, 7, previewCount, 700))
            .Put(700, jpeg)
            .ToArray();
    }

    [Theory]
    [InlineData((ushort)0x927C, 100u, 1)]
    [InlineData((ushort)0x927C, 5u, 1)]
    [InlineData((ushort)0x927C, 4u, 0)]    // 4 inline bytes are data, not an offset
    [InlineData((ushort)0x9286, 100u, 0)]  // another tag is not the MakerNote
    public void ReadArw_MakerNoteEntry_IsAnOffsetOnlyForTagMakerNoteWithMoreThanFourBytes(ushort tag, uint count, int expectedPreviews)
    {
        var jpeg = Jpeg(640, 480);
        var data = ArwWithNote(tag, count, 0x2001, (uint)jpeg.Length, noteOffset: 200, size: 700 + jpeg.Length, jpeg);

        Assert.Equal(expectedPreviews, ReadArw(data).Previews.Count);
    }

    [Fact]
    public void ReadArw_NotePreviewEntryOfExactlyFourBytes_IsNotAPreview()
    {
        var data = ArwWithNote(0x927C, 100, 0x2001, 4, noteOffset: 200, size: 710, [0xFF, 0xD8, 0xFF, 0xD9]);

        Assert.Empty(ReadArw(data).Previews);
    }

    [Fact]
    public void ReadArw_NoteStartingLessThanFourteenBytesBeforeTheEnd_IsIgnoredWithoutThrowing()
    {
        var data = new TiffBytes(true, 200).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 100, 192))
            .ToArray();

        Assert.Empty(ReadArw(data).Previews);
    }

    [Fact]
    public void ReadArw_NoteStartingExactlyFourteenBytesBeforeTheEnd_IsParsed()
    {
        var jpeg = Jpeg(640, 480);
        const int size = 1000;
        const int note = size - 14;
        var file = new TiffBytes(true, size).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 100, note))
            .Put(300, jpeg)
            .U16(note, 1);
        file.Put(note + 2, [0x01, 0x20, 0x07, 0x00, (byte)jpeg.Length, (byte)(jpeg.Length >> 8), 0, 0, 0x2C, 0x01, 0, 0]);

        var preview = Assert.Single(ReadArw(file.ToArray()).Previews);

        Assert.Equal((300L, (long)jpeg.Length), (preview.Offset, preview.Length));
    }

    [Fact]
    public void ReadArw_NoteStartingAtOffsetEight_IsParsed()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 700 + jpeg.Length).Header(400)
            .Ifd(8, 0, At(0x2001, 7, (uint)jpeg.Length, 700))
            .Ifd(400, 0, Long(0x8769, 450))
            .Ifd(450, 0, At(0x927C, 7, 100, 8))
            .Put(700, jpeg)
            .ToArray();

        var preview = Assert.Single(ReadArw(data).Previews);

        Assert.Equal(700L, preview.Offset);
    }

    // ---------------------------------------------------------------- RAF

    private static byte[] RafFile(uint jpegOffset, uint jpegLength, byte[]? cfa, uint? cfaLengthOverride = null, int minimumSize = 400)
    {
        var size = Math.Max(minimumSize, 300 + (cfa?.Length ?? 0));
        var data = new byte[size];
        "FUJIFILMCCD-RAW "u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(84), jpegOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(88), jpegLength);
        if (cfa is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(92), 300);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(96), cfaLengthOverride ?? (uint)cfa.Length);
            cfa.CopyTo(data, 300);
        }

        return data;
    }

    private static byte[] Record(ushort tag, int height, int width)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)width);
        return bytes;
    }

    private static byte[] Cfa(uint count, params byte[][] records)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, count);
        return [.. header, .. records.SelectMany(r => r)];
    }

    [Fact]
    public void RafCanRead_ExactlySixteenByteMagic_IsAcceptedAndFifteenIsNot()
    {
        var magic = "FUJIFILMCCD-RAW "u8.ToArray();
        var reader = new RafContainerReader();

        Assert.True(reader.CanRead(magic, ".raf"));
        Assert.False(reader.CanRead(magic.AsSpan(0, 15), ".raf"));
    }

    [Fact]
    public void ReadRaf_WrongMagicInALongEnoughFile_Throws()
    {
        var data = RafFile(0, 0, null);
        data[3] ^= 0x40;

        Assert.Throws<InvalidDataException>(() => ReadRaf(data));
    }

    [Fact]
    public void ReadRaf_JpegOfExactlyTwoBytesAtTheFirstAllowedOffset_IsAPreview()
    {
        var data = RafFile(100, 2, null);
        data[100] = 0xFF;
        data[101] = 0xD8;

        var preview = Assert.Single(ReadRaf(data).Previews);

        Assert.Equal((100L, 2L), (preview.Offset, preview.Length));
    }

    [Theory]
    [InlineData(99u, 2u)]   // starts inside the pointer table
    [InlineData(100u, 1u)]  // too short for SOI
    public void ReadRaf_JpegRangeOutsideTheAllowedWindow_IsNotAPreview(uint offset, uint length)
    {
        var data = RafFile(offset, length, null);
        data[(int)offset] = 0xFF;
        data[(int)offset + 1] = 0xD8;

        Assert.Empty(ReadRaf(data).Previews);
    }

    [Fact]
    public void ReadRaf_CfaRangeBeyondTheEndOfTheFile_ReportsNoSizeWithoutThrowing()
    {
        var data = RafFile(0, 0, Cfa(1, Record(0x0100, 3000, 4000)), cfaLengthOverride: 1000);

        var info = ReadRaf(data);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRaf_RecordsBeyondTheDeclaredCount_AreNotRead()
    {
        // Count 1: the full size is read; the second record (a valid cropped size) is beyond the count.
        var data = RafFile(0, 0, Cfa(1, Record(0x0100, 3000, 4000), Record(0x0111, 2990, 3990)));

        var info = ReadRaf(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRaf_RecordWhoseDataIsCutOffByTheCfaLength_EndsTheWalk()
    {
        // CFA length 10: count + one record header + only 2 of the 4 data bytes.
        var data = RafFile(0, 0, Cfa(1, Record(0x0100, 3000, 4000)), cfaLengthOverride: 10);

        var info = ReadRaf(data);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRaf_RecordWithAnUnknownTag_IsIgnored()
    {
        var data = RafFile(0, 0, Cfa(1, Record(0x0200, 100, 100)));

        var info = ReadRaf(data);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(4000, 0)]
    public void ReadRaf_FullSizeRecordWithAZeroSide_IsIgnored(int width, int height)
    {
        var data = RafFile(0, 0, Cfa(1, Record(0x0100, height, width)));

        var info = ReadRaf(data);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(3990, 3000, 3990, 3000)]  // cropped height equal to the full height still fits
    [InlineData(4000, 2990, 4000, 2990)]  // cropped width equal to the full width still fits
    [InlineData(3990, 3100, 4000, 3000)]  // taller than the full readout: ignored
    [InlineData(4100, 2990, 4000, 3000)]  // wider than the full readout: ignored
    public void ReadRaf_CroppedSize_IsUsedOnlyWhenItFitsInsideTheFullReadout(int croppedWidth, int croppedHeight, int expectedWidth, int expectedHeight)
    {
        var data = RafFile(0, 0, Cfa(2, Record(0x0100, 3000, 4000), Record(0x0111, croppedHeight, croppedWidth)));

        var info = ReadRaf(data);

        Assert.Equal((expectedWidth, expectedHeight), (info.SensorWidth, info.SensorHeight));
    }
}