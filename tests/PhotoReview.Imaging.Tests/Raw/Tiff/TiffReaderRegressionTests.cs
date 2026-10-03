using System.IO;
using PhotoReview.Imaging.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Hostile-input and layout regressions for the TIFF-family container readers (DNG, NEF, ORF, CR2, ARW, RW2)
/// and <see cref="TiffHeaderNavigator"/>. Files are laid out byte by byte so each bug shape is written exactly.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffReaderRegressionTests
{
    private static readonly byte[] BaselineJpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

    private static readonly byte[] Cr2Marker = "CR\x02\0\0\0\0\0"u8.ToArray();

    private static RawContainerInfo Read(IRawContainerReader reader, byte[] data) =>
        reader.Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    // ---------------------------------------------------------------- TiffHeaderNavigator

    [Fact]
    public void ReadTagUnsignedArray_CountAboveInt32Max_ReturnsBoundedListWithoutThrowing()
    {
        var file = new TiffBytes(true, 512).Header(8).ToArray();
        var entry = new TiffHeaderNavigator.TiffEntry(0x014A, 4, 0xFFFF_FFFFu, 100);

        var values = TiffHeaderNavigator.ReadTagUnsignedArray(new InMemoryRawHeaderSource(file), entry, true, 8);

        Assert.True(values.Count <= 8);
    }

    [Fact]
    public void ReadTagUnsignedArray_MaxItemsZero_ReturnsEmpty()
    {
        var file = new TiffBytes(true, 512).Header(8).ToArray();
        var entry = new TiffHeaderNavigator.TiffEntry(0x014A, 4, 2, 100);

        Assert.Empty(TiffHeaderNavigator.ReadTagUnsignedArray(new InMemoryRawHeaderSource(file), entry, true, 0));
    }

    [Fact]
    public void ReadTagUnsignedArray_RationalType_RoundsToWholeNumbers()
    {
        var file = new TiffBytes(true, 512).Header(8)
            .U32(100, 4672).U32(104, 1).U32(108, 31041).U32(112, 10)
            .ToArray();
        var entry = new TiffHeaderNavigator.TiffEntry(0xC620, 5, 2, 100);

        var values = TiffHeaderNavigator.ReadTagUnsignedArray(new InMemoryRawHeaderSource(file), entry, true, 2);

        Assert.Equal(new long[] { 4672, 3104 }, values);
    }

    [Theory]
    [InlineData(4u)] // inside the TIFF header
    [InlineData(0xFFFF_FFF0u)] // far beyond the file
    [InlineData(510u)] // no room for an entry count
    public void ReadIfdEntries_NextIfdPointerOutsideUsableRange_ReportsChainEnd(uint next)
    {
        var file = new TiffBytes(true, 512).Header(8).Ifd(8, next, Short(0x0112, 1)).ToArray();

        TiffHeaderNavigator.ReadIfdEntries(new InMemoryRawHeaderSource(file), 8, true, out uint reported);

        Assert.Equal(0u, reported);
    }

    [Fact]
    public void ReadIfdEntries_NextIfdPointerInsideFile_IsReturned()
    {
        var file = new TiffBytes(true, 512).Header(8).Ifd(8, 200, Short(0x0112, 1)).ToArray();

        TiffHeaderNavigator.ReadIfdEntries(new InMemoryRawHeaderSource(file), 8, true, out uint reported);

        Assert.Equal(200u, reported);
    }

    [Fact]
    public void TryReadSingleStrip_MultipleStrips_IsRejected()
    {
        var file = new TiffBytes(true, 512).Header(8).ToArray();
        var source = new InMemoryRawHeaderSource(file);
        var entries = new List<TiffHeaderNavigator.TiffEntry>
        {
            new(0x0111, 4, 2, 100),
            new(0x0117, 4, 2, 108),
        };

        Assert.False(TiffHeaderNavigator.TryReadSingleStrip(source, entries, true, out _, out _));
    }

    // ---------------------------------------------------------------- DNG

    /// <summary>IFD0 announces a lossless-JPEG raw SubIFD (SOF3, bigger than the preview) and a baseline preview SubIFD.</summary>
    private static (byte[] Data, long PreviewOffset) BuildDngWithLosslessRawAndPreview(bool cropAsRational)
    {
        byte[] sensor = new byte[BaselineJpeg.Length + 5000];
        AsLosslessJpeg(BaselineJpeg).CopyTo(sensor, 0);
        int sensorPos = 600;
        int previewPos = sensorPos + sensor.Length;

        var tiff = new TiffBytes(true, previewPos + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, At(0x014A, 4, 2, 300))
            .U32(300, 100).U32(304, 200)
            .Ifd(100, 0,
                Long(0x00FE, 0), Long(0x0100, 4000), Long(0x0101, 3000), Short(0x0103, 7),
                Long(0x0111, (uint)sensorPos), Long(0x0117, (uint)sensor.Length),
                cropAsRational ? At(0xC620, 5, 2, 400) : At(0xC620, 4, 2, 400))
            .Ifd(200, 0,
                Long(0x00FE, 1), Long(0x0100, 640), Long(0x0101, 480), Short(0x0103, 7),
                Long(0x0111, (uint)previewPos), Long(0x0117, (uint)BaselineJpeg.Length))
            .Put(sensorPos, sensor)
            .Put(previewPos, BaselineJpeg);
        if (cropAsRational) tiff.U32(400, 3900).U32(404, 1).U32(408, 2900).U32(412, 1);
        else tiff.U32(400, 3900).U32(404, 2900);
        return (tiff.ToArray(), previewPos);
    }

    [Fact]
    public void DngReader_LosslessJpegRawInSubIfd_IsNotListedAsPreview()
    {
        var (data, previewOffset) = BuildDngWithLosslessRawAndPreview(cropAsRational: false);

        var info = Read(new DngContainerReader(), data);

        var preview = Assert.Single(info.Previews);
        Assert.Equal(previewOffset, preview.Offset);
        Assert.Equal(BaselineJpeg.Length, preview.Length);
    }

    [Fact]
    public void DngReader_LosslessJpegWithoutNewSubFileType_IsNotListedAsPreview()
    {
        byte[] lossless = AsLosslessJpeg(BaselineJpeg);
        var data = new TiffBytes(true, 400 + lossless.Length).Header(8)
            .Ifd(8, 0, Long(0x0100, 800), Long(0x0101, 600), Short(0x0103, 7),
                Long(0x0111, 400), Long(0x0117, (uint)lossless.Length))
            .Put(400, lossless)
            .ToArray();

        var info = Read(new DngContainerReader(), data);

        Assert.Empty(info.Previews);
        Assert.Equal((800, 600), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DngReader_DefaultCropSize_UsesCropForSensorSizeWhetherLongOrRational(bool rational)
    {
        var (data, _) = BuildDngWithLosslessRawAndPreview(rational);

        var info = Read(new DngContainerReader(), data);

        Assert.Equal((3900, 2900), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void DngReader_JpegSplitOverSeveralStrips_IsNotListedAsPreview()
    {
        int half = BaselineJpeg.Length / 2;
        var data = new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x00FE, 1), Long(0x0100, 640), Long(0x0101, 480), Short(0x0103, 7),
                At(0x0111, 4, 2, 300), At(0x0117, 4, 2, 308))
            .U32(300, 700).U32(304, (uint)(700 + half))
            .U32(308, (uint)half).U32(312, (uint)(BaselineJpeg.Length - half))
            .Put(700, BaselineJpeg)
            .ToArray();

        var info = Read(new DngContainerReader(), data);

        Assert.Empty(info.Previews);
    }

    // ---------------------------------------------------------------- NEF

    [Fact]
    public void NefReader_ThumbnailInIfd0_TakesSensorSizeFromRawSubIfd()
    {
        var data = new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x00FE, 1), Long(0x0100, 160), Long(0x0101, 120), At(0x014A, 4, 2, 300))
            .U32(300, 100).U32(304, 200)
            .Ifd(100, 0, Long(0x00FE, 0), Long(0x0100, 7424), Long(0x0101, 4924), Short(0x0103, 34713),
                Long(0x0111, 5000), Long(0x0117, 1000))
            .Ifd(200, 0, Long(0x00FE, 1), Short(0x0103, 6), Long(0x0201, 700), Long(0x0202, (uint)BaselineJpeg.Length))
            .Put(700, BaselineJpeg)
            .ToArray();

        var info = Read(new NefContainerReader(), data);

        Assert.Equal((7424, 4924), (info.SensorWidth, info.SensorHeight));
        Assert.Equal(700, Assert.Single(info.Previews).Offset);
    }

    private static TiffBytes BuildNefWithNikonMakerNote(bool littleEndian, uint previewLength)
    {
        // MakerNote "Nikon\0" 02 10 00 00 + TIFF header at +10; PreviewIFD (0x0011) and JPEG offsets are relative to it.
        const int makerNote = 120;
        const int tiffBase = makerNote + 10;
        var tiff = new TiffBytes(littleEndian, 600 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 200, makerNote))
            .Put(makerNote, "Nikon\0"u8)
            .Put(makerNote + 6, [0x02, 0x10, 0x00, 0x00])
            .Put(tiffBase, littleEndian ? "II*\0"u8 : "MM\0*"u8)
            .U32(tiffBase + 4, 8)
            .Ifd(tiffBase + 8, 0, Long(0x0011, 100))
            .Ifd(tiffBase + 100, 0, Long(0x0201, 300), Long(0x0202, previewLength));
        return tiff;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NefReader_MakerNotePreviewIfd_OffsetsRelativeToMakerNoteTiffHeader(bool littleEndian)
    {
        var tiff = BuildNefWithNikonMakerNote(littleEndian, (uint)BaselineJpeg.Length)
            .Put(130 + 300, BaselineJpeg);

        var info = Read(new NefContainerReader(), tiff.ToArray());

        var preview = Assert.Single(info.Previews);
        Assert.Equal(130 + 300, preview.Offset);
        Assert.Equal(BaselineJpeg.Length, preview.Length);
    }

    [Fact]
    public void NefReader_MakerNotePreviewNotJpeg_IsIgnored()
    {
        var tiff = BuildNefWithNikonMakerNote(true, 64);

        var info = Read(new NefContainerReader(), tiff.ToArray());

        Assert.Empty(info.Previews);
    }

    // ---------------------------------------------------------------- ORF

    private static readonly byte[] OlympusSignature = "OLYMPUS\0II\x03\0"u8.ToArray();
    private static readonly byte[] OmSystemSignature = "OM SYSTEM\0\0\0II\x04\0"u8.ToArray();

    public static TheoryData<byte[]> NoteSignatures => new() { OlympusSignature, OmSystemSignature };

    /// <summary>Olympus file whose CameraSettings preview start/length are relative to the MakerNote start.</summary>
    private static byte[] BuildOrf(byte[] noteSignature, uint makerNoteCount, byte[]? previewBytes = null)
    {
        previewBytes ??= BaselineJpeg;
        const int makerNote = 200;
        const uint settingsRelative = 100;
        const uint previewRelative = 400;
        return new TiffBytes(true, makerNote + (int)previewRelative + previewBytes.Length).Header(8, "IIRO"u8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0x927C, 7, makerNoteCount, makerNote))
            .Put(makerNote, noteSignature)
            .Ifd(makerNote + noteSignature.Length, 0, Long(0x2020, settingsRelative))
            .Ifd(makerNote + (int)settingsRelative, 0,
                Long(0x0101, previewRelative), Long(0x0102, (uint)previewBytes.Length))
            .Put(makerNote + (int)previewRelative, previewBytes)
            .ToArray();
    }

    [Theory]
    [MemberData(nameof(NoteSignatures))]
    public void OrfReader_CameraSettingsPreview_OffsetsAreRelativeToMakerNoteStart(byte[] signature)
    {
        var data = BuildOrf(signature, 100);

        var info = Read(new OrfContainerReader(), data);

        var preview = Assert.Single(info.Previews);
        Assert.Equal(200 + 400, preview.Offset);
        Assert.Equal(BaselineJpeg.Length, preview.Length);
    }

    [Fact]
    public void OrfReader_CameraSettingsPreviewNotJpeg_IsIgnored()
    {
        var data = BuildOrf(OlympusSignature, 100, new byte[BaselineJpeg.Length]);

        Assert.Empty(Read(new OrfContainerReader(), data).Previews);
    }

    [Fact]
    public void OrfReader_MakerNoteEntryHoldingInlineBytes_IsNotFollowedAsOffset()
    {
        // Count 4 means the four value bytes ARE the data; they must not be dereferenced as a MakerNote offset.
        var data = BuildOrf(OlympusSignature, 4);

        Assert.Empty(Read(new OrfContainerReader(), data).Previews);
    }

    [Fact]
    public void OrfReader_MakerNoteTruncatedNearEndOfFile_IsSkippedWithoutThrowing()
    {
        // 16..31 bytes remain after the MakerNote start: less than the 32-byte header probe.
        var data = new TiffBytes(true, 300).Header(8, "IIRO"u8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0x927C, 7, 100, 280))
            .ToArray();

        var info = Read(new OrfContainerReader(), data);

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void OrfReader_UnprefixedMakerNoteInBigEndianFile_UsesFileByteOrderAndAbsoluteOffsets()
    {
        const int makerNote = 200;
        var data = new TiffBytes(false, 900 + BaselineJpeg.Length).Header(8, "MMOR"u8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0x927C, 7, 100, makerNote))
            .Ifd(makerNote, 0, Long(0x2020, 500)) // absolute: no signature, so no MakerNote-relative base
            .Ifd(500, 0, Long(0x0101, 900), Long(0x0102, (uint)BaselineJpeg.Length))
            .Put(900, BaselineJpeg)
            .ToArray();

        var info = Read(new OrfContainerReader(), data);

        Assert.Equal(900, Assert.Single(info.Previews).Offset);
    }

    // ---------------------------------------------------------------- CR2

    [Fact]
    public void Cr2Reader_Ifd0IsFullSizePreview_TakesSensorSizeFromExifPixelDimensions()
    {
        // sRAW: IFD0 preview is 5184x3456 while the actual image is 2592x1728.
        var data = new TiffBytes(true, 600 + BaselineJpeg.Length).Header(16)
            .Put(8, Cr2Marker)
            .Ifd(16, 0, Short(0x0100, 5184), Short(0x0101, 3456), Short(0x0103, 6),
                Long(0x0111, 600), Long(0x0117, (uint)BaselineJpeg.Length), Long(0x8769, 300))
            .Ifd(300, 0, Short(0xA002, 2592), Short(0xA003, 1728))
            .Put(600, BaselineJpeg)
            .ToArray();

        var info = Read(new Cr2ContainerReader(), data);

        Assert.Equal((2592, 1728), (info.SensorWidth, info.SensorHeight));
        Assert.Equal(600, Assert.Single(info.Previews).Offset);
    }

    [Fact]
    public void Cr2Reader_NoExifDimensions_FallsBackToLargestIfd()
    {
        var data = new TiffBytes(true, 900).Header(16)
            .Put(8, Cr2Marker)
            .Ifd(16, 200, Short(0x0100, 400), Short(0x0101, 300))
            .Ifd(200, 0, Short(0x0100, 6880), Short(0x0101, 4544))
            .ToArray();

        var info = Read(new Cr2ContainerReader(), data);

        Assert.Equal((6880, 4544), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Cr2Reader_PreviewStoredOverSeveralStrips_IsNotListed()
    {
        var data = new TiffBytes(true, 600 + BaselineJpeg.Length).Header(16)
            .Put(8, Cr2Marker)
            .Ifd(16, 0, Short(0x0103, 6), At(0x0111, 4, 2, 300), At(0x0117, 4, 2, 308))
            .U32(300, 600).U32(304, 610)
            .U32(308, 10).U32(312, (uint)(BaselineJpeg.Length - 10))
            .Put(600, BaselineJpeg)
            .ToArray();

        Assert.Empty(Read(new Cr2ContainerReader(), data).Previews);
    }

    // ---------------------------------------------------------------- ARW

    [Fact]
    public void ArwReader_JpegInSubIfd_IsListedAndRawSubIfdSetsSensorSize()
    {
        var data = new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, At(0x014A, 4, 2, 300))
            .U32(300, 100).U32(304, 200)
            .Ifd(100, 0, Long(0x00FE, 0), Short(0x0100, 6048), Short(0x0101, 4024), Short(0x0103, 32767))
            .Ifd(200, 0, Long(0x00FE, 1), Short(0x0100, 640), Short(0x0101, 480),
                Long(0x0201, 700), Long(0x0202, (uint)BaselineJpeg.Length))
            .Put(700, BaselineJpeg)
            .ToArray();

        var info = Read(new ArwContainerReader(), data);

        Assert.Equal((6048, 4024), (info.SensorWidth, info.SensorHeight));
        var preview = Assert.Single(info.Previews);
        Assert.Equal((700L, 640, 480), (preview.Offset, preview.Width, preview.Height));
    }

    // SYNTHETIC ONLY: no file in the real corpus (A7M3, A7R, NEX-6) carries the Sony MakerNote 0x2001 PreviewImage tag,
    // so this layout follows the documented tag format and is not verified against a real camera file.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArwReader_MakerNotePreviewImage_IsListed(bool sonyDscHeader)
    {
        int noteIfd = sonyDscHeader ? 200 + 12 : 200;
        var tiff = new TiffBytes(true, 700 + BaselineJpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x8769, 60))
            .Ifd(60, 0, At(0x927C, 7, 100, 200));
        if (sonyDscHeader) tiff.Put(200, "SONY DSC \0\0\0"u8);
        tiff.Ifd(noteIfd, 0, At(0x2001, 7, (uint)BaselineJpeg.Length, 700))
            .Put(700, BaselineJpeg);

        var info = Read(new ArwContainerReader(), tiff.ToArray());

        var preview = Assert.Single(info.Previews);
        Assert.Equal((700L, (long)BaselineJpeg.Length), (preview.Offset, preview.Length));
    }

    // ---------------------------------------------------------------- RW2

    private static byte[] BuildRw2(uint jpegCount, uint jpegOffset, int fileSize, params Entry[] extra)
    {
        var entries = new List<Entry> { At(0x002E, 7, jpegCount, jpegOffset) };
        entries.AddRange(extra);
        return new TiffBytes(true, fileSize).Header(8, "IIU\0"u8)
            .Ifd(8, 0, [.. entries])
            .Put(300, BaselineJpeg)
            .ToArray();
    }

    [Fact]
    public void Rw2Reader_JpegLengthBeyondEndOfFile_IsNotListed()
    {
        var data = BuildRw2(jpegCount: 5_000_000, jpegOffset: 300, fileSize: 300 + BaselineJpeg.Length);

        Assert.Empty(Read(new Rw2ContainerReader(), data).Previews);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rw2Reader_JpegOffsetWithinFourBytesOfEnd_IsIgnoredWithoutThrowing(int bytesBeforeEnd)
    {
        const int size = 512;
        var data = BuildRw2(jpegCount: 100, jpegOffset: (uint)(size - bytesBeforeEnd), fileSize: size);

        Assert.Empty(Read(new Rw2ContainerReader(), data).Previews);
    }

    [Fact]
    public void Rw2Reader_SensorBordersPresent_ImageSizeExcludesMargins()
    {
        var data = BuildRw2((uint)BaselineJpeg.Length, 300, 300 + BaselineJpeg.Length,
            Short(0x0002, 5264), Short(0x0003, 3904), Short(0x0004, 8), Short(0x0005, 12),
            Short(0x0006, 3896), Short(0x0007, 5196));

        var info = Read(new Rw2ContainerReader(), data);

        Assert.Equal((5184, 3888), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Rw2Reader_NoBorderTags_FallsBackToSensorWidthAndHeightTags()
    {
        var data = BuildRw2((uint)BaselineJpeg.Length, 300, 300 + BaselineJpeg.Length,
            Short(0x0002, 4060), Short(0x0003, 3016));

        var info = Read(new Rw2ContainerReader(), data);

        Assert.Equal((4060, 3016), (info.SensorWidth, info.SensorHeight));
    }

    // ---------------------------------------------------------------- Hostile shapes shared by every TIFF reader

    private static IRawContainerReader CreateReader(string extension) => extension switch
    {
        ".dng" => new DngContainerReader(),
        ".nef" => new NefContainerReader(),
        ".arw" => new ArwContainerReader(),
        ".cr2" => new Cr2ContainerReader(),
        ".orf" => new OrfContainerReader(),
        ".rw2" => new Rw2ContainerReader(),
        _ => throw new ArgumentOutOfRangeException(nameof(extension)),
    };

    [Theory]
    [InlineData(".dng")]
    [InlineData(".nef")]
    [InlineData(".arw")]
    [InlineData(".cr2")]
    [InlineData(".orf")]
    [InlineData(".rw2")]
    public void Readers_ArrayEntryCountAboveInt32Max_DoNotThrowUnexpectedExceptions(string extension)
    {
        var data = new TiffBytes(true, 512).Header(16)
            .Put(8, Cr2Marker)
            .Ifd(16, 0,
                At(0x014A, 4, 0xFFFF_FFFFu, 100),
                At(0x0111, 4, 0xFFFF_FFFFu, 200),
                At(0x0117, 4, 0xFFFF_FFFFu, 300))
            .ToArray();
        data[2] = extension == ".rw2" ? (byte)0x55 : (byte)0x2A;

        var ex = Record.Exception(() => CreateReader(extension).Read(new InMemoryRawHeaderSource(data), CancellationToken.None));

        Assert.True(ex is null or InvalidDataException, ex?.ToString());
    }
}