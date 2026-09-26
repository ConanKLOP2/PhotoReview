using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Pins the thumbnail request shape (width only, orientation applied): a source narrower than the width is kept as is, never stretched.</summary>
[Trait("Category", "HotPath")]
public sealed class WpfWidthOnlyThumbnailTests
{
    [Theory(DisplayName = "WPF width-only request: wider source is downscaled to the width, narrower source is left alone and not reported as downscaled")]
    [InlineData(640, 480, 320, 240, true)]
    [InlineData(64, 48, 64, 48, false)]
    public void WidthOnly_NeverUpscales(int width, int height, int expectedWidth, int expectedHeight, bool downscaled)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-thumb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = FixtureGenerator.GenerateGradientJpeg(Path.Combine(dir, "t.jpg"), width, height);
            var image = WpfBitmapImageDecoder.DecodeWithFallback(new DecodeRequest(path, 320, ApplyOrientation: true));
            Assert.Equal((expectedWidth, expectedHeight), (image.PixelWidth, image.PixelHeight));
            Assert.Equal(downscaled, image.Downscaled);
            Assert.Equal((width, height), (image.OriginalWidth, image.OriginalHeight));
        }
        finally { Directory.Delete(dir, true); }
    }
}
