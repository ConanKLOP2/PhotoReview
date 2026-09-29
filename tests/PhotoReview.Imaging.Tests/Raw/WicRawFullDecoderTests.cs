using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class WicRawFullDecoderTests(ITestOutputHelper output)
{
    private static readonly string CorpusDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

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

    [Fact]
    public void Decode_AllowsWicWhenEmbeddedPreviewDimensionsAreUnknown()
    {
        var bytes = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: [0xFF, 0xD8, 0xFF, 0xD9]);
        var wic = new FakeDecoder(new FakeImage(640, 480));
        var decoder = new WicRawFullDecoder(new MemorySourceReader(bytes), wic, _ => true);

        Assert.Same(wic.Image, decoder.Decode(new DecodeRequest("sample.dng", DecodeBox.Unbounded)));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void AllRawCorpusSamples_ReportWicFullDecodeAvailability()
    {
        if (!Directory.Exists(CorpusDirectory)) return;

        var decoder = new WicRawFullDecoder();
        var results = new List<(string Format, string Result)>();
        foreach (var path in Directory.GetFiles(CorpusDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(path);
            if (!RawFileTypes.IsRawExtension(path)) continue;

            try
            {
                var image = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
                results.Add((extension, $"full decode {image.OriginalWidth}x{image.OriginalHeight}"));
            }
            catch (NotSupportedException ex)
            {
                results.Add((extension, ex.Message.Contains("only the embedded preview", StringComparison.Ordinal)
                    ? "preview-only"
                    : "unavailable: " + ex.Message));
            }
        }

        Assert.NotEmpty(results);
        Assert.Equal(8, results.Select(result => result.Format).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var group in results.GroupBy(result => result.Format, StringComparer.OrdinalIgnoreCase))
        {
            var outcomes = string.Join("; ", group.GroupBy(result => result.Result, StringComparer.Ordinal)
                .Select(outcome => $"{outcome.Count()} sample(s): {outcome.Key}"));
            output.WriteLine($"{group.Key}: {outcomes}");
        }
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
