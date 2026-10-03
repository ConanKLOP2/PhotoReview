using System.Buffers.Binary;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Exact-value tests for <see cref="PreviewSelector"/>: request-box fitting and transposition, the ranking score (viewable
/// threshold, unknown-size estimate), when a header walk is skipped, the JPEG marker walk (every marker class and bound) and the
/// strict Adobe RGB interoperability check.
/// </summary>
public sealed class PreviewSelectorMutationGapTests
{
    private static readonly byte[] Zeros = new byte[256];

    private static EmbeddedPreview Known(int index, int width, int height, long length = 100) =>
        new(index, 0, length, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown);

    private static int ChosenIndex(IReadOnlyList<EmbeddedPreview> previews, DecodeBox box, int orientation)
    {
        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(Zeros), previews, box, orientation);
        Assert.NotNull(chosen);
        return chosen.Index;
    }

    // ---------------------------------------------------------------- request box

    [Theory]
    [InlineData(0, 0, 1, 2)]        // unbounded: the largest
    [InlineData(-5, -5, 1, 2)]
    [InlineData(0, 1000, 1, 1)]     // one axis only
    [InlineData(1000, 0, 1, 1)]
    [InlineData(1000, 1000, 1, 1)]
    [InlineData(160, 120, 1, 0)]    // exact fit counts
    [InlineData(160, 0, 1, 0)]
    [InlineData(0, 120, 1, 0)]
    [InlineData(1620, 100, 1, 1)]
    [InlineData(100, 1080, 1, 1)]
    [InlineData(1621, 100, 1, 2)]   // one pixel too wide for the middle preview
    [InlineData(100, 1081, 1, 2)]   // one pixel too tall
    [InlineData(2000, 100, 1, 2)]   // both sides must fit: a tall-enough but too-narrow preview does not
    [InlineData(100, 1100, 1, 2)]   // a wide-enough preview that is too short does not either
    [InlineData(0, 1100, 1, 2)]
    [InlineData(1000, 1500, 1, 2)]
    [InlineData(1000, 1500, 6, 1)]  // transposed (portrait) orientation swaps the preview sides
    [InlineData(1000, 1500, 5, 1)]
    [InlineData(1000, 1500, 8, 1)]
    [InlineData(1000, 1500, 4, 2)]
    [InlineData(20000, 20000, 1, 2)] // nothing fits: the largest
    public void SelectPreview_RequestBox_PicksTheSmallestPreviewWhoseBothSidesFit(int boxWidth, int boxHeight, int orientation, int expectedIndex)
    {
        EmbeddedPreview[] previews = [Known(0, 160, 120), Known(1, 1620, 1080), Known(2, 6000, 4000)];

        Assert.Equal(expectedIndex, ChosenIndex(previews, new DecodeBox(boxWidth, boxHeight), orientation));
    }

    // ---------------------------------------------------------------- ranking score

    [Fact]
    public void SelectPreview_LongSideOfExactlyOneThousand_IsViewableAndOutranksALargerThumbnail()
    {
        EmbeddedPreview[] previews = [Known(0, 1000, 500), Known(1, 999, 999)];

        Assert.Equal(0, ChosenIndex(previews, DecodeBox.Unbounded, 1));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 0)]
    public void SelectPreview_PartiallyKnownSizeThatCannotBeResolved_IsRankedByItsByteLengthNotByAnAreaOfZero(int width, int height)
    {
        var source = new InMemoryRawHeaderSource(new byte[12_000]);
        EmbeddedPreview[] previews =
        [
            new(0, 0, 10_000, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown),
            new(1, 10_000, 100, EmbeddedPreviewKind.Jpeg, 160, 120, PreviewColorSpace.Unknown),
        ];

        var chosen = PreviewSelector.SelectPreview(source, previews, DecodeBox.Unbounded, 1);

        Assert.Equal(0, chosen!.Index);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 0)]
    public void SelectPreview_PartiallyKnownSizeOfARealJpeg_IsResolvedFromItsFrameHeader(int width, int height)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(2000, 1500);
        var file = new byte[jpeg.Length + 1000];
        jpeg.CopyTo(file, 0);
        EmbeddedPreview[] previews =
        [
            new(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown),
            new(1, jpeg.Length, 100, EmbeddedPreviewKind.Jpeg, 160, 120, PreviewColorSpace.Unknown),
        ];

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(file), previews, DecodeBox.Unbounded, 1);

        Assert.Equal((0, 2000, 1500), (chosen!.Index, chosen.Width, chosen.Height));
    }

    [Fact]
    public void SelectPreview_UnknownSizeJpegOfEightThousandBytes_OutranksA160x120Thumbnail()
    {
        // Estimated at 4 pixels per byte: 32,000 against the thumbnail's 19,200.
        var source = new InMemoryRawHeaderSource(new byte[8_100]);
        EmbeddedPreview[] previews =
        [
            new(0, 0, 8_000, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown),
            new(1, 8_000, 100, EmbeddedPreviewKind.Jpeg, 160, 120, PreviewColorSpace.Unknown),
        ];

        var chosen = PreviewSelector.SelectPreview(source, previews, DecodeBox.Unbounded, 1);

        Assert.Equal(0, chosen!.Index);
    }

    [Fact]
    public void SelectPreview_SingleUnknownSizePreviewOfARealJpeg_IsResolved()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        EmbeddedPreview[] previews = [new(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown)];

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), previews, DecodeBox.Unbounded, 1);

        Assert.Equal((640, 480), (chosen!.Width, chosen.Height));
    }

    // ---------------------------------------------------------------- when the header is (not) walked

    [Theory]
    [InlineData(256L)]  // offset == source length
    [InlineData(-1L)]
    public void SelectPreview_UnknownSizePreviewOutsideTheSource_IsLeftUnwalked(long offset)
    {
        EmbeddedPreview[] previews = [new(0, offset, 100, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown)];

        PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(Zeros), previews, DecodeBox.Unbounded, 1, out var resolved);

        Assert.False(Assert.Single(resolved).HeaderResolved);
    }

    [Theory]
    [InlineData(256L, 100L, false)] // offset == source length
    [InlineData(-1L, 100L, false)]
    [InlineData(0L, 4L, true)]      // exactly four bytes is still walked (and then found not to be a JPEG)
    [InlineData(0L, 3L, false)]
    [InlineData(0L, 100L, true)]
    public void SelectPreview_KnownSizePreview_ColourSpaceWalkHappensOnlyForAReadableRange(long offset, long length, bool expectedResolved)
    {
        EmbeddedPreview[] previews = [new(0, offset, length, EmbeddedPreviewKind.Jpeg, 1920, 1280, PreviewColorSpace.Unknown)];

        PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(Zeros), previews, DecodeBox.Unbounded, 1, out var resolved);

        Assert.Equal(expectedResolved, Assert.Single(resolved).HeaderResolved);
    }

    // ---------------------------------------------------------------- JPEG marker walk

    private static byte[] Soi => [0xFF, 0xD8];

    private static byte[] Sof(byte marker = 0xC0, int width = 640, int height = 480) =>
        [0xFF, marker, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00];

    private static byte[] Segment(byte marker, int payloadLength)
    {
        var bytes = new byte[4 + payloadLength];
        bytes[0] = 0xFF;
        bytes[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)(payloadLength + 2));
        return bytes;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static bool Walk(byte[] jpeg, out int width, out int height, out PreviewColorSpace colorSpace) =>
        PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out width, out height, out colorSpace);

    [Fact]
    public void TryReadJpegFrame_FrameHeaderEndingExactlyAtTheEndOfTheRange_IsRead()
    {
        Assert.True(Walk(Cat(Soi, Sof()), out var w, out var h, out _));
        Assert.Equal((640, 480), (w, h));
    }

    [Fact]
    public void TryReadJpegFrame_FrameHeaderCutOffByOneByte_IsRejected()
    {
        var jpeg = Cat(Soi, Sof());

        Assert.False(Walk(jpeg[..^1], out var w, out var h, out _));
        Assert.Equal((0, 0), (w, h));
    }

    [Fact]
    public void TryReadJpegFrame_FrameHeaderWithMinimumFiveBytePayload_IsRead()
    {
        byte[] sof = [0xFF, 0xC0, 0x00, 0x07, 0x08, 0x01, 0xE0, 0x02, 0x80];

        Assert.True(Walk(Cat(Soi, sof), out var w, out var h, out _));
        Assert.Equal((640, 480), (w, h));
    }

    [Fact]
    public void TryReadJpegFrame_FrameHeaderWithFourBytePayload_IsRejected()
    {
        byte[] sof = [0xFF, 0xC0, 0x00, 0x06, 0x08, 0x01, 0xE0, 0x02, 0x80];

        Assert.False(Walk(Cat(Soi, sof), out _, out _, out _));
    }

    [Fact]
    public void TryReadJpegFrame_EmptySegmentBeforeTheFrame_IsSkipped()
    {
        Assert.True(Walk(Cat(Soi, [0xFF, 0xE0, 0x00, 0x02], Sof()), out var w, out _, out _));
        Assert.Equal(640, w);
    }

    [Theory]
    [InlineData((byte)0xC4)] // DHT
    [InlineData((byte)0xC8)] // reserved
    [InlineData((byte)0xCC)] // DAC
    public void TryReadJpegFrame_TableOrReservedSegmentInsideTheSofRange_IsSkippedNotTreatedAsAFrame(byte marker)
    {
        Assert.True(Walk(Cat(Soi, Segment(marker, 20), Sof()), out var w, out var h, out _));
        Assert.Equal((640, 480), (w, h));
    }

    [Theory]
    [InlineData((byte)0xC0, true)]
    [InlineData((byte)0xC1, true)]
    [InlineData((byte)0xC2, true)]  // progressive
    [InlineData((byte)0xC3, false)] // lossless
    [InlineData((byte)0xC5, false)]
    [InlineData((byte)0xC9, false)] // arithmetic
    [InlineData((byte)0xCA, false)]
    [InlineData((byte)0xCF, false)] // the last marker of the SOF range still ends the walk
    public void TryReadJpegFrame_FirstFrameMarker_OnlyBaselineExtendedAndProgressiveAreAccepted(byte marker, bool expected)
    {
        // A valid baseline frame follows, so a walk that wrongly skipped the first marker would succeed.
        var jpeg = Cat(Soi, Sof(marker, 1000, 700), Sof(0xC0, 111, 222));

        Assert.Equal(expected, Walk(jpeg, out var w, out var h, out _));
        Assert.Equal(expected ? (1000, 700) : (0, 0), (w, h));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    public void TryReadJpegFrame_ZeroWidthOrHeight_IsRejected(int width, int height)
    {
        Assert.False(Walk(Cat(Soi, Sof(0xC0, width, height)), out _, out _, out _));
    }

    [Theory]
    [InlineData((byte)0x00)] // stuffed byte
    [InlineData((byte)0xDA)] // SOS
    [InlineData((byte)0xD9)] // EOI
    public void TryReadJpegFrame_EntropyDataOrEndMarkerBeforeTheFrame_EndsTheWalk(byte marker)
    {
        Assert.False(Walk(Cat(Soi, [0xFF, marker, 0x00, 0x02], Sof()), out _, out _, out _));
    }

    [Theory]
    [InlineData((byte)0x01)] // TEM
    [InlineData((byte)0xD0)] // RST0
    [InlineData((byte)0xD4)]
    [InlineData((byte)0xD8)] // second SOI
    public void TryReadJpegFrame_StandaloneMarkerBeforeTheFrame_IsSkippedWithoutALength(byte marker)
    {
        Assert.True(Walk(Cat(Soi, [0xFF, marker], Sof()), out var w, out var h, out _));
        Assert.Equal((640, 480), (w, h));
    }

    [Theory]
    [InlineData(511, true)]   // the frame header is the 512th segment: still visited
    [InlineData(512, false)]  // the 513th segment is beyond MaxJpegMarkerSegments
    public void TryReadJpegFrame_FrameHeaderAfterManySegments_IsFoundOnlyWithinTheSegmentCap(int segments, bool expected)
    {
        var parts = new List<byte[]> { Soi };
        for (int i = 0; i < segments; i++) parts.Add([0xFF, 0xE0, 0x00, 0x02]);
        parts.Add(Sof());

        Assert.Equal(expected, Walk(Cat([.. parts]), out _, out _, out _));
    }

    [Theory]
    [InlineData(65_536, true)]   // the fill-byte cap itself is allowed
    [InlineData(65_537, false)]
    public void TryReadJpegFrame_RunOfFillBytesBeforeTheFrame_IsToleratedUpToTheCap(int fillBytes, bool expected)
    {
        var fill = new byte[fillBytes];
        Array.Fill(fill, (byte)0xFF);

        Assert.Equal(expected, Walk(Cat(Soi, fill, Sof()), out _, out _, out _));
    }

    [Fact]
    public void TryReadJpegFrame_NoSoi_IsRejectedEvenWhenAFrameHeaderFollows()
    {
        Assert.False(Walk(Cat([0x00, 0x00], Sof()), out _, out _, out _));
    }

    [Theory]
    [InlineData(-1L, 100L)]
    [InlineData(1L, 100L)]   // offset == source length - 1: only one byte left
    [InlineData(2L, 100L)]   // offset == source length
    public void TryReadJpegFrame_RangeThatStartsOutsideOrAtTheVeryEnd_ReturnsFalseWithoutThrowing(long offset, long length)
    {
        var source = new InMemoryRawHeaderSource([0xFF, 0xD8]);

        Assert.False(PreviewSelector.TryReadJpegFrame(source, offset, length, out _, out _, out _));
    }

    // ---------------------------------------------------------------- Adobe RGB interoperability check

    private static byte[] AdobeExif(bool littleEndian = true) => PreviewSelectorColorSpaceTests.ExifWithInteropIndex("R03", littleEndian);

    [Fact]
    public void IsAdobeRgbExif_ReferencePayload_IsAdobeRgb()
    {
        Assert.True(PreviewSelector.IsAdobeRgbExif(AdobeExif(true)));
        Assert.True(PreviewSelector.IsAdobeRgbExif(AdobeExif(false)));
    }

    [Fact]
    public void IsAdobeRgbExif_InteropEntryEndingExactlyAtTheEndOfTheBlock_IsStillRecognised()
    {
        var payload = AdobeExif();

        Assert.True(PreviewSelector.IsAdobeRgbExif(payload.AsSpan(0, payload.Length - 4)));
    }

    [Theory]
    [InlineData(54, 7)]  // InteropIndex typed UNDEFINED instead of ASCII
    [InlineData(56, 3)]  // count 3 instead of 4
    [InlineData(20, 2)]  // Exif IFD pointer with count 2
    [InlineData(18, 3)]  // Exif IFD pointer typed SHORT
    public void IsAdobeRgbExif_WrongEntryTypeOrCount_IsNotAdobeRgb(int payloadIndex, byte value)
    {
        var payload = AdobeExif();
        payload[payloadIndex] = value;

        Assert.False(PreviewSelector.IsAdobeRgbExif(payload));
    }

    [Fact]
    public void IsAdobeRgbExif_ByteOrderMarkWithOnlyOneMatchingLetter_IsRejected()
    {
        var littleEndianBody = AdobeExif(true);
        littleEndianBody[7] = (byte)'M'; // "IM"
        var bigEndianBody = AdobeExif(false);
        bigEndianBody[7] = (byte)'I'; // "MI"

        Assert.False(PreviewSelector.IsAdobeRgbExif(littleEndianBody));
        Assert.False(PreviewSelector.IsAdobeRgbExif(bigEndianBody));
    }

    private static void PutEntry(byte[] tiff, int at, ushort tag, ushort type, uint count, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), count);
        value.CopyTo(tiff.AsSpan(at + 8));
    }

    [Fact]
    public void IsAdobeRgbExif_FirstIfdOffsetInsideTheTiffHeader_IsRejected()
    {
        // IFD0 offset 2 reads its count from the magic ("*\0" = 42 entries) and its entries from the offset field onwards;
        // entry 1 (at tiff offset 16) is the Exif IFD pointer, the Exif and Interop IFDs are well formed further on.
        var tiff = new byte[200];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 2);
        PutEntry(tiff, 16, 0x8769, 4, 1, BitConverter.GetBytes(100u));
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(100), 1);
        PutEntry(tiff, 102, 0xA005, 4, 1, BitConverter.GetBytes(130u));
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(130), 1);
        PutEntry(tiff, 132, 0x0001, 2, 4, "R03\0"u8);

        Assert.False(PreviewSelector.IsAdobeRgbExif(Cat("Exif\0\0"u8.ToArray(), tiff)));
    }

    [Fact]
    public void IsAdobeRgbExif_TagFoundOnlyAsTheSecondEntryOfEachIfd_IsFound()
    {
        // Every IFD gets a leading decoy entry; the real entry is the second one.
        byte[] decoy(ushort tag) => [(byte)tag, (byte)(tag >> 8), 4, 0, 1, 0, 0, 0, 0, 0, 0, 0];
        byte[] entry(ushort tag, ushort type, uint count, byte[] value) =>
            [(byte)tag, (byte)(tag >> 8), (byte)type, (byte)(type >> 8), (byte)count, (byte)(count >> 8), 0, 0, .. value];
        byte[] ifd(params byte[][] entries) => [(byte)entries.Length, 0, .. entries.SelectMany(e => e), 0, 0, 0, 0];

        byte[] tiff = [.. "II*\0"u8, 8, 0, 0, 0];
        var ifd0Size = 2 + (2 * 12) + 4;
        uint exifAt = (uint)(8 + ifd0Size);
        var exifSize = 2 + (2 * 12) + 4;
        uint interopAt = exifAt + (uint)exifSize;
        var body = Cat(
            tiff,
            ifd(decoy(0x0100), entry(0x8769, 4, 1, BitConverter.GetBytes(exifAt))),
            ifd(decoy(0x0100), entry(0xA005, 4, 1, BitConverter.GetBytes(interopAt))),
            ifd(decoy(0x0002), entry(0x0001, 2, 4, "R03\0"u8.ToArray())));

        Assert.True(PreviewSelector.IsAdobeRgbExif(Cat("Exif\0\0"u8.ToArray(), body)));
    }

    [Fact]
    public void IsAdobeRgbExif_InteropEntryThatOnlyAppearsAfterTheCountedEntries_IsNotRead()
    {
        // The Interop IFD declares one entry (tag 2); the bytes of its next-IFD pointer and what follows happen to spell a
        // valid InteropIndex "R03" entry, which is not part of the IFD.
        var payload = AdobeExif();
        byte[] fake = [0x01, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x00, (byte)'R', (byte)'0', (byte)'3', 0];
        var longer = new byte[payload.Length + 8];
        payload.CopyTo(longer, 0);
        longer[6 + 46] = 0x02; // entry 0 of the Interop IFD (tiff offset 46) becomes tag 0x0002
        fake.CopyTo(longer, 6 + 58);

        Assert.False(PreviewSelector.IsAdobeRgbExif(longer));
    }

    [Fact]
    public void TryReadJpegFrame_ExifInAnApp2Segment_IsNotUsedForTheColourSpace()
    {
        var exif = AdobeExif();
        var app1 = Cat([0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2)], exif);
        var app2 = Cat([0xFF, 0xE2, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2)], exif);

        Assert.True(Walk(Cat(Soi, app1, Sof()), out _, out _, out var inApp1));
        Assert.True(Walk(Cat(Soi, app2, Sof()), out _, out _, out var inApp2));

        Assert.Equal(PreviewColorSpace.AdobeRgb, inApp1);
        Assert.Equal(PreviewColorSpace.Unknown, inApp2);
    }
}