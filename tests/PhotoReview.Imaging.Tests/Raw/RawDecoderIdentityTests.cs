using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Raw;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// File identity (TOCTOU): the container-info cache key must describe the bytes the reader really parsed. A file replaced
/// between the stat, the open, the container read and the preview read is reported as changed (IOException) and never cached
/// under the wrong identity; cached preview offsets are only used when the file still matches the container key.
/// </summary>
public sealed class RawDecoderIdentityTests
{
    private const int PreviewAt = 64;

    private sealed class FixedContainerReader(RawContainerInfo info) : IRawContainerReader
    {
        public int ReadCount { get; private set; }
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);

        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken)
        {
            ReadCount++;
            return info;
        }
    }

    /// <summary>Runs <c>hook(openNumber)</c> (1-based) right before each open, then opens the real file.</summary>
    private sealed class HookedSourceReader(Action<int> hook) : ISourceReader
    {
        private int _opens;

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 4096)
        {
            hook(++_opens);
            return PhysicalSourceReader.Instance.OpenSource(path, priority, bufferSize);
        }
    }

    private static byte[] Jpeg(int width, int height) => SyntheticRawBuilder.CreateMinimalJpeg(width, height);

    private static (byte[] File, RawContainerInfo Info) Layout()
    {
        var jpeg = Jpeg(320, 240);
        var file = new byte[PreviewAt + jpeg.Length];
        jpeg.CopyTo(file, PreviewAt);
        var preview = new EmbeddedPreview(0, PreviewAt, jpeg.Length, EmbeddedPreviewKind.Jpeg, 320, 240, PreviewColorSpace.Srgb);
        return (file, new RawContainerInfo(RawFormat.Dng, 320, 240, 1, [preview], []));
    }

    private static RawDecoder NewDecoder(FixedContainerReader reader, ISourceReader sourceReader, IImageDecoder? inner = null, IImageDecoder? noPreview = null) =>
        new(inner ?? new WpfBitmapImageDecoder(), sourceReader, new RawContainerReaderRegistry([reader]), noPreviewDecoder: noPreview);

    [Fact]
    public void Decode_FileReplacedWithADifferentLengthBetweenStatAndOpen_FailsAndIsNotCached()
    {
        using var temp = new TempRoot("raw-id-open");
        var (file, info) = Layout();
        var path = temp.File("a.dng", file);
        var reader = new FixedContainerReader(info);
        bool swap = true;
        var decoder = NewDecoder(reader, new HookedSourceReader(n =>
        {
            if (n == 1 && swap) File.WriteAllBytes(path, [.. file, .. new byte[500]]);
        }));
        var request = new DecodeRequest(path, DecodeBox.Unbounded);

        Assert.Throws<IOException>(() => decoder.Decode(request));

        // The file is now simply the longer one: a later decode parses it fresh, under its own identity.
        swap = false;
        _ = decoder.Decode(request);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public void Decode_FileReplacedByASameLengthFileBetweenStatAndOpen_FailsAsChanged()
    {
        using var temp = new TempRoot("raw-id-samelen");
        var (file, info) = Layout();
        var path = temp.File("a.dng", file);
        var decoder = NewDecoder(new FixedContainerReader(info), new HookedSourceReader(n =>
        {
            if (n == 1) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10)); // stat(before) != stat(after)
        }));

        Assert.Throws<IOException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));
    }

    [Fact]
    public void ReadInfo_FileReplacedBySameLengthFileBetweenStatAndOpen_FailsAsChangedAndIsNotCached()
    {
        using var temp = new TempRoot("raw-id-readinfo");
        var (file, info) = Layout();
        var path = temp.File("a.dng", file);
        var reader = new FixedContainerReader(info);
        bool touch = true;
        var decoder = NewDecoder(reader, new HookedSourceReader(n =>
        {
            if (n == 1 && touch) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10));
        }));

        Assert.Throws<IOException>(() => decoder.ReadInfo(path));

        touch = false;
        Assert.Equal((320, 240), (decoder.ReadInfo(path).Width, decoder.ReadInfo(path).Height));
        // Parsed in the failed attempt (its result discarded) and again under the identity of the file as it now is; the third
        // call hit the cache. Had the failed attempt cached its result under the stale identity the count would stay 1.
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void Decode_PreviewReadOpensADifferentlySizedFile_FailsInsteadOfReadingTheWrongBytes()
    {
        using var temp = new TempRoot("raw-id-preview-open");
        var (file, info) = Layout();
        var path = temp.File("a.dng", file);
        var decoder = NewDecoder(new FixedContainerReader(info), new HookedSourceReader(n =>
        {
            if (n == 2) File.WriteAllBytes(path, [.. file, .. new byte[500]]); // the preview-range open
        }));

        Assert.Throws<IOException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));
    }

    /// <summary>Inner decoder whose first call "changes the file" (touches its write time) and then fails like a corrupt preview.</summary>
    private sealed class TouchingFailingDecoder(string path) : IImageDecoder
    {
        private int _calls;
        public ImageInfo ReadInfo(string p) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (++_calls == 1)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10));
                throw new InvalidDataException("corrupt preview");
            }

            return new WpfBitmapImageDecoder().Decode(request);
        }
    }

    private sealed class CountingFullDecoder : IImageDecoder
    {
        public int CallCount { get; private set; }
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            CallCount++;
            throw new NotSupportedException();
        }
    }

    [Fact]
    public void Decode_FileChangesAfterTheContainerWasRead_NextPreviewAttemptDoesNotUseTheStaleOffsets()
    {
        using var temp = new TempRoot("raw-id-stale");
        var jpeg = Jpeg(320, 240);
        var file = new byte[PreviewAt + (2 * jpeg.Length)];
        jpeg.CopyTo(file, PreviewAt);
        jpeg.CopyTo(file, PreviewAt + jpeg.Length);
        var path = temp.File("a.dng", file);
        var info = new RawContainerInfo(RawFormat.Dng, 320, 240, 1,
        [
            new EmbeddedPreview(0, PreviewAt, jpeg.Length, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb),
            new EmbeddedPreview(1, PreviewAt + jpeg.Length, jpeg.Length, EmbeddedPreviewKind.Jpeg, 320, 240, PreviewColorSpace.Srgb),
        ], []);
        var reader = new FixedContainerReader(info);
        var full = new CountingFullDecoder();
        var decoder = NewDecoder(reader, PhysicalSourceReader.Instance, new TouchingFailingDecoder(path), full);
        var request = new DecodeRequest(path, DecodeBox.Unbounded);

        // Attempt 1 fails as corrupt and the file changes; attempt 2 must notice the change instead of using the old offsets.
        Assert.Throws<IOException>(() => decoder.Decode(request));
        Assert.Equal(0, full.CallCount);

        // The stale container entry was dropped: the next decode parses the (now changed) file again.
        _ = decoder.Decode(request);
        Assert.Equal(2, reader.ReadCount);
    }
}
