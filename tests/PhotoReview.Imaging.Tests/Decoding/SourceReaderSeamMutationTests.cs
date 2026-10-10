using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing gap closers for the injected <see cref="ISourceReader"/> of the WPF and WIC decoders (the
/// <c>sourceReader ?? PhysicalSourceReader.Instance</c> defaults, the buffer sizes handed to the reader, the priority lane) and for
/// <see cref="WpfBitmapImageDecoder"/> paths that only run when the source stream cannot seek or fails once during the header
/// pre-read (legacy single-axis DecodePixel*, SourceOrientation without a header read, downscale retry).
/// </summary>
public sealed class SourceReaderSeamMutationTests : IDisposable
{
    private readonly TempRoot _root = new("reader-seam");
    private readonly string _plain;
    private readonly string _garbage;

    public SourceReaderSeamMutationTests()
    {
        _plain = FixtureGenerator.GenerateGradientJpeg(_root.Combine("plain-64x48.jpg"), 64, 48);
        _garbage = _root.File("garbage.jpg", [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 3))]);
    }

    public void Dispose() => _root.Dispose();

    private sealed class RecordingReader(Func<string, Stream>? open = null) : ISourceReader
    {
        public List<(string Path, SourceReadPriority Priority, int BufferSize)> Calls { get; } = [];

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024)
        {
            Calls.Add((path, priority, bufferSize));
            return open is null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : open(path);
        }
    }

    /// <summary>Fails the Nth <see cref="Read(byte[], int, int)"/> of THIS stream instance once (the header pre-read of the decoder), then behaves.</summary>
    private sealed class FailingOnceStream(Stream inner, int failOnRead) : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++_reads == failOnRead) throw new IOException("injected one-shot read failure");
            return inner.Read(buffer, offset, count);
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A source whose header pre-read fails once: the decoder then knows nothing about the source size and takes its legacy DecodePixel* path.</summary>
    private static FailingOnceStream Forward(string path) => new(File.OpenRead(path), failOnRead: 2);

    // ---- the injected reader is used, with the documented buffer size and priority lane ----

    [Fact]
    public void WpfDecode_InjectedReader_IsUsedWithOneMegabyteBufferAndTheRequestPriority()
    {
        var reader = new RecordingReader();

        var decoded = new WpfBitmapImageDecoder(reader).Decode(new DecodeRequest(_plain, 0, Priority: SourceReadPriority.Preload));

        Assert.Equal(64, decoded.PixelWidth);
        var call = Assert.Single(reader.Calls);
        Assert.Equal((_plain, SourceReadPriority.Preload, 1024 * 1024), call);
    }

    [Fact]
    public void WpfReadInfo_InjectedReader_IsUsedWithOneMegabyteBufferOnTheViewerLane()
    {
        var reader = new RecordingReader();

        var info = new WpfBitmapImageDecoder(reader).ReadInfo(_plain);

        Assert.Equal((64, 48), (info.Width, info.Height));
        var call = Assert.Single(reader.Calls);
        Assert.Equal((_plain, SourceReadPriority.Viewer, 1024 * 1024), call);
    }

    [Fact]
    public void WicDecode_InjectedReader_IsUsedWithOneMegabyteBufferAndTheRequestPriority()
    {
        var reader = new RecordingReader();

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance, reader).Decode(new DecodeRequest(_plain, 0, Priority: SourceReadPriority.Preload));

        Assert.Equal(64, decoded.PixelWidth);
        var call = Assert.Single(reader.Calls);
        Assert.Equal((_plain, SourceReadPriority.Preload, 1024 * 1024), call);
    }

    [Fact]
    public void WicReadInfo_InjectedReader_IsUsedWithSixtyFourKilobyteBufferOnTheViewerLane()
    {
        var reader = new RecordingReader();

        var info = new WicDirectDecoder(WpfBitmapSourceCodec.Instance, reader).ReadInfo(_plain);

        Assert.Equal((64, 48), (info.Width, info.Height));
        var call = Assert.Single(reader.Calls);
        Assert.Equal((_plain, SourceReadPriority.Viewer, 64 * 1024), call);
    }

    // ---- WPF: when the source size is unknown (failed header pre-read) the legacy single-axis DecodePixel* path is used ----

    [Fact]
    public void WpfDecode_UnknownSourceSizeWithWidthOnlyBox_DecodesToThatWidth()
    {
        var decoder = new WpfBitmapImageDecoder(new RecordingReader(Forward));

        var decoded = decoder.Decode(new DecodeRequest(_plain, new DecodeBox(24, 0)));

        Assert.Equal((24, 18), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.True(decoded.Downscaled);
    }

    [Fact]
    public void WpfDecode_UnknownSourceSizeWithHeightOnlyBox_DecodesToThatHeight()
    {
        var decoder = new WpfBitmapImageDecoder(new RecordingReader(Forward));

        var decoded = decoder.Decode(new DecodeRequest(_plain, new DecodeBox(0, 18)));

        Assert.Equal((24, 18), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.True(decoded.Downscaled);
    }

    [Fact]
    public void WpfDecode_UnknownSourceSizeWithTransposingSourceOrientationAndWidthBox_WidthIsTheDisplayedWidth()
    {
        // Stored 64x48 shown rotated (48x64): a displayed width of 24 is a STORED height of 24 -> 32x24 stored -> 24x32 shown.
        var decoder = new WpfBitmapImageDecoder(new RecordingReader(Forward));

        var decoded = decoder.Decode(new DecodeRequest(_plain, new DecodeBox(24, 0), sourceOrientation: 6));

        Assert.Equal((24, 32), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(6, decoded.Orientation);
        Assert.True(decoded.Downscaled);
    }

    [Fact]
    public void WpfDecode_UnknownSourceSizeWithTransposingSourceOrientationAndHeightBox_HeightIsTheDisplayedHeight()
    {
        var decoder = new WpfBitmapImageDecoder(new RecordingReader(Forward));

        var decoded = decoder.Decode(new DecodeRequest(_plain, new DecodeBox(0, 32), sourceOrientation: 6));

        Assert.Equal((24, 32), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(6, decoded.Orientation);
    }

    [Fact]
    public void WpfDecode_UnknownSourceSizeWithoutBox_FallsBackToTheCallersOrientation()
    {
        var decoder = new WpfBitmapImageDecoder(new RecordingReader(Forward));

        var applied = decoder.Decode(new DecodeRequest(_plain, 0, ApplyOrientation: true, SourceOrientation: 6));
        var unknown = decoder.Decode(new DecodeRequest(_plain, 0, ApplyOrientation: true));

        Assert.Equal((48, 64, 6), (applied.PixelWidth, applied.PixelHeight, applied.Orientation));
        Assert.Equal((64, 48, 1), (unknown.PixelWidth, unknown.PixelHeight, unknown.Orientation));
    }

    // ---- WPF: a header pre-read that fails is not the decode's verdict ----

    [Fact]
    public void WpfDecode_HeaderPreReadFailsOnce_StillDecodesThroughTheLegacyPath()
    {
        var reader = new RecordingReader(path => new FailingOnceStream(File.OpenRead(path), failOnRead: 2));

        var decoded = new WpfBitmapImageDecoder(reader).Decode(new DecodeRequest(_plain, new DecodeBox(32, 0)));

        // Without the catch the exception would reach the downscale-fallback retry (which fails the same way on its fresh stream).
        Assert.Equal((32, 24), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.True(decoded.Downscaled);
        Assert.Single(reader.Calls);
    }

    [Fact]
    public void WpfDecode_HeaderPreReadFailsOnceWithoutBox_StillDecodes()
    {
        var reader = new RecordingReader(path => new FailingOnceStream(File.OpenRead(path), failOnRead: 2));

        var decoded = new WpfBitmapImageDecoder(reader).Decode(new DecodeRequest(_plain, 0, ApplyOrientation: true));

        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Single(reader.Calls);
    }

    // ---- WPF: the full-resolution retry is for downscale requests only ----

    [Fact]
    public void WpfDecode_GarbageWithoutDownscale_FailsAfterASingleOpen()
    {
        var reader = new RecordingReader();

        Assert.ThrowsAny<Exception>(() => new WpfBitmapImageDecoder(reader).Decode(new DecodeRequest(_garbage, 0)));

        Assert.Single(reader.Calls);
    }

    [Fact]
    public void WpfDecode_GarbageWithDownscale_RetriesOnceAtFullResolutionThenFails()
    {
        var reader = new RecordingReader();

        Assert.ThrowsAny<Exception>(() => new WpfBitmapImageDecoder(reader).Decode(new DecodeRequest(_garbage, new DecodeBox(32, 24))));

        Assert.Equal(2, reader.Calls.Count);
    }
}