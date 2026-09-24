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
public sealed class TurboJpegTests : IClassFixture<OrientationFixture>, IDisposable
{
    private readonly string _tempDir;
    private readonly OrientationFixture _files;
    private readonly TurboJpegDecoder _turboDecoder = new();
    private readonly WpfBitmapImageDecoder _wpfDecoder = new();

    public TurboJpegTests(OrientationFixture files)
    {
        _files = files;
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
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void ReadInfoDetectsOrientation(ushort orientation) =>
        OrientationContract.AssertReadInfo(_turboDecoder, _files.PathFor(orientation), orientation);

    [Theory(DisplayName = "TurboJpeg applies EXIF orientation and maps corners correctly")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void DecodeAppliesOrientationCorners(ushort orientation) =>
        OrientationContract.AssertDecodeCorners(_turboDecoder, _files.PathFor(orientation), orientation);

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
        // perf(dims): OriginalWidth/Height must report the full source size, not the
        // downscaled decode result.
        Assert.Equal(400, decoded.OriginalWidth);
        Assert.Equal(300, decoded.OriginalHeight);
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
}
