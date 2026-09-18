using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Decoding;

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

    [Fact(DisplayName = "Decode with target width reports downscaled true")]
    public void DecodeWithTargetWidthReportsDownscaled()
    {
        var request = new DecodeRequest(_imagePath, TargetWidth: 100);
        var decoded = _decoder.Decode(request);

        Assert.NotNull(decoded);
        Assert.True(decoded.Downscaled);
        Assert.IsType<WpfDecodedImage>(decoded);
        var wpfImage = (WpfDecodedImage)decoded;
        Assert.True(wpfImage.Source.IsFrozen);
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
