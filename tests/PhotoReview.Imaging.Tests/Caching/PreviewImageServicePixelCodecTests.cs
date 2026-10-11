using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// WP-04: the persist channel and the disk-cache read are framework-agnostic -- a service wired with the Win32 codec
/// (<see cref="PixelBufferImageCodec"/>) persists previews whose platform image is a <see cref="PixelBuffer"/> (no BitmapSource
/// anywhere) and serves them back as PixelBuffers; the WPF-wired service reads the very same entry as a BitmapSource.
/// </summary>
public sealed class PreviewImageServicePixelCodecTests : IDisposable
{
    private readonly TempRoot _root = new("PreviewPixelCodec");
    private readonly List<PreviewImageService> _services = [];
    private readonly string _source;

    public PreviewImageServicePixelCodecTests()
    {
        _source = Path.Combine(_root.Dir("src"), "photo.jpg");
        File.WriteAllBytes(_source, [1, 2, 3, 4]); // the fake decoder never reads it; the key only needs its stat
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
            service.WaitForPruneAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        _root.Dispose();
    }

    [Fact(DisplayName = "WP-04: PixelBuffer previews persist through the pixels codec and come back from disk as PixelBuffers (and as BitmapSources for the WPF codec)")]
    public async Task PixelBufferPreview_PersistsAndReadsBack_WithEitherCodec()
    {
        var disk = _root.Dir("disk");
        var writer = Track(CreateService(disk, new PixelDecoder(PixelLayout.Bgr32, opaque: true), PixelBufferImageCodec.Instance, new ReviewMetrics()));
        var produced = await writer.GetPreviewAsync(_source);
        Assert.IsType<PixelBuffer>(produced.PlatformImage);
        await writer.ShutdownPersistWorkersAsync();
        var entry = Assert.Single(Directory.GetFiles(disk, "*.pv4"));

        var readerDecoder = new PixelDecoder(PixelLayout.Bgr32, opaque: true);
        var metrics = new ReviewMetrics();
        var reader = Track(CreateService(disk, readerDecoder, PixelBufferImageCodec.Instance, metrics));
        var fromDisk = await reader.GetPreviewAsync(_source);

        Assert.Equal(0, readerDecoder.DecodeCount);
        Assert.Equal(1, metrics.Snapshot().DiskCacheHits);
        var pixels = Assert.IsType<PixelBuffer>(fromDisk.PlatformImage);
        Assert.Equal((PixelDecoder.Width, PixelDecoder.Height, PixelLayout.Bgr32), (pixels.Width, pixels.Height, pixels.Layout));
        Assert.Equal((6, DecoderBackend.TurboJpeg, 6000, 4000), (fromDisk.Orientation, fromDisk.ActualBackend, fromDisk.OriginalWidth, fromDisk.OriginalHeight));
        Assert.True(fromDisk.Downscaled);

        // The same entry through the old WPF reader and through the WPF-wired service: identical pixels.
        var old = LegacyWpfPreviewCache.Read(File.ReadAllBytes(entry));
        using var oldPixels = LegacyWpfPreviewCache.ToPixels(old.Bitmap, PixelLayout.Bgr32);
        PixelAssert.Equal(oldPixels, pixels, compareUnusedByte: false);
        var wpfReader = Track(CreateService(disk, new PixelDecoder(PixelLayout.Bgr32, opaque: true), platformCodec: null, new ReviewMetrics()));
        var wpfImage = await wpfReader.GetPreviewAsync(_source);
        PixelAssert.Equal(Assert.IsAssignableFrom<BitmapSource>(wpfImage.PlatformImage), pixels, compareUnusedByte: false);
    }

    [Theory(DisplayName = "WP-04 / Q-R7: a Pbgra32 PixelBuffer preview persists only when every pixel is opaque")]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Pbgra32Preview_PersistsOnlyWhenOpaque(bool opaque, int expectedEntries)
    {
        var disk = _root.Dir("disk-" + opaque);
        var writer = Track(CreateService(disk, new PixelDecoder(PixelLayout.Pbgra32, opaque), PixelBufferImageCodec.Instance, new ReviewMetrics()));
        await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync();

        Assert.Equal(expectedEntries, Directory.GetFiles(disk, "*.pv4").Length);
    }

    [Fact(DisplayName = "WP-04: a platform image the codec cannot convert is skipped by the persist worker without logging an error")]
    public async Task UnconvertiblePlatformImage_IsNotPersisted_NoError()
    {
        var disk = _root.Dir("disk-foreign");
        var log = new RecordingLog();
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 4, PixelBufferImageCodec.Instance, capacityBytes: 1024 * 1024, diskCacheDirectory: disk,
            disableDiskCacheOverride: false, decoder: new ForeignDecoder(), log: log));
        await service.GetPreviewAsync(_source);
        await service.ShutdownPersistWorkersAsync();

        Assert.Empty(Directory.GetFiles(disk, "*.pv4"));
        Assert.Equal(0, log.Errors);
    }

    private PreviewImageService Track(PreviewImageService service)
    {
        _services.Add(service);
        return service;
    }

    private static PreviewImageService CreateService(string disk, IImageDecoder decoder, IPlatformImageCodec? platformCodec, ReviewMetrics metrics) =>
        new(metrics, () => false, () => 4, platformCodec ?? WpfBitmapSourceCodec.Instance, capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: disk,
            disableDiskCacheOverride: false, decoder: decoder);

    private sealed class PixelDecoder(PixelLayout layout, bool opaque) : IImageDecoder
    {
        public const int Width = 120;
        public const int Height = 80;
        private int _decodeCount;
        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _decodeCount);
            var pixels = LegacyWpfPreviewCache.PatternPixels(Width, Height, layout);
            if (!opaque) pixels.GetRow(Height / 2)[3] = 0; // one transparent pixel (B=G=R=0 is valid premultiplied)
            if (!opaque) pixels.GetRow(Height / 2)[..3].Clear();
            return new DecodedImage(pixels, Width, Height, pixels.ByteCount, downscaled: true, orientation: 6,
                actualBackend: DecoderBackend.TurboJpeg, originalWidth: 6000, originalHeight: 4000);
        }

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class ForeignDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => new DecodedImage(new object(), 8, 8, 256, downscaled: true);

        public ImageInfo ReadInfo(string path) => new(8, 8);
    }

    private sealed class RecordingLog : ILog
    {
        private int _errors;
        public int Errors => Volatile.Read(ref _errors);
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Interlocked.Increment(ref _errors);
    }
}
