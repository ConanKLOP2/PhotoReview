using System.IO;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Exact-value tests for <see cref="NefContainerReader"/>, <see cref="OrfContainerReader"/> and <see cref="Rw2ContainerReader"/>:
/// signature sniffing, IFD caps, orientation / sensor-size ranges, raw-vs-preview IFD choice, full-size JpgFromRaw margins and
/// MakerNote bounds. Every file is built from synthetic bytes.
/// </summary>
public sealed class NefOrfRw2MutationGapTests
{
    private static RawContainerInfo ReadNef(byte[] data) =>
        new NefContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static RawContainerInfo ReadOrf(byte[] data) =>
        new OrfContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static RawContainerInfo ReadRw2(byte[] data) =>
        new Rw2ContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static byte[] Jpeg(int width, int height, int padding = 0) =>
        [.. SyntheticRawBuilder.CreateMinimalJpeg(width, height), .. new byte[padding]];

    private static ushort Us(int value) => (ushort)value;

    // ---------------------------------------------------------------- CanRead

    public static TheoryData<string, int> SignatureBytes()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "nef-le", "nef-be", "orf-iiro", "orf-iirs", "orf-mmor", "orf-tiff", "rw2" })
        {
            for (int i = 0; i < 4; i++) data.Add(name, i);
        }

        return data;
    }

    private static (IRawContainerReader Reader, string Extension, byte[] Head) SignatureCase(string name) => name switch
    {
        "nef-le" => (new NefContainerReader(), ".nef", [0x49, 0x49, 0x2A, 0x00]),
        "nef-be" => (new NefContainerReader(), ".nef", [0x4D, 0x4D, 0x00, 0x2A]),
        "orf-iiro" => (new OrfContainerReader(), ".orf", [0x49, 0x49, 0x52, 0x4F]),
        "orf-iirs" => (new OrfContainerReader(), ".orf", [0x49, 0x49, 0x52, 0x53]),
        "orf-mmor" => (new OrfContainerReader(), ".orf", [0x4D, 0x4D, 0x4F, 0x52]),
        "orf-tiff" => (new OrfContainerReader(), ".orf", [0x49, 0x49, 0x2A, 0x00]),
        "rw2" => (new Rw2ContainerReader(), ".rw2", [0x49, 0x49, 0x55, 0x00]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("nef-le")]
    [InlineData("nef-be")]
    [InlineData("orf-iiro")]
    [InlineData("orf-iirs")]
    [InlineData("orf-mmor")]
    [InlineData("orf-tiff")]
    [InlineData("rw2")]
    public void CanRead_ExactlyFourSignatureBytes_IsAccepted(string name)
    {
        var (reader, extension, head) = SignatureCase(name);

        Assert.True(reader.CanRead(head, extension));
        Assert.False(reader.CanRead(head.AsSpan(0, 3), extension));
    }

    [Theory]
    [MemberData(nameof(SignatureBytes))]
    public void CanRead_AnySingleSignatureByteWrong_IsRejected(string name, int position)
    {
        var (reader, extension, head) = SignatureCase(name);
        head[position] ^= 0x40;

        Assert.False(reader.CanRead(head, extension));
    }

    // ---------------------------------------------------------------- short file / bad magic

    [Fact]
    public void Read_SixteenByteFileWithEmptyIfd_ReturnsEmptyInfoForNefOrfAndRw2()
    {
        foreach (var info in new[]
        {
            ReadNef(new TiffBytes(true, 16).Header(8).Ifd(8, 0).ToArray()),
            ReadOrf(new TiffBytes(true, 16).Header(8).Ifd(8, 0).ToArray()),
            ReadRw2(new TiffBytes(true, 16).Header(8, "IIU\0"u8).Ifd(8, 0).ToArray()),
        })
        {
            Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
            Assert.Equal(1, info.Orientation);
            Assert.Empty(info.Previews);
        }
    }

    [Fact]
    public void ReadNef_ValidByteOrderButMagicFortyThree_Throws()
    {
        var data = new TiffBytes(true, 64).Header(8, "II+\0"u8).Ifd(8, 0, Short(0x0100, 100)).ToArray();

        Assert.Throws<InvalidDataException>(() => ReadNef(data));
    }

    // ---------------------------------------------------------------- NEF: IFD chain, orientation, raw IFD choice

    [Fact]
    public void ReadNef_ChainOfSixtySixIfds_ReadsOnlyTheFirstSixtyFour()
    {
        const int ifdSize = 30;
        var file = new TiffBytes(true, 8 + (66 * ifdSize)).Header(8);
        for (int i = 0; i < 66; i++)
        {
            int at = 8 + (i * ifdSize);
            file.Ifd(at, i < 65 ? (uint)(at + ifdSize) : 0, Short(0x0100, Us(100 + i)), Short(0x0101, 100));
        }

        var info = ReadNef(file.ToArray());

        Assert.Equal((163, 100), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_OrientationOfSubIfdIsIgnoredInFavourOfIfd0()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 0, Short(0x0100, 4000), Short(0x0101, 3000), Short(0x0112, 6), Long(0x014A, 100))
            .Ifd(100, 0, Short(0x0100, 100), Short(0x0101, 100), Short(0x0112, 3))
            .ToArray();

        Assert.Equal(6, ReadNef(data).Orientation);
    }

    [Fact]
    public void ReadNef_ReducedResolutionIfdLargerThanRaw_DoesNotWin()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 0, Short(0x0100, 4000), Short(0x0101, 3000), Long(0x014A, 100))
            .Ifd(100, 0, Long(0x00FE, 1), Short(0x0100, 6000), Short(0x0101, 4500))
            .ToArray();

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_JpegPreviewIfdWithLargerDeclaredSize_IsNotTheRawImage()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 400 + jpeg.Length).Header(8)
            .Ifd(8, 0, Short(0x0100, 6000), Short(0x0101, 4000), Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length), Long(0x014A, 100))
            .Ifd(100, 0, Short(0x0100, 4000), Short(0x0101, 3000))
            .Put(400, jpeg)
            .ToArray();

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
        Assert.Single(info.Previews);
    }

    [Fact]
    public void ReadNef_TwoRawIfdsOfEqualArea_KeepsTheFirst()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 0, Short(0x0100, 4000), Short(0x0101, 3000), Long(0x014A, 100))
            .Ifd(100, 0, Short(0x0100, 3000), Short(0x0101, 4000))
            .ToArray();

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_OnlyReducedResolutionIfd_FallsBackToIfd0Size()
    {
        var data = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 0, Long(0x00FE, 1), Short(0x0100, 160), Short(0x0101, 120))
            .ToArray();

        var info = ReadNef(data);

        Assert.Equal((160, 120), (info.SensorWidth, info.SensorHeight));
    }

    // ---------------------------------------------------------------- NEF: full-size JpgFromRaw frame

    /// <summary>IFD0 = raw image of the given size announcing one SubIFD per preview; each preview IFD optionally declares a size.</summary>
    private static byte[] NefWithSubPreviews(int rawWidth, int rawHeight, params (int? Width, int? Height, byte[] Jpeg)[] previews)
    {
        const int arrayAt = 80;
        const int ifdsAt = 100;
        const int ifdStride = 56;
        const int dataAt = 400;
        var file = new TiffBytes(true, dataAt + previews.Sum(p => p.Jpeg.Length)).Header(8);
        Entry sub = previews.Length == 1 ? Long(0x014A, ifdsAt) : At(0x014A, 4, (uint)previews.Length, arrayAt);
        file.Ifd(8, 0, Short(0x0100, Us(rawWidth)), Short(0x0101, Us(rawHeight)), sub);
        int data = dataAt;
        for (int i = 0; i < previews.Length; i++)
        {
            file.U32(arrayAt + (i * 4), (uint)(ifdsAt + (i * ifdStride)));
            var entries = new List<Entry>();
            if (previews[i].Width is { } w) entries.Add(Short(0x0100, Us(w)));
            if (previews[i].Height is { } h) entries.Add(Short(0x0101, Us(h)));
            entries.Add(Long(0x0201, (uint)data));
            entries.Add(Long(0x0202, (uint)previews[i].Jpeg.Length));
            file.Ifd(ifdsAt + (i * ifdStride), 0, [.. entries]);
            file.Put(data, previews[i].Jpeg);
            data += previews[i].Jpeg.Length;
        }

        return file.ToArray();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadNef_PreviewDeclaringOnlyOneDimension_StillGetsItsFrameSizeRead(bool declareWidth, bool declareHeight)
    {
        var data = NefWithSubPreviews(4010, 3010, (declareWidth ? 4000 : null, declareHeight ? 3000 : null, Jpeg(4000, 3000)));

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(4000, 3010, 4000, 3000, 4000, 3000)] // zero horizontal margin is a match
    [InlineData(4010, 3000, 4000, 3000, 4000, 3000)] // zero vertical margin is a match
    [InlineData(4256, 3000, 4000, 3000, 4000, 3000)] // 256-pixel horizontal margin is the largest accepted
    [InlineData(4000, 3256, 4000, 3000, 4000, 3000)] // 256-pixel vertical margin is the largest accepted
    [InlineData(4257, 3000, 4000, 3000, 4257, 3000)] // 257 is too far: not the same image
    [InlineData(4000, 3257, 4000, 3000, 4000, 3257)]
    [InlineData(3990, 3000, 4000, 3000, 3990, 3000)] // frame larger than the raw IFD is never "masked margins"
    public void ReadNef_FullSizeJpegFrame_ReplacesRawSizeOnlyWithinTheMaskedMarginRange(
        int rawWidth, int rawHeight, int frameWidth, int frameHeight, int expectedWidth, int expectedHeight)
    {
        var info = ReadNef(NefWithSubPreviews(rawWidth, rawHeight, (null, null, Jpeg(frameWidth, frameHeight))));

        Assert.Equal((expectedWidth, expectedHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_TwoFramesWithinMargin_PicksTheLargerAreaEvenWhenTheSmallerFileIsBigger()
    {
        // The 3900x2900 frame has more bytes (so it is examined first) but the 4000x3000 frame has the larger area.
        var data = NefWithSubPreviews(4000, 3000, (null, null, Jpeg(4000, 3000)), (null, null, Jpeg(3900, 2900, padding: 500)));

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_TwoFramesOfEqualArea_KeepsTheFirstExamined()
    {
        // 4000x3000 and 3750x3200 both have area 12,000,000 and both lie within the margin of a 4000x3200 raw IFD.
        var data = NefWithSubPreviews(4000, 3200, (null, null, Jpeg(4000, 3000, padding: 500)), (null, null, Jpeg(3750, 3200)));

        var info = ReadNef(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_DefaultCropSizePresent_IsNotOverriddenByAJpegFrame()
    {
        var jpeg = Jpeg(3980, 2980);
        uint crop = 3990 | (2990u << 16);
        var data = new TiffBytes(true, 500 + jpeg.Length).Header(8)
            .Ifd(8, 0, Short(0x0100, 4000), Short(0x0101, 3000), At(0xC620, 3, 2, crop), Long(0x014A, 300))
            .Ifd(300, 0, Long(0x0201, 500), Long(0x0202, (uint)jpeg.Length))
            .Put(500, jpeg)
            .ToArray();

        var info = ReadNef(data);

        Assert.Equal((3990, 2990), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadNef_SameJpegReferencedByTwoIfds_IsListedOnce()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 400 + jpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length), Long(0x014A, 100))
            .Ifd(100, 0, Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length))
            .Put(400, jpeg)
            .ToArray();

        Assert.Single(ReadNef(data).Previews);
    }

    // ---------------------------------------------------------------- NEF: MakerNote

    private static byte[] NefWithExifNote(ushort noteTag, uint noteCount, int noteOffset, int fileSize)
    {
        // Valid type-2 Nikon note at noteOffset whose PreviewIFD (relative to the embedded TIFF header) lists a JPEG.
        var jpeg = Jpeg(640, 480);
        int tiffBase = noteOffset + 10;
        return new TiffBytes(true, fileSize).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(noteTag, 7, noteCount, (uint)noteOffset))
            .Put(noteOffset, "Nikon\0"u8)
            .Put(noteOffset + 6, [2, 0x10, 0, 0])
            .Put(tiffBase, "II*\0"u8)
            .U32(tiffBase + 4, 8)
            .Ifd(tiffBase + 8, 0, Long(0x0011, 100))
            .Ifd(tiffBase + 100, 0, Long(0x0201, 300), Long(0x0202, (uint)jpeg.Length))
            .Put(tiffBase + 300, jpeg)
            .ToArray();
    }

    [Theory]
    [InlineData((ushort)0x927C, 4u, 0)]  // 4 bytes of UNDEFINED data are stored inline: the "offset" is the data itself
    [InlineData((ushort)0x927C, 5u, 1)]
    [InlineData((ushort)0x9286, 200u, 0)] // a different tag with a large count is not the MakerNote
    public void ReadNef_MakerNoteEntry_IsAnOffsetOnlyForTagMakerNoteWithMoreThanFourBytes(ushort tag, uint count, int expectedPreviews)
    {
        var data = NefWithExifNote(tag, count, noteOffset: 120, fileSize: 1000);

        Assert.Equal(expectedPreviews, ReadNef(data).Previews.Count);
    }

    [Fact]
    public void ReadNef_MakerNoteStartingLessThanEighteenBytesBeforeEndOfFile_IsIgnoredWithoutThrowing()
    {
        var data = new TiffBytes(true, 200).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 8, 183))
            .ToArray();

        Assert.Empty(ReadNef(data).Previews);
    }

    [Fact]
    public void ReadNef_MakerNoteStartingAtOffsetEight_IsParsed()
    {
        var jpeg = Jpeg(640, 480);
        var data = new TiffBytes(true, 1000).Header(400)
            .Put(8, "Nikon\0"u8)
            .Put(14, [2, 0x10, 0, 0])
            .Put(18, "II*\0"u8)
            .U32(22, 8)
            .Ifd(26, 0, Long(0x0011, 100))
            .Ifd(118, 0, Long(0x0201, 600), Long(0x0202, (uint)jpeg.Length))
            .Ifd(400, 0, Long(0x8769, 450))
            .Ifd(450, 0, At(0x927C, 7, 200, 8))
            .Put(618, jpeg)
            .ToArray();

        var preview = Assert.Single(ReadNef(data).Previews);

        Assert.Equal(618, preview.Offset);
    }

    // ---------------------------------------------------------------- ORF: header, chain, orientation, size

    private static TiffBytes Orf(int size) => new TiffBytes(true, size).Header(8, "IIRO"u8);

    [Fact]
    public void ReadOrf_ChainOfSixtySixIfds_ListsOnlyTheFirstSixtyFourPreviews()
    {
        var jpeg = Jpeg(640, 480);
        const int ifdSize = 30;
        int jpegAt = 8 + (66 * ifdSize);
        var file = Orf(jpegAt + jpeg.Length);
        for (int i = 0; i < 66; i++)
        {
            int at = 8 + (i * ifdSize);
            file.Ifd(at, i < 65 ? (uint)(at + ifdSize) : 0, Long(0x0201, (uint)jpegAt), Long(0x0202, (uint)jpeg.Length));
        }

        file.Put(jpegAt, jpeg);

        Assert.Equal(64, ReadOrf(file.ToArray()).Previews.Count);
    }

    [Fact]
    public void ReadOrf_OrientationOfSecondIfd_IsIgnored()
    {
        var data = Orf(128)
            .Ifd(8, 60, Short(0x0112, 6))
            .Ifd(60, 0, Short(0x0112, 3))
            .ToArray();

        Assert.Equal(6, ReadOrf(data).Orientation);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(0, 6)]
    [InlineData(9, 6)]
    public void ReadOrf_SecondOrientationEntryInIfd0_OverridesOnlyWithinOneToEight(int second, int expected)
    {
        var data = Orf(128).Ifd(8, 0, Short(0x0112, 6), Short(0x0112, (uint)second)).ToArray();

        Assert.Equal(expected, ReadOrf(data).Orientation);
    }

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(4000, 0)]
    public void ReadOrf_Ifd0WithAZeroDimension_ReportsNoSensorSize(int width, int height)
    {
        var data = Orf(128).Ifd(8, 0, Short(0x0100, Us(width)), Short(0x0101, Us(height))).ToArray();

        var info = ReadOrf(data);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadOrf_SecondIfdSize_NeverReplacesIfd0Size()
    {
        var data = Orf(160)
            .Ifd(8, 60, Short(0x0100, 4000), Short(0x0101, 3000))
            .Ifd(60, 0, Short(0x0100, 100), Short(0x0101, 100))
            .ToArray();

        var info = ReadOrf(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    // ---------------------------------------------------------------- ORF: MakerNote

    [Theory]
    [InlineData(8u, 1)]
    [InlineData(5u, 1)]
    [InlineData(4u, 0)]
    public void ReadOrf_MakerNoteTagInIfd0_IsAnOffsetOnlyWithMoreThanFourBytes(uint count, int expectedPreviews)
    {
        var jpeg = Jpeg(640, 480);
        var data = Orf(400 + jpeg.Length)
            .Ifd(8, 0, At(0x927C, 7, count, 200))
            .Ifd(200, 0, At(0x0100, 7, (uint)jpeg.Length, 400))
            .Put(400, jpeg)
            .ToArray();

        var previews = ReadOrf(data).Previews;

        Assert.Equal(expectedPreviews, previews.Count);
        if (expectedPreviews > 0)
        {
            Assert.Equal(400, previews[0].Offset);
            Assert.Equal(jpeg.Length, previews[0].Length);
        }
    }

    [Theory]
    [InlineData(17, 1)] // 17 bytes remain: the note is parsed
    [InlineData(16, 0)] // exactly 16 bytes remain: the note is skipped
    public void ReadOrf_MakerNoteNearEndOfFile_IsParsedOnlyWhenMoreThanSixteenBytesRemain(int bytesBeforeEnd, int expectedPreviews)
    {
        var jpeg = Jpeg(640, 480);
        const int size = 600;
        int note = size - bytesBeforeEnd;
        var data = Orf(size)
            .Ifd(8, 0, At(0x927C, 7, 8, (uint)note))
            .Put(100, jpeg)
            .U16(note, 1)
            .U16(note + 2, 0x0100)
            .U16(note + 4, 7)
            .U32(note + 6, (uint)jpeg.Length)
            .U32(note + 10, 100)
            .ToArray();

        Assert.Equal(expectedPreviews, ReadOrf(data).Previews.Count);
    }

    [Theory]
    [InlineData(0x0100, 4u, 0)]   // 4 bytes are inline data, not an offset
    [InlineData(0x0100, 5u, 1)]
    [InlineData(0x0200, 250u, 0)] // another tag is not the thumbnail
    public void ReadOrf_NoteThumbnailEntry_NeedsTagAndMoreThanFourBytes(int tag, uint count, int expectedPreviews)
    {
        var jpeg = Jpeg(640, 480);
        var data = Orf(400 + jpeg.Length)
            .Ifd(8, 0, At(0x927C, 7, 8, 200))
            .Ifd(200, 0, At((ushort)tag, 7, count, 400))
            .Put(400, jpeg)
            .ToArray();

        Assert.Equal(expectedPreviews, ReadOrf(data).Previews.Count);
    }

    [Fact]
    public void ReadOrf_NoteThumbnailOutsideTheFile_IsSkippedWithoutThrowing()
    {
        var data = Orf(400)
            .Ifd(8, 0, At(0x927C, 7, 8, 200))
            .Ifd(200, 0, At(0x0100, 7, 200, 0x7FFFFF00))
            .ToArray();

        Assert.Empty(ReadOrf(data).Previews);
    }

    [Theory]
    [InlineData(3u, 0)]
    [InlineData(4u, 1)]
    public void ReadOrf_CameraSettingsPreview_NeedsAtLeastFourBytes(uint length, int expectedPreviews)
    {
        var data = Orf(600)
            .Ifd(8, 0, At(0x927C, 7, 8, 200))
            .Ifd(200, 0, At(0x2020, 13, 1, 300))
            .Ifd(300, 0, Long(0x0101, 500), Long(0x0102, length))
            .Put(500, [0xFF, 0xD8, 0xFF, 0xD9])
            .ToArray();

        var previews = ReadOrf(data).Previews;

        Assert.Equal(expectedPreviews, previews.Count);
        if (expectedPreviews > 0) Assert.Equal((500L, 4L), (previews[0].Offset, previews[0].Length));
    }

    [Fact]
    public void ReadOrf_NoteThumbnailOfAnAlreadyListedJpeg_IsNotListedTwice()
    {
        var jpeg = Jpeg(640, 480);
        var data = Orf(400 + jpeg.Length)
            .Ifd(8, 0, At(0x927C, 7, 8, 200), Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length))
            .Ifd(200, 0, At(0x0100, 7, (uint)jpeg.Length, 400))
            .Put(400, jpeg)
            .ToArray();

        Assert.Single(ReadOrf(data).Previews);
    }

    [Theory]
    [InlineData("OLYMPUS\0", 12, 8)]
    [InlineData("OM SYSTEM\0", 16, 12)]
    public void ReadOrf_SelfRelativeNote_ByteOrderNeedsBothMarkBytes(string signature, int ifdDelta, int markAt)
    {
        // The mark bytes are 'M','I': not "II", so the note is big-endian (the file itself is little-endian).
        var jpeg = Jpeg(640, 480);
        const int note = 200;
        const int jpegAt = 400;
        var noteBytes = new TiffBytes(false, 64)
            .Put(0, System.Text.Encoding.ASCII.GetBytes(signature))
            .Put(markAt, "MI"u8)
            .Ifd(ifdDelta, 0, At(0x0100, 7, (uint)jpeg.Length, jpegAt - note))
            .ToArray();
        var data = Orf(jpegAt + jpeg.Length)
            .Ifd(8, 0, At(0x927C, 7, 64, note))
            .Put(note, noteBytes)
            .Put(jpegAt, jpeg)
            .ToArray();

        var preview = Assert.Single(ReadOrf(data).Previews);

        Assert.Equal(jpegAt, preview.Offset);
    }

    // ---------------------------------------------------------------- RW2

    private static TiffBytes Rw2(int size) => new TiffBytes(true, size).Header(8, "IIU\0"u8);

    [Fact]
    public void ReadRw2_EqualBorders_FallBackToSensorSizeTags()
    {
        var data = Rw2(256)
            .Ifd(8, 0, Long(0x0002, 6000), Long(0x0003, 4000), Long(0x0004, 50), Long(0x0005, 100), Long(0x0006, 50), Long(0x0007, 100))
            .ToArray();

        var info = ReadRw2(data);

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRw2_BordersOfIntMax_AreAccepted()
    {
        var data = Rw2(256)
            .Ifd(8, 0, Long(0x0004, 0), Long(0x0005, 0), Long(0x0006, int.MaxValue), Long(0x0007, int.MaxValue))
            .ToArray();

        var info = ReadRw2(data);

        Assert.Equal((int.MaxValue, int.MaxValue), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRw2_BordersCropTheSensorSizeTags()
    {
        var data = Rw2(256)
            .Ifd(8, 0, Long(0x0002, 6024), Long(0x0003, 4016), Long(0x0004, 8), Long(0x0005, 24), Long(0x0006, 4008), Long(0x0007, 6024))
            .ToArray();

        var info = ReadRw2(data);

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ReadRw2_SensorSizeTagOfIntMax_IsAcceptedAndAboveIntMaxIsRejected()
    {
        var ok = Rw2(128).Ifd(8, 0, Long(0x0002, int.MaxValue), Long(0x0003, int.MaxValue)).ToArray();
        var tooBig = Rw2(128).Ifd(8, 0, Long(0x0002, 0xFFFFFFFF), Long(0x0003, 0x80000000)).ToArray();

        var accepted = ReadRw2(ok);
        var rejected = ReadRw2(tooBig);

        Assert.Equal((int.MaxValue, int.MaxValue), (accepted.SensorWidth, accepted.SensorHeight));
        Assert.Equal((0, 0), (rejected.SensorWidth, rejected.SensorHeight));
    }

    [Theory]
    [InlineData(4u, 0)]
    [InlineData(5u, 1)]
    public void ReadRw2_JpgFromRaw_NeedsMoreThanFourBytes(uint length, int expectedPreviews)
    {
        var data = Rw2(128).Ifd(8, 0, At(0x002E, 4, length, 64)).Put(64, [0xFF, 0xD8, 0xFF, 0xD9, 0x00]).ToArray();

        Assert.Equal(expectedPreviews, ReadRw2(data).Previews.Count);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(200_000, 128 * 1024)]
    public void ReadRw2_ExifBlockCoversThePreviewUpToOneHundredTwentyEightKiB(int jpegLength, int expectedBlockLength)
    {
        var jpeg = Jpeg(640, 480, padding: jpegLength - Jpeg(640, 480).Length);
        var data = Rw2(64 + jpeg.Length).Ifd(8, 0, At(0x002E, 4, (uint)jpeg.Length, 64)).Put(64, jpeg).ToArray();

        var block = Assert.Single(ReadRw2(data).ExifBlocks);

        Assert.Equal((64L, (long)expectedBlockLength, false), (block.Offset, block.Length, block.IsTiffHeader));
    }
}