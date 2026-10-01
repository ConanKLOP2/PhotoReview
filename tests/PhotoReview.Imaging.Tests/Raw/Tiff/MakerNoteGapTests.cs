using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>MakerNote hostile-layout gaps: a Sony note offset that lands inside another IFD, and a Nikon note of a version that has no embedded TIFF header.</summary>
[Trait("Category", "HotPath")]
public sealed class MakerNoteGapTests
{
    private static readonly byte[] BaselineJpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

    private static RawContainerInfo Read(IRawContainerReader reader, byte[] data) =>
        reader.Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    // ---------------------------------------------------------------- Sony

    /// <summary>
    /// IFD0 at 8 (ExifIFD pointer + a 0x2001 entry whose length is absurd) and the Exif IFD at 60 whose MakerNote offset is
    /// <paramref name="noteOffset"/>, i.e. points into IFD0 itself or into the middle of one of its entries.
    /// </summary>
    private static byte[] SonyWithNoteInsideAnotherIfd(uint noteOffset) =>
        new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x8769, 60), At(0x2001, 7, 0x7FFF_FFF0, 700))
            .Ifd(60, 0, At(0x927C, 7, 100, noteOffset))
            .Put(700, BaselineJpeg)
            .ToArray();

    [Theory]
    [InlineData(8u)] // exactly IFD0: its entries are read as the note's entries
    [InlineData(10u)] // first entry, misaligned by two bytes
    [InlineData(18u)] // second entry
    [InlineData(62u)] // inside the Exif IFD that holds the pointer
    public void ArwReader_MakerNoteOffsetInsideAnotherIfd_ListsNoPreviewAndDoesNotThrow(uint noteOffset)
    {
        var info = Read(new ArwContainerReader(), SonyWithNoteInsideAnotherIfd(noteOffset));

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void ArwReader_MakerNoteWithValidPreviewEntry_ListsItExactlyOnce_Control()
    {
        var tiff = new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 100, 200))
            .Ifd(200, 0, At(0x2001, 7, (uint)BaselineJpeg.Length, 700))
            .Put(700, BaselineJpeg);

        var preview = Assert.Single(Read(new ArwContainerReader(), tiff.ToArray()).Previews);

        Assert.Equal(700L, preview.Offset);
    }

    // ---------------------------------------------------------------- Nikon

    private static byte[] NefWithNikonNote(byte versionByte)
    {
        // "Nikon\0" + version byte + 3 more bytes, then an embedded TIFF header at +10 (relative offsets); version 2 is the only layout that has it.
        const int makerNote = 120;
        const int tiffBase = makerNote + 10;
        return new TiffBytes(true, 600 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 200, makerNote))
            .Put(makerNote, "Nikon\0"u8)
            .Put(makerNote + 6, [versionByte, 0x10, 0x00, 0x00])
            .Put(tiffBase, "II*\0"u8)
            .U32(tiffBase + 4, 8)
            .Ifd(tiffBase + 8, 0, Long(0x0011, 100))
            .Ifd(tiffBase + 100, 0, Long(0x0201, 300), Long(0x0202, (uint)BaselineJpeg.Length))
            .Put(tiffBase + 300, BaselineJpeg)
            .ToArray();
    }

    [Fact]
    public void NefReader_NikonMakerNoteVersionTwo_ListsThePreview_Control()
    {
        var preview = Assert.Single(Read(new NefContainerReader(), NefWithNikonNote(0x02)).Previews);

        Assert.Equal(130 + 300, preview.Offset);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)] // type-1 note: the IFD follows directly, there is no embedded TIFF header to read offsets from
    [InlineData((byte)0x03)]
    [InlineData((byte)0xFF)]
    public void NefReader_NikonMakerNoteVersionOtherThanTwo_IsIgnored(byte version)
    {
        var info = Read(new NefContainerReader(), NefWithNikonNote(version));

        Assert.Empty(info.Previews);
    }
}
