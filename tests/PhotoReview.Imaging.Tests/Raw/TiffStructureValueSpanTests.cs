using System.Text;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// TiffStructure.TryGetValueSpan returns the whole value (every array element, all ASCII/UNDEFINED bytes up to a sane cap)
/// instead of only the first element of an out-of-line value.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffStructureValueSpanTests
{
    private const int Entry = 10; // entry offset inside the IFD at 8: 8 + 2
    private const int Data = 100;

    /// <summary>TIFF of <paramref name="size"/> bytes with a single IFD0 entry (tag 0x1234) of the given type/count pointing at <see cref="Data"/>.</summary>
    private static byte[] Build(ushort type, uint count, int size = 600) =>
        new TiffBytes(true, size).Header(8).Ifd(8, 0, At(0x1234, type, count, Data)).ToArray();

    private static bool TryGet(byte[] tiff, ushort type, uint count, out byte[] value)
    {
        bool ok = TiffStructure.TryGetValueSpan(tiff, Entry, type, count, true, out var span);
        value = ok ? span.ToArray() : [];
        return ok;
    }

    [Fact(DisplayName = "An entry whose offset field lies past the end of the block is rejected, not read from offset 0 (RV-I08)")]
    public void EntryTruncatedBeforeItsOffsetField_ReturnsFalse()
    {
        var tiff = new byte[20];
        "II"u8.CopyTo(tiff);
        tiff[2] = 42;
        tiff[14] = 0x34; tiff[15] = 0x12; // tag
        tiff[16] = 4; // LONG
        tiff[18] = 2; // count 2 -> 8 bytes, out of line; the 4-byte offset field would start at 22 (past the end)

        Assert.False(TiffStructure.TryGetValueSpan(tiff, 14, 4, 2, true, out _));
    }

    [Fact]
    public void ShortArray_OutOfLine_ReturnsEveryElement()
    {
        var tiff = Build(3, 5);
        for (int i = 0; i < 5; i++) tiff[Data + (i * 2)] = (byte)(i + 1);

        Assert.True(TryGet(tiff, 3, 5, out var value));
        Assert.Equal(10, value.Length);
        Assert.Equal(1, TiffStructure.ReadUnsigned(value, 3, true)); // scalar callers still read the first element
    }

    [Fact]
    public void RationalArray_ReturnsAllRationals()
    {
        Assert.True(TryGet(Build(5, 3), 5, 3, out var value));
        Assert.Equal(24, value.Length);
    }

    [Fact]
    public void Undefined_OutOfLine_ReturnsAllBytesAndReadsAsText()
    {
        var tiff = Build(7, 12);
        Encoding.ASCII.GetBytes("Canon EOS M\0").CopyTo(tiff, Data);

        Assert.True(TryGet(tiff, 7, 12, out var value));
        Assert.Equal(12, value.Length);
        Assert.Equal("Canon EOS M", TiffStructure.ReadAscii(value, 7));
    }

    [Fact]
    public void ReadAscii_UndefinedValueLongerThanTheTextCap_IsClampedToTheAsciiCap()
    {
        var value = Enumerable.Repeat((byte)'A', 4000).ToArray(); // UNDEFINED spans may be up to MaxValueBytes, with no NUL

        var text = TiffStructure.ReadAscii(value, 7);

        Assert.Equal(TiffStructure.MaxAsciiBytes, text!.Length);
    }

    [Fact]
    public void Ascii_LongerThanTheCap_IsClampedNotRejected()
    {
        var tiff = Build(2, 1000, size: 2000);
        Array.Fill(tiff, (byte)'A', Data, 1000);

        Assert.True(TryGet(tiff, 2, 1000, out var value));
        Assert.Equal(TiffStructure.MaxAsciiBytes, value.Length);
    }

    [Fact]
    public void Undefined_LongerThanTheValueCap_IsClampedToTheCap()
    {
        var tiff = Build(7, 100_000, size: 6000);

        Assert.True(TryGet(tiff, 7, 100_000, out var value));
        Assert.Equal(TiffStructure.MaxValueBytes, value.Length);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void Text_RunningPastTheEndOfTheBlock_IsRejected(int type) =>
        Assert.False(TryGet(Build((ushort)type, 200, size: Data + 30), (ushort)type, 200, out _));

    [Fact]
    public void Array_ClaimingMoreThanTheBlockHolds_KeepsTheWholeElementsThatExist()
    {
        Assert.True(TryGet(Build(4, 50, size: Data + 11), 4, 50, out var value));
        Assert.Equal(8, value.Length); // two whole LONGs
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void OffsetPastTheBlock_IsRejected(int type)
    {
        var tiff = Build((ushort)type, 40, size: 600);
        tiff[Entry + 8] = 0xFF; tiff[Entry + 9] = 0xFF; // offset 0xFFFF > length

        Assert.False(TryGet(tiff, (ushort)type, 40, out _));
    }

    [Fact]
    public void ArrayWithNoWholeElementInTheBlock_IsRejected() =>
        Assert.False(TryGet(Build(4, 50, size: Data + 3), 4, 50, out _));

    [Fact]
    public void InlineValues_AreUnchanged()
    {
        var tiff = new TiffBytes(true, 100).Header(8).Ifd(8, 0, Short(0x1234, 800)).ToArray();

        Assert.True(TiffStructure.TryGetValueSpan(tiff, Entry, 3, 1, true, out var value));
        Assert.Equal(2, value.Length);
        Assert.Equal(800, TiffStructure.ReadUnsigned(value, 3, true));
    }

    [Fact]
    public void ExifParser_MakeStoredAsOutOfLineUndefined_IsRead()
    {
        // IFD0 { Make (0x010F) as UNDEFINED x 12 at offset 100 }
        var tiff = new TiffBytes(true, 200).Header(8).Ifd(8, 0, At(0x010F, 7, 12, Data));
        tiff.Put(Data, "Canon EOS M\0"u8);

        var summary = ExifParser.TryParseTiffBlock(tiff.ToArray());

        Assert.Equal("Canon EOS M", summary?.CameraMake);
    }
}
