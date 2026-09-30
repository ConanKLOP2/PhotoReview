using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// SourceRawHeaderSource: a failing Length must not leak the stream, and I/O failures of a block read surface as
/// InvalidDataException(inner) so the "InvalidDataException only" contract holds (RawExif stays total) while
/// RawDecoder still reports the real I/O error; cancellation is never wrapped.
/// </summary>
public sealed class RawHeaderSourceIoTests
{
    /// <summary>A stream of <paramref name="length"/> bytes whose Length and Read can be made to throw; records disposal.</summary>
    private sealed class FaultyStream(long length, Func<Exception?> lengthFault, Func<Exception?> readFault) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length => lengthFault() is { } fault ? throw fault : length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readFault() is { } fault) throw fault;
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FixedSourceReader(Stream stream) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 4096) => stream;
    }

    private static Func<Exception?> Never => () => null;

    [Fact]
    public void Constructor_LengthThrows_DisposesTheStream_PathOverload()
    {
        var stream = new FaultyStream(10, () => new NotSupportedException("no length"), Never);

        Assert.Throws<NotSupportedException>(() => new SourceRawHeaderSource("x.dng", new FixedSourceReader(stream)));

        Assert.True(stream.Disposed);
    }

    [Fact]
    public void Constructor_LengthThrows_DisposesTheStream_StreamOverload()
    {
        var stream = new FaultyStream(10, () => new ObjectDisposedException("stream"), Never);

        Assert.Throws<ObjectDisposedException>(() => new SourceRawHeaderSource(stream));

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void Read_StreamIoFailure_BecomesInvalidDataExceptionWithTheInnerException(Type faultType)
    {
        var fault = faultType == typeof(IOException) ? (Exception)new IOException("disk") : new ObjectDisposedException("stream");
        using var source = new SourceRawHeaderSource(new FaultyStream(1000, Never, () => fault));

        var ex = Assert.Throws<InvalidDataException>(() => source.Read(0, 16));

        Assert.Same(fault, ex.InnerException);
    }

    [Fact]
    public void Read_Cancellation_IsNotWrapped()
    {
        using var source = new SourceRawHeaderSource(new FaultyStream(1000, Never, () => new OperationCanceledException()));

        Assert.Throws<OperationCanceledException>(() => source.Read(0, 16));
    }

    [Fact]
    public void TryReadExif_FailingSource_ReturnsNullInsteadOfThrowing()
    {
        using var source = new SourceRawHeaderSource(new FaultyStream(1000, Never, () => new IOException("disk")));
        var info = new RawContainerInfo(RawFormat.Cr2, 10, 10, 1, [], [new ExifBlock(0, 100, true)]);

        Assert.Null(RawExif.TryReadExif(source, info));
    }

    [Fact]
    public void Decode_HeaderIoFailure_SurfacesTheRealIoErrorNotACorruptRawError()
    {
        var fault = new IOException("device not ready");
        var reader = new FixedSourceReader(new FaultyStream(100_000, Never, () => fault));
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(), reader);

        var thrown = Assert.Throws<IOException>(() => decoder.Decode(new DecodeRequest("virtual-io-failure.dng", DecodeBox.Unbounded)));

        Assert.Same(fault, thrown);
    }
}
