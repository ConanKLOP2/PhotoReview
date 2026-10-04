using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The cached container info also caches what a decode resolves from the file (preview dimensions and colour space in ONE
/// JPEG walk, the EXIF summary), so a repeated Decode/ReadInfo of an unchanged file re-reads nothing from the header.
/// </summary>
public sealed class RawDecoderResolvedInfoCacheTests
{
    private const int JpegAt = 70_000; // second 64 KB block: the walk needs a read beyond the probe block

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

    private sealed class CountingSourceReader : ISourceReader
    {
        public long BytesRead { get; set; }
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 4096) =>
            new CountingStream(PhysicalSourceReader.Instance.OpenSource(path, priority, bufferSize), this);

        private sealed class CountingStream(Stream inner, CountingSourceReader owner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, count);
                owner.BytesRead += read;
                return read;
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
    }

    private sealed class CountingHeaderSource(IRawHeaderSource inner) : IRawHeaderSource
    {
        public int ReadCount { get; private set; }
        public long Length => inner.Length;

        public ReadOnlySpan<byte> Read(long offset, int count)
        {
            ReadCount++;
            return inner.Read(offset, count);
        }
    }

    /// <summary>TIFF exif block (Make) at 0; a JPEG of the given size at <see cref="JpegAt"/>.</summary>
    private static byte[] BuildFile(string make, int jpegWidth = 320, int jpegHeight = 240)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(jpegWidth, jpegHeight);
        var file = new byte[JpegAt + jpeg.Length];
        var tiff = new TiffBytes(true, 128).Header(8).Ifd(8, 0, At(0x010F, 2, (uint)make.Length + 1, 100));
        tiff.Put(100, System.Text.Encoding.ASCII.GetBytes(make + "\0"));
        tiff.ToArray().CopyTo(file, 0);
        jpeg.CopyTo(file, JpegAt);
        return file;
    }

    private static RawContainerInfo Info(int jpegLength, int sensorWidth = 0, int sensorHeight = 0) =>
        new(RawFormat.Dng, sensorWidth, sensorHeight, 1,
            [new EmbeddedPreview(0, JpegAt, jpegLength, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown)],
            [new ExifBlock(0, 128, IsTiffHeader: true)]);

    private static RawDecoder NewDecoder(FixedContainerReader reader, ISourceReader sourceReader, SourceBytesCache? cache = null) =>
        new(new WpfBitmapImageDecoder(), sourceReader, new RawContainerReaderRegistry([reader]), cache);

    [Fact]
    public void Decode_SecondTime_ReadsNothingFromTheHeaderAndKeepsTheExif()
    {
        using var temp = new TempRoot("raw-resolved-cache");
        var file = BuildFile("Canon");
        var path = temp.File("a.dng", file);
        var reader = new FixedContainerReader(Info(file.Length - JpegAt));
        var decoder = NewDecoder(reader, PhysicalSourceReader.Instance, new SourceBytesCache(64L << 20));
        var request = new DecodeRequest(path, DecodeBox.Unbounded);

        var first = decoder.Decode(request);
        var second = decoder.Decode(request);

        Assert.True(((ISourceReadMetrics)first).SourceBytesRead > 0);
        Assert.Equal(0, ((ISourceReadMetrics)second).SourceBytesRead); // container, EXIF and preview header all cached; preview range in the byte cache
        Assert.Equal("Canon", first.Exif?.CameraMake);
        Assert.Equal("Canon", second.Exif?.CameraMake);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public void ReadInfo_SecondTime_ReadsNoBytesAndReturnsTheSameSize()
    {
        using var temp = new TempRoot("raw-readinfo-cache");
        var file = BuildFile("Canon");
        var path = temp.File("a.dng", file);
        var counting = new CountingSourceReader();
        var decoder = NewDecoder(new FixedContainerReader(Info(file.Length - JpegAt)), counting);

        var first = decoder.ReadInfo(path);
        long afterFirst = counting.BytesRead;
        var second = decoder.ReadInfo(path);

        Assert.Equal((320, 240), (first.Width, first.Height));
        Assert.Equal(first, second);
        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, counting.BytesRead);
    }

    [Fact]
    public void Decode_ChangedFile_DoesNotReuseTheCachedExif()
    {
        using var temp = new TempRoot("raw-exif-key");
        var path = temp.File("a.dng", BuildFile("Canon"));
        var decoder = NewDecoder(new FixedContainerReader(Info(BuildFile("Canon").Length - JpegAt)), PhysicalSourceReader.Instance);
        var request = new DecodeRequest(path, DecodeBox.Unbounded);

        Assert.Equal("Canon", decoder.Decode(request).Exif?.CameraMake);

        // Same size, new content and a new write time: the key changes, so the EXIF is read again.
        File.WriteAllBytes(path, BuildFile("Nikon"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal("Nikon", decoder.Decode(request).Exif?.CameraMake);
    }

    [Fact]
    public void Decode_TwoFilesWithDifferentExif_KeepTheirOwnSummary()
    {
        using var temp = new TempRoot("raw-exif-two");
        var a = BuildFile("Canon");
        var b = BuildFile("Nikon!");
        var decoder = NewDecoder(new FixedContainerReader(Info(a.Length - JpegAt)), PhysicalSourceReader.Instance);

        var first = decoder.Decode(new DecodeRequest(temp.File("a.dng", a), DecodeBox.Unbounded));
        var second = decoder.Decode(new DecodeRequest(temp.File("b.dng", b), DecodeBox.Unbounded));

        Assert.Equal("Canon", first.Exif?.CameraMake);
        Assert.Equal("Nikon!", second.Exif?.CameraMake);
    }

    [Fact]
    public void SelectPreview_UnknownDimensionsAndColourSpace_WalksTheJpegOnce()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var preview = new EmbeddedPreview(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown);

        var walkOnly = new CountingHeaderSource(new InMemoryRawHeaderSource(jpeg));
        Assert.True(PreviewSelector.TryReadJpegFrame(walkOnly, 0, jpeg.Length, out _, out _, out _));

        var selecting = new CountingHeaderSource(new InMemoryRawHeaderSource(jpeg));
        var chosen = PreviewSelector.SelectPreview(selecting, [preview], DecodeBox.Unbounded, 1);

        Assert.Equal((320, 240), (chosen!.Width, chosen.Height));
        Assert.Equal(walkOnly.ReadCount, selecting.ReadCount);
    }

    [Fact]
    public void SelectPreview_ResolvedList_IsReusableWithoutAnyFurtherRead()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var preview = new EmbeddedPreview(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown);
        var source = new CountingHeaderSource(new InMemoryRawHeaderSource(jpeg));

        PreviewSelector.SelectPreview(source, [preview], DecodeBox.Unbounded, 1, out var resolved);
        int readsAfterFirst = source.ReadCount;
        var again = PreviewSelector.SelectPreview(source, resolved, DecodeBox.Unbounded, 1);

        Assert.True(readsAfterFirst > 0);
        Assert.Equal(readsAfterFirst, source.ReadCount);
        Assert.Equal((320, 240), (again!.Width, again.Height));
    }
}
