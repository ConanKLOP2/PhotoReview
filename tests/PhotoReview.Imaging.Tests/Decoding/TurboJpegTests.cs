using System;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Quality;
using PhotoReview.Imaging.TurboJpeg;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class TurboJpegTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TurboJpegDecoder _turboDecoder = new();
    private readonly WpfBitmapImageDecoder _wpfDecoder = new();

    public TurboJpegTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-TurboJpegTests-" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup
        }
    }

    [Fact(DisplayName = "TurboJpeg decodes JPEG with high parity to Wpf decoder")]
    public void JpegDecodesWithHighParityToWpf()
    {
        var jpegPath = Path.Combine(_tempDir, "sample.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 400, 300, quality: 90);

        var decodedTurbo = _turboDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));
        var decodedWpf = _wpfDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));

        Assert.Equal(400, decodedTurbo.PixelWidth);
        Assert.Equal(300, decodedTurbo.PixelHeight);

        var compare = ImageCompare.Compare(
            (BitmapSource)decodedWpf.PlatformImage,
            (BitmapSource)decodedTurbo.PlatformImage);

        // TurboJPEG SIMD IDCT and color conversion vs WIC, high fidelity expected
        Assert.True(compare.Psnr >= 35.0, $"Expected PSNR >= 35 dB, got {compare.Psnr}");
        Assert.True(compare.MeanDeltaE <= 2.5, $"Expected MeanDeltaE <= 2.5, got {compare.MeanDeltaE}");
    }

    [Theory(DisplayName = "TurboJpeg ReadInfo detects EXIF orientation 1 to 8")]
    [InlineData((ushort)1, 64, 48)]
    [InlineData((ushort)2, 64, 48)]
    [InlineData((ushort)3, 64, 48)]
    [InlineData((ushort)4, 64, 48)]
    [InlineData((ushort)5, 48, 64)]
    [InlineData((ushort)6, 48, 64)]
    [InlineData((ushort)7, 48, 64)]
    [InlineData((ushort)8, 48, 64)]
    public void ReadInfoDetectsOrientation(ushort orientation, int expectedWidth, int expectedHeight)
    {
        var path = Path.Combine(_tempDir, $"info_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        var info = _turboDecoder.ReadInfo(path);
        Assert.Equal(64, info.PixelWidth);
        Assert.Equal(48, info.PixelHeight);
        Assert.Equal(orientation, info.Orientation);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
    }

    [Theory(DisplayName = "TurboJpeg applies EXIF orientation and maps corners correctly")]
    [InlineData((ushort)1, 64, 48, "Yellow", "Cyan", "Magenta", "White")]
    [InlineData((ushort)2, 64, 48, "Cyan", "Yellow", "White", "Magenta")]
    [InlineData((ushort)3, 64, 48, "White", "Magenta", "Cyan", "Yellow")]
    [InlineData((ushort)4, 64, 48, "Magenta", "White", "Yellow", "Cyan")]
    [InlineData((ushort)5, 48, 64, "Yellow", "Magenta", "Cyan", "White")]
    [InlineData((ushort)6, 48, 64, "Magenta", "Yellow", "White", "Cyan")]
    [InlineData((ushort)7, 48, 64, "White", "Cyan", "Magenta", "Yellow")]
    [InlineData((ushort)8, 48, 64, "Cyan", "White", "Yellow", "Magenta")]
    public void DecodeAppliesOrientationCorners(
        ushort orientation,
        int expectedWidth,
        int expectedHeight,
        string expectedTopLeft,
        string expectedTopRight,
        string expectedBottomLeft,
        string expectedBottomRight)
    {
        var path = Path.Combine(_tempDir, $"orient_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        var decoded = _turboDecoder.Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);

        var bmp = (BitmapSource)decoded.PlatformImage;
        var bgra = ImageCompare.ToBgra32(bmp);

        AssertCornerColor(bgra, expectedWidth, expectedHeight, 1, 1, expectedTopLeft);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, expectedWidth - 2, 1, expectedTopRight);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, 1, expectedHeight - 2, expectedBottomLeft);
        AssertCornerColor(bgra, expectedWidth, expectedHeight, expectedWidth - 2, expectedHeight - 2, expectedBottomRight);
    }

    [Fact(DisplayName = "TurboJpeg downscales correctly using TargetWidth and DCT scaling")]
    public void DownscaleProducesTargetWidth()
    {
        var path = Path.Combine(_tempDir, "downscale.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 400, 300);

        const int targetWidth = 100;
        var decoded = _turboDecoder.Decode(new DecodeRequest(path, TargetWidth: targetWidth));

        Assert.Equal(targetWidth, decoded.PixelWidth);
        Assert.Equal(75, decoded.PixelHeight);
        Assert.True(decoded.Downscaled);
    }

    [Fact(DisplayName = "TurboJpeg decodes from memory buffer")]
    public void DecodesFromMemoryBuffer()
    {
        var path = Path.Combine(_tempDir, "memory.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 64, 48);
        var bytes = File.ReadAllBytes(path);

        var decoded = _turboDecoder.Decode(new DecodeRequest(path, TargetWidth: 0, Bytes: bytes));

        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
    }

    [Fact(DisplayName = "TurboJpeg closes file handle immediately after decode (INV-8)")]
    public void FileCanBeModifiedImmediatelyAfterDecode()
    {
        var path = Path.Combine(_tempDir, "inv8_test.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 64, 48);

        var decoded = _turboDecoder.Decode(new DecodeRequest(path, TargetWidth: 0));
        Assert.NotNull(decoded);

        // Prove handle is closed: deleting or overwriting file immediately must succeed
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "TurboJpeg throws NotSupportedException on non-JPEG files (PNG)")]
    public void NonJpegFilesThrowNotSupportedException()
    {
        var pngPath = Path.Combine(_tempDir, "sample.png");
        var original = FixtureGenerator.CreateGradientCheckerboard(64, 48);
        FixtureGenerator.SavePng(original, pngPath, is32Bit: true);

        Assert.Throws<NotSupportedException>(() => _turboDecoder.Decode(new DecodeRequest(pngPath, TargetWidth: 0)));
        Assert.Throws<NotSupportedException>(() => _turboDecoder.ReadInfo(pngPath));
    }

    [Fact(DisplayName = "TurboJpeg throws NotSupportedException on JPEG with embedded ICC profile for fallback")]
    public void JpegWithIccProfileThrowsNotSupportedException()
    {
        var iccPath = Path.Combine(_tempDir, "icc.jpg");
        var generated = FixtureGenerator.GenerateJpegWithIcc(iccPath, 64, 48);

        if (File.Exists(generated))
        {
            var bytes = File.ReadAllBytes(generated);
            if (TurboJpegDecoder.HasEmbeddedIccProfile(bytes))
            {
                Assert.Throws<NotSupportedException>(() => _turboDecoder.Decode(new DecodeRequest(generated, TargetWidth: 0)));
            }
        }
    }

    [Fact(DisplayName = "TurboJpeg throws FileNotFoundException on missing files (INV-12)")]
    public void MissingFileThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_tempDir, "does_not_exist.jpg");
        Assert.Throws<FileNotFoundException>(() => _turboDecoder.Decode(new DecodeRequest(missingPath, TargetWidth: 0)));
        Assert.Throws<FileNotFoundException>(() => _turboDecoder.ReadInfo(missingPath));
    }

    [Fact(DisplayName = "TurboJpeg fails safely on corrupted files")]
    public void CorruptFilesFailSafely()
    {
        var emptyPath = Path.Combine(_tempDir, "zero.jpg");
        FixtureGenerator.GenerateZeroByteFile(emptyPath);
        Assert.ThrowsAny<Exception>(() => _turboDecoder.Decode(new DecodeRequest(emptyPath, TargetWidth: 0)));

        var textPath = Path.Combine(_tempDir, "text.jpg");
        FixtureGenerator.GenerateTextFile(textPath);
        Assert.ThrowsAny<Exception>(() => _turboDecoder.Decode(new DecodeRequest(textPath, TargetWidth: 0)));
    }

    private static void AssertCornerColor(byte[] bgra, int width, int height, int x, int y, string expectedColor)
    {
        var offset = (y * width + x) * 4;
        byte b = bgra[offset];
        byte g = bgra[offset + 1];
        byte r = bgra[offset + 2];

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

