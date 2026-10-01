using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Bmff;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// CR3 hostile-structure gaps: sibling boxes beyond the per-parent child cap and a flood of tiny boxes (bounded by the header
/// budget, mapped to the corrupt-RAW sentence), and the degenerate stsz/stco/co64 count and size combinations.
/// </summary>
public sealed class Cr3BmffBudgetAndStblGapTests
{
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = Cat(parts);
        return Cat(U32((uint)(8 + payload.Length)), Encoding.ASCII.GetBytes(type), payload);
    }

    private static byte[] Ftyp() => Box("ftyp", "crx "u8.ToArray(), U32(1), "crx "u8.ToArray());

    private static readonly byte[] CanonMoovUuid =
    [
        0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48
    ];

    // ---------------------------------------------------------------- top-level flood / siblings

    [Fact]
    public void Read_MoreThanTheChildCapOfSiblingMoovBoxes_StillReadsTheLastOneWithBoundedHeaderReads()
    {
        // The per-parent cap (BmffBoxNavigator.MaxChildBoxes) applies to children, not to top-level siblings: a CR3 whose
        // orientation sits in the 300th moov must still be honoured, and 300 tiny boxes must cost one 64 KiB block, not more.
        var cmt1 = new byte[]
        {
            (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0,
            1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, // one SHORT Orientation = 6 entry
            0, 0, 0, 0,
        };
        var lastMoov = Box("moov", Box("uuid", CanonMoovUuid, Box("CMT1", cmt1)));
        var empties = Enumerable.Range(0, 300).Select(_ => Box("moov", [])).ToArray();
        var file = Cat(Ftyp(), Cat(empties), lastMoov);
        var source = new InMemoryRawHeaderSource(file);

        var info = new Cr3ContainerReader().Read(source, CancellationToken.None);

        Assert.Equal(6, info.Orientation);
        Assert.Equal(file.Length, source.TotalBytesRead); // a single block holds the whole small file
        Assert.True(source.TotalBytesRead <= SourceRawHeaderSource.BlockSize);
    }

    private static byte[] FloodOfTinyBoxes()
    {
        // Two header-budgets' worth of 8-byte 'free' boxes: the walk would need ~1.2M iterations and 9.6 MB of header reads.
        const int Count = 1_200_000;
        var flood = new byte[Count * 8];
        for (var i = 0; i < Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(flood.AsSpan(i * 8), 8);
            "free"u8.CopyTo(flood.AsSpan((i * 8) + 4));
        }

        return Cat(Ftyp(), flood);
    }

    [Fact]
    public void Read_FloodOfTinyTopLevelBoxes_StopsAtTheHeaderBudgetWithAnInvalidDataExceptionNotAnIoFailure()
    {
        var file = FloodOfTinyBoxes();
        using var source = new SourceRawHeaderSource(new MemoryStream(file));

        var ex = Assert.Throws<InvalidDataException>(() => new Cr3ContainerReader().Read(source, CancellationToken.None));

        Assert.Null(ex.InnerException); // a budget stop is "corrupt RAW", never mistaken for a disk failure
        Assert.True(source.TotalBytesRead <= RawContainerLimits.MaxHeaderBytes);
        Assert.True(source.TotalBytesRead > RawContainerLimits.MaxHeaderBytes - SourceRawHeaderSource.BlockSize); // it really ran to the cap
    }

    [Fact]
    public void ReadInfo_FloodOfTinyTopLevelBoxes_FailsWithTheLocalizedCorruptRawSentence()
    {
        using var temp = new TempRoot("cr3-flood");
        var path = temp.File("flood.cr3", FloodOfTinyBoxes());
        var decoder = new RawDecoder(new WpfBitmapImageDecoder());

        var ex = Assert.Throws<InvalidDataException>(() => decoder.ReadInfo(path));

        Assert.Equal(Tr.ImageErrorRawCorrupt, UserFacingError.Describe(ex));
    }

    // ---------------------------------------------------------------- stsz / stco / co64 degenerate values

    private static readonly byte[] Jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

    /// <summary>ftyp + moov(trak/mdia/minf/stbl[<paramref name="stblChildren"/> (chunkOffset -> children)]) followed by the JPEG and zero padding.</summary>
    private static byte[] FileWithTrack(Func<long, byte[][]> stblChildren)
    {
        byte[] Moov(long chunkOffset) => Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stblChildren(chunkOffset))))));
        long offset = Ftyp().Length + Moov(0).Length;
        return Cat(Ftyp(), Moov(offset), Jpeg, new byte[512]);
    }

    private static byte[] Stsz(uint uniform, uint count, params byte[] tail) => Box("stsz", U32(0), U32(uniform), U32(count), tail);

    private static byte[] Co64(long offset) => Box("co64", U32(0), U32(1), U64((ulong)offset));

    private static RawContainerInfo Read(byte[] file) =>
        new Cr3ContainerReader().Read(new InMemoryRawHeaderSource(file), CancellationToken.None);

    [Fact]
    public void Read_StszUniformSizeAndChunkOffsetValid_YieldsTheTrackPreview_Control()
    {
        var file = FileWithTrack(off => [Stsz((uint)Jpeg.Length, 1), Co64(off)]);

        var preview = Assert.Single(Read(file).Previews);

        Assert.Equal(Jpeg.Length, preview.Length);
    }

    [Theory]
    [InlineData(false)] // 12-byte payload: no per-sample table at all
    [InlineData(true)] // 16-byte payload: a table entry exists but the count says there are no samples
    public void Read_StszUniformSizeZeroAndCountZero_YieldsNoPreviewAndDoesNotThrow(bool withTableEntry)
    {
        var tail = withTableEntry ? U32((uint)Jpeg.Length) : [];
        var file = FileWithTrack(off => [Stsz(0, 0, tail), Co64(off)]);

        Assert.Empty(Read(file).Previews);
    }

    [Theory]
    [InlineData(false)] // 8-byte stco payload: header only
    [InlineData(true)] // 12-byte payload: an offset is present but count is 0
    public void Read_StcoCountZero_YieldsNoPreviewAndDoesNotThrow(bool withOffsetEntry)
    {
        var file = FileWithTrack(off =>
        [
            Stsz((uint)Jpeg.Length, 1),
            Box("stco", U32(0), U32(0), withOffsetEntry ? U32((uint)off) : []),
        ]);

        Assert.Empty(Read(file).Previews);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_StszPayloadBetweenTwelveAndFifteenBytes_DoesNotReadASampleSizeFromTheNextBytes(int extraBytes)
    {
        // stsz with uniform size 0 and count 1 needs a 16-byte payload for its first table entry. With a 12..15-byte payload the
        // four bytes that follow the 12-byte header belong to whatever comes next (here a stray 0x00000100), not to this box.
        byte[] stray = [0, 0, 1, 0];
        var file = FileWithTrack(off =>
            [Co64(off), Stsz(0, 1, stray[..extraBytes]), stray[extraBytes..]]);

        Assert.Empty(Read(file).Previews);
    }
}
