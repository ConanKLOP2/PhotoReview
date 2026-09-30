using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// C3: a valid RAW with no embedded JPEG uses the preview fallback (then the full-decode fallback) for every format
/// instead of failing with "No embedded preview".
/// </summary>
public sealed class RawDecoderNoPreviewFallbackTests
{
    private const string LeicaDng = "Leica - M8 - 8bit 8bit uncompressed (3_2).DNG";

    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    /// <summary>TIFF whose JPEG tags point at a zero-length range: a well-formed RAW container with no usable preview.</summary>
    private static string WriteRawWithoutPreview(TempRoot temp, string name) =>
        temp.File(name, SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: []));

    private static byte[] RealJpeg(TempRoot temp, int width, int height)
    {
        var jpegPath = Path.Combine(temp.Path, "thumb.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, width, height);
        return File.ReadAllBytes(jpegPath);
    }

    [Theory]
    [InlineData("nopreview.dng")]
    [InlineData("nopreview.nef")]
    public void Decode_NoEmbeddedPreviewWithFallback_DecodesTheFallbackThumbnail(string fileName)
    {
        using var temp = new TempRoot("raw-no-preview-fallback");
        var path = WriteRawWithoutPreview(temp, fileName);
        var fallback = new RecordingFallback(RealJpeg(temp, 96, 64));

        var decoded = new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: fallback)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(path, fallback.LastPath);
        Assert.Equal((96, 64), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void Decode_NoEmbeddedPreviewWithoutAnyFallback_KeepsTheTypedError()
    {
        using var temp = new TempRoot("raw-no-preview-nofallback");
        var path = WriteRawWithoutPreview(temp, "nopreview.dng");

        var ex = Assert.Throws<InvalidDataException>(() =>
            new RawDecoder(new WpfBitmapImageDecoder()).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        // Not "corrupt": the file is valid, it just carries no JPEG preview, so it gets its own localized sentence.
        Assert.True(PhotoReview.Core.Localization.UserFacingError.IsLocalized(ex));
        Assert.Equal(PhotoReview.Core.Localization.Tr.ImageErrorRawNoPreview, PhotoReview.Core.Localization.UserFacingError.Describe(ex));
    }

    [Fact]
    public void Decode_NoEmbeddedPreviewAndFallbackHasNoJpeg_UsesTheFullDecoder()
    {
        using var temp = new TempRoot("raw-no-preview-fulldecode");
        var path = WriteRawWithoutPreview(temp, "nopreview.dng");
        var full = new RecordingFullDecoder(RealJpeg(temp, 48, 32));
        var fallback = new ThrowingFallback(new NotSupportedException("thumbnail fallback is not enabled for Dng"));

        var decoded = new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: fallback, noPreviewDecoder: full)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(1, full.CallCount);
        Assert.Equal((48, 32), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void Decode_NoEmbeddedPreviewWithOnlyTheFullDecoder_UsesIt()
    {
        using var temp = new TempRoot("raw-no-preview-fullonly");
        var path = WriteRawWithoutPreview(temp, "nopreview.dng");
        var full = new RecordingFullDecoder(RealJpeg(temp, 48, 32));

        var decoded = new RawDecoder(new WpfBitmapImageDecoder(), noPreviewDecoder: full)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, full.CallCount);
        Assert.Equal((48, 32), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void Decode_EmbeddedPreviewPresent_NeverTouchesTheFallbacks()
    {
        using var temp = new TempRoot("raw-with-preview");
        var jpeg = RealJpeg(temp, 64, 48);
        var path = temp.File("preview.dng", SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg));
        var fallback = new RecordingFallback(jpeg);
        var full = new RecordingFullDecoder(jpeg);

        _ = new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: fallback, noPreviewDecoder: full)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(0, fallback.CallCount);
        Assert.Equal(0, full.CallCount);
    }

    [Fact]
    public void Decode_NoEmbeddedPreviewAndFallbackThumbnailEmpty_FailsWithTypedError()
    {
        using var temp = new TempRoot("raw-no-preview-badthumb");
        var path = WriteRawWithoutPreview(temp, "nopreview.dng");

        Assert.Throws<InvalidDataException>(() =>
            new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: new RecordingFallback([]))
                .Decode(new DecodeRequest(path, DecodeBox.Unbounded)));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void Decode_CorpusLeicaDngWithLibRaw_ProducesAnImage()
    {
        var path = Path.Combine(CorpusDir, LeicaDng);
        if (!File.Exists(path) || !LibRawAvailability.Probe(out _)) return;

        var decoded = new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: new LibRawThumbnailFallback(),
                noPreviewDecoder: new LibRawDecoder())
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0);
    }

    private sealed class RecordingFallback(byte[] thumbnail) : IRawPreviewFallback
    {
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }

        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
        {
            CallCount++;
            LastPath = path;
            return thumbnail;
        }
    }

    private sealed class ThrowingFallback(Exception error) : IRawPreviewFallback
    {
        public int CallCount { get; private set; }

        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
        {
            CallCount++;
            throw error;
        }
    }

    /// <summary>Stands in for LibRaw's full decode: returns the image decoded from a prepared JPEG.</summary>
    private sealed class RecordingFullDecoder(byte[] jpeg) : IImageDecoder
    {
        public int CallCount { get; private set; }

        public IDecodedImage Decode(DecodeRequest request)
        {
            CallCount++;
            return new WpfBitmapImageDecoder().Decode(request with { Bytes = jpeg });
        }

        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();
    }

    private sealed class LibRawThumbnailFallback : IRawPreviewFallback
    {
        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format) => LibRawDecoder.ReadJpegThumbnail(path);
    }
}
