using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Quality;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class WicDirectTests : IClassFixture<OrientationFixture>, IDisposable
{
    private readonly string _tempDir;
    private readonly OrientationFixture _files;
    private readonly WicDirectDecoder _wicDecoder = new();
    private readonly WpfBitmapImageDecoder _wpfDecoder = new();

    public WicDirectTests(OrientationFixture files)
    {
        _files = files;
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-WicTests-" + Guid.NewGuid().ToString("N"));
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

    [Fact(DisplayName = "WicDirect decodes lossless PNG with identical fidelity to source (PSNR = inf, DeltaE = 0)")]
    public void PngDecodesWithZeroLoss()
    {
        var pngPath = Path.Combine(_tempDir, "sample.png");
        var original = FixtureGenerator.CreateGradientCheckerboard(64, 48);
        FixtureGenerator.SavePng(original, pngPath, is32Bit: true);

        var decoded = _wicDecoder.Decode(new DecodeRequest(pngPath, TargetWidth: 0));
        Assert.NotNull(decoded);
        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);

        var compare = ImageCompare.Compare(original, (BitmapSource)decoded.PlatformImage);
        Assert.Equal(double.PositiveInfinity, compare.Psnr);
        Assert.Equal(0.0, compare.MeanDeltaE);
        Assert.Equal(0, compare.MaxChannelDiff);
    }

    [Fact(DisplayName = "WicDirect decodes JPEG with high parity to Wpf decoder")]
    public void JpegDecodesWithHighParityToWpf()
    {
        var jpegPath = Path.Combine(_tempDir, "sample.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 400, 300, quality: 90);

        var decodedWic = _wicDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));
        var decodedWpf = _wpfDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));

        Assert.Equal(400, decodedWic.PixelWidth);
        Assert.Equal(300, decodedWic.PixelHeight);

        var compare = ImageCompare.Compare(
            (BitmapSource)decodedWpf.PlatformImage,
            (BitmapSource)decodedWic.PlatformImage);

        // Both use Windows Imaging Component underlying codecs, results should be virtually identical
        Assert.True(compare.Psnr >= 40.0, $"Expected PSNR >= 40 dB, got {compare.Psnr}");
        Assert.True(compare.MeanDeltaE <= 1.0, $"Expected MeanDeltaE <= 1.0, got {compare.MeanDeltaE}");
    }

    [Theory(DisplayName = "WicDirect ReadInfo detects EXIF orientation 1 to 8")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void ReadInfoDetectsOrientation(ushort orientation) =>
        OrientationContract.AssertReadInfo(_wicDecoder, _files.PathFor(orientation), orientation);

    [Theory(DisplayName = "WicDirect applies EXIF orientation and maps corners correctly")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void DecodeAppliesOrientationCorners(ushort orientation) =>
        OrientationContract.AssertDecodeCorners(_wicDecoder, _files.PathFor(orientation), orientation);

    [Fact(DisplayName = "WicDirect downscales correctly using TargetWidth")]
    public void DownscaleProducesTargetWidth()
    {
        var path = Path.Combine(_tempDir, "downscale.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 400, 300);

        const int targetWidth = 100;
        var decoded = _wicDecoder.Decode(new DecodeRequest(path, TargetWidth: targetWidth));

        Assert.Equal(targetWidth, decoded.PixelWidth);
        Assert.Equal(75, decoded.PixelHeight);
        Assert.True(decoded.Downscaled);
        // perf(dims): OriginalWidth/Height must report the full source size, not the
        // downscaled decode result -- this is what lets ImagePresenter learn "original
        // dimensions" from a preview decode instead of a separate ReadInfo/file open.
        Assert.Equal(400, decoded.OriginalWidth);
        Assert.Equal(300, decoded.OriginalHeight);
    }

    [Fact(DisplayName = "WicDirect decodes from memory buffer")]
    public void DecodesFromMemoryBuffer()
    {
        var path = Path.Combine(_tempDir, "memory.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 64, 48);
        var bytes = File.ReadAllBytes(path);

        var decoded = _wicDecoder.Decode(new DecodeRequest(path, TargetWidth: 0, Bytes: bytes));

        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
    }

    [Fact(DisplayName = "WicDirect closes file handle immediately after decode (INV-8)")]
    public void FileCanBeModifiedImmediatelyAfterDecode()
    {
        var path = Path.Combine(_tempDir, "inv8_test.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 64, 48);

        var decoded = _wicDecoder.Decode(new DecodeRequest(path, TargetWidth: 0));
        Assert.NotNull(decoded);

        // Prove handle is closed: deleting or overwriting file immediately must succeed
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "WicDirect fails safely on corrupted files")]
    public void CorruptFilesFailSafely()
    {
        var emptyPath = Path.Combine(_tempDir, "zero.jpg");
        FixtureGenerator.GenerateZeroByteFile(emptyPath);
        Assert.ThrowsAny<Exception>(() => _wicDecoder.Decode(new DecodeRequest(emptyPath, TargetWidth: 0)));

        var textPath = Path.Combine(_tempDir, "text.jpg");
        FixtureGenerator.GenerateTextFile(textPath);
        Assert.ThrowsAny<Exception>(() => _wicDecoder.Decode(new DecodeRequest(textPath, TargetWidth: 0)));
    }
}
