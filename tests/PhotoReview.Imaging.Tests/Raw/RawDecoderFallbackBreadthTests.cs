using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The last-resort full decode (and next-best preview) now covers a failed embedded-preview decode for every format, not
/// only ORF + NotSupportedException; plus SourceBytesRead accounting for the full decode and the range cache.
/// </summary>
public sealed class RawDecoderFallbackBreadthTests
{
    private const int PreviewAt = 64;

    private sealed class FixedContainerReader(RawContainerInfo info) : IRawContainerReader
    {
        public RawFormat Format => info.Format;
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);
        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken) => info;
    }

    /// <summary>Inner decoder: a preview whose third byte is 0x00 ("bad") throws the given failure, anything else is a real JPEG decode.</summary>
    private sealed class SelectiveDecoder(Exception failure) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _real = new();
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (request.Bytes is { } bytes && bytes.Span.Length > 2 && bytes.Span[2] == 0x00) throw failure;
            return _real.Decode(request);
        }
    }

    private sealed class RecordingFullDecoder(byte[] jpeg) : IImageDecoder
    {
        public int CallCount { get; private set; }
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            CallCount++;
            return new WpfBitmapImageDecoder().Decode(request with { Bytes = jpeg });
        }
    }

    private static byte[] Jpeg(TempRoot temp, int width, int height)
    {
        var path = Path.Combine(temp.Path, $"j{width}.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, width, height);
        return File.ReadAllBytes(path);
    }

    private static readonly byte[] BadPreview = [0xFF, 0xD8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>File = 64 zero bytes + bad preview + good preview; the container declares the bad one larger so it is chosen first.</summary>
    private static (string Path, RawContainerInfo Info, byte[] Good) TwoPreviews(TempRoot temp)
    {
        var good = Jpeg(temp, 320, 240);
        var path = temp.File("two.dng", [.. new byte[PreviewAt], .. BadPreview, .. good]);
        var bad = new EmbeddedPreview(0, PreviewAt, BadPreview.Length, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb);
        var ok = new EmbeddedPreview(1, PreviewAt + BadPreview.Length, good.Length, EmbeddedPreviewKind.Jpeg, 320, 240, PreviewColorSpace.Srgb);
        return (path, new RawContainerInfo(RawFormat.Dng, 6000, 4000, 1, [bad, ok], []), good);
    }

    private static RawDecoder NewDecoder(RawContainerInfo info, IImageDecoder inner, IImageDecoder? noPreview = null, SourceBytesCache? cache = null) =>
        new(inner, registry: new RawContainerReaderRegistry([new FixedContainerReader(info)]),
            sourceBytesCache: cache, noPreviewDecoder: noPreview);

    public static TheoryData<string> RecoverableFailures => new()
    {
        nameof(InvalidDataException), nameof(FileFormatException), nameof(NotSupportedException), nameof(COMException),
    };

    /// <summary>Subclass so the test can raise a COMException without constructing the runtime-reserved type directly (CA2201).</summary>
    private sealed class TestComException : COMException;

    private static Exception Make(string kind) => kind switch
    {
        nameof(InvalidDataException) => new InvalidDataException("bad"),
        nameof(FileFormatException) => new FileFormatException("bad"),
        nameof(NotSupportedException) => new NotSupportedException("bad"),
        _ => new TestComException(),
    };

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void Decode_ChosenPreviewFails_NextBestPreviewIsDecodedBeforeTheFullDecode(string kind)
    {
        using var temp = new TempRoot("raw-next-best");
        var (path, info, good) = TwoPreviews(temp);
        var full = new RecordingFullDecoder(good);

        var decoded = NewDecoder(info, new SelectiveDecoder(Make(kind)), full).Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal((320, 240), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(0, full.CallCount);
    }

    [Fact]
    public void Decode_ChosenPreviewFailsWithoutAnyLibRaw_NextBestPreviewIsStillDecoded()
    {
        // No LibRaw (noPreviewDecoder null, no thumbnail fallback): trying the next-best JPEG needs no native code.
        using var temp = new TempRoot("raw-next-best-nolibraw");
        var (path, info, _) = TwoPreviews(temp);

        var decoded = NewDecoder(info, new SelectiveDecoder(new InvalidDataException("bad")), noPreview: null)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal((320, 240), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void Decode_EveryPreviewFailsWithoutAnyLibRaw_TheFirstFailurePropagates()
    {
        using var temp = new TempRoot("raw-all-fail-nolibraw");
        var path = temp.File("bad.dng", [.. new byte[PreviewAt], .. BadPreview]);
        var bad = new EmbeddedPreview(0, PreviewAt, BadPreview.Length, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb);
        var info = new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [bad], []);

        Assert.Throws<InvalidDataException>(() => NewDecoder(info, new SelectiveDecoder(new InvalidDataException("bad")))
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded)));
    }

    private sealed class ThrowingFallback(Exception failure) : IRawPreviewFallback
    {
        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format) => throw failure;
    }

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void Decode_NoPreviewAndThumbnailFallbackRejected_UsesTheFullDecodeForEveryRecoverableFailure(string kind)
    {
        using var temp = new TempRoot("raw-nopreview-thumb-rejected");
        var good = Jpeg(temp, 48, 32);
        var path = temp.File("none.dng", new byte[PreviewAt]);
        var info = new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [], []);
        var full = new RecordingFullDecoder(good);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(),
            registry: new RawContainerReaderRegistry([new FixedContainerReader(info)]),
            previewFallback: new ThrowingFallback(Make(kind)), noPreviewDecoder: full);

        var decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, full.CallCount);
        Assert.Equal((48, 32), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void Decode_EveryPreviewFails_UsesTheFullDecodeForAnyFormat(string kind)
    {
        using var temp = new TempRoot("raw-full-fallback");
        var good = Jpeg(temp, 48, 32);
        var path = temp.File("bad.dng", [.. new byte[PreviewAt], .. BadPreview]);
        var bad = new EmbeddedPreview(0, PreviewAt, BadPreview.Length, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb);
        var full = new RecordingFullDecoder(good);

        var decoded = NewDecoder(new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [bad], []), new SelectiveDecoder(Make(kind)), full)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, full.CallCount);
        Assert.Equal((48, 32), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(IOException))]
    public void Decode_NonRecoverableFailure_PropagatesWithoutTheFallback(Type failureType)
    {
        using var temp = new TempRoot("raw-nonrecoverable");
        var (path, info, good) = TwoPreviews(temp);
        var full = new RecordingFullDecoder(good);
        var failure = (Exception)Activator.CreateInstance(failureType)!;

        var thrown = Assert.ThrowsAny<Exception>(() =>
            NewDecoder(info, new SelectiveDecoder(failure), full).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.IsType(failureType, thrown);
        Assert.Equal(0, full.CallCount);
    }

    [Fact]
    public void Decode_PreviewFailsWithoutAnyFallback_KeepsTheOriginalException()
    {
        // Only one preview exists, so no next-best candidate and no last resort: the original failure stands.
        using var temp = new TempRoot("raw-no-lastresort");
        var path = temp.File("bad.dng", [.. new byte[PreviewAt], .. BadPreview]);
        var bad = new EmbeddedPreview(0, PreviewAt, BadPreview.Length, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb);
        var info = new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [bad], []);
        var failure = new NotSupportedException("original");

        var thrown = Assert.Throws<NotSupportedException>(() =>
            NewDecoder(info, new SelectiveDecoder(failure)).Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void Decode_FullDecodeFallback_CountsTheWholeFileOnceNotTheHeaderOnTopOfIt()
    {
        using var temp = new TempRoot("raw-full-bytes");
        var good = Jpeg(temp, 48, 32);
        var path = temp.File("nopreview.dng", new byte[200_000]);

        var decoded = NewDecoder(new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [], []), new WpfBitmapImageDecoder(), new RecordingFullDecoder(good))
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(200_000, ((ISourceReadMetrics)decoded).SourceBytesRead); // the header probe only reads one 64 KB block
    }

    [Fact]
    public void Decode_PreviewServedFromTheRangeCache_CountsNoPreviewBytes()
    {
        using var temp = new TempRoot("raw-range-hit");
        var jpeg = Jpeg(temp, 320, 240);
        var path = temp.File("one.dng", [.. new byte[PreviewAt], .. jpeg]);
        var preview = new EmbeddedPreview(0, PreviewAt, jpeg.Length, EmbeddedPreviewKind.Jpeg, 320, 240, PreviewColorSpace.Srgb);
        var decoder = NewDecoder(new RawContainerInfo(RawFormat.Dng, 320, 240, 1, [preview], []), new WpfBitmapImageDecoder(),
            cache: new SourceBytesCache(64L << 20));
        var request = new DecodeRequest(path, DecodeBox.Unbounded);

        long first = ((ISourceReadMetrics)decoder.Decode(request)).SourceBytesRead;
        long second = ((ISourceReadMetrics)decoder.Decode(request)).SourceBytesRead;

        Assert.Equal(new FileInfo(path).Length + jpeg.Length, first); // header block (the whole small file) + the preview range
        Assert.Equal(0, second);
    }
}
