using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Robustness;

[Trait("Category", "HotPath")]
public sealed class IccMismatchTests
{
    private static byte[] Encode(PixelFormat format)
    {
        var source = new FormatConvertedBitmap(FixtureGenerator.CreateGradientCheckerboard(24, 16), format, null, 0);
        source.Freeze();
        var context = new ColorContext(new Uri(FixtureGenerator.GetBundledDisplayP3ProfilePath()));
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, null, new ReadOnlyCollection<ColorContext>([context])));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Theory(DisplayName = "An RGB ICC profile on a grayscale or CMYK JPEG (profile/colour-space mismatch) still decodes through every production chain; TurboJpeg hands the file over")]
    [InlineData("Gray8")]
    [InlineData("Bgr24")]
    [InlineData("Cmyk32")]
    public void ProfileColourSpaceMismatch_StillDecodes(string name)
    {
        var format = name switch { "Gray8" => PixelFormats.Gray8, "Bgr24" => PixelFormats.Bgr24, _ => PixelFormats.Cmyk32 };
        var jpeg = Encode(format);
        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(jpeg), "the fixture must carry the profile");

        Assert.Throws<NotSupportedException>(() => new TurboJpegDecoder().Decode(new DecodeRequest("x.jpg", new DecodeBox(12, 12), bytes: jpeg)));
        var chains = new (string Name, IImageDecoder Decoder)[]
        {
            ("Wpf", new WpfBitmapImageDecoder()),
            ("Turbo->Wpf", new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, new WpfBitmapImageDecoder())),
            ("WicDirect->Wpf", new FallbackImageDecoder(new WicDirectDecoder(), DecoderBackend.WicDirect, new WpfBitmapImageDecoder())),
        };
        foreach (var (chainName, chain) in chains)
        {
            foreach (var box in new[] { new DecodeBox(12, 12), DecodeBox.Unbounded })
            {
                var image = chain.Decode(new DecodeRequest("x.jpg", box, bytes: jpeg));
                var expected = box.Fit(24, 16);
                Assert.True((image.PixelWidth, image.PixelHeight) == expected, $"{chainName} ({name}): {image.PixelWidth}x{image.PixelHeight}, expected {expected}");
            }
        }
    }
}
