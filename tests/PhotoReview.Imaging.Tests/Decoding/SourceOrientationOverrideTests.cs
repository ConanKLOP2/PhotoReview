using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.TurboJpeg;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

public sealed class SourceOrientationOverrideTests
{
    [Theory]
    [InlineData(1, 6)]
    [InlineData(6, 1)]
    [InlineData(3, 8)]
    [InlineData(8, 3)]
    public void AllDecoders_SourceOrientationOverride_WinsOverEmbeddedExif(ushort embeddedExif, int overrideOrientation)
    {
        var jpeg = Metadata.ExifTestData.EncodeJpegWithExif(64, 48, withExif: true, orientation: embeddedExif);

        var wpfDecoder = new WpfBitmapImageDecoder();
        var wicDecoder = new WicDirectDecoder();
        var turboDecoder = new TurboJpegDecoder();

        var request = new DecodeRequest(
            Path: "synthetic.jpg",
            TargetWidth: 0,
            ApplyOrientation: true,
            Bytes: jpeg,
            SourceOrientation: overrideOrientation);

        // 1. WPF decoder
        var wpfResult = wpfDecoder.Decode(request);
        Assert.Equal(overrideOrientation, wpfResult.Orientation);

        // 2. WIC decoder
        var wicResult = wicDecoder.Decode(request);
        Assert.Equal(overrideOrientation, wicResult.Orientation);

        // 3. TurboJPEG decoder
        var turboResult = turboDecoder.Decode(request);
        Assert.Equal(overrideOrientation, turboResult.Orientation);
    }

    [Fact]
    public void ImageCacheKey_SourceKind_Inequality()
    {
        var key0 = ImageCacheKey.Create(new FileInfo(typeof(SourceOrientationOverrideTests).Assembly.Location), isOriginal: false, 1000, sourceKind: 0);
        var key1 = ImageCacheKey.Create(new FileInfo(typeof(SourceOrientationOverrideTests).Assembly.Location), isOriginal: false, 1000, sourceKind: 1);
        var key2 = ImageCacheKey.Create(new FileInfo(typeof(SourceOrientationOverrideTests).Assembly.Location), isOriginal: false, 1000, sourceKind: 2);

        Assert.NotEqual(key0, key1);
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key0.GetHashCode(), key1.GetHashCode());
    }

    [Fact]
    public void ImageCacheKey_CreateOriginalCanDistinguishRawFullDecode()
    {
        var source = ImageCacheKey.Create(new FileInfo(typeof(SourceOrientationOverrideTests).Assembly.Location),
            isOriginal: false, 1000, sourceKind: 1);

        var previewOriginal = ImageCacheKey.CreateOriginal(source);
        var rawFullDecode = ImageCacheKey.CreateOriginal(source, sourceKind: 2);

        Assert.Equal(1, previewOriginal.SourceKind);
        Assert.Equal(2, rawFullDecode.SourceKind);
        Assert.NotEqual(previewOriginal, rawFullDecode);
    }
}
