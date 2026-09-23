using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Quality;

[Trait("Category", "HotPath")]
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
    [InlineData(0, 128, 96)]
    [InlineData(64, 64, 48)]
    public void DirectWicDecodesProfiledJpegForPathAndMemory(int targetWidth, int expectedWidth, int expectedHeight)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, $"p3-{targetWidth}.jpg"), 128, 96);
        var decoder = new WicDirectDecoder();

        foreach (var request in new[]
                 {
                     new DecodeRequest(path, targetWidth),
                     new DecodeRequest(path, targetWidth, Bytes: File.ReadAllBytes(path))
                 })
        {
            var decoded = decoder.Decode(request);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
            Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
            Assert.Equal(expectedWidth, decoded.PixelWidth);
            Assert.Equal(expectedHeight, decoded.PixelHeight);
            Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        }

        var info = decoder.ReadInfo(path);
        Assert.Equal(128, info.PixelWidth);
        Assert.Equal(96, info.PixelHeight);
    }

    [Fact]
    public void DirectWicTransformsDisplayP3PixelsToSrgbLikeColorManagedWpf()
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, "p3-color.jpg"), 128, 96);

        var wic = new WicDirectDecoder().Decode(new DecodeRequest(path, 0));
        var wpf = new WpfBitmapImageDecoder().Decode(new DecodeRequest(path, 0));
        var untransformed = DecodeIgnoringColorProfile(path);

        double meanVsWpf = MeanAbsRgbDiff(ImageCompare.ToBgra32(wic), ImageCompare.ToBgra32(wpf));
        double meanUntransformedVsWpf = MeanAbsRgbDiff(
            ImageCompare.ToBgra32(untransformed), ImageCompare.ToBgra32(wpf));

        // The fixture's P3 profile shifts colors noticeably; WicDirect must match WPF's color management.
        Assert.True(meanUntransformedVsWpf > 3.0,
            $"Fixture should differ visibly without color management, mean diff {meanUntransformedVsWpf:F3}");
        Assert.True(meanVsWpf <= 2.0, $"WicDirect vs WPF mean abs channel diff {meanVsWpf:F3} > 2/255");
    }

    [Theory]
    [InlineData(0, 128, 96)]
    [InlineData(64, 64, 48)]
    public void FactoryDecodesProfiledJpegWithoutFallback(int targetWidth, int expectedWidth, int expectedHeight)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(
            Path.Combine(_tempDir, $"nofallback-{targetWidth}.jpg"), 128, 96);
        var metrics = new ReviewMetrics();
        var decoder = new ImageDecoderFactory(metrics: metrics).Create(DecoderBackend.WicDirect);
        Assert.IsType<FallbackImageDecoder>(decoder);

        var decoded = decoder.Decode(new DecodeRequest(path, targetWidth));

        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        Assert.Equal(128, decoder.ReadInfo(path).PixelWidth);
        Assert.Equal(0, metrics.Snapshot().DecoderFallbackCount);
    }

    [Fact]
    public void ProfiledPngWithAlphaDecodesPremultipliedAndKeepsAlpha()
    {
        var path = FixtureGenerator.GeneratePngWithIcc(
            Path.Combine(_tempDir, "p3-alpha.png"), 32, 24);
        var decoder = new ImageDecoderFactory().Create(DecoderBackend.WicDirect);

        var decoded = decoder.Decode(new DecodeRequest(path, 0));
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);

        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);

        var firstPixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), firstPixel, 4, 0);
        Assert.Equal(64, firstPixel[3]);
    }

    private static BitmapImage DecodeIgnoringColorProfile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static double MeanAbsRgbDiff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        long count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                sum += Math.Abs(a[i + c] - b[i + c]);
                count++;
            }
        }

        return (double)sum / count;
    }
}
