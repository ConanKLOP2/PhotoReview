using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class WicRawFullDecoderTests
{
    [Fact]
    public void IsCodecAvailable_CachesProbeResultPerFormat()
    {
        var calls = new Dictionary<RawFormat, int>();
        var decoder = new WicRawFullDecoder(format =>
        {
            calls[format] = calls.GetValueOrDefault(format) + 1;
            return format == RawFormat.Dng;
        });

        Assert.True(decoder.IsCodecAvailable(RawFormat.Dng));
        Assert.True(decoder.IsCodecAvailable(RawFormat.Dng));
        Assert.False(decoder.IsCodecAvailable(RawFormat.Cr3));
        Assert.False(decoder.IsCodecAvailable(RawFormat.Cr3));

        Assert.Equal(1, calls[RawFormat.Dng]);
        Assert.Equal(1, calls[RawFormat.Cr3]);
    }

    [Fact]
    public void Decode_PassesContainerOrientationAndReturnsWicFullResolution()
    {
        var bytes = SyntheticRawBuilder.BuildTiff(littleEndian: true,
            jpegBytes: SyntheticRawBuilder.CreateMinimalJpeg(320, 240), orientation: 6);
        var reader = new MemorySourceReader(bytes);
        var wic = new FakeDecoder(new FakeImage(600, 800));
        var decoder = new WicRawFullDecoder(reader, wic, _ => true);

        var image = decoder.Decode(new DecodeRequest("sample.dng", DecodeBox.Unbounded));

        Assert.Same(wic.Image, image);
        Assert.Equal(6, wic.LastRequest?.SourceOrientation);
        Assert.Equal(600, image.OriginalWidth);
        Assert.Equal(800, image.OriginalHeight);
    }

    [Fact]
    public void Decode_RejectsWicResultThatMatchesEmbeddedPreviewDimensions()
    {
        var bytes = SyntheticRawBuilder.BuildTiff(littleEndian: true,
            jpegBytes: SyntheticRawBuilder.CreateMinimalJpeg(320, 240), orientation: 6);
        var wic = new FakeDecoder(new FakeImage(240, 320));
        var decoder = new WicRawFullDecoder(new MemorySourceReader(bytes), wic, _ => true);

        var error = Assert.Throws<NotSupportedException>(() =>
            decoder.Decode(new DecodeRequest("sample.dng", DecodeBox.Unbounded)));

        Assert.Contains("only the embedded preview", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, wic.LastRequest?.SourceOrientation);
    }

    [Fact]
    public void Decode_RejectsFormatWithoutRegisteredWicCodecBeforeOpeningDecoder()
    {
        var bytes = SyntheticRawBuilder.BuildTiff(littleEndian: true,
            jpegBytes: SyntheticRawBuilder.CreateMinimalJpeg(320, 240));
        var wic = new FakeDecoder(new FakeImage(640, 480));
        var decoder = new WicRawFullDecoder(new MemorySourceReader(bytes), wic, _ => false);

        Assert.Throws<NotSupportedException>(() => decoder.Decode(new DecodeRequest("sample.dng", DecodeBox.Unbounded)));
        Assert.Null(wic.LastRequest);
    }

    private sealed class MemorySourceReader(byte[] bytes) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
            new MemoryStream(bytes, writable: false);
    }

    private sealed class FakeDecoder(IDecodedImage image) : IImageDecoder
    {
        public IDecodedImage Image { get; } = image;
        public DecodeRequest? LastRequest { get; private set; }
        public IDecodedImage Decode(DecodeRequest request)
        {
            LastRequest = request;
            return Image;
        }
        public ImageInfo ReadInfo(string path) => new(Image.OriginalWidth, Image.OriginalHeight);
    }

    private sealed class FakeImage(int width, int height) : IDecodedImage
    {
        public int PixelWidth => width;
        public int PixelHeight => height;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => (long)width * height * 4;
        public object PlatformImage { get; } = new();
        public int OriginalWidth => width;
        public int OriginalHeight => height;
    }
}
