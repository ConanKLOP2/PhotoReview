using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderTests
{
    private static readonly string CorpusDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    public void Decode_OfficialCorpusSamples_ReturnsValidRgbBackedBitmap()
    {
        var files = new[]
        {
            "Canon - EOS 350D - RAW (3_2).CR2",
            "Canon - EOS R6 - 3_2.CR3"
        };
        if (files.Any(file => !File.Exists(Path.Combine(CorpusDirectory, file)))) return;

        var decoder = new LibRawDecoder();
        foreach (var file in files)
        {
            var path = Path.Combine(CorpusDirectory, file);
            var image = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
            var info = decoder.ReadInfo(path);

            Assert.True(image.PixelWidth > 0);
            Assert.True(image.PixelHeight > 0);
            Assert.Equal(DecoderBackend.LibRaw, image.ActualBackend);
            Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(image.PlatformImage);
            Assert.Equal(image.PixelWidth, info.PixelWidth);
            Assert.Equal(image.PixelHeight, info.PixelHeight);
        }
    }
}
