using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// perf(decode): the preview decode box (width x height) flows from the caller into the RAM cache
/// key, the <see cref="DecodeRequest"/> and the disk-cache key, so a different box never reuses
/// pixels sized for another one.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewDecodeBoxTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PhotoReview-PreviewDecodeBox-" + Guid.NewGuid().ToString("N"));
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
    public async Task PreviewBox_FlowsIntoKeyAndDecodeRequest()
    {
        var decoder = new RecordingDecoder();
        var service = Track(CreateService(Path.Combine(_root, "flow"), () => new DecodeBox(2304, 1280), decoder, disableDisk: true));

        var key = service.GetCurrentCacheKey(_source);
        await service.GetPreviewAsync(_source, key);

        Assert.Equal(new DecodeBox(2304, 1280), key.TargetBox);
        var request = Assert.Single(decoder.Requests);
        Assert.Equal(new DecodeBox(2304, 1280), request.Box);
    }

    [Fact]
    public async Task OriginalMode_IgnoresBox_DecodesFullSize()
    {
        var decoder = new RecordingDecoder();
        var service = Track(CreateService(Path.Combine(_root, "original"), () => new DecodeBox(2304, 1280), decoder,
            disableDisk: true, isOriginal: true));

        var key = service.GetCurrentCacheKey(_source);
        await service.GetPreviewAsync(_source, key);

        Assert.True(key.TargetBox.IsUnbounded);
        Assert.False(Assert.Single(decoder.Requests).IsDownscaleRequested);
    }

    [Fact]
    public async Task ChangedBox_MissesRamCache()
    {
        var box = new DecodeBox(2304, 1280);
        var decoder = new RecordingDecoder();
        var service = Track(CreateService(Path.Combine(_root, "ram"), () => box, decoder, disableDisk: true));

        await service.GetPreviewAsync(_source);
        await service.GetPreviewAsync(_source);
        Assert.Single(decoder.Requests);

        box = new DecodeBox(2304, 1408);
        Assert.False(service.TryGetCachedPreview(_source, out _));
        await service.GetPreviewAsync(_source);

        Assert.Equal(2, decoder.Requests.Length);
        Assert.Equal(1408, decoder.Requests[^1].TargetHeight);
    }

    [Fact]
    public async Task DiskCacheKey_IncludesBoxHeight()
    {
        var disk = Path.Combine(_root, "disk");
        var writer = Track(CreateService(disk, () => new DecodeBox(2304, 1280), new RecordingDecoder()));
        await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync();
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));

        // Same width, different height: must not be served the 1280-high entry from disk.
        var tallerDecoder = new RecordingDecoder();
        var tallerMetrics = new ReviewMetrics();
        var taller = Track(CreateService(disk, () => new DecodeBox(2304, 1408), tallerDecoder, metrics: tallerMetrics));
        await taller.GetPreviewAsync(_source);
        Assert.Single(tallerDecoder.Requests);
        Assert.Equal(0, tallerMetrics.Snapshot().DiskCacheHits);

        // Same box: served from disk without decoding the source.
        var sameDecoder = new RecordingDecoder();
        var sameMetrics = new ReviewMetrics();
        var same = Track(CreateService(disk, () => new DecodeBox(2304, 1280), sameDecoder, metrics: sameMetrics));
        await same.GetPreviewAsync(_source);
        Assert.Empty(sameDecoder.Requests);
        Assert.Equal(1, sameMetrics.Snapshot().DiskCacheHits);
    }

    [Fact]
    public async Task WidthOnlyConstructor_KeepsHeightUnconstrained()
    {
        var decoder = new RecordingDecoder();
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 2208,
            capacityBytes: 1024 * 1024, diskCacheDirectory: Path.Combine(_root, "width"),
            disableDiskCacheOverride: true, decoder: decoder));

        var key = service.GetCurrentCacheKey(_source);
        await service.GetPreviewAsync(_source, key);

        Assert.Equal(new DecodeBox(2208, 0), key.TargetBox);
        Assert.Equal(new DecodeBox(2208, 0), Assert.Single(decoder.Requests).Box);
    }

    private PreviewImageService Track(PreviewImageService service)
    {
        _services.Add(service);
        return service;
    }

    private static PreviewImageService CreateService(
        string disk,
        Func<DecodeBox> box,
        IImageDecoder decoder,
        bool disableDisk = false,
        bool isOriginal = false,
        ReviewMetrics? metrics = null) =>
        new(metrics ?? new ReviewMetrics(), () => isOriginal, box,
            capacityBytes: 1024 * 1024,
            diskCacheDirectory: disk,
            disableDiskCacheOverride: disableDisk,
            decoder: decoder);

    private sealed class RecordingDecoder : IImageDecoder
    {
        private readonly ConcurrentQueue<DecodeRequest> _requests = new();
        public DecodeRequest[] Requests => _requests.ToArray();

        public IDecodedImage Decode(DecodeRequest request)
        {
            _requests.Enqueue(request);
            var pixels = new byte[4 * 4 * 3];
            Array.Fill(pixels, (byte)127);
            var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);
            bitmap.Freeze();
            return new WpfDecodedImage(bitmap, downscaled: request.IsDownscaleRequested);
        }

        public ImageInfo ReadInfo(string path) => new(4, 3, 1);
    }
}
