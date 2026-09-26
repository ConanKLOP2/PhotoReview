using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class WpfBitmapImageDecoderTests : IDisposable
{
    private static readonly byte[] ValidPng1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _tempDir;
    private readonly string _imagePath;
    private readonly WpfBitmapImageDecoder _decoder = new();

    public WpfBitmapImageDecoderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-DecoderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _imagePath = Path.Combine(_tempDir, "test.png");
        File.WriteAllBytes(_imagePath, ValidPng1x1);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact(DisplayName = "ReadInfo returns correct image dimensions without full decode")]
    public void ReadInfoReturnsCorrectDimensions()
    {
        var info = _decoder.ReadInfo(_imagePath);
        Assert.Equal(1, info.PixelWidth);
        Assert.Equal(1, info.PixelHeight);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
    }

    [Fact(DisplayName = "Decode full resolution produces frozen bitmap with downscaled false")]
    public void DecodeFullResolutionProducesFrozenBitmap()
    {
        var request = new DecodeRequest(_imagePath, TargetWidth: 0);
        var decoded = _decoder.Decode(request);

        Assert.NotNull(decoded);
        Assert.False(decoded.Downscaled);
        Assert.Equal(1, decoded.PixelWidth);
        Assert.Equal(1, decoded.PixelHeight);
        Assert.True(decoded.EstimatedBytes > 0);
        Assert.IsType<WpfDecodedImage>(decoded);
        var wpfImage = (WpfDecodedImage)decoded;
        Assert.True(wpfImage.Source.IsFrozen);
        Assert.Same(wpfImage.Source, decoded.PlatformImage);
    }

    [Fact(DisplayName = "A width-only request on a source that already fits neither upscales nor reports downscaled")]
    public void WidthOnlyRequestOnSmallSource_DoesNotUpscaleOrReportDownscaled()
    {
        var decoded = _decoder.Decode(new DecodeRequest(_imagePath, TargetWidth: 100)); // the source is 1x1

        Assert.False(decoded.Downscaled);
        Assert.Equal(1, decoded.PixelWidth);
        Assert.Equal(1, decoded.PixelHeight);
    }

    [Fact(DisplayName = "Decode with target width reports downscaled true")]
    public void DecodeWithTargetWidthReportsDownscaled()
    {
        var widePath = Path.Combine(_tempDir, "wide.png");
        File.WriteAllBytes(widePath, TestImages.BuildRgbaPng(300, 200, (_, _) => 255));
        var request = new DecodeRequest(widePath, TargetWidth: 100);
        var decoded = _decoder.Decode(request);

        Assert.NotNull(decoded);
        Assert.True(decoded.Downscaled);
        Assert.IsType<WpfDecodedImage>(decoded);
        var wpfImage = (WpfDecodedImage)decoded;
        Assert.True(wpfImage.Source.IsFrozen);
    }

    [Theory(DisplayName = "A source that already fits the requested width or height is never upscaled (same rule as every other backend)")]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(100, 100)]
    public void DecodeNeverUpscalesASourceThatFits(int targetWidth, int targetHeight)
    {
        // ValidPng1x1 is 1x1: a 100-wide request used to stretch it to 100x100 through DecodePixelWidth.
        var decoded = _decoder.Decode(new DecodeRequest(_imagePath, targetWidth, ApplyOrientation: true, Bytes: null, TargetHeight: targetHeight));

        Assert.Equal((1, 1), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((1, 1), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Fact(DisplayName = "A width-only decode of a larger source scales to exactly that width and still reports the full original size (ApplyOrientation off too)")]
    public void WidthOnlyDecodeReportsOriginalSizeWithoutOrientation()
    {
        var largePath = Path.Combine(_tempDir, "large.png");
        FixtureGenerator.GeneratePng(largePath, 64, 48);

        var decoded = _decoder.Decode(new DecodeRequest(largePath, TargetWidth: 32, ApplyOrientation: false));

        Assert.Equal((32, 24), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((64, 48), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Fact(DisplayName = "Decode with target width reports OriginalWidth/Height separately from the downscaled pixel dimensions")]
    public void DecodeWithTargetWidthReportsOriginalDimensions()
    {
        var path = Path.Combine(_tempDir, "downscale.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 400, 300);

        const int targetWidth = 100;
        var decoded = _decoder.Decode(new DecodeRequest(path, TargetWidth: targetWidth));

        Assert.Equal(targetWidth, decoded.PixelWidth);
        Assert.True(decoded.Downscaled);
        // perf(dims): the header decoder that reads the orientation tag also gives
        // OriginalWidth/Height for free -- must report the full source size (400x300), not the
        // downscaled decode result.
        Assert.Equal(400, decoded.OriginalWidth);
        Assert.Equal(300, decoded.OriginalHeight);
    }

    [Fact(DisplayName = "Box-fitted decode reports correct original dimensions for EXIF-rotated input")]
    public void DecodeWithBoxReportsOriginalDimensionsForRotatedInput()
    {
        var path = Path.Combine(_tempDir, "box_orient_6.jpg");
        // Stored (pre-orientation) frame is 256x192; orientation 6 is transposing, so the
        // displayed (post-orientation) size is 192x256.
        FixtureGenerator.GenerateJpegWithOrientation(path, 256, 192, 6);

        var decoded = _decoder.Decode(new DecodeRequest(path, new DecodeBox(50, 100)));

        Assert.True(decoded.Downscaled);
        // perf(decode)+perf(dims) together: OriginalWidth/Height must report the full displayed
        // (post-orientation) source size -- 192x256 -- not the box-fitted pixels below, and not
        // the stored (pre-orientation) 256x192 raw frame size either.
        Assert.Equal(192, decoded.OriginalWidth);
        Assert.Equal(256, decoded.OriginalHeight);
        // DecodeBox.FitStored fits the 50x100 box against the displayed 192x256 size: width
        // constrains (50/192 <= 100/256), giving exactly 50 x floor(256*50/192) = 50x66.
        Assert.Equal(50, decoded.PixelWidth);
        Assert.Equal(66, decoded.PixelHeight);
    }

    [Fact(DisplayName = "Decode with in-memory bytes decodes from memory stream")]
    public void DecodeWithMemoryBytes()
    {
        var request = new DecodeRequest(_imagePath, TargetWidth: 0, Bytes: new ReadOnlyMemory<byte>(ValidPng1x1));
        var decoded = _decoder.Decode(request);

        Assert.NotNull(decoded);
        Assert.False(decoded.Downscaled);
        Assert.Equal(1, decoded.PixelWidth);
        Assert.IsType<WpfDecodedImage>(decoded);
        var wpfImage = (WpfDecodedImage)decoded;
        Assert.True(wpfImage.Source.IsFrozen);
    }
}

