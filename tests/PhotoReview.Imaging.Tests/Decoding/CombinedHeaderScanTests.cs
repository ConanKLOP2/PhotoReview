using System.Diagnostics;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Metadata;
using PhotoReview.Imaging.Tests.Robustness;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// IMG-07: <see cref="TurboJpegDecoder.Decode"/> gets ICC presence, the EXIF APP1 span and orientation from ONE
/// marker walk (<see cref="TurboJpegDecoder.ScanHeader"/>) instead of three (the old ICC loop, the old orientation
/// loop and <see cref="ExifParser.FindExifTiffBlock"/>). These tests pin the combined walk's edge cases (multiple
/// APP1 segments incl. a non-EXIF one, truncated headers, no EXIF) and report the walk's cost before/after.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class CombinedHeaderScanTests
{
    private readonly ITestOutputHelper _output;

    public CombinedHeaderScanTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "ScanHeader finds ICC and EXIF together, agreeing with the standalone entry points")]
    public void ScanHeader_FindsIccAndExif_AgreesWithStandaloneMethods()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation: 6)),
            .. JpegBytes.Segment(0xE2, JpegBytes.IccTag),
            0xFF, 0xDA, 0x00, 0x02,
        ];

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: true, wantExif: true, out bool hasIcc, out var exifTiff);

        Assert.True(hasIcc);
        Assert.False(exifTiff.IsEmpty);
        Assert.True(JpegIccProbe.HasIcc(jpeg));
        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(jpeg));
        Assert.Equal(ExifParser.FindExifTiffBlock(jpeg).ToArray(), exifTiff.ToArray());
    }

    [Fact(DisplayName = "A non-EXIF APP1 (e.g. XMP) is skipped; the first real Exif-headed APP1 after it still wins")]
    public void ScanHeader_SkipsNonExifApp1_FindsLaterExifApp1()
    {
        byte[] xmpApp1 = JpegBytes.Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta/>"u8.ToArray());
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. xmpApp1,
            .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation: 5)),
            0xFF, 0xDA, 0x00, 0x02,
        ];

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: false, wantExif: true, out _, out var exifTiff);

        Assert.False(exifTiff.IsEmpty);
        Assert.Equal(5, TurboJpegDecoder.ReadExifOrientation(jpeg));
        // OrientationTiff only encodes the orientation tag (no Make/Model/date/etc.), so ExifSummary has no
        // usable field of its own; the point here is that the TIFF block was found and parses without error.
        Assert.Equal(ExifParser.TryParseTiffBlock(exifTiff), ExifParser.TryParseJpeg(jpeg));
    }

    [Fact(DisplayName = "Multiple Exif-headed APP1 segments: the first one wins, even with a garbage TIFF part")]
    public void ScanHeader_MultipleExifApp1_FirstOneWinsEvenIfGarbage()
    {
        byte[] garbageExifApp1 = JpegBytes.Segment(0xE1, [.. JpegBytes.ExifTag, 0xAA, 0xBB]); // too short to be a valid TIFF header
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. garbageExifApp1,
            .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation: 3)),
            0xFF, 0xDA, 0x00, 0x02,
        ];

        // The first Exif-headed APP1 is used even though its TIFF is garbage: orientation falls back to 1,
        // never searching for the second, well-formed APP1 (same rule ExifParser.FindExifTiffBlock documents).
        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(jpeg));
        Assert.Null(ExifParser.TryParseJpeg(jpeg));
    }

    [Fact(DisplayName = "A truncated header (cut mid-segment) never throws and reports 'nothing found'")]
    public void ScanHeader_TruncatedHeader_DoesNotThrow_ReportsNothingFound()
    {
        byte[] full =
        [
            0xFF, 0xD8,
            .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation: 7)),
            .. JpegBytes.Segment(0xE2, JpegBytes.IccTag),
            0xFF, 0xDA, 0x00, 0x02,
        ];

        // Cut off partway through the EXIF APP1 payload, before the ICC segment.
        byte[] truncated = full[..10];

        var ex = Record.Exception(() =>
            TurboJpegDecoder.ScanHeader(truncated, wantIcc: true, wantExif: true, out bool hasIcc, out var exifTiff));
        Assert.Null(ex);
        Assert.False(JpegIccProbe.HasIcc(truncated));
        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(truncated));
        Assert.Null(ExifParser.TryParseJpeg(truncated));
    }

    [Fact(DisplayName = "No EXIF and no ICC: ScanHeader returns empty/false, orientation defaults to 1")]
    public void ScanHeader_NoExifNoIcc_ReturnsEmptyAndFalse()
    {
        byte[] jpeg = [0xFF, 0xD8, .. JpegBytes.Segment(0xE0, "JFIF\0"u8.ToArray()), 0xFF, 0xDA, 0x00, 0x02];

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: true, wantExif: true, out bool hasIcc, out var exifTiff);

        Assert.False(hasIcc);
        Assert.True(exifTiff.IsEmpty);
        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(jpeg));
        Assert.Null(ExifParser.TryParseJpeg(jpeg));
    }

    [Fact(DisplayName = "ExifParser.TryParseTiffBlock: empty span is null, and it matches TryParseJpeg for the same TIFF")]
    public void TryParseTiffBlock_MatchesTryParseJpeg()
    {
        Assert.Null(ExifParser.TryParseTiffBlock(default));
        Assert.Null(ExifParser.TryParseTiffBlock(ReadOnlySpan<byte>.Empty));

        var jpeg = ExifTestData.EncodeJpegWithExif();
        var tiff = ExifParser.FindExifTiffBlock(jpeg);

        var viaBlock = ExifParser.TryParseTiffBlock(tiff);
        var viaJpeg = ExifParser.TryParseJpeg(jpeg);
        ExifTestData.AssertFullCamera(viaBlock);
        Assert.Equal(viaJpeg, viaBlock);
    }

    [Fact(DisplayName = "Decode(): ICC takes precedence over EXIF even when both are present (unchanged ordering)")]
    public void Decode_IccAndExifBothPresent_ThrowsIccFallback()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation: 6)),
            .. JpegBytes.Segment(0xE2, JpegBytes.IccTag),
            0xFF, 0xDA, 0x00, 0x02,
        ];

        var decoder = new TurboJpegDecoder(WpfBitmapSourceCodec.Instance);
        var ex = Assert.Throws<NotSupportedException>(() =>
            decoder.Decode(new DecodeRequest("icc-and-exif.jpg", new DecodeBox(16, 16), bytes: jpeg)));
        Assert.Contains("ICC", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Decode(): EXIF and orientation both come from the same combined walk on a real encoder-written JPEG")]
    public void Decode_RealJpeg_OrientationAndExifBothCorrect()
    {
        var bytes = ExifTestData.EncodeJpegWithExif(64, 48, orientation: 6);
        var decoder = new TurboJpegDecoder(WpfBitmapSourceCodec.Instance);

        var decoded = decoder.Decode(new DecodeRequest("real.jpg", DecodeBox.Unbounded, bytes: bytes));

        Assert.Equal(6, decoded.Orientation);
        ExifTestData.AssertFullCamera(decoded.Exif);
        Assert.Equal(ExifParser.TryParseJpeg(bytes), decoded.Exif);
    }

    /// <summary>
    /// Cost report only (no timing assert: wall-clock numbers are not deterministic). Compares the pre-refactor
    /// shape -- three independent walks (HasEmbeddedIccProfile + ReadExifOrientation, each their own walk, plus
    /// ExifParser.TryParseJpeg's own FindExifTiffBlock walk) -- against the walk Decode uses today (one
    /// ScanHeader call feeding TryParseTiffBlock), on a real encoder-written JPEG header.
    /// </summary>
    [Fact(DisplayName = "Header-walk cost: three separate walks vs. the combined ScanHeader walk")]
    public void ReportsHeaderWalkCost()
    {
        var bytes = ExifTestData.EncodeJpegWithExif(1500, 1000, orientation: 6);
        const int rounds = 2000;

        // Warm-up (JIT).
        _ = JpegIccProbe.HasIcc(bytes);
        _ = TurboJpegDecoder.ReadExifOrientation(bytes);
        _ = ExifParser.TryParseJpeg(bytes);
        TurboJpegDecoder.ScanHeader(bytes, true, true, out _, out var warm);
        _ = ExifParser.TryParseTiffBlock(warm);

        var threeWalks = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            _ = JpegIccProbe.HasIcc(bytes);
            _ = TurboJpegDecoder.ReadExifOrientation(bytes);
            _ = ExifParser.TryParseJpeg(bytes);
        }
        threeWalks.Stop();

        ExifSummary? lastCombined = null;
        var oneWalk = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            TurboJpegDecoder.ScanHeader(bytes, wantIcc: true, wantExif: true, out _, out var exifTiff);
            lastCombined = ExifParser.TryParseTiffBlock(exifTiff);
        }
        oneWalk.Stop();

        _output.WriteLine($"Pre-refactor (3 walks: HasEmbeddedIccProfile + ReadExifOrientation + ExifParser.TryParseJpeg): "
            + $"{threeWalks.Elapsed.TotalMilliseconds * 1000 / rounds:F2} us/call");
        _output.WriteLine($"Combined ScanHeader (1 walk) + TryParseTiffBlock: "
            + $"{oneWalk.Elapsed.TotalMilliseconds * 1000 / rounds:F2} us/call");

        // Not just a timing report: both paths must still agree on the actual EXIF content.
        ExifTestData.AssertFullCamera(lastCombined);
        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(bytes));
    }

    /// <summary>
    /// Same comparison as <see cref="ReportsHeaderWalkCost"/>, but with 20 leading COM segments (60 KB each, ~1.2 MB,
    /// the same shape <see cref="Robustness.LargeHeaderReadInfoTests"/> uses) ahead of the EXIF APP1 -- the case
    /// where a real photo carries a large embedded thumbnail, Photoshop resource block or XMP packet before EXIF, so
    /// each walk has much more header to cross and the saved walk matters more.
    /// </summary>
    [Fact(DisplayName = "Header-walk cost with a large header (EXIF behind ~1.2 MB of leading segments)")]
    public void ReportsHeaderWalkCost_WithLargeHeader()
    {
        var jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var padded = new List<byte> { 0xFF, 0xD8 };
        for (var i = 0; i < 20; i++) padded.AddRange(JpegBytes.Segment(0xFE, new byte[60_000]));
        padded.AddRange(jpeg.AsSpan(2).ToArray());
        var bytes = padded.ToArray();
        const int rounds = 500;

        _ = JpegIccProbe.HasIcc(bytes);
        _ = TurboJpegDecoder.ReadExifOrientation(bytes);
        _ = ExifParser.TryParseJpeg(bytes);
        TurboJpegDecoder.ScanHeader(bytes, true, true, out _, out var warm);
        _ = ExifParser.TryParseTiffBlock(warm);

        var threeWalks = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            _ = JpegIccProbe.HasIcc(bytes);
            _ = TurboJpegDecoder.ReadExifOrientation(bytes);
            _ = ExifParser.TryParseJpeg(bytes);
        }
        threeWalks.Stop();

        var oneWalk = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            TurboJpegDecoder.ScanHeader(bytes, wantIcc: true, wantExif: true, out _, out var exifTiff);
            _ = ExifParser.TryParseTiffBlock(exifTiff);
        }
        oneWalk.Stop();

        _output.WriteLine($"[large header] Pre-refactor (3 walks): {threeWalks.Elapsed.TotalMilliseconds * 1000 / rounds:F1} us/call");
        _output.WriteLine($"[large header] Combined ScanHeader (1 walk): {oneWalk.Elapsed.TotalMilliseconds * 1000 / rounds:F1} us/call");

        // Not just a timing report: the combined walk must still find the (unpadded) EXIF orientation correctly
        // behind the large leading-segment padding.
        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(bytes));
    }
}
