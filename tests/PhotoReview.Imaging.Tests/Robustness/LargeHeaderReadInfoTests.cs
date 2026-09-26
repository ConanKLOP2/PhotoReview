using System.IO;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Metadata;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// ReadInfo (original dimensions for the status bar) must agree with Decode about EXIF orientation even when the header area
/// is larger than the 64 KB TurboJpeg reads first: big APPn/COM segments (Photoshop resources, extended XMP, comments) in
/// front of the EXIF block are legal.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class LargeHeaderReadInfoTests : IDisposable
{
    private readonly TempRoot _root = new("LargeHeader");

    public void Dispose() => _root.Dispose();

    /// <summary>An encoder-written JPEG (24x16, EXIF orientation 6) with <paramref name="paddingSegments"/> COM segments of 60,000 bytes right after SOI.</summary>
    private string WriteJpegWithLeadingSegments(int paddingSegments)
    {
        var jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var padded = new List<byte> { 0xFF, 0xD8 };
        for (var i = 0; i < paddingSegments; i++) padded.AddRange(JpegBytes.Segment(0xFE, new byte[60_000]));
        padded.AddRange(jpeg.AsSpan(2).ToArray());
        return _root.File($"padded-{paddingSegments}.jpg", [.. padded]);
    }

    public static TheoryData<int> PaddingCounts() => [0, 1, 2, 3, 20];

    [Theory(DisplayName = "ReadInfo reports the same orientation and oriented size as Decode when EXIF sits behind 0..1.2 MB of other header segments")]
    [MemberData(nameof(PaddingCounts))]
    public void ReadInfo_AgreesWithDecode_WhenExifIsBeyond64Kb(int paddingSegments)
    {
        var path = WriteJpegWithLeadingSegments(paddingSegments);
        var decoders = new (string Name, IImageDecoder Decoder)[]
        {
            ("Wpf", new WpfBitmapImageDecoder()),
            ("WicDirect", new WicDirectDecoder()),
            ("TurboJpeg", new TurboJpegDecoder()),
        };
        foreach (var (name, decoder) in decoders)
        {
            var decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
            var info = decoder.ReadInfo(path);

            Assert.True(decoded.Orientation == 6, $"{name}: Decode reported orientation {decoded.Orientation}");
            Assert.True(info.Orientation == decoded.Orientation, $"{name}: ReadInfo orientation {info.Orientation} but Decode {decoded.Orientation} ({paddingSegments} x 60 KB leading segments)");
            Assert.Equal((decoded.OriginalWidth, decoded.OriginalHeight), (info.Width, info.Height));
        }
    }
}
