using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class PreviewBackendIdentityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PhotoReview-PreviewBackendIdentity-" + Guid.NewGuid().ToString("N"));
    private readonly List<PreviewImageService> _services = [];
    private string _source = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(service => service.ShutdownPersistWorkersAsync()));
        foreach (var service in _services)
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ExplicitKeySnapshotsBackendAcrossBlockedDecode()
    {
        var current = DecoderBackend.Wpf;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var factory = new RecordingFactory(backend => new FakeDecoder(backend, onDecode: () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        var service = Track(CreateService(Path.Combine(_root, "race"), () => current, factory, disableDisk: true));
        var key = service.GetCurrentCacheKey(_source);

        current = DecoderBackend.WicDirect;
        var load = service.GetPreviewAsync(_source, key);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        var image = await load;

        Assert.Equal(DecoderBackend.Wpf, key.Backend);
        Assert.Equal(DecoderBackend.Wpf, image.ActualBackend);
        Assert.Equal([DecoderBackend.Wpf], factory.CreatedBackends);
    }

    [Fact]
    public async Task OriginalDimensionsUseTheBackendCapturedInTheirKey()
    {
        var calls = 0;
        DecoderBackend CurrentBackend() => Interlocked.Increment(ref calls) == 1
            ? DecoderBackend.Wpf
            : DecoderBackend.WicDirect;
        var factory = new RecordingFactory(backend => new FakeDecoder(backend));
        var service = Track(CreateService(Path.Combine(_root, "info"), CurrentBackend, factory, disableDisk: true));

        var dimensions = await service.GetOriginalDimensionsAsync(_source);

        Assert.Equal((4, 3), dimensions);
        Assert.Equal([DecoderBackend.Wpf], factory.CreatedBackends);
    }

    // perf(cache) task 2: a preview whose decode fell back to a different backend than the one
    // requested (e.g. an ICC JPEG the configured TurboJpeg backend can't handle, decoded via
    // FallbackImageDecoder's inner backend instead) used to never be disk-cached at all -- every
    // future open re-ran the same fallback chain from source. It is now persisted like any other
    // downscaled preview: GetDiskCachePath still hashes the *requested* backend (WicDirect here),
    // but the v4 header truthfully records whichever backend actually produced the pixels (Wpf).
    [Fact]
    public async Task FallbackDecodedPreviewIsPersistedAndDiskHitReportsTheActualBackend()
    {
        var disk = Path.Combine(_root, "fallback");
        var decoder = new FakeDecoder(DecoderBackend.Wpf);
        var factory = new RecordingFactory(_ => decoder);
        var writer = Track(CreateService(disk, () => DecoderBackend.WicDirect, factory));

        var first = await writer.GetPreviewAsync(_source);
        var second = await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync();

        Assert.Same(first, second);
        Assert.Equal(DecoderBackend.Wpf, first.ActualBackend);
        Assert.Equal(1, decoder.DecodeCount);
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));

        // A fresh service requesting the same (WicDirect) backend must hit the disk entry
        // without decoding from source, and see the same ActualBackend (Wpf) a fresh fallback
        // decode would have reported -- the header, not the request, is the source of truth here.
        var readerDecoder = new FakeDecoder(DecoderBackend.Wpf);
        var metrics = new ReviewMetrics();
        var reader = Track(CreateService(disk, () => DecoderBackend.WicDirect,
            new RecordingFactory(_ => readerDecoder), metrics: metrics));
        var fromDisk = await reader.GetPreviewAsync(_source);

        Assert.Equal(DecoderBackend.Wpf, fromDisk.ActualBackend);
        Assert.Equal(0, readerDecoder.DecodeCount);
        Assert.Equal(1, metrics.Snapshot().DiskCacheHits);
        Assert.Equal(0, metrics.Snapshot().SourceReads);
    }

    [Fact]
    public async Task VersionedDiskHitPreservesProducingBackendAndAvoidsSourceDecode()
    {
        var disk = Path.Combine(_root, "disk-hit");
        var writerDecoder = new FakeDecoder(DecoderBackend.WicDirect);
        var writer = Track(CreateService(disk, () => DecoderBackend.WicDirect,
            new RecordingFactory(_ => writerDecoder)));
        var produced = await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync();
        Assert.Equal(DecoderBackend.WicDirect, produced.ActualBackend);
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));

        var readerDecoder = new FakeDecoder(DecoderBackend.WicDirect);
        var metrics = new ReviewMetrics();
        var reader = Track(CreateService(disk, () => DecoderBackend.WicDirect,
            new RecordingFactory(_ => readerDecoder), metrics: metrics));
        var fromDisk = await reader.GetPreviewAsync(_source);

        Assert.Equal(DecoderBackend.WicDirect, fromDisk.ActualBackend);
        Assert.Equal(0, readerDecoder.DecodeCount);
        Assert.Equal(1, metrics.Snapshot().DiskCacheHits);
        Assert.Equal(0, metrics.Snapshot().SourceReads);
    }

    [Fact]
    public async Task LegacyDiskEntryWithoutBackendProvenanceIsAMiss()
    {
        var disk = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(disk);
        var key = ImageCacheKey.Create(_source, false, 2, backend: DecoderBackend.WicDirect);
        var legacyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.Path}|{key.Length}|{key.LastWriteUtcTicks}|{key.IsOriginal}|{key.TargetWidth}|{key.OrientationApplied}|{key.Backend}")));
        SavePng(Path.Combine(disk, legacyHash + ".png"), CreateBitmap());
        var decoder = new FakeDecoder(DecoderBackend.WicDirect);
        var metrics = new ReviewMetrics();
        var service = Track(CreateService(disk, () => DecoderBackend.WicDirect,
            new RecordingFactory(_ => decoder), metrics: metrics));

        var image = await service.GetPreviewAsync(_source, key);

        Assert.Equal(DecoderBackend.WicDirect, image.ActualBackend);
        Assert.Equal(1, decoder.DecodeCount);
        Assert.Equal(0, metrics.Snapshot().DiskCacheHits);
        Assert.Equal(1, metrics.Snapshot().SourceReads);
    }

    private PreviewImageService Track(PreviewImageService service)
    {
        _services.Add(service);
        return service;
    }

    private static PreviewImageService CreateService(
        string disk,
        Func<DecoderBackend> currentBackend,
        IImageDecoderFactory factory,
        bool disableDisk = false,
        ReviewMetrics? metrics = null) =>
        new(metrics ?? new ReviewMetrics(), () => false, () => 2,
            capacityBytes: 1024 * 1024,
            diskCacheDirectory: disk,
            disableDiskCacheOverride: disableDisk,
            decoder: new FakeDecoder(DecoderBackend.Wpf),
            currentBackend: currentBackend,
            decoderFactory: factory);

    private static BitmapSource CreateBitmap()
    {
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)127);
        var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class RecordingFactory(Func<DecoderBackend, IImageDecoder> create) : IImageDecoderFactory
    {
        private readonly ConcurrentQueue<DecoderBackend> _created = new();
        public DecoderBackend[] CreatedBackends => _created.ToArray();

        public IImageDecoder Create(DecoderBackend backend)
        {
            _created.Enqueue(backend);
            return create(backend);
        }

        public bool IsRegistered(DecoderBackend backend) => true;
    }

    private sealed class FakeDecoder(
        DecoderBackend actualBackend,
        Action? onDecode = null) : IImageDecoder
    {
        private int _decodeCount;
        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _decodeCount);
            onDecode?.Invoke();
            return new WpfDecodedImage(CreateBitmap(), downscaled: request.TargetWidth > 0,
                actualBackend: actualBackend);
        }

        public ImageInfo ReadInfo(string path) => new(4, 3, 1);
    }
}

