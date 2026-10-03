using System.Buffers.Binary;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Exact-boundary tests for <see cref="TiffHeaderNavigator"/>: IFD entry reading and clamping, next-IFD pointer range, value
/// reads at the end of the source, the EXIF block extent, strip / crop / scale helpers and the active sensor size choice.
/// </summary>
public sealed class TiffHeaderNavigatorMutationGapTests
{
    private static InMemoryRawHeaderSource Source(byte[] bytes) => new(bytes);

    private static byte[] Entry(ushort tag, ushort type, uint count, uint value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return bytes;
    }

    // ---------------------------------------------------------------- ReadIfdEntries

    [Fact]
    public void ReadIfdEntries_OffsetInsideTheHeader_ReturnsNothing()
    {
        // Bytes 4..7 hold IFD0's offset (8): read as a count they would announce eight entries.
        var data = new TiffBytes(true, 200).Header(8).Ifd(8, 0, Short(0x0100, 1)).ToArray();

        Assert.Empty(TiffHeaderNavigator.ReadIfdEntries(Source(data), 4, true, out var next));
        Assert.Equal(0u, next);
    }

    [Fact]
    public void ReadIfdEntries_OffsetOneByteBeforeEndOfSource_ReturnsNothingWithoutThrowing()
    {
        var data = new byte[64];

        Assert.Empty(TiffHeaderNavigator.ReadIfdEntries(Source(data), 63, true, out _));
    }

    [Fact]
    public void ReadIfdEntries_CountLargerThanTheBytesThatExist_ReturnsOnlyTheWholeEntriesThatFit()
    {
        // Count 10 but only 3 whole entries (+5 bytes) fit after the count.
        var data = new TiffBytes(true, 8 + 2 + (3 * 12) + 5).Header(8)
            .Put(10, Entry(0x0100, 3, 1, 1)).Put(22, Entry(0x0101, 3, 1, 2)).Put(34, Entry(0x0102, 3, 1, 3))
            .U16(8, 10)
            .ToArray();

        var entries = TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Equal([(ushort)0x0100, (ushort)0x0101, (ushort)0x0102], entries.Select(e => e.Tag));
        Assert.Equal(0u, next);
    }

    [Fact]
    public void ReadIfdEntries_CountOfFourThousandNinetySix_StillReadsTheNextIfdPointer()
    {
        var entries = Enumerable.Repeat(Short(0x0100, 1), 4096).ToArray();
        var data = new TiffBytes(true, 8 + 2 + (4096 * 12) + 4).Header(8).Ifd(8, 8, entries).ToArray();

        var read = TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Equal(4096, read.Count);
        Assert.Equal(8u, next);
    }

    [Fact]
    public void ReadIfdEntries_CountAboveTheLimit_ReadsTheLimitAndEndsTheChain()
    {
        var entries = Enumerable.Repeat(Short(0x0100, 1), 4096).ToArray();
        var data = new TiffBytes(true, 8 + 2 + (4096 * 12) + 64).Header(8).Ifd(8, 8, entries).U16(8, 4097).ToArray();

        var read = TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Equal(4096, read.Count);
        Assert.Equal(0u, next);
    }

    [Theory]
    [InlineData(8u, 8u)]
    [InlineData(20u, 20u)]   // the largest accepted pointer: file length 26 - 6
    [InlineData(21u, 0u)]
    [InlineData(7u, 0u)]
    [InlineData(0u, 0u)]
    public void ReadIfdEntries_NextIfdPointer_MustLandAfterTheHeaderWithRoomForAnEmptyIfd(uint nextPointer, uint expected)
    {
        var data = new TiffBytes(true, 26).Header(8).Ifd(8, nextPointer, Short(0x0100, 1)).ToArray();

        TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Equal(expected, next);
    }

    [Fact]
    public void ReadIfdEntries_NextPointerFieldEndingExactlyAtEndOfSource_IsRead()
    {
        var data = new TiffBytes(true, 26).Header(8).Ifd(8, 12, Short(0x0100, 1)).ToArray();

        TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Equal(12u, next);
    }

    [Fact]
    public void ReadIfdEntries_NoRoomForTheNextPointer_ReturnsEntriesWithoutThrowing()
    {
        // One entry, then only two bytes of file: the 4-byte pointer does not exist.
        var data = new TiffBytes(true, 24).Header(8).U16(8, 1).Put(10, Entry(0x0100, 3, 1, 7)).ToArray();

        var entries = TiffHeaderNavigator.ReadIfdEntries(Source(data), 8, true, out var next);

        Assert.Single(entries);
        Assert.Equal(0u, next);
    }

    // ---------------------------------------------------------------- ReadTagUnsigned / ReadTagUnsignedArray

    private static TiffHeaderNavigator.TiffEntry E(ushort type, uint count, uint valueOrOffset) => new(0x0100, type, count, valueOrOffset);

    [Theory]
    [InlineData((ushort)0, 1u)] // unknown type
    [InlineData((ushort)3, 0u)] // no values
    public void ReadTagUnsigned_UnknownTypeOrZeroCount_IsNull(ushort type, uint count)
    {
        Assert.Null(TiffHeaderNavigator.ReadTagUnsigned(Source(new byte[32]), E(type, count, 6), true));
    }

    [Theory]
    [InlineData(28u, true)]   // value occupies the last four bytes
    [InlineData(29u, false)]  // would run one past the end
    [InlineData(100u, false)]
    public void ReadTagUnsigned_OutOfLineValue_MustLieFullyInsideTheSource(uint offset, bool expectedValue)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 0xCAFEBABE);

        var value = TiffHeaderNavigator.ReadTagUnsigned(Source(data), E(4, 2, offset), true);

        Assert.Equal(expectedValue ? 0xCAFEBABEL : null, value);
    }

    [Fact]
    public void ReadTagUnsigned_OutOfLineValueAtOffsetZero_ReadsTheFirstBytesOfTheSource()
    {
        var data = new TiffBytes(true, 32).Header(8).ToArray();

        Assert.Equal(0x002A4949L, TiffHeaderNavigator.ReadTagUnsigned(Source(data), E(4, 2, 0), true));
    }

    [Fact]
    public void ReadTagUnsignedArray_OutOfLineArray_MustLieFullyInsideTheSource()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 22);

        Assert.Equal([11L, 22L], TiffHeaderNavigator.ReadTagUnsignedArray(Source(data), E(4, 2, 24), true));
        Assert.Empty(TiffHeaderNavigator.ReadTagUnsignedArray(Source(data), E(4, 2, 25), true));
        Assert.Empty(TiffHeaderNavigator.ReadTagUnsignedArray(Source(data), E(4, 3, 24), true));
    }

    [Fact]
    public void ReadTagUnsignedArray_OutOfLineArrayAtOffsetZero_ReadsTheFirstBytesOfTheSource()
    {
        var data = new TiffBytes(true, 32).Header(8).ToArray();

        Assert.Equal([0x002A4949L, 8L], TiffHeaderNavigator.ReadTagUnsignedArray(Source(data), E(4, 2, 0), true));
    }

    // ---------------------------------------------------------------- IsRangeInFile / strips / exif pixel size

    [Theory]
    [InlineData(10L, 90L, 100L, true)]   // ends exactly at the end
    [InlineData(10L, 91L, 100L, false)]
    [InlineData(0L, 10L, 100L, false)]   // offset 0 is never a payload start
    [InlineData(10L, 0L, 100L, false)]
    [InlineData(1L, 1L, 100L, true)]
    [InlineData(-1L, 10L, 100L, false)]
    public void IsRangeInFile_Boundaries(long offset, long length, long fileLength, bool expected)
    {
        Assert.Equal(expected, TiffHeaderNavigator.IsRangeInFile(offset, length, fileLength));
    }

    [Theory]
    [InlineData(2u, 1u)]
    [InlineData(1u, 2u)]
    public void TryReadSingleStrip_MoreThanOneStripEntry_IsRejected(uint offsetCount, uint lengthCount)
    {
        var data = new byte[256];
        TiffHeaderNavigator.TiffEntry[] entries =
        [
            new(0x0111, 4, offsetCount, 100),
            new(0x0117, 4, lengthCount, 50),
        ];

        Assert.False(TiffHeaderNavigator.TryReadSingleStrip(Source(data), entries, true, out var offset, out var length));
        Assert.Equal((0L, 0L), (offset, length));
    }

    [Fact]
    public void TryReadSingleStrip_SingleStripInsideTheFile_IsReturned()
    {
        TiffHeaderNavigator.TiffEntry[] entries = [new(0x0111, 4, 1, 100), new(0x0117, 4, 1, 50)];

        Assert.True(TiffHeaderNavigator.TryReadSingleStrip(Source(new byte[256]), entries, true, out var offset, out var length));
        Assert.Equal((100L, 50L), (offset, length));
    }

    [Fact]
    public void TryReadExifPixelDimensions_OnlyOneOfWidthAndHeight_ReturnsFalseAndZeros()
    {
        var onlyHeight = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Long(0xA003, 480)).ToArray();
        var onlyWidth = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Long(0xA002, 640)).ToArray();
        var both = new TiffBytes(true, 128).Header(8).Ifd(8, 0, Long(0xA002, 640), Long(0xA003, 480)).ToArray();

        Assert.False(TiffHeaderNavigator.TryReadExifPixelDimensions(Source(onlyHeight), 8, true, out var w, out var h));
        Assert.Equal((0, 0), (w, h));
        Assert.False(TiffHeaderNavigator.TryReadExifPixelDimensions(Source(onlyWidth), 8, true, out w, out h));
        Assert.Equal((0, 0), (w, h));
        Assert.True(TiffHeaderNavigator.TryReadExifPixelDimensions(Source(both), 8, true, out w, out h));
        Assert.Equal((640, 480), (w, h));
    }

    // ---------------------------------------------------------------- DefaultCropSize / DefaultScale / active size

    private static (InMemoryRawHeaderSource Source, TiffHeaderNavigator.TiffEntry[] Entries) CropFile(uint count, uint first, uint second)
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), first);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), second);
        return (Source(data), [new TiffHeaderNavigator.TiffEntry(0xC620, 4, count, 16)]);
    }

    [Theory]
    [InlineData(2u, 6000u, 4000u, true, 6000, 4000)]
    [InlineData(2u, 2147483647u, 100u, true, int.MaxValue, 100)]
    [InlineData(2u, 100u, 2147483647u, true, 100, int.MaxValue)]
    [InlineData(2u, 0u, 100u, false, 0, 0)]
    [InlineData(2u, 100u, 0u, false, 0, 0)]
    [InlineData(2u, 2147483648u, 100u, false, 0, 0)]
    [InlineData(2u, 100u, 2147483648u, false, 0, 0)]
    [InlineData(1u, 6000u, 4000u, false, 0, 0)]
    public void TryReadDefaultCropSize_NeedsTwoPositiveIntRangeValues(uint count, uint first, uint second, bool expected, int width, int height)
    {
        var (source, entries) = CropFile(count, first, second);

        Assert.Equal(expected, TiffHeaderNavigator.TryReadDefaultCropSize(source, entries, true, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    private static (InMemoryRawHeaderSource Source, TiffHeaderNavigator.TiffEntry[] Entries) ScaleFile(
        uint hn, uint hd, uint vn, uint vd, ushort type = 5, uint count = 2, uint offset = 16, int length = 64)
    {
        var data = new byte[length];
        if (offset + 16 <= length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset), hn);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset + 4), hd);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset + 8), vn);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset + 12), vd);
        }

        return (Source(data), [new TiffHeaderNavigator.TiffEntry(0xC61E, type, count, offset)]);
    }

    [Fact]
    public void HasSquareDefaultScale_AbsentEntry_IsSquare()
    {
        Assert.True(TiffHeaderNavigator.HasSquareDefaultScale(Source(new byte[16]), [], true));
    }

    [Theory]
    [InlineData(1u, 1u, 1u, 1u, true)]
    [InlineData(4u, 2u, 4u, 2u, true)]   // equal ratios with a denominator other than 1
    [InlineData(2u, 1u, 1u, 1u, false)]
    [InlineData(1u, 2u, 1u, 3u, false)]
    [InlineData(0u, 1u, 1u, 1u, false)]  // zero numerator
    [InlineData(1u, 1u, 0u, 1u, false)]
    [InlineData(1u, 0u, 1u, 1u, false)]  // zero denominator
    [InlineData(1u, 1u, 1u, 0u, false)]
    [InlineData(0u, 0u, 1u, 1u, false)]
    [InlineData(1u, 1u, 0u, 0u, false)]
    public void HasSquareDefaultScale_RatioCheck(uint hn, uint hd, uint vn, uint vd, bool expected)
    {
        var (source, entries) = ScaleFile(hn, hd, vn, vd);

        Assert.Equal(expected, TiffHeaderNavigator.HasSquareDefaultScale(source, entries, true));
    }

    [Theory]
    [InlineData((ushort)5, 2u, 8u, 64, true)]     // offset 8: the first allowed
    [InlineData((ushort)5, 2u, 7u, 64, false)]
    [InlineData((ushort)5, 2u, 48u, 64, true)]    // data ends exactly at the end of the source
    [InlineData((ushort)5, 2u, 49u, 64, false)]
    [InlineData((ushort)5, 1u, 16u, 64, false)]   // a single rational is not a pair
    [InlineData((ushort)4, 2u, 16u, 64, false)]   // wrong type
    [InlineData((ushort)5, 3u, 16u, 64, true)]
    public void HasSquareDefaultScale_EntryShapeAndRange(ushort type, uint count, uint offset, int length, bool expected)
    {
        var (source, entries) = ScaleFile(1, 1, 1, 1, type, count, offset, length);

        Assert.Equal(expected, TiffHeaderNavigator.HasSquareDefaultScale(source, entries, true));
    }

    [Theory]
    [InlineData(4000, 3000, 3990, 2990, 3980, 2980, 3990, 2990)] // crop first
    [InlineData(4000, 3000, 4000, 3000, 3990, 2990, 4000, 3000)] // crop equal to raw still fits
    [InlineData(4000, 3000, 3990, 3000, 3980, 2980, 3990, 3000)] // equal height fits
    [InlineData(4000, 3000, 4000, 2990, 3980, 2980, 4000, 2990)] // equal width fits
    [InlineData(4000, 3000, 4001, 3000, 3980, 2980, 3980, 2980)] // crop wider than raw: exif
    [InlineData(4000, 3000, 3990, 3001, 3980, 2980, 3980, 2980)] // crop taller than raw: exif
    [InlineData(4000, 3000, 0, 100, 3980, 2980, 3980, 2980)]     // a zero crop side is no crop
    [InlineData(4000, 3000, 100, 0, 3980, 2980, 3980, 2980)]
    [InlineData(4000, 3000, 0, 0, 4001, 2980, 4000, 3000)]       // nothing fits: raw
    [InlineData(0, 3000, 50, 50, 0, 0, 50, 50)]                  // unknown raw width: any positive crop
    [InlineData(4000, 0, 50, 50, 0, 0, 50, 50)]                  // unknown raw height
    [InlineData(0, 0, 50, 50, 0, 0, 50, 50)]
    public void ChooseActiveSensorSize_PrefersCropThenExifThenRawWithinTheRawSize(
        int rawW, int rawH, int cropW, int cropH, int exifW, int exifH, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), TiffHeaderNavigator.ChooseActiveSensorSize(rawW, rawH, cropW, cropH, exifW, exifH));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    public void ReconcileJpegSize_OnlyOneDeclaredDimension_LeavesBothUntouched(int width, int height)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        TiffHeaderNavigator.ReconcileJpegSize(Source(jpeg), 0, jpeg.Length, ref width, ref height);

        Assert.Equal(width == 100 ? (100, 0) : (0, 100), (width, height));
    }

    [Fact]
    public void ReconcileJpegSize_BothDeclared_TheFrameHeaderWins()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        int width = 100, height = 50;

        TiffHeaderNavigator.ReconcileJpegSize(Source(jpeg), 0, jpeg.Length, ref width, ref height);

        Assert.Equal((640, 480), (width, height));
    }

    // ---------------------------------------------------------------- ComputeExifBlock

    private const int Default = 128 * 1024;

    [Fact]
    public void ComputeExifBlock_ExifIfdBeyondTheDefaultBlock_GrowsToCoverItsEntriesAndNextPointer()
    {
        var data = new TiffBytes(true, 300_000).Header(8)
            .Ifd(8, 0, Long(0x8769, 200_000))
            .Ifd(200_000, 0, Short(0x8827, 100), Short(0xA001, 1), Short(0x9209, 0))
            .ToArray();

        var block = TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8);

        Assert.Equal((0L, 200_000L + 2 + 36 + 4, true), (block.Offset, block.Length, block.IsTiffHeader));
    }

    [Fact]
    public void ComputeExifBlock_WantedOutOfLineValueBeyondTheDefaultBlock_GrowsToItsEnd()
    {
        var data = new TiffBytes(true, 300_000).Header(8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0xA434, 2, 20, 250_000))
            .ToArray();

        Assert.Equal(250_020L, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }

    [Fact]
    public void ComputeExifBlock_ExtentEndingExactlyAtTheEndOfTheFile_StillGrows()
    {
        var data = new TiffBytes(true, 250_020).Header(8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0xA434, 2, 20, 250_000))
            .ToArray();

        Assert.Equal(250_020L, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }

    [Fact]
    public void ComputeExifBlock_ExtentBeyondTheEndOfTheFile_KeepsTheDefaultBlock()
    {
        var data = new TiffBytes(true, 250_000).Header(8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0xA434, 2, 20, 250_000))
            .ToArray();

        Assert.Equal(Default, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }

    [Fact]
    public void ComputeExifBlock_UnwantedTagWithAFarValue_DoesNotGrowTheBlock()
    {
        var data = new TiffBytes(true, 300_000).Header(8)
            .Ifd(8, 0, At(0x9286, 7, 100, 250_000))
            .ToArray();

        Assert.Equal(Default, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }

    [Fact]
    public void ComputeExifBlock_WantedIfd0TagOtherThanTheFirstWithAFarValue_GrowsTheBlock()
    {
        var data = new TiffBytes(true, 300_000).Header(8)
            .Ifd(8, 0, At(0x0110, 2, 20, 250_000))
            .ToArray();

        Assert.Equal(250_020L, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }

    [Fact]
    public void ComputeExifBlock_FourByteInlineWantedValue_IsNotTreatedAsAnOffset()
    {
        // Make is 4 inline bytes: "ValueOrOffset" holds text, not an offset, so it must not stretch the block.
        var data = new TiffBytes(true, 300_000).Header(8)
            .Ifd(8, 0, At(0x010F, 2, 4, 0xFFFFFFF0), Long(0x8769, 200_000))
            .Ifd(200_000, 0, Short(0x8827, 100))
            .ToArray();

        Assert.Equal(200_000L + 2 + 12 + 4, TiffHeaderNavigator.ComputeExifBlock(Source(data), true, 8).Length);
    }
}