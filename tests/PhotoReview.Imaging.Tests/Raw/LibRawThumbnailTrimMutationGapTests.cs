using System.IO;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Mutation-testing gap tests (Stryker round 2) for <c>LibRawDecoder.TrimToEndOfImage</c> and <c>ToExtendedLengthPath</c>: the walk that
/// finds the real end of a padded RAW thumbnail and the extended-length path rule. A wrong end offset hands the JPEG decoder trailing
/// junk or a truncated stream, so the structure walk and the structure-free fallback are pinned separately: every input below ends in
/// a byte that is neither 0x00 nor 0xFF (no fast path) and carries a tail that the fallback would reject, so only a correct
/// structure walk returns the image.
/// </summary>
public sealed class LibRawThumbnailTrimMutationGapTests
{
    private static readonly byte[] Soi = [0xFF, 0xD8];
    private static readonly byte[] Eoi = [0xFF, 0xD9];

    /// <summary>Padding after the EOI that the structure-free fallback must reject (an APP1 marker inside it), ending in a plain byte.</summary>
    private static readonly byte[] RejectedByFallback = [0x12, 0xFF, 0xE1, 0x34];

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] Segment(byte marker, int payloadLength)
    {
        var length = payloadLength + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. new byte[payloadLength]];
    }

    private static byte[] Sos(params byte[] entropy) => Concat([0xFF, 0xDA, 0x00, 0x02], entropy);

    private static void AssertTrimmedTo(byte[] image)
    {
        var padded = Concat(image, RejectedByFallback);

        var trimmed = LibRawDecoder.TrimToEndOfImage(padded);

        Assert.Equal(image, trimmed);
    }

    // ---- header and trivial streams ----------------------------------------------------------------------------------------

    [Fact]
    public void TrimToEndOfImage_FourByteStreamOfSoiAndEoi_IsReturnedAsIs()
    {
        var bytes = Concat(Soi, Eoi);

        Assert.Same(bytes, LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    public void TrimToEndOfImage_StreamShorterThanFourBytes_IsRejectedAsIncompleteWithoutReadingPastIt(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Fact]
    public void TrimToEndOfImage_FirstByteIsFfButSecondIsNotSoi_IsRejected()
    {
        var bytes = Concat([0xFF, 0xE0, 0x11, 0x22], Eoi);

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Fact]
    public void TrimToEndOfImage_ZeroAndFfPaddingAfterTheEoi_IsTrimmed()
    {
        var image = Concat(Soi, [0x11, 0x22], Eoi);
        var padded = Concat(image, [0x00, 0xFF, 0x00, 0xFF, 0xFF]);

        Assert.Equal(image, LibRawDecoder.TrimToEndOfImage(padded));
    }

    // ---- structure walk ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TrimToEndOfImage_StandaloneMarkersBetweenSegments_AreSkippedWithoutALength()
    {
        // SOI again, RST0, RST7, TEM (0x01) and a stuffed 0x00 marker byte all carry no length field.
        var image = Concat(Soi, [0xFF, 0xD8], [0xFF, 0xD0], Segment(0xE0, 4), [0xFF, 0xD7], [0xFF, 0x01], [0xFF, 0x00], Eoi);

        AssertTrimmedTo(image);
    }

    [Fact]
    public void TrimToEndOfImage_SegmentLongerThan255Bytes_IsSkippedByItsFullLength()
    {
        var image = Concat(Soi, Segment(0xE1, 298), Eoi); // length field 0x012C

        AssertTrimmedTo(image);
    }

    [Fact]
    public void TrimToEndOfImage_SegmentWithAnEmptyPayload_IsAValidSegment()
    {
        var image = Concat(Soi, Segment(0xE1, 0), Eoi); // length field 2

        AssertTrimmedTo(image);
    }

    [Fact]
    public void TrimToEndOfImage_SegmentWithALengthBelowTwo_ReachesNoEoi()
    {
        var bytes = Concat(Soi, [0xFF, 0xE1, 0x00, 0x01, 0x11], Eoi, RejectedByFallback);

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Fact]
    public void TrimToEndOfImage_EntropyDataWithStuffingRestartsAndFill_IsWalkedToTheRealEoi()
    {
        // 0xFF00 is a stuffed data byte, FFD0 / FFD7 are restart markers, FFFF is fill: none of them ends the scan.
        var image = Concat(Soi, Sos(0x11, 0xFF, 0x00, 0x22, 0xFF, 0xD0, 0x33, 0xFF, 0xD7, 0x44, 0xFF, 0xFF, 0xFF), Eoi);

        AssertTrimmedTo(image);
    }

    [Fact]
    public void TrimToEndOfImage_ScanEndingWithAFinalFfByte_IsRejectedWithoutReadingPastTheEnd()
    {
        var bytes = Concat(Soi, Sos(0x11, 0x22, 0xFF));

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Fact]
    public void TrimToEndOfImage_StreamEndingRightAfterASegment_IsRejectedWithoutReadingPastTheEnd()
    {
        var bytes = Concat(Soi, [0xFF, 0xE0, 0x00, 0x04, 0x11, 0x22]);

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Fact]
    public void TrimToEndOfImage_StreamEndingRightAfterAMarkerByte_IsRejectedWithoutReadingPastTheEnd()
    {
        var bytes = Concat(Soi, [0xFF, 0xE0]);

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    // ---- structure-free fallback -------------------------------------------------------------------------------------------

    [Fact]
    public void TrimToEndOfImage_UnwalkableStreamWithAStructureFreeTail_ReturnsTheStreamUpToTheLastEoi()
    {
        var image = Concat(Soi, [0x11, 0x22], Eoi);
        var padded = Concat(image, [0x12, 0x13, 0xFF, 0x14]); // an FF followed by a plain byte is not structure

        Assert.Equal(image, LibRawDecoder.TrimToEndOfImage(padded));
    }

    [Fact]
    public void TrimToEndOfImage_TailEndingInAFfByte_IsStillRecognisedAsStructureFree()
    {
        var image = Concat(Soi, [0x11, 0x22], Eoi);
        var padded = Concat(image, [0x12, 0x13, 0xFF]); // the last FF has no marker byte after it

        Assert.Equal(image, LibRawDecoder.TrimToEndOfImage(padded));
    }

    [Fact]
    public void TrimToEndOfImage_StructureLikeBytesJustBeforeTheEoi_AreNotPartOfTheTail()
    {
        // FF DB sits right before the EOI: it precedes it, so it must not be judged as structure AFTER it.
        var image = Concat(Soi, [0x11, 0x11, 0xFF, 0xDB], Eoi);
        var padded = Concat(image, [0x12, 0x12, 0x12]);

        Assert.Equal(image, LibRawDecoder.TrimToEndOfImage(padded));
    }

    [Fact]
    public void TrimToEndOfImage_EoiAtTheStartOfTheLast64KbWindow_IsFound()
    {
        // 70000 bytes: the window starts at 70000 - 65536 = 4464, exactly where the EOI sits.
        var bytes = new byte[70_000];
        Array.Fill(bytes, (byte)0x12);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        const int EoiAt = 70_000 - 65_536;
        bytes[EoiAt] = 0xFF;
        bytes[EoiAt + 1] = 0xD9;

        var trimmed = LibRawDecoder.TrimToEndOfImage(bytes);

        Assert.Equal(EoiAt + 2, trimmed.Length);
    }

    [Theory]
    [InlineData(0xD8)]
    [InlineData(0xDA)]
    [InlineData(0xDB)]
    [InlineData(0xDD)]
    [InlineData(0xFE)]
    [InlineData(0xC0)]
    [InlineData(0xC8)]
    [InlineData(0xCF)]
    [InlineData(0xE0)]
    [InlineData(0xE8)]
    [InlineData(0xEF)]
    public void TrimToEndOfImage_TailContainingJpegStructure_IsRejectedAsATruncatedStream(int marker)
    {
        var bytes = Concat(Soi, [0x11, 0x22], Eoi, [0x12, 0xFF, (byte)marker, 0x34]);

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(bytes));
    }

    [Theory]
    [InlineData(0x12)]
    [InlineData(0xBF)] // just below the SOF/DHT range
    [InlineData(0xD1)] // a restart marker is not structure
    [InlineData(0xF0)] // just above the APPn range
    public void TrimToEndOfImage_TailWithAMarkerLikePairThatIsNotStructure_IsAccepted(int marker)
    {
        var image = Concat(Soi, [0x11, 0x22], Eoi);
        var padded = Concat(image, [0x12, 0xFF, (byte)marker, 0x34]);

        Assert.Equal(image, LibRawDecoder.TrimToEndOfImage(padded));
    }

    [Fact]
    public void TrimToEndOfImage_TailWithASecondEoi_KeepsEverythingUpToTheLastEoi()
    {
        var bytes = Concat(Soi, [0x11, 0x22], Eoi, [0x12], Eoi, [0x34]);

        Assert.Equal(bytes.Length - 1, LibRawDecoder.TrimToEndOfImage(bytes).Length);
    }
    // ---- ToExtendedLengthPath ----------------------------------------------------------------------------------------------

    [Fact]
    public void ToExtendedLengthPath_PathOfExactlyTheThresholdLength_GetsTheExtendedPrefix()
    {
        var path = @"C:\" + new string('a', LibRawDecoder.LongPathThreshold - 3);
        Assert.Equal(LibRawDecoder.LongPathThreshold, path.Length);

        Assert.Equal(@"\\?\" + path, LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Fact]
    public void ToExtendedLengthPath_LongPathThatCannotBeNormalised_IsLeftAlone()
    {
        // A NUL character makes Path.GetFullPath throw ArgumentException: the helper reports "no extended form", it never throws.
        var path = @"C:\" + new string('a', LibRawDecoder.LongPathThreshold) + "\0" + @"\x.cr2";

        Assert.Null(LibRawDecoder.ToExtendedLengthPath(path));
    }
}
