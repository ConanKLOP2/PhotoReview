using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Quality;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Decoding.Wic;
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
    public void ReadInfo_CachesParsedContainerBySourceIdentity()
    {
        var reader = new TrackingSourceReader(new byte[128]);
        var containerReader = new CountingContainerReader();
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(reader), reader,
            new RawContainerReaderRegistry([containerReader]));

        var first = decoder.ReadInfo("virtual.dng");
        var second = decoder.ReadInfo("virtual.dng");

        Assert.Equal(first, second);
        Assert.Equal(1, containerReader.ReadCount);
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

    [Fact]
    public void Decode_CachesPreviewRangeInsteadOfWholeRawFile()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg);
        var largeFile = new byte[10 * 1024 * 1024];
        Array.Copy(tiff, largeFile, tiff.Length);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dng");
        File.WriteAllBytes(path, largeFile);
        try
        {
            var reader = new TrackingSourceReader(largeFile);
            var cache = new SourceBytesCache(2 * 1024 * 1024, reader);
            var decoder = new RawDecoder(new WpfBitmapImageDecoder(reader), reader, sourceBytesCache: cache);
            var request = new DecodeRequest(path, new DecodeBox(320, 240), priority: SourceReadPriority.Preload);

            decoder.Decode(request);
            var bytesReadAfterFirstDecode = reader.TotalBytesRead;
            decoder.Decode(request);

            Assert.Equal(jpeg.Length, cache.CurrentSize);
            Assert.Equal(1, cache.Count);
            Assert.True(reader.TotalBytesRead <= bytesReadAfterFirstDecode + 128 * 1024,
                $"The second decode should reuse the cached preview byte range; additional bytes={reader.TotalBytesRead - bytesReadAfterFirstDecode}.");
            Assert.True(cache.CurrentSize < largeFile.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PreviewService_DoesNotPreReadWholeRawFileIntoSourceCache()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg);
        var largeFile = new byte[10 * 1024 * 1024];
        Array.Copy(tiff, largeFile, tiff.Length);
        var root = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "camera.dng");
        File.WriteAllBytes(path, largeFile);
        var bytesCache = new SourceBytesCache(16 * 1024 * 1024);
        var metrics = new ReviewMetrics();
        var service = new PreviewImageService(metrics, () => false, () => new DecodeBox(320, 240),
            capacityBytes: 64 * 1024 * 1024, diskCacheDirectory: Path.Combine(root, "preview-cache"),
            diskCacheCapacityBytes: 0, disableDiskCacheOverride: true,
            decoder: new RawDecoder(new WpfBitmapImageDecoder(), sourceBytesCache: bytesCache), sourceBytesCache: bytesCache);
        try
        {
            Assert.Equal(1, service.GetCurrentCacheKey(path).SourceKind);
            var image = await service.GetPreviewAsync(path);

            Assert.True(image.PixelWidth > 0);
            Assert.Equal(jpeg.Length, bytesCache.CurrentSize);
            Assert.True(metrics.Snapshot().SourceBytesRead < largeFile.Length);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void Decode_AppliesOrientationAndSensorTransposition(ushort orientation, bool expectTransposed)
    {
        var jpegPath = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-orientation-{Guid.NewGuid():N}.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 640, 480);
        var jpeg = File.ReadAllBytes(jpegPath);
        File.Delete(jpegPath);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: orientation);

        var reader = new TrackingSourceReader(tiff);
        var innerDecoder = new WpfBitmapImageDecoder(reader);
        var decoder = new RawDecoder(innerDecoder, reader);

        var request = new DecodeRequest("test.dng", DecodeBox.Unbounded, applyOrientation: true);
        var result = decoder.Decode(request);

        Assert.Equal(orientation, result.Orientation);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(result.PlatformImage);
        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, bitmap.Format);
        if (expectTransposed)
        {
            Assert.Equal(480, result.OriginalWidth);
            Assert.Equal(640, result.OriginalHeight);
            Assert.Equal(480, result.PixelWidth);
            Assert.Equal(640, result.PixelHeight);
        }
        else
        {
            Assert.Equal(640, result.OriginalWidth);
            Assert.Equal(480, result.OriginalHeight);
            Assert.Equal(640, result.PixelWidth);
            Assert.Equal(480, result.PixelHeight);
        }
    }

    [Fact]
    public void AdobeRgbHint_InjectsBundledProfileAndMatchesProfileTaggedJpeg()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-adobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var profilePath = Path.Combine(root, "AdobeCompat-v2.icc");
        var taggedPath = Path.Combine(root, "tagged.jpg");
        var rawPath = Path.Combine(root, "camera.dng");
        try
        {
            File.WriteAllBytes(profilePath, RawJpegIccProfile.GetBundledAdobeRgbProfile());
            FixtureGenerator.GenerateJpegWithIcc(taggedPath, 128, 96, profilePath);
            var taggedJpeg = AddAdobeRgbHint(File.ReadAllBytes(taggedPath));
            Assert.Same(taggedJpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(taggedJpeg));
            var untaggedPreview = StripIccProfile(taggedJpeg);
            File.WriteAllBytes(taggedPath, taggedJpeg);
            File.WriteAllBytes(rawPath, SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: untaggedPreview));

            var wic = new WicDirectDecoder();
            var expected = wic.Decode(new DecodeRequest(taggedPath, DecodeBox.Unbounded));
            var actual = new RawDecoder(wic).Decode(new DecodeRequest(rawPath, DecodeBox.Unbounded));
            var comparison = ImageCompare.Compare(expected, actual);

            Assert.True(comparison.Psnr >= 50, $"RAW Adobe RGB conversion PSNR {comparison.Psnr} dB < 50 dB");
            Assert.True(comparison.MeanDeltaE <= 0.1, $"RAW Adobe RGB conversion MeanDeltaE {comparison.MeanDeltaE} > 0.1");
            Assert.Equal(DecoderBackend.WicDirect, actual.ActualBackend);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AdobeRgbProfile_IsNotDuplicatedWhenJpegAlreadyHasIcc()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var profilePath = Path.Combine(root, "AdobeCompat-v2.icc");
        var jpegPath = Path.Combine(root, "profile.jpg");
        try
        {
            File.WriteAllBytes(profilePath, RawJpegIccProfile.GetBundledAdobeRgbProfile());
            FixtureGenerator.GenerateJpegWithIcc(jpegPath, 32, 24, profilePath);
            var jpeg = File.ReadAllBytes(jpegPath);
            Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Decode_TruncatedRawContainerOrEmbeddedPreview_FailsWithTypedErrors()
    {
        var truncatedContainerReader = new TrackingSourceReader([0x49, 0x49, 0x2A]);
        var truncatedContainerDecoder = new RawDecoder(new WpfBitmapImageDecoder(truncatedContainerReader), truncatedContainerReader);
        Assert.ThrowsAny<NotSupportedException>(() => truncatedContainerDecoder.Decode(new DecodeRequest("broken.dng", DecodeBox.Unbounded)));

        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var complete = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg);
        var truncated = complete.AsSpan(0, complete.Length - 8).ToArray();
        var truncatedPreviewReader = new TrackingSourceReader(truncated);
        var truncatedPreviewDecoder = new RawDecoder(new WpfBitmapImageDecoder(truncatedPreviewReader), truncatedPreviewReader);
        Assert.Throws<InvalidDataException>(() => truncatedPreviewDecoder.Decode(new DecodeRequest("broken.dng", DecodeBox.Unbounded)));
    }

    [Fact]
    public void Decode_ReleasesRawFileHandleImmediatelyAfterPreviewDecode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "camera.dng");
        var jpegPath = Path.Combine(root, "preview.jpg");
        try
        {
            FixtureGenerator.GenerateGradientJpeg(jpegPath, 80, 60);
            File.WriteAllBytes(path, SyntheticRawBuilder.BuildTiff(true, File.ReadAllBytes(jpegPath)));
            var decoded = new RawDecoder(new WpfBitmapImageDecoder()).Decode(new DecodeRequest(path, DecodeBox.Unbounded));
            Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] AddAdobeRgbHint(byte[] jpeg)
    {
        var exif = PreviewSelectorColorSpaceTests.ExifWithInteropIndex("R03");
        var length = exif.Length + 2;
        byte[] result = [.. jpeg.AsSpan(0, 2).ToArray(), 0xFF, 0xE1, (byte)(length >> 8), (byte)length,
            .. exif, .. jpeg.AsSpan(2).ToArray()];
        return result;
    }

    private static byte[] StripIccProfile(byte[] jpeg)
    {
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        var offset = 2;
        while (offset + 4 <= jpeg.Length && jpeg[offset] == 0xFF)
        {
            var markerStart = offset;
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset >= jpeg.Length) break;
            var marker = jpeg[offset++];
            if (marker is 0xDA or 0xD9)
            {
                output.Write(jpeg, markerStart, jpeg.Length - markerStart);
                break;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write(jpeg, markerStart, offset - markerStart);
                continue;
            }
            if (offset + 2 > jpeg.Length) break;
            var length = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (length < 2 || offset + length > jpeg.Length) break;
            var isIcc = marker == 0xE2 && length >= 14 && jpeg.AsSpan(offset + 2, 12).SequenceEqual("ICC_PROFILE\0"u8);
            if (!isIcc) output.Write(jpeg, markerStart, 2 + length);
            offset += length;
        }
        return output.ToArray();
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

    [Theory]
    [InlineData("test.nrw")]
    [InlineData("test.pef")]
    public void FormatRoutingDecoder_DoesNotRouteReservedRawExtensions(string path)
    {
        var standard = new RoutingProbeDecoder();
        var raw = new RoutingProbeDecoder();
        var router = new FormatRoutingDecoder(standard, raw, () => true);

        Assert.Equal(1, router.ReadInfo(path).PixelWidth);
        Assert.Equal(1, standard.ReadInfoCount);
        Assert.Equal(0, raw.ReadInfoCount);
    }

    [Fact]
    public void RawDecoder_UsesInjectedFallbackOnlyForUnsupportedOrfPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var invalidPreview = new byte[] { 0x13, 0x37, 0x00, 0x00 };
        var orf = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: invalidPreview);
        var reader = new TrackingSourceReader(orf);
        var fallback = new TestRawPreviewFallback(jpeg);
        var inner = new FallbackAwareDecoder(new WpfBitmapImageDecoder());

        var decoded = new RawDecoder(inner, reader, previewFallback: fallback)
            .Decode(new DecodeRequest("test.orf", DecodeBox.Unbounded));

        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(RawFormat.Orf, fallback.LastFormat);
        Assert.True(decoded.PixelWidth > 0);
        Assert.True(decoded.PixelHeight > 0);
    }

    [Fact]
    public void RawDecoder_UsesEmbeddedOrfPreviewBeforeFallback()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var orf = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg);
        var reader = new TrackingSourceReader(orf);
        var fallback = new TestRawPreviewFallback(jpeg);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(), reader, previewFallback: fallback);

        _ = decoder.Decode(new DecodeRequest("test.orf", DecodeBox.Unbounded));

        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public void RawDecoder_DoesNotUseOrfFallbackForOtherFormats()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var invalidPreview = new byte[] { 0x13, 0x37, 0x00, 0x00 };
        var dng = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: invalidPreview);
        var reader = new TrackingSourceReader(dng);
        var fallback = new TestRawPreviewFallback(jpeg);
        var inner = new FallbackAwareDecoder(new WpfBitmapImageDecoder());
        var decoder = new RawDecoder(inner, reader, previewFallback: fallback);

        Assert.Throws<NotSupportedException>(() => decoder.Decode(new DecodeRequest("test.dng", DecodeBox.Unbounded)));
        Assert.Equal(0, fallback.CallCount);
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

    private sealed class CountingContainerReader : IRawContainerReader
    {
        public RawFormat Format => RawFormat.Dng;
        public int ReadCount { get; private set; }
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);
        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken)
        {
            ReadCount++;
            return new RawContainerInfo(RawFormat.Dng, 4000, 3000, 1, [], []);
        }
    }

    private sealed class RoutingProbeDecoder : IImageDecoder
    {
        public int ReadInfoCount { get; private set; }
        public ImageInfo ReadInfo(string path)
        {
            ReadInfoCount++;
            return new ImageInfo(1, 1, 1);
        }

        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException();
    }

    private sealed class TestRawPreviewFallback(byte[] thumbnailBytes) : IRawPreviewFallback
    {
        public int CallCount { get; private set; }
        public RawFormat LastFormat { get; private set; }

        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
        {
            CallCount++;
            LastFormat = format;
            return thumbnailBytes;
        }
    }

    private sealed class FallbackAwareDecoder(IImageDecoder jpegDecoder) : IImageDecoder
    {
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (request.Bytes is not { } bytes || bytes.Span[0] != 0xFF)
                throw new NotSupportedException("No imaging component suitable to complete this operation was found.");
            return jpegDecoder.Decode(request);
        }
    }
}
