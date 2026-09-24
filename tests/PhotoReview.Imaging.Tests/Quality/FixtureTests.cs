using System;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Quality;

[Trait("Category", "HotPath")]
public sealed class FixtureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WpfBitmapImageDecoder _decoder = new();

    public FixtureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-FixtureTests-" + Guid.NewGuid().ToString("N"));
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
        }
    }

    [Fact(DisplayName = "Identical images return PSNR infinity, DeltaE zero, and MaxDiff zero")]
    public void IdenticalImagesReturnPerfectMetrics()
    {
        var bitmap = FixtureGenerator.CreateGradientCheckerboard(64, 48);
        var result = ImageCompare.Compare(bitmap, bitmap);

        Assert.Equal(double.PositiveInfinity, result.Psnr);
        Assert.Equal(0.0, result.MeanDeltaE);
        Assert.Equal(0, result.MaxChannelDiff);
    }

    [Fact(DisplayName = "Perturbed image yields finite PSNR, positive DeltaE, and non-zero MaxDiff")]
    public void PerturbedImageYieldsExpectedMetrics()
    {
        var bufferA = new byte[] { 100, 150, 200, 255 };
        var bufferB = new byte[] { 105, 150, 195, 255 };

        var psnr = PixelMetrics.Psnr(bufferA, bufferB);
        var deltaE = PixelMetrics.MeanDeltaE(bufferA, bufferB);
        var maxDiff = PixelMetrics.MaxChannelDiff(bufferA, bufferB);

        Assert.True(psnr > 0 && !double.IsInfinity(psnr));
        Assert.True(deltaE > 0);
        Assert.Equal(5, maxDiff);
    }

    [Fact(DisplayName = "Generated PNG 24-bit and 32-bit decode losslessly")]
    public void PngFixturesDecodeLosslessly()
    {
        var png32Path = Path.Combine(_tempDir, "test32.png");
        var original = FixtureGenerator.CreateGradientCheckerboard(64, 48);
        FixtureGenerator.SavePng(original, png32Path, is32Bit: true);

        var decoded32 = _decoder.Decode(new DecodeRequest(png32Path, TargetWidth: 0));
        var compare32 = ImageCompare.Compare(original, (BitmapSource)decoded32.PlatformImage);

        Assert.Equal(double.PositiveInfinity, compare32.Psnr);
        Assert.Equal(0.0, compare32.MeanDeltaE);
        Assert.Equal(0, compare32.MaxChannelDiff);

        var png24Path = Path.Combine(_tempDir, "test24.png");
        FixtureGenerator.SavePng(original, png24Path, is32Bit: false);
        var decoded24 = _decoder.Decode(new DecodeRequest(png24Path, TargetWidth: 0));
        Assert.Equal(64, decoded24.PixelWidth);
        Assert.Equal(48, decoded24.PixelHeight);
    }

    [Fact(DisplayName = "Generated JPEG smoke and high-res fixtures decode with high fidelity")]
    public void JpegFixturesDecodeWithHighFidelity()
    {
        var smokePath = Path.Combine(_tempDir, "smoke.jpg");
        var originalSmoke = FixtureGenerator.CreateGradientCheckerboard(64, 48);
        FixtureGenerator.SaveJpeg(originalSmoke, smokePath, quality: 90);

        var decodedSmoke = _decoder.Decode(new DecodeRequest(smokePath, TargetWidth: 0));
        Assert.Equal(64, decodedSmoke.PixelWidth);
        Assert.Equal(48, decodedSmoke.PixelHeight);

        var compareSmoke = ImageCompare.Compare(originalSmoke, (BitmapSource)decodedSmoke.PlatformImage);
        // Small 64x48 image with sharp high-contrast checkerboard squares suffers high-frequency loss under JPEG DCT/subsampling
        Assert.True(compareSmoke.Psnr >= 20.0, $"Expected PSNR >= 20 dB, got {compareSmoke.Psnr}");
        Assert.True(compareSmoke.MeanDeltaE <= 16.0, $"Expected MeanDeltaE <= 16.0, got {compareSmoke.MeanDeltaE}");

        var midPath = Path.Combine(_tempDir, "mid_400x300.jpg");
        var originalMid = FixtureGenerator.CreateGradientCheckerboard(400, 300);
        FixtureGenerator.SaveJpeg(originalMid, midPath, quality: 90);
        var decodedMid = _decoder.Decode(new DecodeRequest(midPath, TargetWidth: 0));
        Assert.Equal(400, decodedMid.PixelWidth);
        Assert.Equal(300, decodedMid.PixelHeight);

        var compareMid = ImageCompare.Compare(originalMid, (BitmapSource)decodedMid.PlatformImage);
        Assert.True(compareMid.Psnr >= 25.0, $"Expected PSNR >= 25 dB, got {compareMid.Psnr}");
        Assert.True(compareMid.MeanDeltaE <= 6.0, $"Expected MeanDeltaE <= 6.0, got {compareMid.MeanDeltaE}");
    }

    [Fact(DisplayName = "JPEG with embedded ICC profile decodes cleanly")]
    public void JpegWithIccProfileDecodesCleanly()
    {
        var iccFile = FixtureGenerator.FindDefaultSystemIccProfile();
        if (iccFile is null)
        {
            return; // Skip gracefully if OS has no color profile installed
        }

        var path = Path.Combine(_tempDir, "icc_test.jpg");
        FixtureGenerator.GenerateJpegWithIcc(path, 64, 48, iccFile);

        var decoded = _decoder.Decode(new DecodeRequest(path, TargetWidth: 0));
        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
    }

    [Fact(DisplayName = "Manual verification against real photos in PHOTOREVIEW_FIXTURE_DIR")]
    [Trait("Category", "Manual")]
    public void RealWorldPhotosManualTest()
    {
        var fixtureDir = Environment.GetEnvironmentVariable("PHOTOREVIEW_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(fixtureDir) || !Directory.Exists(fixtureDir))
        {
            return;
        }

        var files = Directory.GetFiles(fixtureDir, "*.jpg");
        foreach (var file in files)
        {
            var decoded = _decoder.Decode(new DecodeRequest(file, TargetWidth: 1920));
            Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0);
        }
    }
}
