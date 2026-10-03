using System.Buffers.Binary;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>
/// Exact-boundary tests for <see cref="ExifRational"/>, <see cref="ExifSummary.Create"/> / text cleaning and the
/// <see cref="ExifSummaryCodec"/> date range.
/// </summary>
public sealed class ExifSummaryMutationGapTests
{
    [Theory]
    [InlineData(1u, 250u, 0.004)]
    [InlineData(3u, 2u, 1.5)]
    [InlineData(5u, 0u, 0.0)]
    [InlineData(0u, 0u, 0.0)]
    public void ExifRational_Value_IsNumeratorOverDenominatorOrZero(uint numerator, uint denominator, double expected)
    {
        Assert.Equal(expected, new ExifRational(numerator, denominator).Value, 12);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(10_000_000L, true)]
    [InlineData(10_000_001L, false)]
    public void Create_Iso_MustBePositiveAndAtMostTenMillion(long iso, bool expected)
    {
        var summary = ExifSummary.Create(null, "Canon", null, null, iso, null, null, null);

        Assert.Equal(expected ? (int?)iso : null, summary!.Iso);
    }

    [Theory]
    [InlineData(0u, 5u)]
    [InlineData(5u, 0u)]
    public void Create_RationalWithAZeroPart_IsDropped(uint numerator, uint denominator)
    {
        var rational = new ExifRational(numerator, denominator);

        var summary = ExifSummary.Create(null, "Canon", null, null, null, rational, rational, rational);

        Assert.Null(summary!.FocalLength);
        Assert.Null(summary.FNumber);
        Assert.Null(summary.ExposureTime);
    }

    [Fact]
    public void CleanText_TextStartingWithNul_IsNull()
    {
        Assert.Null(ExifSummary.CleanText("\0abc"));
        Assert.Equal("abc", ExifSummary.CleanText("abc\0def"));
    }

    [Fact]
    public void CleanText_CutThroughASurrogatePair_DropsTheHalfCharacter()
    {
        var text = new string('a', ExifSummary.MaxTextLength - 1) + "\uD83D\uDE00";

        Assert.Equal(new string('a', ExifSummary.MaxTextLength - 1), ExifSummary.CleanText(text));
    }

    private static byte[] DateBlock(long ticks)
    {
        var bytes = new byte[2 + 8];
        bytes[0] = 1;
        bytes[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), ticks);
        return bytes;
    }

    [Fact]
    public void Decode_DateAtTheExactEdgesOfTheDateTimeRange_IsAccepted()
    {
        Assert.Equal(DateTime.MinValue, ExifSummaryCodec.Decode(DateBlock(DateTime.MinValue.Ticks))!.DateTaken);
        Assert.Equal(DateTime.MaxValue, ExifSummaryCodec.Decode(DateBlock(DateTime.MaxValue.Ticks))!.DateTaken);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(3155378976000000000L)]
    public void Decode_DateOutsideTheDateTimeRange_IsNull(long ticks)
    {
        Assert.Null(ExifSummaryCodec.Decode(DateBlock(ticks)));
    }
}