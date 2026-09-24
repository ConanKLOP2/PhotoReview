using System;
using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// perf(decode): every <see cref="IImageDecoder"/> must decode into the width x height box of
/// <see cref="DecodeRequest"/> (after EXIF orientation), preserving aspect and never upscaling,
/// with exactly the size <see cref="DecodeBox.Fit"/> predicts.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecodeBoxDecoderTests(DecodeBoxDecoderTests.SourceImages images)
    : IClassFixture<DecodeBoxDecoderTests.SourceImages>
{
    private static readonly DecodeBox Box = new(320, 160);

    /// <summary>Source images generated once per class; the tests only read them.</summary>
    public sealed class SourceImages : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "PhotoReview-DecodeBoxTests-" + Guid.NewGuid().ToString("N"));

        public SourceImages()
        {
            Directory.CreateDirectory(_dir);
            Landscape = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_dir, "landscape.jpg"), 600, 400);
            Portrait = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_dir, "portrait.jpg"), 400, 600);
            Panorama = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_dir, "pano.jpg"), 960, 240);
            Tiny = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_dir, "tiny.jpg"), 120, 80);
            Rotated6 = FixtureGenerator.GenerateJpegWithOrientation(Path.Combine(_dir, "rotated-6.jpg"), 600, 400, 6);
            Rotated8 = FixtureGenerator.GenerateJpegWithOrientation(Path.Combine(_dir, "rotated-8.jpg"), 600, 400, 8);
            LargePng = FixtureGenerator.GeneratePng(Path.Combine(_dir, "large.png"), 400, 600);
            SmallPng = FixtureGenerator.GeneratePng(Path.Combine(_dir, "small.png"), 100, 60);
        }

        public string Landscape { get; }
        public string Portrait { get; }
        public string Panorama { get; }
        public string Tiny { get; }
        public string Rotated6 { get; }
        public string Rotated8 { get; }
        public string LargePng { get; }
        public string SmallPng { get; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static TheoryData<string> JpegDecoders => ["Wpf", "WicDirect", "TurboJpeg"];
    public static TheoryData<string> PngDecoders => ["Wpf", "WicDirect"];

    private static IImageDecoder Create(string name) => name switch
    {
        "Wpf" => new WpfBitmapImageDecoder(),
        "WicDirect" => new WicDirectDecoder(),
        "TurboJpeg" => new TurboJpegDecoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory(DisplayName = "Landscape JPEG: height of the box constrains")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_LandscapeJpeg_FitsBoxHeight(string decoder)
    {
        var path = images.Landscape;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box));

        // 600x400 in 320x160 -> 240x160 (width-only 320 would have been 320x213).
        AssertSize(240, 160, decoded);
        Assert.True(decoded.Downscaled);
    }

    [Theory(DisplayName = "Portrait JPEG: height of the box constrains")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_PortraitJpeg_FitsBoxHeight(string decoder)
    {
        var path = images.Portrait;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box));

        // 400x600 in 320x160 -> 106x160 (width-only 320 would have been 320x480: 9x the pixels).
        AssertSize(106, 160, decoded);
    }

    [Theory(DisplayName = "EXIF 6 / 8 rotated JPEG: box applies to the rotated (displayed) size")]
    [InlineData("Wpf", (ushort)6)]
    [InlineData("Wpf", (ushort)8)]
    [InlineData("WicDirect", (ushort)6)]
    [InlineData("WicDirect", (ushort)8)]
    [InlineData("TurboJpeg", (ushort)6)]
    [InlineData("TurboJpeg", (ushort)8)]
    public void Decode_ExifRotatedJpeg_BoxAppliedAfterOrientation(string decoder, ushort orientation)
    {
        // Stored 600x400 landscape displayed as a 400x600 portrait.
        var path = orientation == 6 ? images.Rotated6 : images.Rotated8;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box));

        AssertSize(106, 160, decoded);
        Assert.Equal(orientation, decoded.Orientation);
    }

    [Theory(DisplayName = "Wide panorama JPEG: width of the box constrains")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_PanoramaJpeg_FitsBoxWidth(string decoder)
    {
        var path = images.Panorama;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box));

        AssertSize(320, 80, decoded);
    }

    [Theory(DisplayName = "Image smaller than the box is not upscaled")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_TinyJpeg_NotUpscaled(string decoder)
    {
        var path = images.Tiny;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box));

        AssertSize(120, 80, decoded);
        Assert.False(decoded.Downscaled);
    }

    [Theory(DisplayName = "PNG: box honoured and no upscale")]
    [MemberData(nameof(PngDecoders))]
    public void Decode_Png_FitsBox(string decoder)
    {
        var large = images.LargePng;
        var small = images.SmallPng;

        AssertSize(106, 160, Create(decoder).Decode(new DecodeRequest(large, Box)));
        AssertSize(100, 60, Create(decoder).Decode(new DecodeRequest(small, Box)));
    }

    [Theory(DisplayName = "Pre-read bytes path honours the box too")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_FromBytes_FitsBox(string decoder)
    {
        var path = images.Portrait;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, Box, bytes: File.ReadAllBytes(path)));

        AssertSize(106, 160, decoded);
    }

    [Theory(DisplayName = "Unbounded box decodes at full size (Original mode)")]
    [MemberData(nameof(JpegDecoders))]
    public void Decode_UnboundedBox_FullSize(string decoder)
    {
        var path = images.Portrait;

        var decoded = Create(decoder).Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        AssertSize(400, 600, decoded);
        Assert.False(decoded.Downscaled);
    }

    private static void AssertSize(int expectedWidth, int expectedHeight, IDecodedImage decoded)
    {
        Assert.Equal((expectedWidth, expectedHeight), (decoded.PixelWidth, decoded.PixelHeight));
    }
}
