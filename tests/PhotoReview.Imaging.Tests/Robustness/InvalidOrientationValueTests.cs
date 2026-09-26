using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Metadata;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>An EXIF orientation outside 1..8 (0, 9, 255, 65535, or a wrong-typed value) means "no rotation" in every decoder, never a rotation and never an error.</summary>
[Trait("Category", "HotPath")]
public sealed class InvalidOrientationValueTests
{
    private static (int ValueOffset, bool Little) FindOrientationValue(byte[] jpeg)
    {
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xE1) i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        var tiff = i + 4 + 6;
        var little = jpeg[tiff] == (byte)'I';
        int U16(int at) => little ? jpeg[at] | (jpeg[at + 1] << 8) : (jpeg[at] << 8) | jpeg[at + 1];
        int U32(int at) => little ? U16(at) | (U16(at + 2) << 16) : (U16(at) << 16) | U16(at + 2);
        var ifd0 = tiff + U32(tiff + 4);
        for (var e = 0; e < U16(ifd0); e++)
        {
            var at = ifd0 + 2 + e * 12;
            if (U16(at) == 0x0112) return (at + 8, little);
        }

        throw new InvalidOperationException("orientation entry not found");
    }

    public static TheoryData<int> InvalidValues() => [0, 9, 10, 255, 256, 32768, 65535];

    [Theory(DisplayName = "Orientation values outside 1..8 are ignored (orientation 1, pixels unrotated) by every decoder and chain")]
    [MemberData(nameof(InvalidValues))]
    public void OutOfRangeOrientation_IsTreatedAsNormal(int value)
    {
        var jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var (at, little) = FindOrientationValue(jpeg);
        // SHORT value, left-justified in the 4-byte field, in the file's byte order.
        jpeg[at] = (byte)(little ? value & 0xFF : value >> 8);
        jpeg[at + 1] = (byte)(little ? value >> 8 : value & 0xFF);

        var decoders = new (string Name, IImageDecoder Decoder)[]
        {
            ("Wpf", new WpfBitmapImageDecoder()),
            ("WicDirect", new WicDirectDecoder()),
            ("TurboJpeg", new TurboJpegDecoder()),
            ("Turbo->Wpf", new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, new WpfBitmapImageDecoder())),
        };
        foreach (var (name, decoder) in decoders)
        {
            var image = decoder.Decode(new DecodeRequest("bad-orientation.jpg", DecodeBox.Unbounded, bytes: jpeg));
            Assert.True(image.Orientation == 1, $"{name}: orientation {image.Orientation} for stored value {value}");
            Assert.Equal((24, 16), (image.PixelWidth, image.PixelHeight));
            Assert.Equal((24, 16), (image.OriginalWidth, image.OriginalHeight));
        }
    }
}
