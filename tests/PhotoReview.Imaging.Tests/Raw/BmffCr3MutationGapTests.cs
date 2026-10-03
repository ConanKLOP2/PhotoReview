using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Imaging.Raw.Bmff;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Exact-value and exact-boundary tests for <see cref="BmffBoxNavigator"/> and <see cref="Cr3ContainerReader"/> that pin the
/// behaviour surviving mutants of the bounds checks, the orientation / sensor-size ranges and the track chunk lookup.
/// </summary>
public sealed class BmffCr3MutationGapTests
{
    private static readonly byte[] CanonMoovUuid =
    [
        0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48
    ];

    private static readonly byte[] PreviewUuidBytes =
    [
        0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16
    ];

    // ---------------------------------------------------------------- BmffBoxNavigator.TryReadBox

    [Fact]
    public void TryReadBox_EightByteBoxAtVeryEndOfFile_IsRead()
    {
        var ftyp = Ftyp();
        var file = Cat(ftyp, Box("free"));
        var source = new InMemoryRawHeaderSource(file);

        Assert.True(BmffBoxNavigator.TryReadBox(source, ftyp.Length, out var box));
        Assert.Equal("free", box.Type);
        Assert.Equal(8, box.TotalSize);
        Assert.Equal(0, box.PayloadSize);
        Assert.Equal(file.Length, box.PayloadOffset);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(1)]
    public void TryReadBox_HeaderTruncatedByEndOfFile_ReturnsFalseWithoutThrowing(int bytesLeft)
    {
        var file = Cat(Ftyp(), new byte[bytesLeft]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.False(BmffBoxNavigator.TryReadBox(source, file.Length - bytesLeft, out _));
    }

    [Fact]
    public void TryReadBox_NegativeOffset_ReturnsFalseWithoutThrowing()
    {
        var source = new InMemoryRawHeaderSource(Cat(Ftyp(), Box("free")));

        Assert.False(BmffBoxNavigator.TryReadBox(source, -1, out _));
    }

    [Fact]
    public void TryReadBox_LargesizeBoxWithPayloadFillingTheRestOfTheFile_ReadsSixteenByteHeader()
    {
        var ftyp = Ftyp();
        var large = Cat(U32(1), Ascii("free"), U64(20), [1, 2, 3, 4]);
        var file = Cat(ftyp, large);
        var source = new InMemoryRawHeaderSource(file);

        Assert.True(BmffBoxNavigator.TryReadBox(source, ftyp.Length, out var box));
        Assert.Equal(20, box.TotalSize);
        Assert.Equal(ftyp.Length + 16, box.PayloadOffset);
        Assert.Equal(4, box.PayloadSize);
    }

    [Fact]
    public void TryReadBox_LargesizeBoxThatIsExactlySixteenBytesAtEndOfFile_HasEmptyPayload()
    {
        var ftyp = Ftyp();
        var file = Cat(ftyp, U32(1), Ascii("free"), U64(16));
        var source = new InMemoryRawHeaderSource(file);

        Assert.True(BmffBoxNavigator.TryReadBox(source, ftyp.Length, out var box));
        Assert.Equal(16, box.TotalSize);
        Assert.Equal(0, box.PayloadSize);
    }

    [Fact]
    public void TryReadBox_LargesizeHeaderCutOffByEndOfFile_ReturnsFalseWithoutThrowing()
    {
        // size==1 announces a 16-byte header but only 12 bytes exist.
        var ftyp = Ftyp();
        var file = Cat(ftyp, U32(1), Ascii("free"), new byte[4]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.False(BmffBoxNavigator.TryReadBox(source, ftyp.Length, out _));
    }

    [Fact]
    public void TryReadBox_SizeZeroWithContainerEndZero_ReturnsFalse()
    {
        // containerEnd 0 is a real (empty) container end, not the "unspecified" default: the box would end before it starts.
        var source = new InMemoryRawHeaderSource(Cat(U32(0), Ascii("free"), new byte[24]));

        Assert.False(BmffBoxNavigator.TryReadBox(source, 0, out _, containerEnd: 0));
    }

    [Fact]
    public void TryReadBox_SizeZeroExtendsToContainerEndOrEndOfFile()
    {
        var source = new InMemoryRawHeaderSource(Cat(U32(0), Ascii("free"), new byte[28]));

        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var inContainer, containerEnd: 20));
        Assert.Equal(20, inContainer.TotalSize);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var topLevel));
        Assert.Equal(36, topLevel.TotalSize);
    }

    // ---------------------------------------------------------------- BmffBoxNavigator.ReadChildBoxes

    [Fact]
    public void ReadChildBoxes_ParentPayloadStartingAtFileStart_ReturnsItsChildren()
    {
        var file = Cat(Box("aaaa", [1]), Box("bbbb", [2, 3]));
        var source = new InMemoryRawHeaderSource(file);
        var parent = new BmffBoxNavigator.BmffBox("root", 0, file.Length, 0, file.Length);

        var children = BmffBoxNavigator.ReadChildBoxes(source, parent);

        Assert.Equal(["aaaa", "bbbb"], children.Select(c => c.Type));
        Assert.Equal([0L, 9L], children.Select(c => c.Offset));
    }

    [Fact]
    public void ReadChildBoxes_NegativePayloadSkip_ReturnsEmpty()
    {
        var file = Box("moov", Box("free"));
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));

        Assert.Empty(BmffBoxNavigator.ReadChildBoxes(source, moov, payloadSkip: -8));
    }

    [Fact]
    public void ReadChildBoxes_EightByteChildFillingExactlyTheLastEightPayloadBytes_IsIncluded()
    {
        var file = Box("moov", Box("free"));
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));

        var child = Assert.Single(BmffBoxNavigator.ReadChildBoxes(source, moov));
        Assert.Equal("free", child.Type);
        Assert.Equal(8, child.Offset);
    }

    [Fact]
    public void ReadChildBoxes_PayloadSkipLeavingExactlyOneEmptyBox_ReturnsIt()
    {
        var file = Box("uuid", new byte[16], new byte[8], Box("free"));
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var uuid));

        var child = Assert.Single(BmffBoxNavigator.ReadChildBoxes(source, uuid, payloadSkip: 8));
        Assert.Equal("free", child.Type);
    }

    // ---------------------------------------------------------------- Cr3ContainerReader.CanRead

    [Fact]
    public void CanRead_TwelveByteCrxFtypHeader_IsAccepted()
    {
        var head = Cat(U32(16), Ascii("ftypcrx "));

        Assert.Equal(12, head.Length);
        Assert.True(new Cr3ContainerReader().CanRead(head, ".cr3"));
    }

    [Fact]
    public void CanRead_ElevenBytes_IsRejected()
    {
        var head = Cat(U32(16), Ascii("ftypcrx "))[..11];

        Assert.False(new Cr3ContainerReader().CanRead(head, ".cr3"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void CanRead_AnySingleBrandByteWrong_IsRejected(int position)
    {
        var head = Cat(U32(16), Ascii("ftypcrx "), new byte[4]);
        head[position] ^= 0x20;

        Assert.False(new Cr3ContainerReader().CanRead(head, ".cr3"));
    }

    [Fact]
    public void CanRead_ValidBrandWithWrongExtension_IsRejected()
    {
        var head = Cat(U32(16), Ascii("ftypcrx "), new byte[4]);

        Assert.True(new Cr3ContainerReader().CanRead(head, ".CR3"));
        Assert.False(new Cr3ContainerReader().CanRead(head, ".cr2"));
    }

    // ---------------------------------------------------------------- Cr3ContainerReader.Read: top level

    [Fact]
    public void Read_SixteenByteFileWithOnlyFtyp_ReturnsEmptyInfoInsteadOfThrowing()
    {
        var info = Read(Ftyp());

        Assert.Empty(info.Previews);
        Assert.Equal(1, info.Orientation);
        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_FifteenByteFile_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => Read(Ftyp()[..15]));
    }

    [Fact]
    public void Read_ForeignUuidBoxAtTopLevel_IsNotTreatedAsPreviewUuid()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(1620, 1080);
        var foreign = UuidBox(new byte[16], U32(0), U32(1), PrvwBox(jpeg, 1620, 1080));

        Assert.Empty(Read(Cat(Ftyp(), foreign)).Previews);
        Assert.Single(Read(Cat(Ftyp(), UuidBox(PreviewUuidBytes, U32(0), U32(1), PrvwBox(jpeg, 1620, 1080)))).Previews);
    }

    [Fact]
    public void Read_ForeignUuidBoxInsideMoov_IsNotTreatedAsCanonUuid()
    {
        var foreign = UuidBox(new byte[16], Box("CMT1", Cmt1Tiff(8, Short(0x0112, 6))));

        var info = Read(Cat(Ftyp(), Box("moov", foreign)));

        Assert.Equal(1, info.Orientation);
        Assert.Empty(info.ExifBlocks);
    }

    // ---------------------------------------------------------------- PRVW / THMB header layout

    [Fact]
    public void Read_TopLevelPrvwWithPayloadExactlyTheSoi_IsAPreviewOfTwoBytes()
    {
        var file = Cat(Ftyp(), Box("PRVW", [0xFF, 0xD8]));

        var preview = Assert.Single(Read(file).Previews);

        Assert.Equal(file.Length - 2, preview.Offset);
        Assert.Equal(2, preview.Length);
        Assert.Equal((0, 0), (preview.Width, preview.Height));
    }

    [Fact]
    public void Read_PrvwWithHeaderAndOnlyTheSoi_IsAPreviewOfTwoBytesWithHeaderSize()
    {
        // 16-byte header + FFD8 = 18-byte payload: the smallest headered preview.
        var prvw = Box("PRVW", U32(0), U16(1), U16(1620), U16(1080), U16(1), U32(0), [0xFF, 0xD8]);
        var file = Cat(Ftyp(), UuidBox(PreviewUuidBytes, U32(0), U32(1), prvw));

        var preview = Assert.Single(Read(file).Previews);

        Assert.Equal(file.Length - 2, preview.Offset);
        Assert.Equal(2, preview.Length);
        Assert.Equal((1620, 1080), (preview.Width, preview.Height));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void Read_PrvwPayloadShorterThanHeaderPlusSoiAtEndOfFile_IsSkippedWithoutThrowing(int payloadBytes)
    {
        var file = Cat(Ftyp(), Box("PRVW", new byte[payloadBytes]));

        Assert.Empty(Read(file).Previews);
    }

    [Fact]
    public void Read_PrvwDeclaredSizeSmallerThanBoxPayload_UsesDeclaredSize()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = Cat(Ftyp(), PreviewUuidWith(jpeg, declared: (uint)jpeg.Length, trailing: new byte[10]));

        var preview = Assert.Single(Read(file).Previews);

        Assert.Equal(jpeg.Length, preview.Length);
    }

    [Fact]
    public void Read_PrvwDeclaredSizeZero_UsesWholeBoxPayload()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = Cat(Ftyp(), PreviewUuidWith(jpeg, declared: 0, trailing: new byte[10]));

        var preview = Assert.Single(Read(file).Previews);

        Assert.Equal(jpeg.Length + 10, preview.Length);
    }

    [Fact]
    public void Read_PrvwDeclaredSizeOfOneByte_IsRejectedEvenWithValidSoi()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = Cat(Ftyp(), PreviewUuidWith(jpeg, declared: 1, trailing: []));

        Assert.Empty(Read(file).Previews);
    }

    // ---------------------------------------------------------------- CMT1 orientation / sensor size

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(0, 6)]
    [InlineData(9, 6)]
    public void Read_Cmt1SecondOrientationEntry_OverridesOnlyWhenWithinOneToEight(int second, int expected)
    {
        var info = Read(CanonCmt1(Short(0x0112, 6), Short(0x0112, (uint)second)));

        Assert.Equal(expected, info.Orientation);
    }

    [Fact]
    public void Read_Cmt1SensorWidthAndHeightOfIntMax_AreAccepted()
    {
        var info = Read(CanonCmt1(Long(0x0100, int.MaxValue), Long(0x0101, int.MaxValue)));

        Assert.Equal((int.MaxValue, int.MaxValue), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_Cmt1ZeroSensorSize_DoesNotReplaceEarlierValue()
    {
        var info = Read(CanonCmt1(Long(0x0100, 6000), Long(0x0101, 4000), Long(0x0100, 0), Long(0x0101, 0)));

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_Cmt1Ifd0OffsetInsideTheTiffHeader_IsRejected()
    {
        // ifd0 = 2 would read the count from the magic bytes (42 entries) and the IFD overlaps the header.
        var tiff = new TiffBytes(true, 40).Header(2).Put(16, [0x12, 0x01, 0x03, 0x00, 1, 0, 0, 0, 6, 0, 0, 0]).ToArray();

        var info = Read(Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", tiff)))));

        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public void Read_Cmt1EntryStraddlingPayloadEnd_IsNotRead()
    {
        // IFD with 2 entries at 8: entry 0 (width 6000) fits in the 31-byte payload, entry 1 (orientation 6) ends 3 bytes
        // past it, in bytes that belong to whatever follows the box.
        var tiff = new TiffBytes(true, 38).Header(8).Ifd(8, 0, Short(0x0100, 6000), Short(0x0112, 6)).ToArray();
        var file = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", tiff[..31]))), tiff[31..], new byte[8]);

        var info = Read(file);

        Assert.Equal(6000, info.SensorWidth);
        Assert.Equal(1, info.Orientation);
    }

    [Theory]
    [InlineData(36, 6000)]  // value occupies the last four bytes of the payload
    [InlineData(37, 0)]     // value would run past the payload
    public void Read_Cmt1OutOfLineValue_MustLieFullyInsidePayload(uint valueOffset, int expectedWidth)
    {
        var tiff = new TiffBytes(true, 40).Header(8).Ifd(8, 0, At(0x0100, 4, 2, valueOffset)).ToArray();
        if (valueOffset == 36)
        {
            tiff[36] = 0x70; // 0x1770 = 6000
            tiff[37] = 0x17;
        }
        else
        {
            tiff[37] = 1; // 1 when the fourth byte (first byte after the payload) is 0
            tiff[38] = 0;
            tiff[39] = 0;
        }

        var file = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", tiff))), new byte[8]);

        Assert.Equal(expectedWidth, Read(file).SensorWidth);
    }

    // ---------------------------------------------------------------- track chunk lookup

    [Fact]
    public void Read_StcoChunkOffset_YieldsTrackPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(Stsz(uniform: (uint)jpeg.Length, count: 1), Box("stco", U32(0), U32(1), U32((uint)off))));

        var preview = Assert.Single(Read(file).Previews);
        Assert.Equal(file.Length - jpeg.Length, preview.Offset);
        Assert.Equal(jpeg.Length, preview.Length);
        Assert.Equal((640, 480), (preview.Width, preview.Height));
    }

    [Fact]
    public void Read_StcoWithoutOffsetEntry_DoesNotFallBackToCo64()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Stsz(uniform: (uint)jpeg.Length, count: 1),
            Box("stco", U32(0), U32(1)),
            Box("co64", U32(0), U32(1), U64(off))));

        Assert.Empty(Read(file).Previews);
    }

    [Fact]
    public void Read_StszWithPerSampleTable_UsesFirstSampleSize()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        // uniform size 0, count 1, then the single 4-byte entry: payload is exactly 16 bytes.
        var file = TrackFile(jpeg, off => Trak(
            Box("stsz", U32(0), U32(0), U32(1), U32((uint)jpeg.Length)),
            Box("co64", U32(0), U32(1), U64(off))));

        var preview = Assert.Single(Read(file).Previews);
        Assert.Equal(jpeg.Length, preview.Length);
    }

    [Fact]
    public void Read_StszWithZeroSamplesAndZeroUniformSize_YieldsNoPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Box("stsz", U32(0), U32(0), U32(0), U32((uint)jpeg.Length)),
            Box("co64", U32(0), U32(1), U64(off))));

        Assert.Empty(Read(file).Previews);
    }

    [Fact]
    public void Read_Co64WithZeroEntryCount_YieldsNoPreviewEvenWhenOffsetBytesArePresent()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Stsz(uniform: (uint)jpeg.Length, count: 1),
            Box("co64", U32(0), U32(0), U64(off))));

        Assert.Empty(Read(file).Previews);
    }

    [Fact]
    public void Read_SampleSizeOneByteBeyondEndOfFile_YieldsNoPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Stsz(uniform: (uint)jpeg.Length + 1, count: 1),
            Box("co64", U32(0), U32(1), U64(off))));

        Assert.Empty(Read(file).Previews);
    }

    // ---------------------------------------------------------------- builders

    private static RawContainerInfo Read(byte[] file) =>
        new Cr3ContainerReader().Read(new InMemoryRawHeaderSource(file), CancellationToken.None);

    private static byte[] CanonCmt1(params Entry[] entries) =>
        Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", Cmt1Tiff(8, entries)))));

    private static byte[] Cmt1Tiff(uint ifd0, params Entry[] entries)
    {
        var size = (int)ifd0 + 2 + (entries.Length * 12) + 4;
        return new TiffBytes(true, size).Header(ifd0).Ifd((int)ifd0, 0, entries).ToArray();
    }

    /// <summary>ftyp + moov(trak) + jpeg; the trak builder receives the file offset of the jpeg (the moov size does not depend on it).</summary>
    private static byte[] TrackFile(byte[] jpeg, Func<long, byte[]> trak)
    {
        long offset = Cat(Ftyp(), Box("moov", trak(0))).Length;
        return Cat(Ftyp(), Box("moov", trak(offset)), jpeg);
    }

    private static byte[] Trak(params byte[][] stblChildren) => Box("trak", Box("mdia", Box("minf", Box("stbl", stblChildren))));

    private static byte[] Stsz(uint uniform, uint count) => Box("stsz", U32(0), U32(uniform), U32(count));

    private static byte[] PrvwBox(byte[] jpeg, int width, int height) =>
        Box("PRVW", U32(0), U16(1), U16(width), U16(height), U16(1), U32((uint)jpeg.Length), jpeg);

    private static byte[] PreviewUuidWith(byte[] jpeg, uint declared, byte[] trailing) =>
        UuidBox(PreviewUuidBytes, U32(0), U32(1),
            Box("PRVW", U32(0), U16(1), U16(100), U16(50), U16(1), U32(declared), jpeg, trailing));

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] U16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v);
        return b;
    }

    private static byte[] Ftyp() => Box("ftyp", Ascii("crx "), U32(1), Ascii("crx "));

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = Cat(parts);
        return Cat(U32((uint)(8 + payload.Length)), Ascii(type), payload);
    }

    private static byte[] UuidBox(byte[] uuid, params byte[][] parts) => Box("uuid", [uuid, .. parts]);
}