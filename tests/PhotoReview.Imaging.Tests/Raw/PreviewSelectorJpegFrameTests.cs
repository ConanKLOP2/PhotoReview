using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Fuji RAF embeds a JPEG whose EXIF APP1 alone is about 65 KB, so the SOF marker sits past 64 KiB.
/// <see cref="PreviewSelector.TryReadJpegFrame"/> must find it with bounded per-segment reads, not a fixed-size window.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewSelectorJpegFrameTests
{
    private static byte[] AppSegment(byte marker, int payloadLength, byte fill = 0x11)
    {
        var segment = new byte[payloadLength + 4];
        segment[0] = 0xFF;
        segment[1] = marker;
        var length = payloadLength + 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)length;
        Array.Fill(segment, fill, 4, payloadLength);
        return segment;
    }

    /// <summary>Minimal JPEG with <paramref name="padding"/> inserted right after SOI, i.e. before every other segment.</summary>
    private static byte[] JpegWithLeadingSegments(int width, int height, params byte[][] padding)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(width, height);
        using var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        foreach (var segment in padding) ms.Write(segment);
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    /// <summary>EXIF-sized APP1 (65450 bytes) plus a second APP2: the SOF ends up past 85 KB.</summary>
    private static byte[] FujiShapedJpeg(int width, int height) =>
        JpegWithLeadingSegments(width, height, AppSegment(0xE1, 65_450), AppSegment(0xE2, 20_000));

    private static int SofOffset(byte[] jpeg) => jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });

    [Fact]
    public void TryReadJpegFrame_SofPastSixtyFourKiB_IsFound()
    {
        var jpeg = FujiShapedJpeg(6000, 4000);
        Assert.True(SofOffset(jpeg) > 64 * 1024);

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out int width, out int height, out _));

        Assert.Equal((6000, 4000), (width, height));
    }

    [Fact]
    public void TryReadJpegFrame_ReadsOnlySegmentHeadersAndTheFrameNotTheWholeRange()
    {
        var jpeg = FujiShapedJpeg(6000, 4000);
        var source = new InMemoryRawHeaderSource(jpeg);

        Assert.True(PreviewSelector.TryReadJpegFrame(source, 0, jpeg.Length, out _, out _, out _));

        // Only the APP1 payload (colour-space scan) plus a few header reads: the 20 KB APP2 is skipped, not read.
        Assert.True(source.TotalBytesRead < 70_000, $"Read {source.TotalBytesRead} bytes.");
    }

    [Fact]
    public void TryReadJpegFrame_PreviewInsideLargerFile_HonoursOffset()
    {
        var jpeg = FujiShapedJpeg(4896, 3264);
        var file = new byte[160 + jpeg.Length + 500];
        jpeg.CopyTo(file, 160);

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(file), 160, jpeg.Length, out int width, out int height, out _));

        Assert.Equal((4896, 3264), (width, height));
    }

    [Fact]
    public void TryReadJpegFrame_RangeEndsBeforeTheFrameHeader_ReportsUnknown()
    {
        var jpeg = FujiShapedJpeg(6000, 4000);

        Assert.False(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, 70_000, out int width, out int height, out _));
        Assert.Equal((0, 0), (width, height));
    }

    [Fact]
    public void TryReadJpegFrame_RangeClaimedPastEndOfFile_IsClampedNotThrown()
    {
        var jpeg = FujiShapedJpeg(6000, 4000);

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, long.MaxValue / 2, out int width, out _, out _));
        Assert.Equal(6000, width);
    }

    [Theory]
    [InlineData((byte)0xC3, "lossless")]
    [InlineData((byte)0xC9, "arithmetic sequential")]
    [InlineData((byte)0xCA, "arithmetic progressive")]
    [InlineData((byte)0xC5, "hierarchical")]
    public void TryReadJpegFrame_UndecodableFrameType_ReportsUnknown(byte sofMarker, string description)
    {
        var jpeg = FujiShapedJpeg(6000, 4000);
        jpeg[SofOffset(jpeg) + 1] = sofMarker;

        Assert.False(
            PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out int width, out int height, out _),
            description);
        Assert.Equal((0, 0), (width, height));
    }

    [Theory]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    public void TryReadJpegFrame_ExtendedSequentialAndProgressive_AreAccepted(byte sofMarker)
    {
        var jpeg = FujiShapedJpeg(6000, 4000);
        jpeg[SofOffset(jpeg) + 1] = sofMarker;

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out int width, out _, out _));
        Assert.Equal(6000, width);
    }

    [Fact]
    public void TryReadJpegFrame_MoreSegmentsThanTheCap_ReportsUnknownAfterBoundedWork()
    {
        var padding = Enumerable.Range(0, 600).Select(_ => AppSegment(0xE2, 4)).ToArray();
        var jpeg = JpegWithLeadingSegments(640, 480, padding);
        var source = new InMemoryRawHeaderSource(jpeg);

        Assert.False(PreviewSelector.TryReadJpegFrame(source, 0, jpeg.Length, out _, out _, out _));

        Assert.True(source.TotalBytesRead < 512 * 5, $"Read {source.TotalBytesRead} bytes.");
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0x00, 0x00 })] // segment length below its own 2-byte field
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xF0, 0x00, 0x00 })] // segment runs past the range
    [InlineData(new byte[] { 0xFF, 0xD8, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC })] // not a marker
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00 })] // SOS before any frame header
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })] // no SOI
    public void TryReadJpegFrame_MalformedSegments_ReportUnknownWithoutThrowing(byte[] bytes)
    {
        Assert.False(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(bytes), 0, bytes.Length, out int width, out int height, out _));
        Assert.Equal((0, 0), (width, height));
    }

    [Fact]
    public void TryReadJpegFrame_AdobeRgbMarkerInExif_IsReported()
    {
        var app1 = AppSegment(0xE1, 200);
        "Adobe RGB"u8.CopyTo(app1.AsSpan(20));
        var jpeg = JpegWithLeadingSegments(640, 480, app1);

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out _, out _, out var colorSpace));

        Assert.Equal(PreviewColorSpace.AdobeRgb, colorSpace);
    }

    [Fact]
    public void SelectPreview_SingleJpegWithSofPastSixtyFourKiB_ResolvesDimensions()
    {
        var jpeg = FujiShapedJpeg(6000, 4000);
        var previews = new[] { new EmbeddedPreview(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown) };

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), previews, DecodeBox.Unbounded, 1);

        Assert.NotNull(chosen);
        Assert.Equal((6000, 4000), (chosen.Width, chosen.Height));
    }

    [Fact]
    public void RawDecoder_ReadInfoOnRafWithSofPastSixtyFourKiB_ReturnsTheJpegDimensions()
    {
        var raf = SyntheticRawBuilder.BuildRaf(FujiShapedJpeg(6000, 4000));
        using var temp = new TempRoot("raf-sof-past-64k");
        var path = temp.File("fuji.raf", raf);

        var info = new RawDecoder(new NoDecodeDecoder()).ReadInfo(path);

        Assert.Equal((6000, 4000), (info.Width, info.Height));
    }

    private sealed class NoDecodeDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException();
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();
    }
}
