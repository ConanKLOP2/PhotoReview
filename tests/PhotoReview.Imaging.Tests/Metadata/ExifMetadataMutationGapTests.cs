using System.Buffers.Binary;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>
/// Exact-value and exact-boundary tests for <see cref="ExifParser"/> (byte-order mark, IFD0 offset range, date priority, the
/// entry loop's upper bound, Exif pointer range), <see cref="TiffStructure"/> (value spans, ASCII, integer and rational readers)
/// and <see cref="ExifQueryInterpreter"/> (date priority, array and packed-rational query values).
/// </summary>
public sealed class ExifMetadataMutationGapTests
{
    private const uint AbcInline = 0x00434241; // "ABC\0" little-endian

    private static readonly byte[] DateOriginal = "2024:05:01 14:03:22\0"u8.ToArray();
    private static readonly byte[] DateDigitized = "2023:04:02 13:02:21\0"u8.ToArray();
    private static readonly byte[] DateTimeValue = "2022:03:03 12:01:20\0"u8.ToArray();

    private static byte[] FakeEntry(ushort tag, ushort type, uint count, uint value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return bytes;
    }

    // ---------------------------------------------------------------- ExifParser: byte-order mark

    [Fact]
    public void TryReadOrientation_ByteOrderMarkWithOnlyOneMatchingLetter_IsRejected()
    {
        var little = new TiffBytes(true, 64).Header(8).Ifd(8, 0, Short(0x0112, 6)).ToArray();
        var big = new TiffBytes(false, 64).Header(8).Ifd(8, 0, Short(0x0112, 6)).ToArray();
        Assert.Equal(6, ExifParser.TryReadOrientation(little));
        Assert.Equal(6, ExifParser.TryReadOrientation(big));

        little[1] = (byte)'M'; // "IM"
        big[1] = (byte)'I';    // "MI"

        Assert.Null(ExifParser.TryReadOrientation(little));
        Assert.Null(ExifParser.TryReadOrientation(big));
    }

    [Fact]
    public void TryParseTiffBlock_ByteOrderMarkWithOnlyOneMatchingLetter_IsRejected()
    {
        var little = new TiffBytes(true, 64).Header(8).Ifd(8, 0, At(0x010F, 2, 4, AbcInline)).ToArray();
        var big = new TiffBytes(false, 64).Header(8).Ifd(8, 0, At(0x010F, 2, 4, 0x41424300)).ToArray();
        Assert.Equal("ABC", ExifParser.TryParseTiffBlock(little)!.CameraMake);
        Assert.Equal("ABC", ExifParser.TryParseTiffBlock(big)!.CameraMake);

        little[1] = (byte)'M';
        big[1] = (byte)'I';

        Assert.Null(ExifParser.TryParseTiffBlock(little));
        Assert.Null(ExifParser.TryParseTiffBlock(big));
    }

    // ---------------------------------------------------------------- ExifParser: IFD0 offset range

    [Fact]
    public void TryReadOrientation_Ifd0AtOffsetSixteen_IsRead()
    {
        var tiff = new TiffBytes(true, 64).Header(16).Ifd(16, 0, Short(0x0112, 6)).ToArray();

        Assert.Equal(6, ExifParser.TryReadOrientation(tiff));
    }

    [Fact]
    public void TryReadOrientation_Ifd0OffsetInsideTheHeader_IsRejected()
    {
        // IFD at 2: count = 42 (from the magic), entry 1 lies at offset 16 and is a valid orientation entry.
        var tiff = new TiffBytes(true, 64).Header(2).Put(16, FakeEntry(0x0112, 3, 1, 6)).ToArray();

        Assert.Null(ExifParser.TryReadOrientation(tiff));
    }

    [Fact]
    public void TryParseTiffBlock_Ifd0OffsetInsideTheHeader_IsRejected()
    {
        var tiff = new TiffBytes(true, 64).Header(2).Put(16, FakeEntry(0x010F, 2, 4, AbcInline)).ToArray();

        Assert.Null(ExifParser.TryParseTiffBlock(tiff));
    }

    [Fact]
    public void TryParseTiffBlock_Ifd0AtOffsetSixteen_IsRead()
    {
        var tiff = new TiffBytes(true, 64).Header(16).Ifd(16, 0, At(0x010F, 2, 4, AbcInline)).ToArray();

        Assert.Equal("ABC", ExifParser.TryParseTiffBlock(tiff)!.CameraMake);
    }

    // ---------------------------------------------------------------- ExifParser: entry loop upper bound

    [Fact]
    public void TryReadOrientation_BytesAfterTheCountedEntriesThatLookLikeAnEntry_AreNotRead()
    {
        // One counted entry (not orientation); its next-IFD pointer and the bytes after it spell a valid orientation entry.
        var tiff = new TiffBytes(true, 40).Header(8).Ifd(8, 0, Short(0x0100, 5)).Put(22, FakeEntry(0x0112, 3, 1, 6)).ToArray();

        Assert.Null(ExifParser.TryReadOrientation(tiff));
    }

    [Fact]
    public void TryParseTiffBlock_BytesAfterTheCountedEntriesThatLookLikeAnEntry_AreNotRead()
    {
        var ifd0 = new TiffBytes(true, 40).Header(8).Ifd(8, 0, Short(0x0100, 5)).Put(22, FakeEntry(0x010F, 2, 4, AbcInline)).ToArray();
        var exifIfd = new TiffBytes(true, 40).Header(8).Ifd(8, 0, Short(0x0100, 5)).Put(22, FakeEntry(0x8827, 3, 1, 640)).ToArray();

        Assert.Null(ExifParser.TryParseTiffBlock(ifd0));
        Assert.Null(ExifParser.TryParseTiffBlock(exifIfd, ifdIsExif: true));
    }

    // ---------------------------------------------------------------- ExifParser: dates and Exif pointer

    private static byte[] DatesTiff(bool original, bool digitized, bool dateTime)
    {
        var exif = new List<Entry>();
        if (original) exif.Add(At(0x9003, 2, 20, 300));
        if (digitized) exif.Add(At(0x9004, 2, 20, 330));
        var ifd0 = new List<Entry> { Long(0x8769, 100) };
        if (dateTime) ifd0.Add(At(0x0132, 2, 20, 360));
        return new TiffBytes(true, 400).Header(8)
            .Ifd(8, 0, [.. ifd0])
            .Ifd(100, 0, [.. exif])
            .Put(300, DateOriginal)
            .Put(330, DateDigitized)
            .Put(360, DateTimeValue)
            .ToArray();
    }

    [Theory]
    [InlineData(true, true, true, 2024, 5, 1)]
    [InlineData(true, false, true, 2024, 5, 1)]
    [InlineData(false, true, true, 2023, 4, 2)]
    [InlineData(false, true, false, 2023, 4, 2)]
    [InlineData(false, false, true, 2022, 3, 3)]
    public void TryParseTiffBlock_Dates_OriginalBeatsDigitizedBeatsDateTime(bool original, bool digitized, bool dateTime, int year, int month, int day)
    {
        var summary = ExifParser.TryParseTiffBlock(DatesTiff(original, digitized, dateTime));

        Assert.Equal(year, summary!.DateTaken!.Value.Year);
        Assert.Equal((month, day), (summary.DateTaken.Value.Month, summary.DateTaken.Value.Day));
    }

    [Fact]
    public void TryParseTiffBlock_ExifPointerOfEight_IsFollowedOnce()
    {
        // The pointer addresses IFD0 itself; read in Exif mode it yields the ISO that sits in IFD0.
        var tiff = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 0, At(0x010F, 2, 4, AbcInline), Long(0x8769, 8), Short(0x8827, 100))
            .ToArray();

        var summary = ExifParser.TryParseTiffBlock(tiff);

        Assert.Equal("ABC", summary!.CameraMake);
        Assert.Equal(100, summary.Iso);
    }

    [Fact]
    public void TryParseTiffBlock_ExifPointerBelowEight_IsNotFollowed()
    {
        var tiff = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 0, At(0x010F, 2, 4, AbcInline), Long(0x8769, 7), Short(0x8827, 100))
            .ToArray();

        var summary = ExifParser.TryParseTiffBlock(tiff);

        Assert.Equal("ABC", summary!.CameraMake);
        Assert.Null(summary.Iso);
    }

    // ---------------------------------------------------------------- TiffStructure

    [Fact]
    public void TryReadHeader_ByteOrderMarkWithOnlyOneMatchingLetter_IsRejected()
    {
        Assert.True(TiffStructure.TryReadHeader("II*\0\x08\0\0\0"u8, out var little, out var magic, out var ifd0));
        Assert.True(little);
        Assert.Equal((42, 8u), (magic, ifd0));
        Assert.True(TiffStructure.TryReadHeader("MM\0*\0\0\0\x08"u8, out little, out magic, out ifd0));
        Assert.False(little);
        Assert.Equal((42, 8u), (magic, ifd0));

        Assert.False(TiffStructure.TryReadHeader("IM*\0\x08\0\0\0"u8, out _, out _, out _));
        Assert.False(TiffStructure.TryReadHeader("MI\0*\0\0\0\x08"u8, out _, out _, out _));
    }

    [Theory]
    [InlineData((ushort)3, 1u)]  // inline SHORT x 1
    [InlineData((ushort)4, 1u)]  // inline LONG x 1: the value ends exactly at the end of the block
    public void TryGetValueSpan_EntryThatIsExactlyTheWholeBlock_IsRead(ushort type, uint count)
    {
        var block = FakeEntry(0x0112, type, count, 0x00001234);

        Assert.True(TiffStructure.TryGetValueSpan(block, 0, type, count, true, out var value));
        Assert.Equal(type == 3 ? 2 : 4, value.Length);
        Assert.Equal(0x34, value[0]);
    }

    [Theory]
    [InlineData((ushort)0, 1u)] // unknown type
    [InlineData((ushort)3, 0u)] // zero count
    public void TryGetValueSpan_UnknownTypeOrZeroCount_HasNoValue(ushort type, uint count)
    {
        var block = FakeEntry(0x0112, type, count, 0x00001234);

        Assert.False(TiffStructure.TryGetValueSpan(block, 0, type, count, true, out _));
    }

    [Fact]
    public void ReadAscii_ValueStartingWithNul_IsNull()
    {
        Assert.Null(TiffStructure.ReadAscii("\0abc"u8, 2));
        Assert.Equal("ab", TiffStructure.ReadAscii("ab\0c"u8, 2));
        Assert.Equal("abc", TiffStructure.ReadAscii("abc"u8, 7));
    }

    [Fact]
    public void ReadUnsigned_ValuesOfExactlyTheTypeSize_AreReadAndShorterOnesAreNull()
    {
        Assert.Equal(7L, TiffStructure.ReadUnsigned([7], 1, true));
        Assert.Null(TiffStructure.ReadUnsigned([], 1, true));
        Assert.Equal(0x0201L, TiffStructure.ReadUnsigned([1, 2], 3, true));
        Assert.Null(TiffStructure.ReadUnsigned([1], 3, true));
        Assert.Equal(0x04030201L, TiffStructure.ReadUnsigned([1, 2, 3, 4], 4, true));
        Assert.Null(TiffStructure.ReadUnsigned([1, 2, 3], 4, true));
        Assert.Equal(-2L, TiffStructure.ReadUnsigned([0xFE, 0xFF], 8, true));
        Assert.Null(TiffStructure.ReadUnsigned([0xFE], 8, true));
        Assert.Null(TiffStructure.ReadUnsigned([], 8, true));
        Assert.Equal(-2L, TiffStructure.ReadUnsigned([0xFE, 0xFF, 0xFF, 0xFF], 9, true));
        Assert.Null(TiffStructure.ReadUnsigned([0xFE, 0xFF, 0xFF], 9, true));
        Assert.Null(TiffStructure.ReadUnsigned([], 9, true));
    }

    [Fact]
    public void ReadRational_FirstOfSeveralRationals_IsReturnedAndAShortValueIsNull()
    {
        byte[] two = [1, 0, 0, 0, 250, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0];

        Assert.Equal(new ExifRational(1, 250), TiffStructure.ReadRational(two, 5, true));
        Assert.Equal(new ExifRational(1, 250), TiffStructure.ReadRational(two.AsSpan(0, 8), 5, true));
        Assert.Null(TiffStructure.ReadRational(two.AsSpan(0, 7), 5, true));
    }

    [Theory]
    [InlineData(1, 250, true)]
    [InlineData(0, 250, false)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 250, false)]
    [InlineData(1, -250, false)]
    [InlineData(-1, -250, false)]
    public void ReadRational_SignedRational_NeedsPositiveNumeratorAndDenominator(int numerator, int denominator, bool expectedValue)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, numerator);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), denominator);

        var rational = TiffStructure.ReadRational(bytes, 10, true);

        Assert.Equal(expectedValue ? new ExifRational(1, 250) : null, rational);
    }

    // ---------------------------------------------------------------- ExifQueryInterpreter

    private static Func<string, object?> Query(string root, IReadOnlyDictionary<(bool Exif, ushort Tag), object> values) =>
        path =>
        {
            foreach (var ((exif, tag), value) in values)
            {
                var expected = exif ? $"{root}/exif/{{ushort={tag}}}" : $"{root}/{{ushort={tag}}}";
                if (path == expected) return value;
            }

            return null;
        };

    private static ExifSummary? Read(Dictionary<(bool Exif, ushort Tag), object> values, string root = ExifQueryInterpreter.JpegIfdRoot) =>
        ExifQueryInterpreter.Read(Query(root, values), root);

    private static ulong Packed(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;

    [Theory]
    [InlineData(ExifQueryInterpreter.JpegIfdRoot)]
    [InlineData(ExifQueryInterpreter.TiffIfdRoot)]
    public void Read_EveryTag_MapsToItsSummaryField(string root)
    {
        var summary = Read(new()
        {
            [(true, ExifParser.TagDateTimeOriginal)] = "2024:05:01 14:03:22",
            [(false, ExifParser.TagMake)] = "Canon",
            [(false, ExifParser.TagModel)] = "EOS R5".ToCharArray(),
            [(true, ExifParser.TagLensModel)] = "RF50",
            [(true, ExifParser.TagIso)] = (ushort)400,
            [(true, ExifParser.TagFocalLength)] = Packed(50, 1),
            [(true, ExifParser.TagFNumber)] = Packed(28, 10),
            [(true, ExifParser.TagExposureTime)] = Packed(1, 250),
        }, root);

        Assert.Equal(new DateTime(2024, 5, 1, 14, 3, 22), summary!.DateTaken);
        Assert.Equal("Canon", summary.CameraMake);
        Assert.Equal("EOS R5", summary.CameraModel);
        Assert.Equal("RF50", summary.LensModel);
        Assert.Equal(400, summary.Iso);
        Assert.Equal(new ExifRational(50, 1), summary.FocalLength);
        Assert.Equal(new ExifRational(28, 10), summary.FNumber);
        Assert.Equal(new ExifRational(1, 250), summary.ExposureTime);
    }

    [Theory]
    [InlineData(true, true, true, 2024)]
    [InlineData(true, false, true, 2024)]
    [InlineData(true, false, false, 2024)]
    [InlineData(false, true, true, 2023)]
    [InlineData(false, true, false, 2023)]
    [InlineData(false, false, true, 2022)]
    public void Read_Dates_OriginalBeatsDigitizedBeatsDateTime(bool original, bool digitized, bool dateTime, int expectedYear)
    {
        var values = new Dictionary<(bool Exif, ushort Tag), object>();
        if (original) values[(true, ExifParser.TagDateTimeOriginal)] = "2024:05:01 14:03:22";
        if (digitized) values[(true, ExifParser.TagDateTimeDigitized)] = "2023:04:02 13:02:21";
        if (dateTime) values[(false, ExifParser.TagDateTime)] = "2022:03:03 12:01:20";

        Assert.Equal(expectedYear, Read(values)!.DateTaken!.Value.Year);
    }

    [Fact]
    public void Read_EmptyArrays_AreIgnoredWithoutThrowing()
    {
        var summary = Read(new()
        {
            [(false, ExifParser.TagMake)] = "Canon",
            [(true, ExifParser.TagIso)] = Array.Empty<ushort>(),
            [(true, ExifParser.TagFocalLength)] = Array.Empty<ulong>(),
            [(true, ExifParser.TagFNumber)] = Array.Empty<long>(),
        });

        Assert.Equal("Canon", summary!.CameraMake);
        Assert.Null(summary.Iso);
        Assert.Null(summary.FocalLength);
        Assert.Null(summary.FNumber);
    }

    [Theory]
    [MemberData(nameof(EmptyIntegerArrays))]
    public void AsInteger_EmptyArray_IsNull(object empty)
    {
        Assert.Null(ExifQueryInterpreter.AsInteger(empty));
    }

    public static TheoryData<object> EmptyIntegerArrays() =>
        [Array.Empty<ushort>(), Array.Empty<uint>(), Array.Empty<short>(), Array.Empty<int>()];

    [Theory]
    [MemberData(nameof(IntegerValues))]
    public void AsInteger_SupportedWicTypes_ReturnTheFirstValue(object value)
    {
        Assert.Equal(400L, ExifQueryInterpreter.AsInteger(value));
    }

    public static TheoryData<object> IntegerValues()
    {
        var data = new TheoryData<object>();
        foreach (var value in new object[]
        {
            (ushort)400, (short)400, 400u, 400,
            new ushort[] { 400, 1 }, new uint[] { 400, 1 }, new short[] { 400, 1 }, new int[] { 400, 1 },
        })
        {
            data.Add(value);
        }

        return data;
    }

    [Fact]
    public void AsInteger_ByteAndUnsupportedTypes()
    {
        Assert.Equal(144L, ExifQueryInterpreter.AsInteger((byte)144));
        Assert.Null(ExifQueryInterpreter.AsInteger("400"));
        Assert.Null(ExifQueryInterpreter.AsInteger(null));
    }

    [Fact]
    public void AsRational_PackedUnsigned_SplitsNumeratorAndDenominator()
    {
        Assert.Equal(new ExifRational(1, 250), ExifQueryInterpreter.AsRational(Packed(1, 250)));
        ulong[] array = [Packed(1, 250), Packed(9, 9)];
        Assert.Equal(new ExifRational(1, 250), ExifQueryInterpreter.AsRational(array));
        Assert.Equal(new ExifRational(uint.MaxValue, 5), ExifQueryInterpreter.AsRational(Packed(uint.MaxValue, 5)));
    }

    [Theory]
    [InlineData(1, 250, true)]
    [InlineData(0, 250, false)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 250, false)]
    [InlineData(1, -250, false)]
    [InlineData(-1, -250, false)]
    public void AsRational_PackedSigned_NeedsPositiveNumeratorAndDenominator(int numerator, int denominator, bool expectedValue)
    {
        long packed = ((long)denominator << 32) | (uint)numerator;
        var expected = expectedValue ? new ExifRational(1, 250) : (ExifRational?)null;

        Assert.Equal(expected, ExifQueryInterpreter.AsRational(packed));
        long[] array = [packed, 0x0000_0001_0000_0001L];
        Assert.Equal(expected, ExifQueryInterpreter.AsRational(array));
    }

    [Fact]
    public void AsRational_EmptyArraysAndOtherTypes_AreNull()
    {
        Assert.Null(ExifQueryInterpreter.AsRational(Array.Empty<ulong>()));
        Assert.Null(ExifQueryInterpreter.AsRational(Array.Empty<long>()));
        Assert.Null(ExifQueryInterpreter.AsRational(2.8));
        Assert.Null(ExifQueryInterpreter.AsRational(null));
    }

    [Fact]
    public void AsString_StringAndCharArray_AreAcceptedOthersAreNull()
    {
        Assert.Equal("a", ExifQueryInterpreter.AsString("a"));
        char[] chars = ['a', 'b'];
        Assert.Equal("ab", ExifQueryInterpreter.AsString(chars));
        Assert.Null(ExifQueryInterpreter.AsString(5));
        Assert.Null(ExifQueryInterpreter.AsString(null));
    }

    [Fact]
    public void Read_NullQuery_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExifQueryInterpreter.Read(null!, ExifQueryInterpreter.JpegIfdRoot));
    }
}