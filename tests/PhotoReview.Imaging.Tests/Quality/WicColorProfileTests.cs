using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Quality;

public sealed class WicColorProfileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "PhotoReview-WicColorProfileTests-" + Guid.NewGuid().ToString("N"));

    public WicColorProfileTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void BundledDisplayP3ProfileHasPinnedHashAndJpegContainsIccApp2()
    {
        FixtureGenerator.AssertBundledDisplayP3ProfileIntegrity();
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, "p3.jpg"), 128, 96);

        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void DirectWicRejectsProfiledJpegForPathAndMemory(int targetWidth)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, $"p3-{targetWidth}.jpg"), 128, 96);
        var decoder = new WicDirectDecoder();

        Assert.Throws<NotSupportedException>(() =>
            decoder.Decode(new DecodeRequest(path, targetWidth)));
        Assert.Throws<NotSupportedException>(() =>
            decoder.Decode(new DecodeRequest(path, targetWidth, Bytes: File.ReadAllBytes(path))));
        Assert.Throws<NotSupportedException>(() => decoder.ReadInfo(path));
    }

    [Theory]
    [InlineData(0, 128, 96)]
    [InlineData(64, 64, 48)]
    public void FactoryFallsBackToWpfForProfiledJpeg(int targetWidth, int expectedWidth, int expectedHeight)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, $"fallback-{targetWidth}.jpg"), 128, 96);
        var decoder = new ImageDecoderFactory().Create(DecoderBackend.WicDirect);

        var decoded = decoder.Decode(new DecodeRequest(path, targetWidth));

        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        Assert.Equal(128, decoder.ReadInfo(path).PixelWidth);
    }

    [Fact]
    public void FactoryFallbackPreservesAlphaForProfiledPng()
    {
        var path = FixtureGenerator.GeneratePngWithIcc(
            Path.Combine(_tempDir, "p3-alpha.png"), 32, 24);
        var decoder = new ImageDecoderFactory().Create(DecoderBackend.WicDirect);

        var decoded = decoder.Decode(new DecodeRequest(path, 0));
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);

        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.True(bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32);
    }
}
