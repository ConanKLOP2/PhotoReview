using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Metadata;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Slow")]
public sealed class RawPreloadBudgetSlowTests
{
    private const int ImageCount = 200;
    private const int Width = 640;
    private const int Height = 480;
    private const int HeaderBlockBytes = SourceRawHeaderSource.BlockSize;
    private const long CacheCapacityBytes = 512L * 1024 * 1024;

    [Fact]
    public async Task TwoHundredRawFiles_EstimateMatchesMeasuredCache_AndReadsOnlyHeaderAndPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoReview-raw-preload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var jpeg = ExifTestData.EncodeJpegWithExif(Width, Height, withExif: false);
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg);
        var contents = new byte[512 * 1024];
        Array.Copy(tiff, contents, tiff.Length);
        var entries = Enumerable.Range(0, ImageCount)
            .Select(index =>
            {
                var path = Path.Combine(root, $"image-{index:D3}.dng");
                File.WriteAllBytes(path, contents);
                return new CatalogEntry(path).WithMetadata(contents.Length, File.GetLastWriteTimeUtc(path), Width, Height);
            })
            .ToArray();

        var sourceReader = new CountingSourceReader();
        var sourceBytesCache = new SourceBytesCache(16 * 1024 * 1024, sourceReader);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(sourceReader), sourceReader, sourceBytesCache: sourceBytesCache);
        var metrics = new ReviewMetrics();
        var log = new CapturingLog();
        var service = new PreviewImageService(metrics, () => false, () => new DecodeBox(Width, Height),
            capacityBytes: CacheCapacityBytes, diskCacheDirectory: Path.Combine(root, "preview-cache"),
            diskCacheCapacityBytes: 0, disableDiskCacheOverride: true, decoder: decoder,
            sourceBytesCache: sourceBytesCache, sourceReader: sourceReader);
        try
        {
            await service.GetPreviewAsync(entries[0].Path);
            using var scheduler = new PreloadScheduler(service, metrics, () => entries,
                new PreloadOptions(WorkerCount: 8, FullFolderThresholdBytes: CacheCapacityBytes),
                new FakeMemoryProbe(true), log);

            await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));

            var estimatedBytes = ParseEstimatedBytes(log.Messages.First(message => message.Contains("estimatedBytes=", StringComparison.Ordinal)));
            var measuredBytes = service.CacheBytes;
            Assert.Equal(ImageCount, service.CacheCount);
            Assert.Equal((long)ImageCount * Width * Height * 4, measuredBytes);
            Assert.InRange(estimatedBytes, (long)(measuredBytes * 0.8), (long)(measuredBytes * 1.2));
            Assert.Equal(ImageCount, sourceReader.ReadBytesByPath.Count);
            var expectedMaxRead = Math.Min(contents.Length, 2L * HeaderBlockBytes) + jpeg.Length;
            Assert.True(expectedMaxRead < contents.Length, "The synthetic RAW file must be larger than its allowed header-plus-preview read.");
            Assert.Equal(expectedMaxRead, sourceReader.ReadBytesByPath.Values.Max());
            Assert.All(sourceReader.ReadBytesByPath.Values,
                bytesRead => Assert.InRange(bytesRead, 1, expectedMaxRead));
            Assert.Equal((long)ImageCount * jpeg.Length, sourceBytesCache.CurrentSize);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static long ParseEstimatedBytes(string message)
    {
        const string marker = "estimatedBytes=";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf(' ', start);
        return long.Parse(message.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CapturingLog : ILog
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public bool Enabled => true;
        public string[] Messages => _messages.ToArray();
        public void Info(string message) => _messages.Enqueue(message);
        public void Warn(string message) => _messages.Enqueue(message);
        public void Error(string message, Exception? ex = null) => _messages.Enqueue(message);
    }

    private sealed class CountingSourceReader : ISourceReader
    {
        private readonly ConcurrentDictionary<string, long> _readBytes = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, long> ReadBytesByPath => _readBytes;

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
            new CountingStream(path, PhysicalSourceReader.Instance.OpenSource(path, priority, bufferSize), this);

        private void Record(string path, int bytes) => _readBytes.AddOrUpdate(path, bytes, (_, previous) => previous + bytes);

        private sealed class CountingStream(string path, Stream inner, CountingSourceReader owner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                if (read > 0) owner.Record(path, read);
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = inner.Read(buffer);
                if (read > 0) owner.Record(path, read);
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
