using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Metadata;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// DecodeRequest carries the pre-read bytes for a source; the path is then only a label. Every backend must decode from the
/// bytes whatever the label is (TurboJpeg already says "memory buffer" in its errors), so the fallback chain cannot fail just
/// because the first backend declined and the second insisted on a real path.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class MemoryBufferDecodeTests
{
    private static readonly byte[] Jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);

    public static TheoryData<string> Labels() => ["", "   ", "missing-file-label.jpg"];

    [Theory(DisplayName = "Every backend decodes from the supplied bytes whatever the path label is (empty, blank, or a file that does not exist)")]
    [MemberData(nameof(Labels))]
    public void BytesWin_OverThePathLabel(string label)
    {
        var decoders = new (string Name, IImageDecoder Decoder)[]
        {
            ("Wpf", new WpfBitmapImageDecoder()),
            ("WicDirect", new WicDirectDecoder()),
            ("TurboJpeg", new TurboJpegDecoder()),
        };
        foreach (var (name, decoder) in decoders)
        {
            var image = decoder.Decode(new DecodeRequest(label, new DecodeBox(12, 12), bytes: Jpeg));
            Assert.True(image.Orientation == 6, $"{name} with label '{label}': orientation {image.Orientation}");
            Assert.Equal((8, 12), (image.PixelWidth, image.PixelHeight));
        }
    }
}
