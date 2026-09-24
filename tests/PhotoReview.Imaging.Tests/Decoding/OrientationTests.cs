using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Quality;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class OrientationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WpfBitmapImageDecoder _decoder = new();

    public OrientationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-OrientTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact(DisplayName = "ExifOrientation.Read handles null, invalid, and standard values cleanly")]
    public void ReadHandlesNullAndInvalidCleanly()
    {
        Assert.Equal(1, ExifOrientation.Read(null));
    }

    [Theory(DisplayName = "ImageInfo.Width and Height swap correctly for transposed orientations 5 to 8")]
    [InlineData(1, 64, 48, 64, 48)]
    [InlineData(2, 64, 48, 64, 48)]
    [InlineData(3, 64, 48, 64, 48)]
    [InlineData(4, 64, 48, 64, 48)]
    [InlineData(5, 64, 48, 48, 64)]
    [InlineData(6, 64, 48, 48, 64)]
    [InlineData(7, 64, 48, 48, 64)]
    [InlineData(8, 64, 48, 48, 64)]
    public void ImageInfoDimensionSwap(int orientation, int pixelWidth, int pixelHeight, int expectedWidth, int expectedHeight)
    {
        var info = new ImageInfo(pixelWidth, pixelHeight, orientation);
        Assert.Equal(pixelWidth, info.PixelWidth);
        Assert.Equal(pixelHeight, info.PixelHeight);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
    }

    [Theory(DisplayName = "ReadInfo detects EXIF orientation 1 to 8 and reports visual dimensions")]
    [InlineData((ushort)1, 64, 48)]
    [InlineData((ushort)2, 64, 48)]
    [InlineData((ushort)3, 64, 48)]
    [InlineData((ushort)4, 64, 48)]
    [InlineData((ushort)5, 48, 64)]
    [InlineData((ushort)6, 48, 64)]
    [InlineData((ushort)7, 48, 64)]
    [InlineData((ushort)8, 48, 64)]
    public void ReadInfoDetectsOrientation(ushort orientation, int expectedVisualWidth, int expectedVisualHeight)
    {
        var path = Path.Combine(_tempDir, $"info_orient_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        var info = _decoder.ReadInfo(path);
        Assert.Equal(64, info.PixelWidth);
        Assert.Equal(48, info.PixelHeight);
        Assert.Equal(orientation, info.Orientation);
        Assert.Equal(expectedVisualWidth, info.Width);
        Assert.Equal(expectedVisualHeight, info.Height);
    }

    [Theory(DisplayName = "Decode full-res applies EXIF orientation and moves corner colors to correct positions")]
    [InlineData((ushort)1, 64, 48, "Yellow", "Cyan", "Magenta", "White")]
    [InlineData((ushort)2, 64, 48, "Cyan", "Yellow", "White", "Magenta")]
    [InlineData((ushort)3, 64, 48, "White", "Magenta", "Cyan", "Yellow")]
    [InlineData((ushort)4, 64, 48, "Magenta", "White", "Yellow", "Cyan")]
    [InlineData((ushort)5, 48, 64, "Yellow", "Magenta", "Cyan", "White")]
    [InlineData((ushort)6, 48, 64, "Magenta", "Yellow", "White", "Cyan")]
    [InlineData((ushort)7, 48, 64, "White", "Cyan", "Magenta", "Yellow")]
    [InlineData((ushort)8, 48, 64, "Cyan", "White", "Yellow", "Magenta")]
    public void DecodeFullResAppliesOrientationCorners(
        ushort orientation,
        int expectedWidth,
        int expectedHeight,
        string expectedTopLeft,
        string expectedTopRight,
        string expectedBottomLeft,
        string expectedBottomRight)
    {
        var path = Path.Combine(_tempDir, $"full_orient_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        var decoded = _decoder.Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        // perf(dims): an undownscaled decode's OriginalWidth/Height must match its own pixel
        // dimensions -- including for a transposing orientation (5-8), which swaps both.
        Assert.Equal(expectedWidth, decoded.OriginalWidth);
        Assert.Equal(expectedHeight, decoded.OriginalHeight);

        var bmp = (BitmapSource)decoded.PlatformImage;
        var bgra = ImageCompare.ToBgra32(bmp);

        AssertCornerColor(bgra, expectedWidth, expectedHeight, 1, 1, expectedTopLeft);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, expectedWidth - 2, 1, expectedTopRight);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, 1, expectedHeight - 2, expectedBottomLeft);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, expectedWidth - 2, expectedHeight - 2, expectedBottomRight);
    }

    [Theory(DisplayName = "Decode downscaled preserves visual TargetWidth for orientations 1 to 8")]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    [InlineData((ushort)3)]
    [InlineData((ushort)4)]
    [InlineData((ushort)5)]
    [InlineData((ushort)6)]
    [InlineData((ushort)7)]
    [InlineData((ushort)8)]
    public void DecodeDownscaledPreservesTargetWidth(ushort orientation)
    {
        var path = Path.Combine(_tempDir, $"downscale_orient_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        const int targetWidth = 32;
        var decoded = _decoder.Decode(new DecodeRequest(path, TargetWidth: targetWidth, ApplyOrientation: true));

        Assert.Equal(targetWidth, decoded.PixelWidth);
        Assert.True(decoded.Downscaled);

        // perf(dims): OriginalWidth/Height must report the full (post-orientation) source size
        // -- 64x48 for non-transposing orientations, swapped to 48x64 for a transposing one (5-8)
        // -- not the downscaled decode result (targetWidth=32).
        var transposed = orientation is >= 5 and <= 8;
        Assert.Equal(transposed ? 48 : 64, decoded.OriginalWidth);
        Assert.Equal(transposed ? 64 : 48, decoded.OriginalHeight);
    }

    [Fact(DisplayName = "Decode with ApplyOrientation=false ignores EXIF orientation tag")]
    public void DecodeWithoutApplyOrientationIgnoresTag()
    {
        var path = Path.Combine(_tempDir, "orient_ignore_6.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, 6);

        var decoded = _decoder.Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: false));
        // Without orientation, original raw dimensions are preserved
        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
        // OriginalWidth/Height fall back to PixelWidth/Height here (no header re-read without
        // ApplyOrientation) -- correct anyway since this decode is undownscaled.
        Assert.Equal(64, decoded.OriginalWidth);
        Assert.Equal(48, decoded.OriginalHeight);
    }

    private static void AssertCornerColor(byte[] bgra, int width, int height, int x, int y, string expectedColor)
    {
        var offset = (y * width + x) * 4;
        byte b = bgra[offset];
        byte g = bgra[offset + 1];
        byte r = bgra[offset + 2];

        // Yellow: R high, G high, B low
        // Cyan: R low, G high, B high
        // Magenta: R high, G low, B high
        // White: R high, G high, B high
        switch (expectedColor)
        {
            case "Yellow":
                Assert.True(r > 150 && g > 150 && b < 100, $"Expected Yellow at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "Cyan":
                Assert.True(r < 100 && g > 150 && b > 150, $"Expected Cyan at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "Magenta":
                Assert.True(r > 150 && g < 100 && b > 150, $"Expected Magenta at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "White":
                Assert.True(r > 180 && g > 180 && b > 180, $"Expected White at ({x},{y}), got R={r},G={g},B={b}");
                break;
            default:
                throw new ArgumentException($"Unknown color: {expectedColor}", nameof(expectedColor));
        }
    }
}

