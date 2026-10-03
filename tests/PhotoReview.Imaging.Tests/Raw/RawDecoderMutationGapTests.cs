using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Metadata;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Mutation-testing gap tests for <see cref="RawDecoder"/> (Stryker round 2): the sensor-size / downscaled / EXIF decisions of
/// <c>Decode</c>, the source-bytes accounting, the ORF thumbnail fallback, the next-best preview rules and the exact range checks of
/// the preview read. Everything is synthetic: an in-memory <see cref="ISourceReader"/> (virtual path, no file), scripted inner
/// decoders that identify a preview by its byte length, and fixed container readers.
/// </summary>
public sealed class RawDecoderMutationGapTests
{
    private const string VirtualPath = @"C:\raw-virtual-nonexistent\a.dng";
    private const int FileSize = 4096; // below one 64 KB header block: the header source reads exactly FileSize bytes

    // ---- fakes -------------------------------------------------------------------------------------------------------------

    private sealed class FixedContainerReader(RawContainerInfo info, Action? onRead = null) : IRawContainerReader
    {
        public RawFormat Format => info.Format;
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);

        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken)
        {
            onRead?.Invoke();
            return info;
        }
    }

    private sealed class FakeImage : IDecodedImage
    {
        public FakeImage(int width, int height)
        {
            PixelWidth = width;
            PixelHeight = height;
            OriginalWidth = width;
            OriginalHeight = height;
        }

        public int PixelWidth { get; }
        public int PixelHeight { get; }
        public bool Downscaled { get; init; }
        public int Orientation => 1;
        public long EstimatedBytes => (long)PixelWidth * PixelHeight * 4;
        public object PlatformImage { get; } = new();
        public int OriginalWidth { get; init; }
        public int OriginalHeight { get; init; }
        public ExifSummary? Exif { get; init; }
    }

    /// <summary>Inner decoder: records the byte length of every request and answers with a scripted result (throwing or returning).</summary>
    private sealed class ScriptedDecoder(Func<int, IDecodedImage> onDecode) : IImageDecoder
    {
        public List<int> Lengths { get; } = [];
        public List<DecodeRequest> Requests { get; } = [];
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            var length = request.Bytes?.Length ?? -1;
            Lengths.Add(length);
            Requests.Add(request);
            return onDecode(length);
        }
    }

    private sealed class StubFallback(Func<ReadOnlyMemory<byte>> read) : IRawPreviewFallback
    {
        public int Calls { get; private set; }

        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
        {
            Calls++;
            return read();
        }
    }

    private sealed class MemorySourceReader(Func<Stream> open) : ISourceReader
    {
        public MemorySourceReader(byte[] data, int maxChunk = int.MaxValue)
            : this(() => new ChunkedStream(new MemoryStream(data, writable: false), maxChunk)) { }

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 4096) => open();
    }

    /// <summary>Returns at most <paramref name="maxChunk"/> bytes per Read, like a network or throttled stream.</summary>
    private sealed class ChunkedStream(Stream inner, int maxChunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxChunk));
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A zero-filled stream of a declared length that allocates nothing: stands in for a file with a huge preview.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, Math.Max(0, length - Position));
            Array.Clear(buffer, offset, n);
            Position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- builders ----------------------------------------------------------------------------------------------------------

    private static EmbeddedPreview Preview(int index, long offset, long length, int width, int height) =>
        new(index, offset, length, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Srgb);

    private static RawContainerInfo Info(int sensorWidth, int sensorHeight, int orientation, RawFormat format, params EmbeddedPreview[] previews) =>
        new(format, sensorWidth, sensorHeight, orientation, previews, []);

    private static RawContainerInfo DngInfo(int sensorWidth, int sensorHeight, params EmbeddedPreview[] previews) =>
        Info(sensorWidth, sensorHeight, 1, RawFormat.Dng, previews);

    private static RawDecoder NewDecoder(RawContainerInfo info, IImageDecoder inner, ISourceReader? source = null,
        IRawPreviewFallback? fallback = null, IImageDecoder? noPreview = null) =>
        new(inner, source ?? new MemorySourceReader(new byte[FileSize]),
            new RawContainerReaderRegistry([new FixedContainerReader(info)]),
            previewFallback: fallback, noPreviewDecoder: noPreview);

    private static DecodeRequest Request(bool applyOrientation = true) =>
        new(VirtualPath, DecodeBox.Unbounded, applyOrientation);

    private static ScriptedDecoder Always(int width, int height, ExifSummary? exif = null) =>
        new(_ => new FakeImage(width, height) { Exif = exif });

    private static long BytesRead(IDecodedImage image) => ((ISourceReadMetrics)image).SourceBytesRead;

    // ---- ReadInfo: one missing sensor side ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(4000, 0)]
    public void ReadInfo_OnlyOneSensorSideDeclared_UsesTheBestPreviewSize(int sensorWidth, int sensorHeight)
    {
        var decoder = NewDecoder(DngInfo(sensorWidth, sensorHeight, Preview(0, 64, 32, 1200, 900)), Always(1, 1));

        var info = decoder.ReadInfo(VirtualPath);

        Assert.Equal((1200, 900), (info.Width, info.Height));
    }

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(4000, 0)]
    public void ReadInfo_OnlyOneSensorSideDeclaredAndNoPreview_Throws(int sensorWidth, int sensorHeight)
    {
        var decoder = NewDecoder(DngInfo(sensorWidth, sensorHeight), Always(1, 1));

        Assert.Throws<InvalidDataException>(() => decoder.ReadInfo(VirtualPath));
    }

    // ---- Decode: sensor size, orientation, downscaled flag, EXIF ----------------------------------------------------------

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(4000, 0)]
    public void Decode_OnlyOneSensorSideDeclared_ReportsTheDecodedImageSizeAsTheOriginal(int sensorWidth, int sensorHeight)
    {
        var decoder = NewDecoder(DngInfo(sensorWidth, sensorHeight, Preview(0, 64, 32, 1200, 900)), Always(1200, 900));

        var decoded = decoder.Decode(Request());

        Assert.Equal((1200, 900), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Fact]
    public void Decode_TransposedOrientationWithOrientationApplied_SwapsTheDeclaredSensorSize()
    {
        var info = Info(6000, 4000, orientation: 6, RawFormat.Dng, Preview(0, 64, 32, 1200, 900));

        var decoded = NewDecoder(info, Always(900, 1200)).Decode(Request(applyOrientation: true));

        Assert.Equal((4000, 6000), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Decode_OrientationNotApplied_KeepsTheDeclaredSensorSize(int orientation)
    {
        var info = Info(6000, 4000, orientation, RawFormat.Dng, Preview(0, 64, 32, 1200, 900));

        var decoded = NewDecoder(info, Always(1200, 900)).Decode(Request(applyOrientation: false));

        Assert.Equal((6000, 4000), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Theory]
    [InlineData(256, 192, true)]   // exactly the thumbnail limit: a thumbnail is never the photo, even when sizes agree
    [InlineData(257, 193, false)]  // one pixel past it: a real (sensor-sized) image
    public void Decode_PreviewAtTheThumbnailLimitWithNoDeclaredSensorSize_IsDownscaledOnlyWhenItIsATinyThumbnail(int width, int height, bool expectedDownscaled)
    {
        var decoder = NewDecoder(DngInfo(0, 0, Preview(0, 64, 32, width, height)), Always(width, height));

        var decoded = decoder.Decode(Request());

        Assert.Equal(expectedDownscaled, decoded.Downscaled);
    }

    [Theory]
    [InlineData(6000, 900, true)]   // narrower than the sensor only
    [InlineData(1200, 4000, true)]  // shorter than the sensor only
    [InlineData(1200, 900, false)]  // same size on both sides
    public void Decode_PreviewDiffersFromTheSensorInOneDimension_IsDownscaledUnlessBothSidesMatch(int sensorWidth, int sensorHeight, bool expectedDownscaled)
    {
        var decoder = NewDecoder(DngInfo(sensorWidth, sensorHeight, Preview(0, 64, 32, 1200, 900)), Always(1200, 900));

        var decoded = decoder.Decode(Request());

        Assert.Equal(expectedDownscaled, decoded.Downscaled);
    }

    private static (byte[] File, RawContainerInfo Info) FileWithContainerExif(string make)
    {
        var file = new byte[FileSize];
        var tiff = new Tiff.TiffBytes(true, 128).Header(8).Ifd(8, 0, At(0x010F, 2, (uint)make.Length + 1, 100));
        tiff.Put(100, System.Text.Encoding.ASCII.GetBytes(make + "\0"));
        tiff.ToArray().CopyTo(file, 0);
        var info = new RawContainerInfo(RawFormat.Dng, 0, 0, 1, [Preview(0, 1024, 32, 1200, 900)], [new ExifBlock(0, 128, IsTiffHeader: true)]);
        return (file, info);
    }

    [Fact]
    public void Decode_ContainerAndDecodedImageBothCarryExif_TheContainersExifWins()
    {
        var (file, info) = FileWithContainerExif("Canon");
        var inner = Always(1200, 900, new ExifSummary { CameraMake = "FromTheJpeg" });

        var decoded = NewDecoder(info, inner, new MemorySourceReader(file)).Decode(Request());

        Assert.Equal("Canon", decoded.Exif?.CameraMake);
    }

    [Fact]
    public void Decode_ContainerHasNoExif_FallsBackToTheDecodedImagesExif()
    {
        var info = DngInfo(0, 0, Preview(0, 64, 32, 1200, 900));
        var inner = Always(1200, 900, new ExifSummary { CameraMake = "FromTheJpeg" });

        var decoded = NewDecoder(info, inner).Decode(Request());

        Assert.Equal("FromTheJpeg", decoded.Exif?.CameraMake);
    }

    // ---- Decode: SourceBytesRead accounting --------------------------------------------------------------------------------

    [Fact]
    public void Decode_NextBestPreviewAfterAFailure_CountsTheFailedAndTheDecodedPreviewBytes()
    {
        // bad preview: 40 bytes (largest, chosen first, fails); good preview: 24 bytes. The whole file is one header block.
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 4000, 3000), Preview(1, 128, 24, 1200, 900));
        var inner = new ScriptedDecoder(length => length == 40 ? throw new InvalidDataException("bad") : new FakeImage(1200, 900));

        var decoded = NewDecoder(info, inner).Decode(Request());

        Assert.Equal(FileSize + 24 + 40, BytesRead(decoded));
        Assert.True(decoded.IsDegradedFallback);
    }

    [Fact]
    public void Decode_FullDecodeAfterEveryPreviewFailed_CountsTheWholeFilePlusTheFailedPreviewBytes()
    {
        using var temp = new TempRoot("raw-full-bytes");
        var path = temp.File("a.dng", new byte[FileSize]);
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 4000, 3000));
        var inner = new ScriptedDecoder(_ => throw new InvalidDataException("bad"));
        var full = new ScriptedDecoder(_ => new FakeImage(6000, 4000));
        var decoder = new RawDecoder(inner, registry: new RawContainerReaderRegistry([new FixedContainerReader(info)]), noPreviewDecoder: full);

        var decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(FileSize + 40, BytesRead(decoded));
    }

    [Fact]
    public void Decode_ThumbnailFallbackWithoutAnyPreview_CountsTheHeaderPlusTheThumbnailBytes()
    {
        var fallback = new StubFallback(() => new byte[30]);

        var decoded = NewDecoder(DngInfo(0, 0), Always(1200, 900), fallback: fallback).Decode(Request());

        Assert.Equal(FileSize + 30, BytesRead(decoded));
    }

    [Fact]
    public void Decode_FullDecodeOfAFileThatHasNoPhysicalLength_StillCountsTheHeaderBytes()
    {
        // A virtual (non-physical) source: the full decode cannot report a file length, so the header read stands as the count.
        var full = new ScriptedDecoder(_ => new FakeImage(6000, 4000));

        var decoded = NewDecoder(DngInfo(0, 0), Always(1, 1), noPreview: full).Decode(Request());

        Assert.Equal(FileSize, BytesRead(decoded));
    }

    [Fact]
    public void Decode_SingleJpegPreviewDeclaringNoBytes_UsesTheThumbnailFallbackInsteadOfDecodingNothing()
    {
        var info = DngInfo(0, 0, Preview(0, 64, 0, 1200, 900));
        var fallback = new StubFallback(() => new byte[30]);
        var inner = Always(1200, 900);

        NewDecoder(info, inner, fallback: fallback).Decode(Request());

        Assert.Equal([30], inner.Lengths); // never the empty preview
        Assert.Equal(1, fallback.Calls);
    }

    // ---- ORF thumbnail fallback --------------------------------------------------------------------------------------------

    private static RawContainerInfo OrfInfo() => Info(6000, 4000, 1, RawFormat.Orf, Preview(0, 64, 40, 4000, 3000));

    [Fact]
    public void Decode_OrfPreviewUnsupported_DecodesTheThumbnailFallbackAndMarksItDegraded()
    {
        var fallback = new StubFallback(() => new byte[20]);
        var inner = new ScriptedDecoder(length => length == 40 ? throw new NotSupportedException("unsupported") : new FakeImage(160, 120));

        var decoded = NewDecoder(OrfInfo(), inner, fallback: fallback).Decode(Request());

        Assert.Equal(1, fallback.Calls);
        Assert.Equal([40, 20], inner.Lengths);
        Assert.Equal(160, decoded.PixelWidth);
        Assert.True(decoded.IsDegradedFallback);
    }

    [Fact]
    public void Decode_OrfPreviewUnsupportedWithoutAThumbnailProvider_PropagatesTheUnsupportedFailure()
    {
        var inner = new ScriptedDecoder(_ => throw new NotSupportedException("unsupported"));

        Assert.Throws<NotSupportedException>(() => NewDecoder(OrfInfo(), inner).Decode(Request()));
    }

    [Fact]
    public void Decode_NonOrfPreviewUnsupported_NeverAsksForTheThumbnail()
    {
        var fallback = new StubFallback(() => new byte[20]);
        var info = Info(6000, 4000, 1, RawFormat.Dng, Preview(0, 64, 40, 4000, 3000));
        var inner = new ScriptedDecoder(_ => throw new NotSupportedException("unsupported"));

        Assert.Throws<NotSupportedException>(() => NewDecoder(info, inner, fallback: fallback).Decode(Request()));

        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void Decode_OrfThumbnailFallbackFailsRecoverablyWithAFullDecoder_UsesTheFullDecode()
    {
        var fallback = new StubFallback(() => throw new InvalidDataException("thumb-broken"));
        var inner = new ScriptedDecoder(_ => throw new NotSupportedException("unsupported"));
        var full = new ScriptedDecoder(_ => new FakeImage(6000, 4000));

        var decoded = NewDecoder(OrfInfo(), inner, fallback: fallback, noPreview: full).Decode(Request());

        Assert.Equal(6000, decoded.PixelWidth);
        Assert.False(decoded.IsDegradedFallback); // the full decode is the genuine best, not a fallback
        Assert.Single(full.Lengths);
    }

    [Theory]
    [InlineData(nameof(InvalidDataException))]
    [InlineData(nameof(IOException))]
    public void Decode_OrfThumbnailFallbackFailsWithoutAFullDecoder_ThePropagatedFailureIsTheThumbnailsOwn(string kind)
    {
        // Without a full decoder the thumbnail failure is not caught at all: not even the first (unsupported) preview failure replaces it.
        Exception failure = kind == nameof(IOException) ? new IOException("thumb-broken") : new InvalidDataException("thumb-broken");
        var fallback = new StubFallback(() => throw failure);
        var inner = new ScriptedDecoder(_ => throw new NotSupportedException("unsupported"));

        var thrown = Assert.ThrowsAny<Exception>(() => NewDecoder(OrfInfo(), inner, fallback: fallback).Decode(Request()));

        Assert.Equal("thumb-broken", thrown.Message);
    }

    [Fact]
    public void Decode_OrfThumbnailFallbackFailsNonRecoverablyWithAFullDecoder_DoesNotSwallowIt()
    {
        var fallback = new StubFallback(() => throw new IOException("thumb-io"));
        var inner = new ScriptedDecoder(_ => throw new NotSupportedException("unsupported"));
        var full = new ScriptedDecoder(_ => new FakeImage(6000, 4000));

        var thrown = Assert.Throws<IOException>(() => NewDecoder(OrfInfo(), inner, fallback: fallback, noPreview: full).Decode(Request()));

        Assert.Equal("thumb-io", thrown.Message);
        Assert.Empty(full.Lengths);
    }

    // ---- next-best preview selection ---------------------------------------------------------------------------------------

    [Fact]
    public void Decode_MoreThanFourPreviewsAllTheFirstFourFail_OnlyFourAreTriedBeforeGivingUp()
    {
        // 5 viewable previews of decreasing size; only the fifth (length 25) would decode.
        var info = DngInfo(6000, 4000,
            Preview(0, 64, 20, 5000, 4000), Preview(1, 128, 21, 4900, 3900), Preview(2, 192, 22, 4800, 3800),
            Preview(3, 256, 23, 4700, 3700), Preview(4, 320, 25, 4600, 3600));
        var inner = new ScriptedDecoder(length => length == 25 ? new FakeImage(4600, 3600) : throw new InvalidDataException("bad"));

        Assert.Throws<InvalidDataException>(() => NewDecoder(info, inner).Decode(Request()));

        Assert.Equal(4, inner.Lengths.Count);
    }

    [Fact]
    public void Decode_ChosenPreviewFailsAndTheOnlyOtherJpegDeclaresNoBytes_DoesNotTryTheEmptyOne()
    {
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 4000, 3000), Preview(1, 128, 0, 3900, 2900));
        var inner = new ScriptedDecoder(length => length == 40 ? throw new InvalidDataException("bad") : new FakeImage(1, 1));

        Assert.Throws<InvalidDataException>(() => NewDecoder(info, inner).Decode(Request()));

        Assert.Equal([40], inner.Lengths);
    }

    [Fact]
    public void Decode_UnsupportedPreviewThenAnUnknownSizePreview_TheUnknownSizeOneIsStillTaken()
    {
        // The second preview's header has no frame (garbage bytes): its size stays 0x0, which is "unknown", never "a thumbnail".
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 4000, 3000), new EmbeddedPreview(1, 128, 24, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Srgb));
        var inner = new ScriptedDecoder(length => length == 40 ? throw new NotSupportedException("unsupported") : new FakeImage(24, 24));

        var decoded = NewDecoder(info, inner).Decode(Request());

        Assert.Equal(24, decoded.PixelWidth);
    }

    [Fact]
    public void Decode_UnsupportedPreviewThenAPreviewOfExactlyTheUsefulLongSide_ThatPreviewIsTaken()
    {
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 4000, 3000), Preview(1, 128, 24, 1000, 750));
        var inner = new ScriptedDecoder(length => length == 40 ? throw new NotSupportedException("unsupported") : new FakeImage(1000, 750));

        var decoded = NewDecoder(info, inner).Decode(Request());

        Assert.Equal(1000, decoded.PixelWidth); // 1000 px is the smallest still "useful" size, not a thumbnail
    }

    [Fact]
    public void Decode_ThumbnailSizedPreviewFailsThenAnEquallySizedOneExists_TheEquallySizedOneIsTaken()
    {
        // Both are thumbnails (800 px): a thumbnail as large as the one that failed is allowed (it is not a step down).
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 800, 600), Preview(1, 128, 24, 800, 600));
        var inner = new ScriptedDecoder(length => length == 40 ? throw new NotSupportedException("unsupported") : new FakeImage(800, 600));

        var decoded = NewDecoder(info, inner).Decode(Request());

        Assert.Equal([40, 24], inner.Lengths);
        Assert.Equal(800, decoded.PixelWidth);
    }

    // ---- preview range read ------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_PreviewStartingAtTheVeryFirstByte_IsRead()
    {
        var info = DngInfo(6000, 4000, Preview(0, 0, 40, 1200, 900));
        var inner = Always(1200, 900);

        NewDecoder(info, inner).Decode(Request());

        Assert.Equal([40], inner.Lengths);
    }

    [Fact]
    public void Decode_PreviewRangeRunsPastTheEndOfTheFile_FailsWithTheOutsideTheFileError()
    {
        var info = DngInfo(6000, 4000, Preview(0, FileSize - 6, 40, 1200, 900));

        var thrown = Assert.Throws<EndOfStreamException>(() => NewDecoder(info, Always(1200, 900)).Decode(Request()));

        Assert.Contains("outside", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_PreviewEndingExactlyAtTheEndOfTheFile_IsRead()
    {
        var info = DngInfo(6000, 4000, Preview(0, FileSize - 40, 40, 1200, 900));
        var inner = Always(1200, 900);

        NewDecoder(info, inner).Decode(Request());

        Assert.Equal([40], inner.Lengths);
    }

    [Fact]
    public void Decode_SourceReturningShortReads_StillDeliversTheWholePreview()
    {
        var file = new byte[FileSize];
        for (var i = 0; i < 40; i++) file[64 + i] = (byte)(i + 1);
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 1200, 900));
        var inner = Always(1200, 900);

        NewDecoder(info, inner, new MemorySourceReader(file, maxChunk: 7)).Decode(Request());

        Assert.Equal([40], inner.Lengths);
        Assert.Equal(1, inner.Requests[0].Bytes!.Value.Span[0]);
        Assert.Equal(40, inner.Requests[0].Bytes!.Value.Span[39]);
    }

    [Fact]
    public void Decode_PreviewOfExactlyTheMaximumSize_IsAcceptedNotTreatedAsTooLarge()
    {
        const int Max = RawContainerLimits.MaxPreviewBytes;
        var info = DngInfo(6000, 4000, Preview(0, 64, Max, 4000, 3000));
        var source = new MemorySourceReader(() => new ZeroStream(64L + Max + 64));
        var inner = Always(4000, 3000);

        NewDecoder(info, inner, source).Decode(Request());

        Assert.Equal([Max], inner.Lengths);
    }

    [Fact]
    public void Decode_ThumbnailFallbackOfExactlyTheMaximumSize_IsAcceptedNotTreatedAsInvalid()
    {
        const int Max = 32 << 20;
        var fallback = new StubFallback(() => new byte[Max]);
        var inner = Always(1200, 900);

        NewDecoder(DngInfo(0, 0), inner, fallback: fallback).Decode(Request());

        Assert.Equal([Max], inner.Lengths);
    }

    // ---- file identity -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_FileRemovedWhileTheContainerIsBeingRead_FailsAsChangedWhileReading()
    {
        using var temp = new TempRoot("raw-vanished");
        var path = temp.File("a.dng", new byte[FileSize]);
        var info = DngInfo(6000, 4000, Preview(0, 64, 40, 1200, 900));
        var decoder = new RawDecoder(Always(1200, 900),
            registry: new RawContainerReaderRegistry([new FixedContainerReader(info, onRead: () => File.Move(path, path + ".moved"))]));

        // Exactly IOException: a raw FileNotFoundException would be the unlocalized stat failure leaking out.
        var thrown = Assert.Throws<IOException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("changed while reading", thrown.Message, StringComparison.Ordinal);
    }
}
