using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class RawDecoderTests
{
    private sealed class TrackingSourceReader : ISourceReader
    {
        public long TotalBytesRead { get; private set; }
        public int OpenCount { get; private set; }
        private readonly byte[] _fileBytes;

        public TrackingSourceReader(byte[] fileBytes)
        {
            _fileBytes = fileBytes;
        }

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 4096)
        {
            OpenCount++;
            return new TrackingStream(new MemoryStream(_fileBytes), this);
        }

        private sealed class TrackingStream : Stream
        {
            private readonly Stream _inner;
            private readonly TrackingSourceReader _parent;

            public TrackingStream(Stream inner, TrackingSourceReader parent)
            {
                _inner = inner;
                _parent = parent;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                _parent.TotalBytesRead += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                int read = _inner.Read(buffer);
                _parent.TotalBytesRead += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        }
    }

    [Fact]
    public void ReadInfo_ReturnsSensorDimensionsAndOrientation()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: 6);

        var reader = new TrackingSourceReader(tiff);
        var innerDecoder = new WpfBitmapImageDecoder(reader);
        var decoder = new RawDecoder(innerDecoder, reader);

        var info = decoder.ReadInfo("test.dng");

        Assert.Equal(6, info.Orientation);
        // Since TIFF IFD didn't specify sensor width/height, fallback to preview (640, 480)
        Assert.Equal(640, info.PixelWidth);
        Assert.Equal(480, info.PixelHeight);
        // Transposed orientation 6 swaps visual Width and Height
        Assert.Equal(480, info.Width);
        Assert.Equal(640, info.Height);
    }

    [Fact]
    public void Decode_ReadsOnlyPreviewBytesAndHeader_NoWholeFileRead()
    {
        // Create 10MB synthetic file with 640x480 JPEG embedded inside
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: 1);

        // Pad file to 10 MB
        byte[] largeFile = new byte[10 * 1024 * 1024];
        Array.Copy(tiff, largeFile, tiff.Length);

        var trackingReader = new TrackingSourceReader(largeFile);
        var innerDecoder = new WpfBitmapImageDecoder(trackingReader);
        var decoder = new RawDecoder(innerDecoder, trackingReader);

        var request = new DecodeRequest("large.dng", new DecodeBox(300, 300));
        var result = decoder.Decode(request);

        Assert.NotNull(result);
        Assert.True(result.PixelWidth > 0);
        Assert.True(result.PixelHeight > 0);

        // Assert disk read accounting: total bytes read must be far less than 10MB
        // Should be header (<= 1MB) + preview length
        long maxExpectedRead = 1024 * 1024 + jpeg.Length + 64 * 1024;
        Assert.True(trackingReader.TotalBytesRead < maxExpectedRead,
            $"Expected total bytes read < {maxExpectedRead}, but was {trackingReader.TotalBytesRead}");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void Decode_AppliesOrientationAndSensorTransposition(ushort orientation, bool expectTransposed)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: orientation);

        var reader = new TrackingSourceReader(tiff);
        var innerDecoder = new WpfBitmapImageDecoder(reader);
        var decoder = new RawDecoder(innerDecoder, reader);

        var request = new DecodeRequest("test.dng", DecodeBox.Unbounded, applyOrientation: true);
        var result = decoder.Decode(request);

        Assert.Equal(orientation, result.Orientation);
        if (expectTransposed)
        {
            Assert.Equal(480, result.OriginalWidth);
            Assert.Equal(640, result.OriginalHeight);
        }
        else
        {
            Assert.Equal(640, result.OriginalWidth);
            Assert.Equal(480, result.OriginalHeight);
        }
    }

    [Fact]
    public void FormatRoutingDecoder_RoutesRawExtensionWhenEnabled()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: 1);
        var reader = new TrackingSourceReader(tiff);

        var wpfDecoder = new WpfBitmapImageDecoder(reader);
        var rawDecoder = new RawDecoder(wpfDecoder, reader);

        bool rawEnabled = false;
        var router = new FormatRoutingDecoder(wpfDecoder, rawDecoder, () => rawEnabled);

        // 1. When disabled, RAW extension throws or delegates to standard decoder (WPF fails on RAW container)
        Assert.ThrowsAny<Exception>(() => router.ReadInfo("test.dng"));

        // 2. When enabled, RAW extension routes to rawDecoder and succeeds
        rawEnabled = true;
        var info = router.ReadInfo("test.dng");
        Assert.Equal(640, info.PixelWidth);
        Assert.Equal(480, info.PixelHeight);

        var decoded = router.Decode(new DecodeRequest("test.dng", DecodeBox.Unbounded));
        Assert.NotNull(decoded);
        Assert.Equal(640, decoded.OriginalWidth);
        Assert.Equal(480, decoded.OriginalHeight);
    }

    [Fact]
    public void PreviewSelector_SelectsSmallestMatchingBox()
    {
        var pSmall = new EmbeddedPreview(100, 0, 1000, EmbeddedPreviewKind.Jpeg, 320, 240, PreviewColorSpace.Srgb);
        var pMedium = new EmbeddedPreview(200, 0, 2000, EmbeddedPreviewKind.Jpeg, 1024, 768, PreviewColorSpace.Srgb);
        var pLarge = new EmbeddedPreview(300, 0, 5000, EmbeddedPreviewKind.Jpeg, 4000, 3000, PreviewColorSpace.Srgb);

        var previews = new[] { pSmall, pLarge, pMedium };
        var dummyHeaderSource = new InMemoryRawHeaderSource(new byte[64]);

        // Request box 800x600 -> should pick pMedium (1024x768)
        var selected = PreviewSelector.SelectPreview(dummyHeaderSource, previews, new DecodeBox(800, 600), orientation: 1);
        Assert.NotNull(selected);
        Assert.Equal(1024, selected.Width);
        Assert.Equal(768, selected.Height);

        // Request unbounded -> should pick largest (4000x3000)
        var selectedUnbounded = PreviewSelector.SelectPreview(dummyHeaderSource, previews, DecodeBox.Unbounded, orientation: 1);
        Assert.NotNull(selectedUnbounded);
        Assert.Equal(4000, selectedUnbounded.Width);
        Assert.Equal(3000, selectedUnbounded.Height);
    }
}
