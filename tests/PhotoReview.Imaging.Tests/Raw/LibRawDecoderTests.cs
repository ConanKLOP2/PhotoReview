using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderTests
{
    private static readonly string CorpusDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    [Trait("Category", "Native")]
    public void AvailabilityProbe_LoadsAndInitializesPinnedRuntime()
    {
        Assert.True(LibRawAvailability.Probe(out var reason), reason);
        Assert.Null(reason);
        Assert.True(LibRawAvailability.Probe(out reason));
        Assert.Null(reason);
    }

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
        for (var index = 0; index < files.Length; index++)
        {
            var path = Path.Combine(CorpusDirectory, files[index]);
            var request = index == 0
                ? new DecodeRequest(path, DecodeBox.Unbounded, bytes: File.ReadAllBytes(path))
                : new DecodeRequest(path, DecodeBox.Unbounded);
            var image = decoder.Decode(request);
            var info = decoder.ReadInfo(path);

            Assert.True(image.PixelWidth > 0);
            Assert.True(image.PixelHeight > 0);
            Assert.Equal(DecoderBackend.LibRaw, image.ActualBackend);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
            Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
            Assert.Equal(96, bitmap.DpiX);
            Assert.Equal(96, bitmap.DpiY);
            Assert.Equal(image.PixelWidth, info.PixelWidth);
            Assert.Equal(image.PixelHeight, info.PixelHeight);

            if (index == 0)
            {
                var bounded = decoder.Decode(new DecodeRequest(path, new DecodeBox(640, 480)));
                Assert.True(bounded.Downscaled);
                Assert.True(bounded.PixelWidth <= 640);
                Assert.True(bounded.PixelHeight <= 480);
                Assert.Equal(image.OriginalWidth, bounded.OriginalWidth);
                Assert.Equal(image.OriginalHeight, bounded.OriginalHeight);
            }
        }
    }

    [Fact]
    public void Decode_RejectsRequestThatWouldLeaveOrientationUnapplied()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest("unused.cr2", DecodeBox.Unbounded, applyOrientation: false)));

        Assert.Contains("always applies", error.Message, StringComparison.Ordinal);
    }
}
