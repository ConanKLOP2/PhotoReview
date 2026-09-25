using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// preview-v7: the disk-cache entry keeps the decoder's EXIF, so a disk hit shows the photo information line
/// without reading the source; v6 entries stay usable (no EXIF) and a bad EXIF block never costs the pixels.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewCacheExifTests : IAsyncLifetime
{
    private static readonly ExifSummary Exif = new()
    {
        DateTaken = new DateTime(2024, 5, 1, 14, 3, 22),
        CameraMake = "Canon",
        CameraModel = "Canon EOS R5",
        LensModel = "RF24-70mm F2.8 L IS USM",
        Iso = 400,
        FocalLength = new ExifRational(50, 1),
        FNumber = new ExifRational(28, 10),
        ExposureTime = new ExifRational(1, 250),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-PreviewCacheExif-" + Guid.NewGuid().ToString("N"));
    private readonly List<PreviewImageService> _services = [];
    private string _source = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.jpg");
        File.WriteAllBytes(_source, [1, 2, 3, 4]); // never decoded: the fake decoder below supplies the pixels
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(service => service.ShutdownPersistWorkersAsync()));
        foreach (var service in _services) await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CachePath(string name) => Path.Combine(_root, name + ".pv4");

    private static BitmapSource Bitmap()
    {
        var pixels = new byte[8 * 6 * 4];
        Array.Fill(pixels, (byte)90);
        var bitmap = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgr32, null, pixels, 8 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact(DisplayName = "A cache entry round-trips the EXIF together with its pixels and header fields")]
    public async Task EntryRoundTripsExif()
    {
        var path = CachePath("roundtrip");
        await PreviewCacheFile.WriteAtomicallyAsync(
            new WpfDecodedImage(Bitmap(), downscaled: true, orientation: 6, originalWidth: 4000, originalHeight: 6000, exif: Exif), path);

        var read = PreviewCacheFile.ReadAsDecodedImage(path);

        Assert.Equal(Exif, read.Exif);
        Assert.Equal(6, read.Orientation);
        Assert.Equal((4000, 6000), (read.OriginalWidth, read.OriginalHeight));
        Assert.Equal((8, 6), (read.PixelWidth, read.PixelHeight));
    }

    [Fact(DisplayName = "An entry without EXIF reads back with none")]
    public async Task EntryWithoutExif()
    {
        var path = CachePath("none");
        await PreviewCacheFile.WriteAtomicallyAsync(new WpfDecodedImage(Bitmap(), downscaled: true), path);

        Assert.Null(PreviewCacheFile.ReadAsDecodedImage(path).Exif);
        Assert.Equal(PreviewCacheFile.CurrentVersion, File.ReadAllBytes(path)[4]);
    }

    [Fact(DisplayName = "A v6 entry (no EXIF block) is still served, without EXIF, instead of forcing a source re-read")]
    public async Task Version6EntryIsReadWithoutExif()
    {
        var path = CachePath("v6");
        await PreviewCacheFile.WriteAtomicallyAsync(new WpfDecodedImage(Bitmap(), downscaled: true, originalWidth: 800, originalHeight: 600), path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24, 2))); // empty EXIF block
        File.WriteAllBytes(path, [.. bytes[..24], .. bytes[26..]]); // v6 layout: payload right after the header
        File.WriteAllBytes(path, SetVersion(File.ReadAllBytes(path), 6));

        var read = PreviewCacheFile.ReadAsDecodedImage(path);

        Assert.Null(read.Exif);
        Assert.Equal((800, 600), (read.OriginalWidth, read.OriginalHeight));
        Assert.Equal((8, 6), (read.PixelWidth, read.PixelHeight));
    }

    [Fact(DisplayName = "A corrupt EXIF block leaves the entry usable, without EXIF")]
    public async Task CorruptExifBlockKeepsPixels()
    {
        var path = CachePath("corrupt");
        await PreviewCacheFile.WriteAtomicallyAsync(new WpfDecodedImage(Bitmap(), downscaled: true, exif: Exif), path);
        var bytes = File.ReadAllBytes(path);
        bytes[26] = 99; // EXIF block format version

        File.WriteAllBytes(path, bytes);
        var read = PreviewCacheFile.ReadAsDecodedImage(path);

        Assert.Null(read.Exif);
        Assert.Equal(8, read.PixelWidth);
    }

    [Fact(DisplayName = "An EXIF length beyond the codec's bound is rejected as a corrupt entry")]
    public async Task OversizedExifLengthIsRejected()
    {
        var path = CachePath("oversized");
        await PreviewCacheFile.WriteAtomicallyAsync(new WpfDecodedImage(Bitmap(), downscaled: true, exif: Exif), path);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24, 2), ushort.MaxValue);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.ReadAsDecodedImage(path));
    }

    [Fact(DisplayName = "A disk-cache hit in a new session shows the EXIF without decoding (reading) the source")]
    public async Task DiskHitCarriesExifWithoutSourceRead()
    {
        var disk = Path.Combine(_root, "service");
        var writerDecoder = new ExifDecoder(Exif);
        var writer = Track(CreateService(disk, writerDecoder, new ReviewMetrics()));
        var produced = await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync();
        Assert.Equal(Exif, produced.Exif);
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));

        var readerDecoder = new ExifDecoder(null);
        var metrics = new ReviewMetrics();
        var reader = Track(CreateService(disk, readerDecoder, metrics));
        var fromDisk = await reader.GetPreviewAsync(_source);

        Assert.Equal(Exif, fromDisk.Exif);
        Assert.Equal(0, readerDecoder.DecodeCount);
        Assert.Equal(1, metrics.Snapshot().DiskCacheHits);
        Assert.Equal(0, metrics.Snapshot().SourceReads);

        // And the RAM hit that follows is the same object, EXIF included.
        Assert.Same(fromDisk, await reader.GetPreviewAsync(_source));
    }

    private static byte[] SetVersion(byte[] bytes, byte version)
    {
        bytes[4] = version;
        return bytes;
    }

    private PreviewImageService Track(PreviewImageService service)
    {
        _services.Add(service);
        return service;
    }

    private static PreviewImageService CreateService(string disk, IImageDecoder decoder, ReviewMetrics metrics) =>
        new(metrics, () => false, () => 4, capacityBytes: 1024 * 1024, diskCacheDirectory: disk,
            disableDiskCacheOverride: false, decoder: decoder);

    private sealed class ExifDecoder(ExifSummary? exif) : IImageDecoder
    {
        private int _decodeCount;
        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _decodeCount);
            return new WpfDecodedImage(Bitmap(), downscaled: true, originalWidth: 6000, originalHeight: 4000, exif: exif);
        }

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }
}
