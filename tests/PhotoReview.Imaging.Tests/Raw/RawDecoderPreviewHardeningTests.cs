using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RawDecoder preview hardening: a preview-length cap checked before allocation, localized "corrupt" errors for every
/// preview-phase InvalidDataException, preview resolution that survives an exhausted header budget, and thumbnail-only
/// containers that are still decoded but marked downscaled.
/// </summary>
public sealed class RawDecoderPreviewHardeningTests
{
    /// <summary>Container reader returning a fixed <see cref="RawContainerInfo"/> for any .dng.</summary>
    private sealed class FixedContainerReader(RawContainerInfo info) : IRawContainerReader
    {
        public RawFormat Format => info.Format;
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);
        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken) => info;
    }

    private static RawDecoder NewDecoder(RawContainerInfo info, IImageDecoder? inner = null, IRawPreviewFallback? fallback = null) =>
        new(inner ?? new WpfBitmapImageDecoder(),
            registry: new RawContainerReaderRegistry([new FixedContainerReader(info)]),
            previewFallback: fallback);

    private static RawContainerInfo Info(int sensorWidth, int sensorHeight, params EmbeddedPreview[] previews) =>
        new(RawFormat.Dng, sensorWidth, sensorHeight, 1, previews, []);

    [Fact]
    public void Decode_PreviewLongerThanTheCap_FailsLocalizedBeforeReading()
    {
        using var temp = new TempRoot("raw-preview-cap");
        var path = temp.File("huge.dng", new byte[256]);
        var preview = new EmbeddedPreview(0, 16, RawContainerLimits.MaxPreviewBytes + 1L, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Unknown);

        var ex = Assert.Throws<InvalidDataException>(() => NewDecoder(Info(4000, 3000, preview)).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.Equal(Tr.ImageErrorRawCorrupt, UserFacingError.Describe(ex));
    }

    [Fact]
    public void Decode_AdobeRgbPreviewThatIsNotAJpeg_FailsWithTheLocalizedCorruptSentence()
    {
        using var temp = new TempRoot("raw-preview-icc");
        var path = temp.File("notjpeg.dng", Enumerable.Repeat((byte)0x11, 256).ToArray());
        var preview = new EmbeddedPreview(0, 16, 64, EmbeddedPreviewKind.Jpeg, 640, 480, PreviewColorSpace.AdobeRgb);

        var ex = Assert.Throws<InvalidDataException>(() => NewDecoder(Info(640, 480, preview)).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Equal(Tr.ImageErrorRawCorrupt, UserFacingError.Describe(ex));
    }

    [Fact]
    public void Decode_NoPreviewAndEmptyFallbackThumbnail_FailsWithTheNoPreviewSentence()
    {
        using var temp = new TempRoot("raw-preview-emptythumb");
        var path = temp.File("none.dng", new byte[256]);

        var ex = Assert.Throws<InvalidDataException>(() =>
            NewDecoder(Info(4000, 3000), fallback: new FixedFallback([])).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Equal(Tr.ImageErrorRawNoPreview, UserFacingError.Describe(ex));
    }

    [Fact]
    public void SelectPreview_HeaderBudgetExhausted_TreatsDimensionsAsUnknownInsteadOfThrowing()
    {
        const int previewAt = RawContainerLimits.MaxHeaderBytes + 1000;
        var file = new byte[RawContainerLimits.MaxHeaderBytes + 100_000];
        SyntheticRawBuilder.CreateMinimalJpeg(320, 240).CopyTo(file, previewAt);
        var source = new InMemoryRawHeaderSource(file);
        _ = source.Read(0, RawContainerLimits.MaxHeaderBytes); // burns the whole 8 MiB budget
        var previews = new[]
        {
            new EmbeddedPreview(0, previewAt, 200, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown),
            new EmbeddedPreview(1, previewAt, 100, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown),
        };

        var chosen = PreviewSelector.SelectPreview(source, previews, DecodeBox.Unbounded, 1);

        Assert.NotNull(chosen);
    }

    [Fact]
    public void Decode_OnlyAThumbnailJpeg_IsStillDecodedButMarkedDownscaledEvenWithoutASensorSize()
    {
        using var temp = new TempRoot("raw-thumb-only");
        var jpegPath = Path.Combine(temp.Path, "thumb.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 160, 120);
        var jpeg = File.ReadAllBytes(jpegPath);
        var path = temp.File("thumbonly.dng", [.. new byte[64], .. jpeg]);
        var preview = new EmbeddedPreview(0, 64, jpeg.Length, EmbeddedPreviewKind.Jpeg, 160, 120, PreviewColorSpace.Unknown);

        var decoded = NewDecoder(Info(0, 0, preview)).Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal((160, 120), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.True(decoded.Downscaled);
    }

    [Fact]
    public void Decode_LargePreviewWithoutASensorSize_IsNotMarkedDownscaled()
    {
        using var temp = new TempRoot("raw-big-preview");
        var jpegPath = Path.Combine(temp.Path, "big.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 640, 480);
        var jpeg = File.ReadAllBytes(jpegPath);
        var path = temp.File("big.dng", [.. new byte[64], .. jpeg]);
        var preview = new EmbeddedPreview(0, 64, jpeg.Length, EmbeddedPreviewKind.Jpeg, 640, 480, PreviewColorSpace.Unknown);

        var decoded = NewDecoder(Info(0, 0, preview)).Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.False(decoded.Downscaled);
    }

    private sealed class FixedFallback(byte[] thumbnail) : IRawPreviewFallback
    {
        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format) => thumbnail;
    }
}
